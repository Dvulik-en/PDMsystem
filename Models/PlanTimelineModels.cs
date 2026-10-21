using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using BKMsystem.Services;

namespace BKMsystem.ViewModels;

/// <summary>Одна плитка на дневном таймлайне — задание (Заказ/Изделие/Узел) с долей
/// времени, выделенной на этот конкретный день. Start/End считает
/// PlanScheduleService.ComputeSchedule по порядку плиток дня.</summary>
public class DayTileViewModel : ViewModelBase
{
    public int TaskId { get; set; }
    public string Title { get; set; } = "";
    public bool IsUrgent { get; set; }

    private TimeSpan _duration;
    public TimeSpan Duration
    {
        get => _duration;
        set { _duration = value; OnPropertyChanged(); OnPropertyChanged(nameof(HeightPx)); OnPropertyChanged(nameof(DurationText)); }
    }

    private DateTime _start;
    public DateTime Start
    {
        get => _start;
        set { _start = value; OnPropertyChanged(); OnPropertyChanged(nameof(TopPx)); OnPropertyChanged(nameof(TimeRangeText)); OnPropertyChanged(nameof(IsSecondShift)); OnPropertyChanged(nameof(TileBackground)); OnPropertyChanged(nameof(TileBorderColor)); OnPropertyChanged(nameof(HeightPx)); }
    }

    private DateTime _end;
    public DateTime End
    {
        get => _end;
        set { _end = value; OnPropertyChanged(); OnPropertyChanged(nameof(TimeRangeText)); OnPropertyChanged(nameof(HeightPx)); }
    }

    /// <summary>Сколько ещё часов теоретически можно накрутить на ЭТОТ сегмент, упираясь в
    /// норму трудоёмкости ВСЕГО задания (норма минус уже расписанное на ДРУГИЕ дни) — не
    /// зависит от вместимости конкретного дня, это отдельное, более строгое ограничение:
    /// день может быть почти пустым, но растягивать плитку с фиксированным временем резки
    /// больше, чем реально нужно резать по норме, всё равно нельзя. PositiveInfinity, если
    /// для задания вообще нет нормы (аналог старого поведения — не мешаем).</summary>
    public double MaxTotalNormHours { get; set; } = double.PositiveInfinity;

    public bool IsSecondShift => Start.TimeOfDay >= TimeSpan.FromHours(PlanScheduleService.Shift1EndHour);

    public double TopPx => (Start.TimeOfDay.TotalHours - PlanScheduleService.DayStartHour) * PlanScheduleService.PixelsPerHour;

    /// <summary>Высота плитки на сетке — считаем по РЕАЛЬНОМУ настенному интервалу (End-Start),
    /// а не по чистой рабочей длительности Duration. Если сегмент "перепрыгивает" через обед
    /// (12:00-13:00), Duration меньше на этот час, чем видимый интервал на сетке — раньше высота
    /// считалась от Duration напрямую, из-за чего такая плитка визуально "недотягивала" ровно на
    /// час (не доходила до реальной границы конца, например 17:00), хотя подпись времени была
    /// верной. Теперь геометрия и подпись всегда согласованы.</summary>
    public double HeightPx => Math.Max(22, (End - Start).TotalHours * PlanScheduleService.PixelsPerHour);

    public string DurationText => PlanScheduleService.FormatHours(Duration.TotalHours);
    public string TimeRangeText => $"{Start:HH:mm}–{End:HH:mm}";

    /// <summary>Цвет плитки: срочная дорезка — красный, вторая смена — жёлтый,
    /// обычное плановое задание в первую смену — фирменный голубой.</summary>
    public string TileBackground => IsUrgent ? "#7A2E2E" : (IsSecondShift ? "#5A4300" : "#0D4F63");
    public string TileBorderColor => IsUrgent ? "#FF5252" : (IsSecondShift ? "#FFC107" : "#00E5FF");
}

/// <summary>Один день таймлайна — заголовок с датой и шкалой занятости + стопка плиток.</summary>
public class DayColumnViewModel : ViewModelBase
{
    public DateTime Date { get; set; }

    public string DayNumber => Date.Day.ToString();
    public string WeekdayShort => Date.ToString("ddd", CultureInfo.GetCultureInfo("ru-RU"));
    public bool IsToday => Date.Date == DateTime.Today;

    // Готовые цвета для заголовка — проще, чем городить конвертеры в XAML под bool.
    public string HeaderBackground => IsToday ? "#003547" : "#1E1E1E";
    public string HeaderAccent => IsToday ? "#00E5FF" : "#3A3A3A";

