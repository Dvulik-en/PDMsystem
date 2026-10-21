using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using BKMsystem.Models;
using BKMsystem.Services;

namespace BKMsystem.ViewModels;

/// <summary>
/// Вкладка "Метрики" — только для директора (и админа), только чтение. Две части, как просил
/// пользователь по референс-изображениям:
///   1) Месячная сетка квадратиков (зелёный/красный день, наведение — статус + причина от мастера).
///      Полностью готово — построено на уже существующих GetDayCutStatus/GetDelayReasonsForDate.
///   2) Сводная статистика по срокам (когда создан план / создана УП / взято в работу / выполнено).
///      Пока ЧАСТИЧНО: "план создан" ещё нет в схеме (PlannedTasks без CreatedAt), поэтому ниже
///      считаем только то, что уже фиксируется — от создания УП до взятия в работу и до выполнения
///      (cutting_sheets.CreatedAt/UsbExportedAt/CompletedAt). См. пояснение в StatsNote.
/// </summary>
public class MetricsViewModel : ViewModelBase
{
    private readonly NestingRepository _repo;

    public ObservableCollection<MetricsDaySquareModel> DaySquares { get; } = new();
    public ObservableCollection<MetricsStatCardModel> StatCards { get; } = new();
    public ObservableCollection<RingGaugeModel> RingGauges { get; } = new();
    public ObservableCollection<MetricsUnitTimeRow> UnitTimeRows { get; } = new();

    private bool _hasUnitTimeRows;
    /// <summary>Есть ли вообще что показывать в блоке "норма vs факт" — за месяц может не быть
    /// ни одной полностью выполненной УП, тогда честнее скрыть блок с пояснением, а не рисовать
    /// пустой список.</summary>
    public bool HasUnitTimeRows
    {
        get => _hasUnitTimeRows;
        set { _hasUnitTimeRows = value; OnPropertyChanged(); }
    }

    private bool _showMonthEndWarning;
    public bool ShowMonthEndWarning
    {
        get => _showMonthEndWarning;
        set { _showMonthEndWarning = value; OnPropertyChanged(); }
    }

    private string _monthEndWarningText = "";
    public string MonthEndWarningText
    {
        get => _monthEndWarningText;
        set { _monthEndWarningText = value; OnPropertyChanged(); }
    }

    private DateTime _monthCursor = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    public DateTime MonthCursor
    {
        get => _monthCursor;
        set { _monthCursor = value; OnPropertyChanged(); OnPropertyChanged(nameof(MonthTitle)); LoadMonth(); }
    }

    public string MonthTitle => MonthCursor.ToString("MMMM yyyy", new CultureInfo("ru-RU"));

    /// <summary>Честное пояснение под статистикой — что именно посчитано, а чего пока нет
    /// в базе (см. класс-комментарий выше).</summary>
    public string StatsNote { get; private set; } =
        "Время от постановки плана пока не считается — в базе ещё нет отметки \"план создан\" (PlannedTasks.CreatedAt). " +
        "Ниже — только то, что уже фиксируется автоматически: от загрузки УП технологом до взятия в работу оператором, и до отметки \"Выполнено\".";

    public void MonthBack() => MonthCursor = MonthCursor.AddMonths(-1);
    public void MonthForward() => MonthCursor = MonthCursor.AddMonths(1);
    public void JumpToCurrentMonth() => MonthCursor = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

