using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace BKMsystem.Services;

/// <summary>
/// Правила именования карт раскроя для AJAN (макс. 8 символов в имени файла).
/// Лист:  04S_SL01  (толщина 4 мм, слот 1)
/// Чечевица: 04C_SL01 (дефис заменяем на "C")
/// </summary>
public static class NamingService
{
    /// <summary>
    /// Строит имя карты по шаблону {TT}{P}_SL{NN}.
    /// TT — толщина в мм с ведущим нулём (04, 06, 10, 12...),
    /// P  — S (гладкий лист) или C (чечевица/рифлёный),
    /// NN — номер слота с ведущим нулём (01..99).
    /// </summary>
    public static string BuildSheetName(double thickness, bool isChequered, int slot)
    {
        if (slot < 1) slot = 1;
        if (slot > 99) slot = 99; // предохранитель от выхода за 8 символов

        string tt = thickness.ToString("00", CultureInfo.InvariantCulture);
        if (tt.Length > 2) tt = tt[..2]; // предохранитель для толщин >= 100

        return $"{tt}{(isChequered ? "C" : "S")}_SL{slot:00}";
    }

    /// <summary>Гарантированное соответствие лимиту AJAN — 8 символов.</summary>
    public static string EnforceAjanLimit(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return name.Length <= 8 ? name : name[..8];
    }

    /// <summary>Безопасное для файловой системы имя (без запрещённых символов).</summary>
    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "SHEET";
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }
}
