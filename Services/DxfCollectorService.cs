using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BKMsystem.Services
{
    public class DxfCollectorService
    {
        public async Task<(int Copied, List<string> Missing)> CollectDxfFilesAsync(
            IEnumerable<(string DrawingNumber, int Quantity)> itemsToCopy,
            string networkSourcePath,
            string targetDestinationPath)
        {
            var missingFiles = new List<string>();
            int copiedCount = 0;

            return await Task.Run(() =>
            {
                if (!Directory.Exists(networkSourcePath))
                {
                    throw new DirectoryNotFoundException($"Сетевая папка недоступна: {networkSourcePath}");
                }

                if (!Directory.Exists(targetDestinationPath))
                {
                    Directory.CreateDirectory(targetDestinationPath);
                }

                var enumOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                };

                var networkFiles = Directory.GetFiles(networkSourcePath, "*.dxf", enumOptions);

                foreach (var item in itemsToCopy)
                {
                    string drawing = item.DrawingNumber?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(drawing)) continue;

                    string wantKey = NestingPackageParser.NormalizeDrawingKey(
                        NestingPackageParser.ExtractDrawingNumber(drawing));

                    // DXF на диске часто: "4_ППС10.А.01.00.00.02 - Пластина.dxf"
                    var matchedFile = networkFiles.FirstOrDefault(f =>
                    {
                        string fileKey = NestingPackageParser.NormalizeDrawingKey(
                            NestingPackageParser.ExtractDrawingNumber(Path.GetFileNameWithoutExtension(f)));
                        if (!string.IsNullOrEmpty(wantKey) && fileKey == wantKey)
                            return true;

                        // запасной вариант: прямое вхождение номера в имя файла
                        string fileName = Path.GetFileNameWithoutExtension(f);
                        return fileName.Contains(drawing, StringComparison.OrdinalIgnoreCase);
                    });

                    if (matchedFile != null)
                    {
                        // Каноническое имя для AJAN: префикс толщины уже может быть в исходнике —
                        // оставляем исходное имя + количество, без дублирования "(Nшт)" если уже есть.
                        string original = Path.GetFileNameWithoutExtension(matchedFile);
                        original = System.Text.RegularExpressions.Regex.Replace(
                            original, @"\s*\(\d+\s*шт\)\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        string newFileName = $"{original} ({item.Quantity}шт).dxf";
                        string destFilePath = Path.Combine(targetDestinationPath, newFileName);

                        File.Copy(matchedFile, destFilePath, overwrite: true);
                        copiedCount++;
                    }
                    else
                    {
                        missingFiles.Add(drawing);
                    }
                }

                return (copiedCount, missingFiles);
            });
        }
    }
}
