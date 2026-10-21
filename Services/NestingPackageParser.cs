using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;
using BKMsystem.Models;

namespace BKMsystem.Services;

/// <summary>
/// Собирает комплект раскроя (УП + PDF + Excel) в модель NestingUploadModel.
/// Толщину, размер листа и время резки читает из PDF-отчёта AJAN,
/// детали — из Excel-карты раскроя.
/// </summary>
public static class NestingPackageParser
{
    public static NestingUploadModel BuildPackage(string mpgPath, string? pdfPath, string? xlsxPath)
    {
        var model = new NestingUploadModel
        {
            NestingName = NamingService.EnforceAjanLimit(
                NamingService.SanitizeFileName(Path.GetFileNameWithoutExtension(mpgPath)))
        };

        model.FilePaths[Path.GetFileName(mpgPath)] = mpgPath;
        if (!string.IsNullOrEmpty(pdfPath)) model.FilePaths[Path.GetFileName(pdfPath)] = pdfPath;
        if (!string.IsNullOrEmpty(xlsxPath)) model.FilePaths[Path.GetFileName(xlsxPath)] = xlsxPath;

        if (!string.IsNullOrEmpty(pdfPath) && File.Exists(pdfPath))
        {
            try
            {
                string text = PdfTextExtractor.ExtractText(pdfPath);
                model.SheetSize = PdfTextExtractor.ParseSheetSize(text);
                model.Thickness = (decimal)PdfTextExtractor.ParseThickness(text);
                model.TotalTime = PdfTextExtractor.ParseTotalTime(text);
            }
            catch { /* PDF не распарсился */ }
        }

        if (!string.IsNullOrEmpty(xlsxPath) && File.Exists(xlsxPath))
        {
            model.Parts = ParseNestingExcel(xlsxPath);
        }

        return model;
    }

