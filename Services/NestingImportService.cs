using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BKMsystem.Models;
using ExcelDataReader; // Нужен NuGet пакет ExcelDataReader

namespace BKMsystem.Services;

public class NestingImportService
{
    /// <summary>
    /// Извлекает чистый чертежный номер из строки вида "4_BKM.2U21.010.01.00.01 - Стенка (1шт)"
    /// </summary>
    public static string ExtractDrawingNumber(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return string.Empty;

        // Регулярка ищет шаблоны обозначений (буквы, цифры, точки, дефисы после BKM/аббревиатур)
        var match = Regex.Match(rawText, @"[A-Za-zА-Яа-я0-9]+\.[A-Za-zА-Яа-я0-9\.\-]+");
        if (match.Success)
        {
            return match.Value.Trim();
        }

        // Если специфический формат BKM:
        var bkmMatch = Regex.Match(rawText, @"BKM\.[A-Za-z0-9\.\-]+");
        if (bkmMatch.Success)
        {
            return bkmMatch.Value.Trim();
        }

        return rawText.Trim();
    }

    /// <summary>
    /// Импортирует данные из файла раскроя 4.XLSX и связывает с элементами дерева
    /// </summary>
    public void LinkNestingToStatement(string excelFilePath, IEnumerable<StatementItem> treeItems)
    {
        // 1. Индексируем все детали из дерева в плоский словарь по чертежному номеру для быстрого поиска
        var treeDictionary = new Dictionary<string, StatementItem>(StringComparer.OrdinalIgnoreCase);
        IndexTreeItems(treeItems, treeDictionary);

        // 2. Читаем Excel файл раскроя
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        using var stream = File.Open(excelFilePath, FileMode.Open, FileAccess.Read);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        bool isDetailTable = false;

        while (reader.Read())
        {
            var col0 = reader.GetValue(0)?.ToString()?.Trim();
            var rawName = reader.GetValue(2)?.ToString()?.Trim() ?? reader.GetValue(1)?.ToString()?.Trim();

            // Определяем начало таблицы с деталями
            if (col0 == "Номер детали" || rawName == "Название детали")
            {
                isDetailTable = true;
                continue;
            }

            if (!isDetailTable || string.IsNullOrEmpty(rawName))
                continue;

            // Извлекаем чистый чертежный номер
            string drawingNumber = ExtractDrawingNumber(rawName);

            if (string.IsNullOrEmpty(drawingNumber))
                continue;

            // Считываем количество раскроенных деталей (в Excel 4.XLSX это обычно 6 или 7 колонка)
            int nestedQuantity = 0;
            if (reader.GetValue(6) != null)
            {
                int.TryParse(reader.GetValue(6).ToString(), out nestedQuantity);
            }

            // 3. Ищем соответствие в нашем дереве
            if (treeDictionary.TryGetValue(drawingNumber, out var targetItem))
            {
                // Привязываем путь к файлу раскроя
                targetItem.ExcelFilePath = excelFilePath;
                
                // Здесь вы можете обновить количество или статус технологии
                // targetItem.QuantityCut = nestedQuantity;
            }
        }
    }

    /// <summary>
    /// Рекурсивно собирает все листовые детали (IsGroup == false) из дерева в словарь
    /// </summary>
    private void IndexTreeItems(IEnumerable<StatementItem> items, Dictionary<string, StatementItem> dict)
    {
        foreach (var item in items)
        {
            if (!item.IsGroup)
            {
                string cleanNumber = ExtractDrawingNumber(item.DrawingNumber);
                if (string.IsNullOrEmpty(cleanNumber))
                {
                    cleanNumber = ExtractDrawingNumber(item.Title);
                }

                if (!string.IsNullOrEmpty(cleanNumber) && !dict.ContainsKey(cleanNumber))
                {
                    dict[cleanNumber] = item;
                }
            }

            if (item.Children.Count > 0)
            {
                IndexTreeItems(item.Children, dict);
            }
        }
    }
}