    public MetricsViewModel()
    {
        _repo = new NestingRepository();
        _repo.EnsureTables();
        LoadMonth();

        DatabaseService.OnDatabaseUpdated += (tableName) =>
        {
            if (tableName.Equals("plannedtasks", StringComparison.OrdinalIgnoreCase) ||
                tableName.Equals("cutting_sheets", StringComparison.OrdinalIgnoreCase) ||
                tableName.Equals("details", StringComparison.OrdinalIgnoreCase) ||
                tableName.Equals("cuttingdelayreasons", StringComparison.OrdinalIgnoreCase))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(LoadMonth);
            }
        };
    }

    private void LoadMonth()
    {
        LoadDaySquares();
        LoadStatCards();
        LoadRingGauges();
        LoadMonthEndWarning();
        LoadUnitTimeComparison();
    }

    /// <summary>Норма (ведомость, UnitLaborNorms) vs факт (реальное рабочее время резки) по
    /// каждому узлу — то, ради чего технолог просил вкладку "Метрики": не бейдж времени
    /// оператору, а сводка для директора, по каким узлам расходятся норма и факт и на сколько.</summary>
    private void LoadUnitTimeComparison()
    {
        UnitTimeRows.Clear();

        var from = MonthCursor;
        var to = MonthCursor.AddMonths(1).AddDays(-1);
        var rows = _repo.GetUnitTimeComparison(from, to);

        foreach (var r in rows)
        {
            UnitTimeRows.Add(new MetricsUnitTimeRow
            {
                OrderName = r.OrderName,
                ProductName = r.ProductName,
                UnitName = r.UnitName,
                NormHours = r.NormHours,
                FactHours = r.FactHours,
                SheetsCounted = r.SheetsCounted
            });
        }

        HasUnitTimeRows = UnitTimeRows.Count > 0;
    }

    /// <summary>Сетка недель: первая клетка ставится на понедельник той недели, где 1 число
    /// месяца — так квадратики выравниваются в привычную календарную сетку, как на референсе.</summary>
    private void LoadDaySquares()
    {
        DaySquares.Clear();

        var first = MonthCursor;
        int daysInMonth = DateTime.DaysInMonth(first.Year, first.Month);

        // Понедельник = 0 ... воскресенье = 6
        int leadingBlanks = ((int)first.DayOfWeek + 6) % 7;

        var statuses = _repo.GetDayCutStatusesForMonth(first.Year, first.Month);

        for (int i = 0; i < leadingBlanks; i++)
        {
            DaySquares.Add(new MetricsDaySquareModel
            {
                Date = first.AddDays(-(leadingBlanks - i)),
                IsCurrentMonth = false
            });
        }

        for (int d = 1; d <= daysInMonth; d++)
        {
            var date = new DateTime(first.Year, first.Month, d);
            var square = new MetricsDaySquareModel { Date = date, IsCurrentMonth = true };
            square.CutStatus = statuses.TryGetValue(date, out var st) ? st : DayCutStatus.NotApplicable;

            if (square.CutStatus == DayCutStatus.NotDoneNoReason || square.CutStatus == DayCutStatus.NotDoneWithReason)
            {
                var reasons = _repo.GetDelayReasonsForDate(date).Values
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Distinct()
                    .ToList();
                if (reasons.Count > 0)
                    square.DelayReasonsJoined = string.Join("; ", reasons);

                // Директору важно видеть не просто "не успели", а КАКИЕ именно узлы — чтобы
                // учитывать это при планировании следующих заказов.
                square.UnfinishedPartsJoined = BuildUnfinishedPartsText(date);
            }

            DaySquares.Add(square);
        }
    }

    /// <summary>Список невырезанных узлов на дату, сгруппированный по заказам — для подсказки
    /// квадратика. Читает уже проверенную GetDashboardCards (та же логика, что строит карточки
    /// в "Панели"/"Мастере"), а не считает сама заново.</summary>
    private string? BuildUnfinishedPartsText(DateTime date)
    {
        var cards = _repo.GetDashboardCards(date);
        var groups = new List<string>();

        foreach (var card in cards)
        {
            var unfinished = card.Parts
                .Where(p => p.SheetsTotal == 0 || p.SheetsDone < p.SheetsTotal)
                .Select(p => p.Cipher)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct()
                .ToList();

            if (unfinished.Count > 0)
                groups.Add($"{card.OrderName}: {string.Join(", ", unfinished)}");
        }

        return groups.Count > 0 ? string.Join("; ", groups) : null;
    }

    private void LoadStatCards()
    {
        StatCards.Clear();

        var from = MonthCursor;
        var to = MonthCursor.AddMonths(1).AddDays(-1);
        var rows = _repo.GetSheetTimingStats(from, to);

        int total = rows.Count;
        int completed = rows.Count(r => r.CompletedAt.HasValue);

        var toWork = rows.Where(r => r.HoursToTakenInWork.HasValue).Select(r => r.HoursToTakenInWork!.Value).ToList();
        var toDone = rows.Where(r => r.HoursToCompleted.HasValue).Select(r => r.HoursToCompleted!.Value).ToList();

        StatCards.Add(new MetricsStatCardModel
        {
            Title = "Загружено УП за месяц",
            Value = total.ToString(),
            Subtitle = $"выполнено {completed} из {total}"
        });

        StatCards.Add(new MetricsStatCardModel
        {
            Title = "Среднее время до взятия в работу",
            Value = toWork.Count > 0 ? FormatHours(toWork.Average()) : "—",
            Subtitle = "от загрузки УП технологом до выгрузки на флешку оператором"
        });

        StatCards.Add(new MetricsStatCardModel
        {
            Title = "Среднее время резки",
            Value = toDone.Count > 0 ? FormatHours(toDone.Average()) : "—",
            Subtitle = "от взятия в работу до отметки \"Выполнено\""
        });
    }

    private static string FormatHours(double hours)
    {
        if (hours < 1) return $"{Math.Round(hours * 60)} мин";
        if (hours < 24) return $"{hours:0.#} ч";
        return $"{hours / 24:0.#} дн";
    }

    /// <summary>Часов, отведённых на "быстрое" взятие УП в работу — если оператор забрал лист
    /// на резку дольше этого срока, момент не считается быстрым (для кольца "Взято в работу
    /// вовремя"). Условная величина, ту же логику технолог легко может попросить изменить.</summary>
    private const double FastTakeInWorkHours = 24.0;

    /// <summary>Три кольца-показателя (аналог "Заряд/Состояние/Давление" на референсе
    /// пользователя) — считаются по уже загруженным DaySquares/StatCards дням месяца, поэтому
    /// вызывается ПОСЛЕ них. Только дни ≤ сегодня учитываются — будущие дни ещё не имеют статуса.</summary>
    private void LoadRingGauges()
    {
        RingGauges.Clear();

        var pastDaysWithTasks = DaySquares
            .Where(s => s.IsCurrentMonth && s.CutStatus != DayCutStatus.NotApplicable && s.CutStatus != DayCutStatus.NoTasks)
            .ToList();

        // "Нечего сравнивать" — это ОТСУТСТВИЕ данных, а не "всё идеально": раньше пустой месяц
        // тихо превращался в 100%, что вводило в заблуждение. Теперь честно показываем "—".
        bool hasOnTimeData = pastDaysWithTasks.Count > 0;
        double onTimePercent = hasOnTimeData
            ? 100.0 * pastDaysWithTasks.Count(s => s.CutStatus == DayCutStatus.Done) / pastDaysWithTasks.Count
            : 0;

        var notDoneDays = pastDaysWithTasks.Where(s => s.CutStatus != DayCutStatus.Done).ToList();
        bool hasReasonsData = notDoneDays.Count > 0;
        double reasonsPercent = hasReasonsData
            ? 100.0 * notDoneDays.Count(s => s.CutStatus == DayCutStatus.NotDoneWithReason) / notDoneDays.Count
            : 0;

        var from = MonthCursor;
        var to = MonthCursor.AddMonths(1).AddDays(-1);
        var timing = _repo.GetSheetTimingStats(from, to).Where(r => r.HoursToTakenInWork.HasValue).ToList();
        bool hasTimingData = timing.Count > 0;
        double takenInWorkPercent = hasTimingData
            ? 100.0 * timing.Count(r => r.HoursToTakenInWork!.Value <= FastTakeInWorkHours) / timing.Count
            : 0;

        RingGauges.Add(new RingGaugeModel { Label = "Выполнено вовремя", Percent = onTimePercent, HasData = hasOnTimeData });
        RingGauges.Add(new RingGaugeModel { Label = "Причины оформлены", Percent = reasonsPercent, HasData = hasReasonsData });
        RingGauges.Add(new RingGaugeModel { Label = $"Взято в работу < {FastTakeInWorkHours:0} ч", Percent = takenInWorkPercent, HasData = hasTimingData });
    }

    /// <summary>Предупреждение "плазма не успевает к концу месяца" — грубая эвристика: если до
    /// конца ТЕКУЩЕГО месяца осталось немного дней, а доля выполненных вовремя дней уже низкая,
    /// это тревожный сигнал ухудшить нечем не успеть. Для прошлых/будущих месяцев не показывается —
    /// там "успеть до конца месяца" уже не актуальный вопрос.</summary>
    private void LoadMonthEndWarning()
    {
        ShowMonthEndWarning = false;
        MonthEndWarningText = "";

        bool isCurrentMonth = MonthCursor.Year == DateTime.Today.Year && MonthCursor.Month == DateTime.Today.Month;
        if (!isCurrentMonth) return;

        var onTimeRing = RingGauges.FirstOrDefault(r => r.Label == "Выполнено вовремя");
        if (onTimeRing == null || !onTimeRing.HasData) return; // нечего сравнивать — предупреждать не о чем

        int daysInMonth = DateTime.DaysInMonth(MonthCursor.Year, MonthCursor.Month);
        int daysRemaining = daysInMonth - DateTime.Today.Day;

        if (daysRemaining <= 7 && onTimeRing.Percent < 80)
        {
            ShowMonthEndWarning = true;
            MonthEndWarningText =
                $"До конца месяца {daysRemaining} дн., а вовремя выполнено только {Math.Round(onTimeRing.Percent)}% дней — " +
                "похоже, план может не успеть закрыться, если ничего не скорректировать.";
        }
    }
}