    /// <summary>
    /// Читает Excel-карту AJAN целиком.
    /// В одном файле несколько карт листов + в конце «Общий список деталей».
    /// Название — колонка C (индекс 2), количество — колонка «Кол-во» (обычно G / индекс 6).
    /// Одинаковые названия суммируются по количеству.
    /// Предпочтительно берём блок «Общий список деталей» (уже итог по всем листам).
    /// </summary>
    public static List<NestingPartModel> ParseNestingExcel(string xlsxPath)
    {
        var summaryRows = new List<(string Name, int Qty)>();
        var allMapRows = new List<(string Name, int Qty)>();

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var stream = new FileStream(xlsxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            bool inSummary = false;
            bool inPartTable = false;
            int nameCol = 2; // C — название детали (данные AJAN)
            int qtyCol = 6;  // G — Кол-во

            do
            {
                inSummary = false;
                inPartTable = false;

                while (reader.Read())
                {
                    string? col0 = Get(reader, 0);
                    string? col1 = Get(reader, 1);

                    // Итоговый блок по всем листам комплекта
                    if (col0?.Contains("Общий список", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        inSummary = true;
                        inPartTable = false;
                        continue;
                    }

                    // Заголовок таблицы деталей (на каждом листе и в общем списке)
                    if (IsPartsHeaderRow(reader))
                    {
                        DetectColumns(reader, ref nameCol, ref qtyCol);
                        inPartTable = true;
                        continue;
                    }

                    if (!inPartTable) continue;

                    // Конец текущей таблицы — не останавливаем файл, ищем следующие карты
                    if (IsTotalsRow(col0, col1) || IsSheetMetaRestart(col0))
                    {
                        inPartTable = false;
                        // «Имя клиента» после Итого начинает новый лист — уже вышли из таблицы
                        continue;
                    }

                    string? nameRaw = Get(reader, nameCol);
                    if (string.IsNullOrWhiteSpace(nameRaw))
                    {
                        // иногда название в соседней колонке из‑за merge
                        nameRaw = Get(reader, 2) ?? Get(reader, 1);
                    }
                    if (string.IsNullOrWhiteSpace(nameRaw)) continue;
                    if (!LooksLikePartName(nameRaw)) continue;

                    int qty = ParseQtyCell(Get(reader, qtyCol));
                    if (qty <= 0) qty = ParseQtyFromName(nameRaw);
                    if (qty <= 0) qty = 1;

                    var row = (nameRaw.Trim(), qty);
                    if (inSummary) summaryRows.Add(row);
                    else allMapRows.Add(row);
                }
            } while (reader.NextResult());
        }
        catch
        {
            return new List<NestingPartModel>();
        }

        // Предпочитаем «Общий список» — там уже правильные суммы по комплекту.
        // Если его нет — суммируем строки всех карт листов по чертежному номеру.
        var source = summaryRows.Count > 0 ? summaryRows : allMapRows;
        return AggregateParts(source);
    }

    private static List<NestingPartModel> AggregateParts(List<(string Name, int Qty)> rows)
    {
        var map = new Dictionary<string, NestingPartModel>(StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var (name, qty) in rows)
        {
            string key = NormalizeDrawingKey(ExtractDrawingNumber(name));
            if (string.IsNullOrWhiteSpace(key)) continue;

            if (map.TryGetValue(key, out var existing))
            {
                existing.Quantity += qty;
            }
            else
            {
                n++;
                map[key] = new NestingPartModel
                {
                    PartNumber = n,
                    PartName = name,
                    Quantity = qty,
                    WeightKg = 0
                };
            }
        }
        return map.Values.OrderBy(p => p.PartNumber).ToList();
    }

    private static bool IsPartsHeaderRow(IExcelDataReader reader)
    {
        for (int c = 0; c < Math.Min(reader.FieldCount, 10); c++)
        {
            string? v = Get(reader, c);
            if (v == null) continue;
            if (v.Contains("Название детал", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.Contains("Номер детал", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.Equals("Лист", StringComparison.OrdinalIgnoreCase) &&
                (Get(reader, 1)?.Contains("Название", StringComparison.OrdinalIgnoreCase) == true))
                return true;
        }
        return false;
    }

    private static void DetectColumns(IExcelDataReader reader, ref int nameCol, ref int qtyCol)
    {
        for (int c = 0; c < Math.Min(reader.FieldCount, 12); c++)
        {
            string? v = Get(reader, c);
            if (v == null) continue;
            if (v.Contains("Название", StringComparison.OrdinalIgnoreCase))
            {
                // Заголовок часто в B, а значения в C (merge) — данные читаем из C
                nameCol = 2;
            }
            if (v.Contains("Кол-во", StringComparison.OrdinalIgnoreCase) ||
                (v.Contains("Количество", StringComparison.OrdinalIgnoreCase) &&
                 !v.Contains("прокол", StringComparison.OrdinalIgnoreCase)))
            {
                qtyCol = c;
            }
        }
    }

    private static bool IsTotalsRow(string? col0, string? col1) =>
        col0?.Contains("Итого", StringComparison.OrdinalIgnoreCase) == true ||
        col1?.Contains("Итого", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSheetMetaRestart(string? col0) =>
        col0?.Contains("Имя клиента", StringComparison.OrdinalIgnoreCase) == true ||
        col0?.Contains("Размер листа", StringComparison.OrdinalIgnoreCase) == true ||
        col0?.StartsWith("TOTAL_", StringComparison.OrdinalIgnoreCase) == true;

    private static bool LooksLikePartName(string name)
    {
        // Типично: "4_ППС10.А.30.00.00.03 - Лист (8шт)" или просто чертёжный номер
        if (name.Contains('_') || name.Contains('.')) return true;
        return Regex.IsMatch(name, @"[A-Za-zА-Яа-я]\d");
    }

    private static int ParseQtyCell(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int q) && q > 0)
            return q;
        // иногда "8.0"
        if (double.TryParse(raw.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double d)
            && d > 0 && Math.Abs(d - Math.Round(d)) < 1e-6)
            return (int)Math.Round(d);
        return 0;
    }

    /// <summary>Количество деталей в имени: "... - Лист (8шт)".</summary>
    public static int ParseQtyFromName(string partName)
    {
        var m = Regex.Match(partName ?? "", @"\((\d+)\s*шт");
        return m.Success && int.TryParse(m.Groups[1].Value, out int q) ? q : 0;
    }

    /// <summary>
    /// Чертежный номер из имени карты/DXF:
    /// "4_ППС10.А.30.00.00.03 - Лист (8шт)" → "ППС10.А.30.00.00.03".
    /// </summary>
    public static string ExtractDrawingNumber(string partName)
    {
        string s = (partName ?? "").Trim();
        if (s.Length == 0) return "";

        // убрать расширение .dxf
        if (s.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
            s = s[..^4];

        int dash = s.IndexOf(" - ", StringComparison.Ordinal);
        string head = dash > 0 ? s[..dash] : s;

        // убираем ведущий префикс толщины "4_" / "10_"
        head = Regex.Replace(head, @"^\d+_", "").Trim();

        // отрезать хвост количества, если вшили в имя без " - "
        head = Regex.Replace(head, @"\s*\(\d+\s*шт\)\s*$", "", RegexOptions.IgnoreCase).Trim();

        var m = Regex.Match(head, @"[A-Za-zА-Яа-яЁё0-9]+(?:\.[A-Za-zА-Яа-яЁё0-9\-]+)+");
        return m.Success ? m.Value.Trim() : head.Trim();
    }

    /// <summary>Единый ключ сравнения: регистр + латинская/кириллическая А/В/С/Е/Н/О/Р/Т/Х.</summary>
    public static string NormalizeDrawingKey(string drawing)
    {
        if (string.IsNullOrWhiteSpace(drawing)) return "";
        var sb = new StringBuilder(drawing.Length);
        foreach (char ch in drawing.Trim())
        {
            char c = char.ToUpperInvariant(ch);
            c = c switch
            {
                'А' => 'A', // Cyrillic A
                'В' => 'B',
                'С' => 'C',
                'Е' => 'E',
                'Н' => 'H',
                'К' => 'K',
                'М' => 'M',
                'О' => 'O',
                'Р' => 'P',
                'Т' => 'T',
                'Х' => 'X',
                _ => c
            };
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string? Get(IExcelDataReader reader, int index)
    {
        if (index < 0 || index >= reader.FieldCount) return null;
        return reader.GetValue(index)?.ToString()?.Trim();
    }
}
