using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BKMsystem.Models;

namespace BKMsystem.Services;

public class DxfStorageService
{
    private readonly string _dxfFolderPath = @"Y:\"; 

    /// <summary>
    /// Точечный поиск DXF на основе распарсенных данных детали
    /// </summary>
    public string? FindDxfPath(Detail detail, string productGroupName)
    {
        if (detail == null || string.IsNullOrWhiteSpace(detail.Name) || !Directory.Exists(_dxfFolderPath))
            return null;

        // Безопасное имя файла (чертежа)
        string safeDrawingName = string.Concat(detail.Name.Split(Path.GetInvalidFileNameChars()));
        
        // Форматируем толщину для поиска (например, "10", "10.0", "10,0")
        string thickStr = detail.Thickness.ToString("G").Replace(',', '.');

        try
        {
            // --- ШАГ 1: Точечный заход в целевую Базу ---
            string cleanGroupName = productGroupName.Replace("Продукция:", "").Trim();
            
            var baseDir = Directory.EnumerateDirectories(_dxfFolderPath)
                                   .FirstOrDefault(d => Path.GetFileName(d).Contains(cleanGroupName, StringComparison.OrdinalIgnoreCase));

            // Если нашли базу (например "База котлов"), проверяем, есть ли в ней подпапка "DXF"
            string searchStartPath = _dxfFolderPath;
            if (baseDir != null)
            {
                string dxfSubDir = Path.Combine(baseDir, "DXF");
                searchStartPath = Directory.Exists(dxfSubDir) ? dxfSubDir : baseDir;
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };

            // --- ШАГ 2: Фильтруем папки СТРОГО и по толщине, И по марке стали ---
            var targetLeafDirectories = Directory.EnumerateDirectories(searchStartPath, "*", options)
                .Where(dir => 
                {
                    string folderName = Path.GetFileName(dir);
                    return IsDirectoryMatching(folderName, thickStr, detail.SteelGrade);
                });

            // --- ШАГ 3: Сканируем только найденные целевые папки ---
            foreach (var leafDir in targetLeafDirectories)
            {
                var file = Directory.EnumerateFiles(leafDir, $"*{safeDrawingName}*.dxf")
                                    .FirstOrDefault();
                if (file != null) return file;
            }

            // Резервный поиск в пределах выбранной Базы/Диска (если папка называлась нестандартно)
            return Directory.EnumerateFiles(searchStartPath, $"*{safeDrawingName}*.dxf", options)
                            .FirstOrDefault();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Ошибка при точечном поиске DXF: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Проверяет, что имя папки (например "Лист гк 10х2000х6000мм,09Г2С") 
    /// одновременно содержит правильную толщину И марку стали.
    /// </summary>
    private bool IsDirectoryMatching(string folderName, string thickness, string? steelGrade)
    {
        // 1. Проверка марки стали (если заполнена)
        if (!string.IsNullOrWhiteSpace(steelGrade))
        {
            // Очищаем марку и имя папки от пробелов и дефисов для гибкого сравнения
            string cleanSteel = steelGrade.Replace(" ", "").Replace("-", "");
            string cleanFolder = folderName.Replace(" ", "").Replace("-", "");

            if (!cleanFolder.Contains(cleanSteel, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // 2. Строгая проверка толщины (чтобы 10мм не цепляло 100мм, 16мм или 1мм)
        // Ищет число толщины, за которым следом идет "мм", "mm", "х" или конец слова
        string pattern = $@"(?<=^|[^\d]){Regex.Escape(thickness)}(?=мм|mm|\times|x|х|[^\d]|$)";
        
        return Regex.IsMatch(folderName, pattern, RegexOptions.IgnoreCase);
    }
}