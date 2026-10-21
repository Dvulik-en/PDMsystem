using System;

namespace BKMsystem.Services;

/// <summary>
/// Считает, сколько РАБОЧИХ часов реально прошло между двумя моментами времени —
/// то есть календарную разницу минус ночи, обед и выходные, на которые ничего не запланировано.
///
/// Правило рабочего дня (см. обсуждение с технологом): будний день (пн-пт) — всегда рабочий,
/// суббота/воскресенье — рабочие ТОЛЬКО если на эту дату есть хоть одна запись плана
/// (DatabaseService.HasPlanOnDateStatic) — отдельную галочку "рабочий выходной" заводить не
/// пришлось, этот же факт уже виден по тому, поставил ли ПДО туда что-то в календаре.
///
/// Смены — те же константы, что и в PlanScheduleService (единый источник правды о часах
/// работы): 08:00–17:00 с обедом 12:00–13:00 (8ч), плюс 17:00–22:00 (5ч), если для конкретного
/// дня явно включена вторая смена (DatabaseService.GetShift2EnabledStatic — та же кнопка,
/// что и в "Плане").
///
/// Используется для: 1) таймера "в работе: Xч Yм" на карточке УП в "Раскрои" (сколько рабочего
/// времени прошло с UsbExportedAt); 2) в будущем — сверки факта с нормой ведомости в "Метриках"
/// без искажения ночами/выходными.
/// </summary>
public static class WorkTimeCalculator
{
    /// <summary>Сколько рабочих часов прошло между start и end. Если end раньше или равен
    /// start — 0 (например, для ещё не завершённой УП end подставляют как DateTime.Now).</summary>
    public static double WorkingHoursElapsed(DateTime start, DateTime end)
    {
        if (end <= start) return 0;

        double total = 0;
        var cursor = start.Date;

        while (cursor <= end.Date)
        {
            if (IsWorkingDay(cursor))
            {
                var dayStart = cursor.AddHours(PlanScheduleService.DayStartHour);
                var lunchStart = cursor.AddHours(PlanScheduleService.LunchStartHour);
                var lunchEnd = cursor.AddHours(PlanScheduleService.LunchEndHour);
                double dayEndHour = DatabaseService.GetShift2EnabledStatic(cursor)
                    ? PlanScheduleService.Shift2EndHour
                    : PlanScheduleService.Shift1EndHour;
                var dayEnd = cursor.AddHours(dayEndHour);

                // Обрезаем [dayStart, dayEnd] по границам всего интервала [start, end] — на
                // первый и последний день считаем не весь рабочий день, а только пересечение.
                var windowStart = Max(cursor == start.Date ? start : cursor, dayStart);
                var windowEnd = Min(cursor == end.Date ? end : cursor.AddDays(1), dayEnd);

                if (windowEnd > windowStart)
                {
                    double hours = (windowEnd - windowStart).TotalHours;

                    var lunchOverlapStart = Max(windowStart, lunchStart);
                    var lunchOverlapEnd = Min(windowEnd, lunchEnd);
                    if (lunchOverlapEnd > lunchOverlapStart)
                        hours -= (lunchOverlapEnd - lunchOverlapStart).TotalHours;

                    total += Math.Max(0, hours);
                }
            }

            cursor = cursor.AddDays(1);
        }

        return total;
    }

    /// <summary>Будни — всегда рабочие; суббота/воскресенье — только если на эту дату
    /// действительно что-то запланировано (см. пояснение у класса).</summary>
    public static bool IsWorkingDay(DateTime date)
    {
        bool isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        return !isWeekend || DatabaseService.HasPlanOnDateStatic(date);
    }

    /// <summary>Короткий текст для бейджа на карточке УП: "2ч 15м", "5ч", "35м".</summary>
    public static string FormatElapsed(double hours)
    {
        if (hours < (1.0 / 60.0)) return "меньше минуты";
        int h = (int)hours;
        int m = (int)Math.Round((hours - h) * 60);
        if (m == 60) { h += 1; m = 0; }
        if (h == 0) return $"{m}м";
        return m == 0 ? $"{h}ч" : $"{h}ч {m:00}м";
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
