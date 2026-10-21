using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using BKMsystem.Models;

namespace BKMsystem.Services;

// Структура для передачи подробностей прогресса
public struct ProgressReport
{
    public int Current { get; set; }
    public int Total { get; set; }
    public string CurrentFileName { get; set; }
}

public class ExcelParserService
{
    /// <summary>
    /// Считает суммарную длину реза детали по тексту столбца "Размеры заготовки" ведомости.
    /// Раньше это поле просто брало ПОСЛЕДНЕЕ число в строке — работало только для случайных
    /// прямоугольных заготовок вида "1055x1582" и давало 0 для круглых деталей вида "Ø115",
    /// т.к. там всего одно число (см. отчёт технолога — деталь "Заглушка" всегда выходила
    /// с длиной реза 0 мм). Теперь различаем два формата записи:
    ///   1) Диаметр(ы), помеченные значком Ø/⌀ — круглая деталь и/или внутренние отверстия
    ///      в ней. Резать плазмой нужно контур КАЖДОГО круга (наружный + все внутренние
    ///      отверстия), поэтому длина реза = сумма длин окружностей (π×D) по всем найденным
    ///      значениям с этим значком.
    ///   2) Прямоугольная заготовка вида "1055x1582" (без значка Ø) — длина реза = периметр
    ///      контура, 2×(ширина+длина); это физически ближе к реальному резу, чем произвольно
    ///      взятая одна сторона.
    /// Если формат не распознан — возвращаем 0 (как и раньше), это не должно приводить к
    /// падению, только к пустой оценке времени, которую технолог допишет вручную.
    /// </summary>
    internal static double ComputeCuttingLength(string dimensions)
    {
        if (string.IsNullOrWhiteSpace(dimensions)) return 0;

        var diameterMatches = Regex.Matches(dimensions, @"[Ø⌀]\s*(\d+(?:[.,]\d+)?)");
        if (diameterMatches.Count > 0)
        {
            double total = 0;
            foreach (Match m in diameterMatches)
            {
                if (double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                    total += Math.PI * d;
            }
            return total;
        }

        var dimMatches = Regex.Matches(dimensions, @"\d+(?:[.,]\d+)?");
        if (dimMatches.Count >= 2)
        {
            double.TryParse(dimMatches[0].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double w);
            double.TryParse(dimMatches[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double h);
            return 2 * (w + h);
        }
        if (dimMatches.Count == 1)
        {
            double.TryParse(dimMatches[0].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double single);
            return single;
        }
        return 0;
    }

    public List<Detail> ParsePlasmaDetails(string filePath)
    {
        var fileName = Path.GetFileName(filePath);

        if (fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
        {
            return new List<Detail>();
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var workbook = new XLWorkbook(stream);
            var worksheet = workbook.Worksheet(1);

            var headerRow = worksheet.RowsUsed()
                .FirstOrDefault(r => r.CellsUsed().Any(c => c.GetValue<string>().ToLower().Contains("наименование продукции")));

            if (headerRow == null) return new List<Detail>();

            var cols = new Dictionary<string, int>();
            foreach (var cell in headerRow.CellsUsed())
            {
                var text = cell.GetValue<string>().Trim().ToLower();
                cols[text] = cell.Address.ColumnNumber;
            }

            string GetVal(IXLRow row, string colName)
            {
                var key = cols.Keys.FirstOrDefault(k => k.Contains(colName.ToLower()));
                return key != null ? row.Cell(cols[key]).GetValue<string>().Trim() : "";
            }

            var rawList = new List<Detail>();
            var dataRows = worksheet.RowsUsed().Where(r => r.RowNumber() > headerRow.RowNumber());

            foreach (var row in dataRows)
            {
                var name = GetVal(row, "наименование продукции");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var op005 = GetVal(row, "операция 005").ToLower();
                var op010 = GetVal(row, "операция 010").ToLower();

                bool isOp005Plasma = op005.Contains("плазмен");
                bool isOp010Plasma = op010.Contains("плазмен");

                // Отсекаем всё, кроме плазмы на оп 005 или оп 010
                if (!isOp005Plasma && !isOp010Plasma)
                {
                    continue;
                }

                // Назначаем тип строго по номеру операции
                string detailType = isOp005Plasma ? "Плазма" : "Труба";

                var dimensions = GetVal(row, "размеры заготовки");
                int.TryParse(GetVal(row, "количество"), out int qty);

                double length = ComputeCuttingLength(dimensions);

                rawList.Add(new Detail
                {
                    DrawingNumber = GetVal(row, "кд продукции"),
                    Name = name,
                    SteelGrade = GetVal(row, "материал"),
                    Dimensions = dimensions,
                    Quantity = qty > 0 ? qty : 1,
                    DetailType = detailType,
                    Profile = dimensions,
                    Length = length
                });
            }

            var groupedDetails = rawList
                .GroupBy(d => new { d.DrawingNumber, d.Name, d.DetailType })
                .Select(g => new Detail
                {
                    DrawingNumber = g.Key.DrawingNumber,
                    Name = g.Key.Name,
                    SteelGrade = g.First().SteelGrade,
                    Dimensions = g.First().Dimensions,
                    Quantity = g.Sum(x => x.Quantity),
                    DetailType = g.Key.DetailType,
                    Profile = g.First().Profile,
                    Length = g.First().Length
                })
                .ToList();

            return groupedDetails;
        }
        catch (Exception)
        {
            return new List<Detail>();
        }
    }

    /// <summary>
    /// Читает лист "Трудоёмкость" ведомости (.xlsx/.xlsm) и возвращает нормо-время
    /// на операцию "103 Установка плазменной резки" (участок №1), как оно указано
    /// в таблице "Итого по рабочим местам".
    ///
    /// Строка ищется по тексту, а не по фиксированному номеру строки — в реальных
    /// ведомостях он "плавает" (зависит от количества операций в изделии выше по листу).
    /// Признак строки: ячейка одновременно содержит "плазм" и "резки" — этого достаточно,
    /// чтобы не спутать её со строкой "Плазменная" из таблицы "Итого по операциям"
    /// (там слова "резки" нет).
    ///
    /// Значение в файле хранится как десятичное число часов (например 3.508 = 3ч 30мин 29сек),
    /// поэтому пересчёт в TimeSpan — через TimeSpan.FromHours, без доп. преобразований.
    /// </summary>
    public TimeSpan? GetPlasmaCuttingTime(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
            return null;

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var workbook = new XLWorkbook(stream);

            if (!workbook.Worksheets.TryGetWorksheet("Трудоёмкость", out var ws))
                return null;

            foreach (var cell in ws.CellsUsed())
            {
                string text = cell.GetValue<string>().Trim().ToLower();
                if (text.Contains("плазм") && text.Contains("резки"))
                {
                    // Значение — в следующей заполненной ячейке этой же строки правее найденной
                    var row = cell.WorksheetRow();
                    var valueCell = row.Cells(cell.Address.ColumnNumber + 1, ws.LastColumnUsed().ColumnNumber())
                        .FirstOrDefault(c => !c.IsEmpty());

                    if (valueCell != null && valueCell.TryGetValue<double>(out double hours))
                        return TimeSpan.FromHours(hours);
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public List<StatementItem> BuildStatementTree(string orderFolderPath, IProgress<ProgressReport>? progress = null)
    {
        var rootNodes = new List<StatementItem>();
        var orderDirectory = new DirectoryInfo(orderFolderPath);

        var orderNode = new StatementItem
        {
            Title = $"Заказ: {orderDirectory.Name}",
            Icon = "📁",
            IsGroup = true
        };

        // Отсеиваем временные файлы Excel, начинающиеся с ~$
        var excelFiles = Directory.GetFiles(orderFolderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                         f.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase)) &&
                        !Path.GetFileName(f).StartsWith("~$", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int totalFiles = excelFiles.Count;
        int currentFileIndex = 0;

        var productsGroup = excelFiles.GroupBy(filePath => Directory.GetParent(filePath)?.Name ?? "Без категории");

        foreach (var productGoup in productsGroup)
        {
            var productNode = new StatementItem
            {
                Title = $"Продукция: {productGoup.Key}",
                Icon = "📦",
                IsGroup = true
            };

            foreach (var excelPath in productGoup)
            {
                currentFileIndex++;
                progress?.Report(new ProgressReport
                {
                    Current = currentFileIndex,
                    Total = totalFiles,
                    CurrentFileName = Path.GetFileName(excelPath)
                });

                string nodeName = Path.GetFileNameWithoutExtension(excelPath);
                var unitNode = new StatementItem
                {
                    Title = $"Узел: {nodeName}",
                    Icon = "🧩",
                    IsGroup = true
                };

                // Время резки на плазме для этого узла (лист "Трудоёмкость" той же книги)
                var plasmaTime = GetPlasmaCuttingTime(excelPath);
                unitNode.PlasmaCuttingTime = plasmaTime;

                List<Detail> rawDetails = ParsePlasmaDetails(excelPath);
                var sortedDetails = rawDetails
                    .OrderBy(d => d.DetailType)
                    .ThenBy(d => d.SteelGrade)
                    .ThenBy(d => d.Thickness)
                    .ThenBy(d => d.Name)
                    .ToList();

                foreach (var detail in sortedDetails)
                {
                    // Огонек для оп 005 (Плазма), кружок для оп 010 (Труба/Профиль)
                    string itemIcon = detail.DetailType == "Плазма" ? "🔥" : "⭕";

                    string displayTitle = string.IsNullOrWhiteSpace(detail.DrawingNumber)
                        ? detail.Name
                        : $"[{detail.DrawingNumber}]  {detail.Name}";

                    unitNode.Children.Add(new StatementItem
                    {
                        Title = displayTitle,
                        Icon = itemIcon,
                        IsGroup = false,
                        DrawingNumber = detail.DrawingNumber,
                        Thickness = detail.Thickness > 0 ? detail.Thickness.ToString() : "",
                        SteelGrade = detail.SteelGrade,
                        Quantity = detail.Quantity,
                        DetailType = detail.DetailType,
                        Profile = detail.Profile,
                        Length = detail.Length,
                        DetailData = detail,
                        ProductGroupName = productGoup.Key
                    });
                }

                if (unitNode.Children.Count > 0)
                    productNode.Children.Add(unitNode);
            }

            if (productNode.Children.Count > 0)
                orderNode.Children.Add(productNode);
        }

        rootNodes.Add(orderNode);
        return rootNodes;
    }
}
