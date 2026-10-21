using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BKMsystem.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace BKMsystem.Services;

/// <summary>
/// Извлечение текста из PDF-отчётов AJANCAM через PdfPig (NuGet: UglyToad.PdfPig).
/// ВАЖНО: раньше здесь был самописный regex-парсер PDF-потоков (без внешних пакетов) —
/// он оказался НЕНАДЁЖНЫМ на реальных отчётах: текст там закодирован через встроенный
/// CID-шрифт (шестнадцатеричные строки с таблицей ToUnicode), а не простые (...)-литералы,
/// которые парсер понимал. На проверке реального файла самописный вариант вернул только
/// бинарный мусор — ни одной кириллической буквы. PdfPig корректно разбирает шрифты и
/// кодировки сам, поэтому вместо латания самописного парсера правильнее полностью
/// положиться на проверенную библиотеку для самого извлечения текста.
/// </summary>
public static class PdfTextExtractor
{
    // ------------------------- Извлечение текста -------------------------

    /// <summary>
    /// Возвращает текст документа в порядке чтения (сверху вниз, слева направо),
    /// один "видимый кусок" — на отдельной строке. Именно в таком построчном виде
    /// его ожидают ParseSheetSize/ParseThickness/ParseTotalTime/ParsePartsTable ниже.
    /// </summary>
    public static string ExtractText(string pdfPath)
    {
        var lines = new List<string>();
        using var document = PdfDocument.Open(pdfPath);
        foreach (var page in document.GetPages())
        {
            // ContentOrderTextExtractor сохраняет порядок как в потоке контента страницы —
            // для табличных PDF-отчётов AJAN это даёт устойчивый построчный вывод ячеек.
            string pageText = ContentOrderTextExtractor.GetText(page);
            foreach (var raw in pageText.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length > 0) lines.Add(line);
            }
        }
        return string.Join("\n", lines);
    }

    // ------------------------- Разбор отчёта AJANCAM -------------------------

    /// <summary>Размер листа вида "6000X1500 mm" -> "6000 x 1500".</summary>
    public static string ParseSheetSize(string text)
    {
        var m = Regex.Match(text, @"(?<!\d)(\d{3,5})\s*[XxХх]\s*(\d{3,5})(?<!\d)");
        return m.Success ? $"{m.Groups[1].Value} x {m.Groups[2].Value}" : "";
    }

    /// <summary>Толщина: строка "Толщина (mm)" и следующее за ней число.</summary>
    public static double ParseThickness(string text)
    {
        var m = Regex.Match(text, @"Толщина\s*\(mm\)\s*[\r\n ]*(\d+([.,]\d+)?)");
        if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'),
                NumberStyles.Any, CultureInfo.InvariantCulture, out double t))
            return t;

        // Запасной вариант: "Толщина (mm)" может идти ПОСЛЕ значения в отчёте
        var m2 = Regex.Match(text, @"(\d+([.,]\d+)?)\s*[\r\n ]*Толщина\s*\(mm\)");
        if (m2.Success && double.TryParse(m2.Groups[1].Value.Replace(',', '.'),
                NumberStyles.Any, CultureInfo.InvariantCulture, out double t2))
            return t2;

        return 0;
    }

    /// <summary>Общее время резки: "Общее время резки = 0:23:49".</summary>
    public static string ParseTotalTime(string text)
    {
        var m = Regex.Match(text, @"Общее время резки\s*=\s*(\d+:\d{2}:\d{2})");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>
    /// Чечевица определяется по марке стали/материалу (в отчёте AJAN обычно не пишется) —
    /// передавайте сюда марку стали из ведомости/плана: "рифл", "чечев" -> true.
    /// </summary>
    public static bool IsChequeredSteel(string steelGradeOrMaterial)
    {
        string s = (steelGradeOrMaterial ?? "").ToLowerInvariant();
        return s.Contains("чечев") || s.Contains("рифл") || s.Contains("чеч.");
    }

    // ------------------------- Таблица деталей ОДНОГО отчёта -------------------------

    /// <summary>
    /// Разбирает таблицу деталей ИЗ ОДНОГО отчёта (одна карта = один лист металла).
    /// Ищет по всему тексту страницы (не построчно — PdfPig может группировать текст в строки
    /// по-разному) последовательность: [Номер] [Название (Nшт)] [Размеры ШxВ]
    /// [необязательно Ø-диаметр] [Вес] [Время] [Кол-во] [Периметр], с любым пробелом/переносом
    /// строки между полями.
    /// </summary>
    public static List<NestingPartModel> ParsePartsTable(string text)
    {
        var parts = new List<NestingPartModel>();
        string normalized = (text ?? "").Trim();
        if (normalized.Length == 0) return parts;

        // КРИТИЧНО: обрезаем всё до конца заголовка таблицы. Если этого не сделать, самое первое
        // совпадение regex ниже может "съесть" шапку документа И первую строку таблицы целиком
        // как один мусорный кусок (шапка отбраковывается по длине, но Regex.Matches() уже
        // пропустил весь этот текст, включая настоящую первую деталь, похороненную внутри) —
        // из-за этого именно первая деталь каждой карты стабильно терялась.
        // Якорь — конец "Заметка 1" (последний заголовок столбца), а не "Периметр детали (mm)":
        // иначе цифра "1" из "Заметка 1" сама принимается за номер первой строки, и настоящий
        // номер первой детали "прилипает" к её названию ("1 4_ППС10... - Лист (8шт)").
        var headerAnchor = Regex.Match(normalized, @"Заметка\s*1", RegexOptions.IgnoreCase);
        if (headerAnchor.Success)
        {
            normalized = normalized[(headerAnchor.Index + headerAnchor.Length)..];
        }
        else
        {
            int headerEnd = normalized.IndexOf("Периметр детали", StringComparison.OrdinalIgnoreCase);
            if (headerEnd >= 0)
            {
                int afterHeader = normalized.IndexOf(')', headerEnd);
                normalized = afterHeader >= 0 ? normalized[(afterHeader + 1)..] : normalized[headerEnd..];
            }
        }

        // Гибкая регулярка по ВСЕМУ тексту, а не построчно: \s+ совпадает и с пробелом, и с
        // переносом строки, поэтому неважно, как именно PdfPig разбил текст на строки —
        // построчно по ячейке или целой визуальной строкой таблицы. Порядок полей:
        // [Номер] [Название (Nшт)] [Размеры ШxВ] [необязательно Ø-диаметр] [Вес] [Время] [Кол-во] [Периметр].
        var rowPattern = new Regex(
            @"(?<num>\d{1,3})\s+" +
            @"(?<name>.+?\(\d+\s*шт[^)]*\))\s+" +
            @"(?<dims>\d+(?:[.,]\d+)?\s*[XxХх]\s*\d+(?:[.,]\d+)?)\s*" +
            @"(?:[ØΦ]\d+(?:[.,]\d+)?\s*)?" +
            @"(?<weight>\d+(?:[.,]\d+)?)\s+" +
            @"(?<time>\d+:\d{2}:\d{2})\s+" +
            @"(?<qty>\d+)\s+" +
            @"(?<perimeter>\d+(?:[.,]\d+)?)",
            RegexOptions.Singleline);

        foreach (Match m in rowPattern.Matches(normalized))
        {
            string name = Regex.Replace(m.Groups["name"].Value, @"\s+", " ").Trim();

            // Отсекаем ложные совпадения, где regex случайно захватил кусок шапки/заголовка
            // таблицы вместо настоящей строки детали (такое бывает на самом первом совпадении).
            if (name.Length > 120) continue;
            if (name.Contains("Номер", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("Название", StringComparison.OrdinalIgnoreCase)) continue;
            if (!LooksLikePartName(name)) continue;

            if (!int.TryParse(m.Groups["qty"].Value, out int qty) || qty <= 0) continue;

            // Дубликат (одна и та же деталь могла попасться повторно, если строка растянута
            // на несколько визуальных блоков) — не считаем дважды.
            if (parts.Any(p => p.PartName.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;

            double length = 0, width = 0;
            var dm = Regex.Match(m.Groups["dims"].Value, @"(\d+(?:[.,]\d+)?)\s*[XxХх]\s*(\d+(?:[.,]\d+)?)");
            if (dm.Success)
            {
                double.TryParse(dm.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double d1);
                double.TryParse(dm.Groups[2].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double d2);
                length = Math.Max(d1, d2);
                width = Math.Min(d1, d2);
            }

            double.TryParse(m.Groups["weight"].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double w);

            parts.Add(new NestingPartModel
            {
                PartNumber = parts.Count + 1,
                PartName = name,
                Quantity = qty,
                WeightKg = (decimal)w,
                Length = length,
                Width = width
            });
        }

        return parts;
    }

    private static bool LooksLikePartName(string name)
    {
        // Типично: "4_ППС10.А.30.00.00.03 - Лист (8шт)" или похожий чертёжный номер
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains('_') || name.Contains('.')) return true;
        return Regex.IsMatch(name, @"[A-Za-zА-Яа-я]\d");
    }
}