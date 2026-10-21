using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using BKMsystem.Models;

namespace BKMsystem.Services;

public class ParsedAjanSheet
{
    public string MpgFileName { get; set; } = "";
    public string SheetSize { get; set; } = "";
    public double Thickness { get; set; }
    public string Material { get; set; } = "";
    public string TotalTime { get; set; } = "";
    public List<NestingPartModel> Parts { get; set; } = new();
    /// <summary>Путь к PDF, из которого разобрана эта карта — важно, когда пользователь
    /// загружает несколько ОТДЕЛЬНЫХ PDF (по одному на карту), а не один общий многостраничный.</summary>
    public string SourcePdfPath { get; set; } = "";
}

public static class AjanPdfParser
{
    private static readonly Regex MpgRegex = new(
        @"(?:Имя\s*файла|Имяфайла|файла)?\s*[:\s]*([A-Za-z0-9_\-\s]+?\.mpg)", 
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<ParsedAjanSheet> ParsePdf(string pdfPath)
    {
        Debug.WriteLine("\n==================== [AJAN PDF PARSER START] ====================");
        Debug.WriteLine($"[PDF] Открываем файл: {pdfPath}");

        var sheets = new List<ParsedAjanSheet>();
        ParsedAjanSheet? currentSheet = null;

        using var document = PdfDocument.Open(pdfPath);
        Debug.WriteLine($"[PDF] Всего страниц в документе: {document.NumberOfPages}");

        int pageNum = 0;
        foreach (var page in document.GetPages())
        {
            pageNum++;
            // ContentOrderTextExtractor — тот же способ, что и в PdfTextExtractor.ExtractText():
            // построчный вывод, под который заточен ParsePartsTable (одно значение на строку).
            // Сырой page.Text не гарантирует такую структуру.
            string pageText = ContentOrderTextExtractor.GetText(page) ?? "";
            Debug.WriteLine($"\n--- [Страница {pageNum}] Символов текста: {pageText.Length} ---");

            if (string.IsNullOrWhiteSpace(pageText))
            {
                Debug.WriteLine($"[Страница {pageNum}] Пустая страница (нет текста), пропускаем.");
                continue;
            }

            // Выводим фрагмент текста для понимания структуры
            string preview = pageText.Length > 200 
                ? pageText.Substring(0, 200).Replace("\r", " ").Replace("\n", " ") + "..." 
                : pageText.Replace("\r", " ").Replace("\n", " ");
            Debug.WriteLine($"[Страница {pageNum}] Превью шапки: {preview}");

            if (pageText.Contains("Общий список деталей") || pageText.Contains("Общий\nсписок\nдеталей"))
            {
                Debug.WriteLine($"[Страница {pageNum}] Найден маркер 'Общий список деталей' -> завершаем парсинг карт.");
                break;
            }

            bool hasSheetSize = Regex.IsMatch(pageText, @"Размер\s*листа", RegexOptions.IgnoreCase);
            bool hasThickness = pageText.Contains("Толщина", StringComparison.OrdinalIgnoreCase);
            Debug.WriteLine($"[Страница {pageNum}] Проверка шапки: 'Размер листа' = {hasSheetSize}, 'Толщина' = {hasThickness}");

            bool isSheetStart = hasSheetSize && hasThickness;

            if (isSheetStart)
            {
                currentSheet = new ParsedAjanSheet { SourcePdfPath = pdfPath };
                
                var mpgMatch = MpgRegex.Match(pageText);
                if (mpgMatch.Success)
                {
                    currentSheet.MpgFileName = mpgMatch.Groups[1].Value.Trim();
                    Debug.WriteLine($"[Страница {pageNum}] Найдено имя MPG: '{currentSheet.MpgFileName}'");
                }
                else
                {
                    Debug.WriteLine($"[Страница {pageNum}] Имя MPG не распознано в тексте страницы!");
                }

                currentSheet.SheetSize = PdfTextExtractor.ParseSheetSize(pageText);
                currentSheet.Thickness = PdfTextExtractor.ParseThickness(pageText);
                currentSheet.TotalTime = PdfTextExtractor.ParseTotalTime(pageText);
                currentSheet.Material = ExtractMaterial(pageText);

                Debug.WriteLine($"[Карта #{sheets.Count + 1}] Габариты: '{currentSheet.SheetSize}', Толщина: {currentSheet.Thickness} мм, Материал: '{currentSheet.Material}', Время: '{currentSheet.TotalTime}'");

                sheets.Add(currentSheet);
            }

            if (currentSheet == null)
            {
                Debug.WriteLine($"[Страница {pageNum}] Карта еще не инициализирована, пропускаем разбор деталей.");
                continue;
            }

            ParsePartsFromPage(pageText, currentSheet, pageNum);
        }

        // Индексные имена, если имя MPG не было найдено в шапке
        for (int i = 0; i < sheets.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(sheets[i].MpgFileName))
            {
                sheets[i].MpgFileName = $"SHEET{i + 1:D2}.MPG";
                Debug.WriteLine($"[Итог] Для карты #{i + 1} назначено автоимя: {sheets[i].MpgFileName}");
            }
        }

        Debug.WriteLine("\n==================== [ИТОГИ РАЗБОРА PDF] ====================");
        Debug.WriteLine($"Всего сформировано карт: {sheets.Count}");
        for (int i = 0; i < sheets.Count; i++)
        {
            var s = sheets[i];
            Debug.WriteLine($"  Карта #{i + 1}: MPG='{s.MpgFileName}', Лист={s.SheetSize}, S={s.Thickness}мм, Деталей={s.Parts.Count} шт.");
        }
        Debug.WriteLine("=============================================================\n");

        return sheets;
    }

