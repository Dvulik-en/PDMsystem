using System;
using System.Collections.Generic;
using System.IO;

namespace BKMsystem.Services
{
    public class NestPartItem
    {
        public string DxfPath { get; set; } = string.Empty;
        public string PartId { get; set; } = string.Empty;
        public int Quantity { get; set; } = 1;
    }

    public class NestGeneratorService
    {
        public string GetAjanStockHoldingDirectory()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string targetDir = Path.Combine(localAppData, @"AJANCAM V8.0\SheetStockHolding");

            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            return targetDir;
        }
    }
}