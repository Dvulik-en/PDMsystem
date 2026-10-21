using System;
using System.Collections.Generic;
using BKMsystem.ViewModels;

namespace BKMsystem.Services;

/// <summary>
/// Модель рабочего дня для визуального таймлайна: первая смена 08:00–17:00 с обедом
/// 12:00–13:00 (итого 8 рабочих часов), вторая смена — продолжение той же даты
/// 17:00–22:00 (5 часов, без обеда). Плитки одного дня выстраиваются друг за другом
/// в порядке SortOrder начиная с 08:00, автоматически перепрыгивая через обед; если
/// очередь доходит до 17:00 — задания продолжают падать во вторую смену (это НЕ авто-перенос
/// на завтра, тот случай — отдельное решение ПДО через SplitTaskAcrossDays/AddNextDayToSplit).
/// </summary>
public static class PlanScheduleService
{
    public const double DayStartHour = 8;
    public const double LunchStartHour = 12;
    public const double LunchEndHour = 13;
    public const double Shift1EndHour = 17;
    public const double Shift2EndHour = 22;

    public const double Shift1CapacityHours = (Shift1EndHour - DayStartHour) - (LunchEndHour - LunchStartHour); // 8
    public const double Shift2CapacityHours = Shift2EndHour - Shift1EndHour; // 5
    public const double TotalCapacityHours = Shift1CapacityHours + Shift2CapacityHours; // 13

    /// <summary>Масштаб таймлайна: пикселей на один рабочий час.</summary>
    public const double PixelsPerHour = 50;

    /// <summary>Полная высота холста дня в пикселях (с 08:00 до 22:00).</summary>
    public const double TimelineHeightPx = (Shift2EndHour - DayStartHour) * PixelsPerHour;

    public const double LunchTopPx = (LunchStartHour - DayStartHour) * PixelsPerHour;
    public const double LunchHeightPx = (LunchEndHour - LunchStartHour) * PixelsPerHour;
    public const double Shift2TopPx = (Shift1EndHour - DayStartHour) * PixelsPerHour;

    /// <summary>Прибавляет к моменту времени рабочую длительность, автоматически
    /// перепрыгивая через обеденный час, если тот попадает внутрь интервала.</summary>
    public static DateTime AddWorkTime(DateTime start, TimeSpan duration)
    {
        var lunchStart = start.Date.AddHours(LunchStartHour);
        var lunchEnd = start.Date.AddHours(LunchEndHour);

        var cursor = (start >= lunchStart && start < lunchEnd) ? lunchEnd : start;
        var end = cursor + duration;

        if (cursor < lunchStart && end > lunchStart)
            end += (lunchEnd - lunchStart);

        return end;
    }

    /// <summary>
    /// Обратная операция к AddWorkTime: сколько РАБОЧИХ часов помещается между start и end
    /// по настенным (wall-clock) часам, если обеденный час (12:00–13:00), попавший в этот
    /// промежуток, не считается рабочим временем. Нужна, чтобы перевести "куда дотянули
    /// мышью на сетке" обратно в реальную длительность резки — без этого плитка, растянутая
    /// мышью через обед, получала бы на час больше времени резки, чем видно на сетке (или
    /// наоборот, визуально не дотягивала бы до отметки 17:00 ровно на час обеда).
    /// </summary>
    public static double WallClockSpanToWorkHours(DateTime start, DateTime end)
    {
        if (end <= start) return 0;

        var lunchStart = start.Date.AddHours(LunchStartHour);
        var lunchEnd = start.Date.AddHours(LunchEndHour);

        var span = end - start;
        var overlapStart = start > lunchStart ? start : lunchStart;
        var overlapEnd = end < lunchEnd ? end : lunchEnd;
        if (overlapEnd > overlapStart) span -= (overlapEnd - overlapStart);

        return Math.Max(0, span.TotalHours);
    }

    /// <summary>Проставляет Start/End каждой плитке по порядку (список уже должен быть
    /// отсортирован по SortOrder), начиная с 08:00 указанной даты.</summary>
    public static void ComputeSchedule(DateTime date, List<DayTileViewModel> orderedTiles)
    {
        var cursor = date.Date.AddHours(DayStartHour);
        foreach (var tile in orderedTiles)
        {
            tile.Start = cursor;
            tile.End = AddWorkTime(cursor, tile.Duration);
            cursor = tile.End;
        }
    }

    public static string FormatHours(double hours)
    {
        int h = (int)hours;
        int m = (int)Math.Round((hours - h) * 60);
        if (m == 60) { h += 1; m = 0; }
        return m == 0 ? $"{h}ч" : $"{h}ч {m:00}м";
    }
}