    private static void ParsePartsFromPage(string pageText, ParsedAjanSheet sheet, int pageNum)
    {
        // ВАЖНО: раньше здесь была своя регулярка (RxPartLine), которая брала количество
        // из числа в СКОБКАХ НАЗВАНИЯ ДЕТАЛИ ("Планка (96шт)") — а это общее количество по
        // всему проекту, а не то, сколько реально вырезано на ЭТОМ листе. Из-за этого в базу
        // писалось одно и то же число на каждой карте, вместо настоящего значения из колонки
        // "Кол-во" таблицы. Теперь разбор таблицы идёт через PdfTextExtractor.ParsePartsTable,
        // которая читает именно эту колонку по позиции, а не число из текста названия.
        var parsedRows = PdfTextExtractor.ParsePartsTable(pageText);
        int addedCount = 0;

        foreach (var row in parsedRows)
        {
            if (sheet.Parts.Any(p => p.PartName.Equals(row.PartName, StringComparison.OrdinalIgnoreCase)))
            {
                Debug.WriteLine($"   [-] Пропуск дубликата: '{row.PartName}'");
                continue;
            }

            row.PartNumber = sheet.Parts.Count + 1;
            sheet.Parts.Add(row);
            addedCount++;

            Debug.WriteLine($"   [+] Деталь #{row.PartNumber}: '{row.PartName}' | Кол-во на листе: {row.Quantity} шт | Размеры: {row.Length}x{row.Width}");
        }

        Debug.WriteLine($"[Детали | Стр {pageNum}] Добавлено на странице: {addedCount} шт. Всего в текущей карте: {sheet.Parts.Count} шт.");
    }

    private static string ExtractMaterial(string text)
    {
        var m = Regex.Match(text, @"Материал\s*[:\s]*([0-9A-Za-zА-Яа-я\-\s]+?)(?=(?:Левый|Нижний|Верхний|Отступ|Расстояние|Сила|\r?\n|$))", RegexOptions.IgnoreCase);
        string mat = m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value) ? m.Groups[1].Value.Trim() : "09Г2С";
        return mat;
    }
}