    private double _occupiedHours;
    public double OccupiedHours
    {
        get => _occupiedHours;
        set
        {
            _occupiedHours = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Shift1Fraction));
            OnPropertyChanged(nameof(TotalFraction));
            OnPropertyChanged(nameof(IsOverShift1));
            OnPropertyChanged(nameof(OccupancyText));
            OnPropertyChanged(nameof(GaugeColor));
        }
    }

    /// <summary>Заполнение шкалы относительно ОДНОЙ смены (8ч) — если задания заходят во
    /// вторую смену, шкала просто продолжает расти дальше 100% (см. TotalFraction).</summary>
    public double Shift1Fraction => Math.Min(1.0, OccupiedHours / PlanScheduleService.Shift1CapacityHours);

    /// <summary>Заполнение относительно всего дня с учётом второй смены (13ч потолок).</summary>
    public double TotalFraction => Math.Min(1.0, OccupiedHours / PlanScheduleService.TotalCapacityHours);

    public bool IsOverShift1 => OccupiedHours > PlanScheduleService.Shift1CapacityHours;

    public string OccupancyText =>
        $"{PlanScheduleService.FormatHours(OccupiedHours)} / {PlanScheduleService.FormatHours(PlanScheduleService.Shift1CapacityHours)}"
        + (IsOverShift1 ? $" (+{PlanScheduleService.FormatHours(OccupiedHours - PlanScheduleService.Shift1CapacityHours)} во 2-ю смену)" : "");

    /// <summary>Цвет шкалы: спокойный, пока укладываемся в первую смену; тревожный —
    /// как только заходим во вторую.</summary>
    public string GaugeColor => IsOverShift1 ? "#FF5252" : "#00E5FF";

    private bool _shift2Enabled;
    /// <summary>Разрешена ли вторая смена (17:00–22:00) именно на этот день — управляется
    /// кнопкой на колонке. Пока выключено, день режется ровно по 17:00, а всё, что не влезло,
    /// остаётся недорезанным остатком в правой панели.</summary>
    public bool Shift2Enabled
    {
        get => _shift2Enabled;
        set { _shift2Enabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(Shift2ButtonText)); }
    }

    public string Shift2ButtonText => Shift2Enabled ? "2-я смена включена" : "Вывести 2-ю смену";

    public ObservableCollection<DayTileViewModel> Tiles { get; set; } = new();

    // --- Обратные вызовы в PlanViewModel, проставляются им же при создании колонки в
    //     RefreshTimeline() — так DayColumnView может попросить сохранить новый порядок
    //     плиток или новую длительность, не зная про PlanViewModel напрямую. ---

    /// <summary>Вызывается DayColumnView после drag-переноса плитки внутри дня —
    /// передаёт TaskId всех плиток дня в новом визуальном порядке сверху вниз.</summary>
    public Action<List<int>>? ReorderRequested { get; set; }

    /// <summary>Вызывается DayColumnView после drag-изменения размера плитки (грип снизу) —
    /// (TaskId, новая длительность в этот день).</summary>
    public Action<int, TimeSpan>? ResizeRequested { get; set; }

    /// <summary>Вызывается DayColumnView, когда на колонку перетащили плитку из правой панели
    /// нераспределённых Заказ/Изделие/Узел — (OrderName, ProductName, UnitName; UnitName пустой,
    /// если бросили плитку изделия целиком, не конкретного узла).</summary>
    public Action<string, string, string>? PoolItemDropped { get; set; }

    /// <summary>Вызывается DayColumnView, когда на колонку перетащили "недорезанный остаток"
    /// из правой панели — (TaskId).</summary>
    public Action<int>? LeftoverDropped { get; set; }

    /// <summary>Вызывается DayColumnView при переносе уже стоящей плитки на ДРУГОЙ день —
    /// (TaskId, дата этой колонки, дата колонки-получателя).</summary>
    public Action<int, DateTime, DateTime>? MoveTileRequested { get; set; }

    /// <summary>Вызывается DayColumnView по ПКМ → "Убрать с этого дня" — снимает сегмент
    /// именно этого дня; если у задания больше нигде не осталось сегментов, оно полностью
    /// возвращается в правую панель как незапланированное.</summary>
    public Action<int>? RemoveFromDayRequested { get; set; }

    /// <summary>Вызывается кнопкой "Вывести 2-ю смену" в заголовке колонки.</summary>
    public Action? ToggleShift2Requested { get; set; }
}
