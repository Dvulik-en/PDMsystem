using System;
using System.Collections.Generic;
using System.Linq;

namespace BKMsystem.Services;

/// <summary>
/// Распределение общего времени резки узла по нескольким дням.
/// Специально не привязано к "ровно 2 дня" или "поровну пополам" — принимает
/// произвольный список дат (1, 2, 5, сколько угодно) и просто делит время поровну
/// между ними. Хвост от целочисленного деления тиков добавляется в последний день,
/// чтобы сумма всех выделенных долей всегда точно совпадала с total (не терялись
/// и не появлялись лишние секунды из-за округления).
///
/// Ручная (не равномерная) раскладка по дням — задать список (дата, время) вручную
/// и передать напрямую в DatabaseService.SetTaskSegments — этот сервис для неё не нужен,
/// он только для "разложить поровну".
/// </summary>
public static class PlanSplitService
{
    public static List<(DateTime Date, TimeSpan Duration)> EvenSplit(TimeSpan total, List<DateTime> dates)
    {
        if (dates.Count == 0) return new List<(DateTime, TimeSpan)>();
        if (dates.Count == 1) return new List<(DateTime, TimeSpan)> { (dates[0].Date, total) };

        var ordered = dates.Select(d => d.Date).Distinct().OrderBy(d => d).ToList();

        long totalTicks = total.Ticks;
        long perDayTicks = totalTicks / ordered.Count;

        var result = new List<(DateTime, TimeSpan)>();
        long usedTicks = 0;

        for (int i = 0; i < ordered.Count; i++)
        {
            bool isLast = i == ordered.Count - 1;
            long ticks = isLast ? (totalTicks - usedTicks) : perDayTicks;
            usedTicks += ticks;
            result.Add((ordered[i], TimeSpan.FromTicks(ticks)));
        }

        return result;
    }
}
