using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BKMsystem.Models;
using BKMsystem.Services;

namespace BKMsystem.ViewModels;

public class PlanViewModel : ViewModelBase
{
    private readonly DatabaseService _dbService;

    /// <summary>Меньше этого в день не кладём — если свободного места мельче, считаем, что
    /// места нет вообще (не заводим еле заметный обрубок-плитку на пару минут). Строго —
    /// либо влезло видимым куском, либо не влезло совсем, никакой "почти незаметной" середины.</summary>
    private const double MinPlaceableHours = 0.1; // 6 минут

    /// <summary>Условная накладная часть срочной дорезки, которая не является самой резкой —
    /// принести металл к столу, убрать со стола готовые/бракованные полуфабрикаты, разметить и
    /// т.п. По расчётной скорости реза считается только сам рез, поэтому эту часть всегда
    /// прибавляем сверху (см. RecomputeUrgentEstimate) — иначе оценка занижена, а до этого без
    /// данных резки в базе АДЖАН оценка вообще была 0 и добавить дорезку было нечем.</summary>
    private const double UrgentOverheadHours = 15.0 / 60.0; // 15 минут

    public ObservableCollection<string> Orders { get; set; } = new();
    public ObservableCollection<string> Products { get; set; } = new();
    public ObservableCollection<string> Units { get; set; } = new();

    // Это и есть наше дерево, которое отобразит TreeView
    public ObservableCollection<PlanTreeNode> PlanTree { get; set; } = new();

    // --- Визуальный таймлайн (плитки по дням, как в "onetask") ---
    /// <summary>Сколько дней показываем на таймлайне одновременно (окно можно листать).</summary>
    private const int TimelineDayCount = 10;
    /// <summary>На сколько дней сдвигается окно по кнопкам "Раньше"/"Позже".</summary>
    private const int TimelineShiftStep = 5;
    public ObservableCollection<DayColumnViewModel> TimelineDays { get; set; } = new();

    private DateTime _timelineStartDate = DateTime.Today;
    /// <summary>Первый день видимого окна таймлайна. НЕ привязан к "сегодня" — окно можно
    /// листать в прошлое (напр. если ПДО не успел перенести задание вовремя, оно осталось
    /// висеть на прошедшей дате и должно быть доступно для правки, а не потеряно).</summary>
    public DateTime TimelineStartDate
    {
        get => _timelineStartDate;
        set { _timelineStartDate = value.Date; OnPropertyChanged(); OnPropertyChanged(nameof(TimelineRangeText)); RefreshTimeline(); }
    }

    public string TimelineRangeText =>
        $"{TimelineStartDate:dd.MM.yyyy} – {TimelineStartDate.AddDays(TimelineDayCount - 1):dd.MM.yyyy}";

    public void TimelineShiftBack() => TimelineStartDate = TimelineStartDate.AddDays(-TimelineShiftStep);
    public void TimelineShiftForward() => TimelineStartDate = TimelineStartDate.AddDays(TimelineShiftStep);
    public void TimelineJumpToToday() => TimelineStartDate = DateTime.Today;

    // --- Правая панель: ещё не запланированные Заказ/Изделие/Узел, плитками, с поиском ---
    public ObservableCollection<PlanPoolOrderGroup> PoolOrders { get; set; } = new();

    /// <summary>"Недорезанные остатки" — задания, у которых по норме осталось не пристроенное
    /// ни на один день время (сжали плитку, упёрлись в 17:00, при переносе не влезло целиком).
    /// Отдельный раздел правой панели, перетаскивается на день так же, как обычная плитка.</summary>
    public ObservableCollection<PlanPoolLeftoverItem> PoolLeftovers { get; set; } = new();

    private bool _hasLeftovers;
    /// <summary>Есть ли хоть один недорезанный остаток — чтобы прятать заголовок раздела,
    /// когда показывать нечего.</summary>
    public bool HasLeftovers
    {
        get => _hasLeftovers;
        private set { _hasLeftovers = value; OnPropertyChanged(); }
    }

    private string _poolSearchText = "";
    public string PoolSearchText
    {
        get => _poolSearchText;
        set { _poolSearchText = value; OnPropertyChanged(); RebuildPool(); }
    }

    private string? _selectedOrder;
    public string? SelectedOrder
    {
        get => _selectedOrder;
        set { _selectedOrder = value; OnPropertyChanged(); LoadProductsForOrder(); UpdateTimeEstimate(); }
    }

    private string? _selectedProduct;
    public string? SelectedProduct
    {
        get => _selectedProduct;
        set { _selectedProduct = value; OnPropertyChanged(); LoadUnitsForProduct(); UpdateTimeEstimate(); }
    }

    private string? _selectedUnit;
    public string? SelectedUnit
    {
        get => _selectedUnit;
        set { _selectedUnit = value; OnPropertyChanged(); UpdateTimeEstimate(); }
    }

    // --- Шкала-оценка времени резки для текущего выбора (Заказ/Изделие/Узел) ---

    private string _selectedUnitTimeText = "";
    /// <summary>Текст под шкалой: "Оценка резки: 3ч 30м" или пояснение, почему данных нет.</summary>
    public string SelectedUnitTimeText
    {
        get => _selectedUnitTimeText;
        set { _selectedUnitTimeText = value; OnPropertyChanged(); }
    }

    private double _selectedUnitTimeFraction;
    /// <summary>0..1 — заполнение шкалы относительно одной смены (8ч). Чисто наглядный
    /// ориентир, не ограничение: если резки на 14ч, шкала просто дойдёт до 100% и упрётся.</summary>
    public double SelectedUnitTimeFraction
    {
        get => _selectedUnitTimeFraction;
        set { _selectedUnitTimeFraction = value; OnPropertyChanged(); }
    }

    private void UpdateTimeEstimate()
    {
        if (string.IsNullOrEmpty(SelectedOrder) || string.IsNullOrEmpty(SelectedProduct) || string.IsNullOrEmpty(SelectedUnit))
        {
            SelectedUnitTimeText = "";
            SelectedUnitTimeFraction = 0;
            return;
        }

        var time = _dbService.GetPlasmaCuttingTime(SelectedOrder, SelectedProduct, SelectedUnit);
        if (!time.HasValue)
        {
            SelectedUnitTimeText = "Нет данных о трудоёмкости для этого узла (нет листа \"Трудоёмкость\" в ведомости)";
            SelectedUnitTimeFraction = 0;
            return;
        }

        SelectedUnitTimeText = $"Оценка резки на плазме: {(int)time.Value.TotalHours}ч {time.Value.Minutes}м";
        SelectedUnitTimeFraction = Math.Min(1.0, time.Value.TotalHours / 8.0);
    }

    private DateTimeOffset _plannedDate = DateTimeOffset.Now;
    public DateTimeOffset PlannedDate
    {
        get => _plannedDate;
        set { _plannedDate = value; OnPropertyChanged(); }
    }

    private int _selectedSheetIndex = 0;
    public int SelectedSheetIndex
    {
        get => _selectedSheetIndex;
        set { _selectedSheetIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsCustomSheet)); }
    }

    public bool IsCustomSheet => SelectedSheetIndex == 2;
    public double CustomWidth { get; set; } = 2000;
    public double CustomLength { get; set; } = 6000;

    private bool _isUrgent;
    public bool IsUrgent
    {
        get => _isUrgent;
        set
        {
            _isUrgent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowUrgentForm));
            if (value) { UrgentValidationError = ""; UrgentReasonMissing = false; }
        }
    }
    private string _reworkReason = "";
    /// <summary>Обязательное поле — без причины дорезку не поставить (см. AddUrgentRecut).
    /// Полноценное свойство (не auto-property), чтобы: 1) сброс после успешной постановки был
    /// виден в самом текстбоксе, 2) снимать подсветку "поле обязательно" как только начали
    /// писать, не дожидаясь повторного нажатия кнопки.</summary>
    public string ReworkReason
    {
        get => _reworkReason;
        set
        {
            _reworkReason = value;
            OnPropertyChanged();
            if (UrgentReasonMissing && !string.IsNullOrWhiteSpace(value)) UrgentReasonMissing = false;
        }
    }

    private bool _urgentReasonMissing;
    /// <summary>Подсветка поля "Причина дорезки" красным — включается только попыткой поставить
    /// дорезку с пустой причиной (AddUrgentRecut), а не сразу при открытии формы.</summary>
    public bool UrgentReasonMissing
    {
        get => _urgentReasonMissing;
        private set { _urgentReasonMissing = value; OnPropertyChanged(); }
    }

    private string _urgentValidationError = "";
    /// <summary>Причина, по которой "Добавить дорезку в план" ничего не сделала — раньше при
    /// невыполненном условии (например время оценки = 0) метод просто молча выходил, и техник
    /// не понимал, что вообще произошло. Теперь любой отказ объясняется текстом здесь.</summary>
    public string UrgentValidationError
    {
        get => _urgentValidationError;
        private set { _urgentValidationError = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUrgentValidationError)); }
    }
    public bool HasUrgentValidationError => !string.IsNullOrEmpty(UrgentValidationError);

    /// <summary>Форму срочной дорезки показываем только когда галочка включена — до этого
    /// момента поле причины/поиска детали технологу не нужны и только заняли бы место.</summary>
    public bool ShowUrgentForm => IsUrgent;

    // ------------------------------------------------------------------
    // Срочная дорезка: найти деталь по названию/чертежу (уже в ведомости, Details), указать
    // количество — время резки считается по заводской базе АДЖАН (CncCuttingNorms) через
    // марку стали + толщину этой детали, а суммарная длина реза берётся из самой ведомости
    // (то же поле Length, что и для генерации программ раскроя). Результат создаётся как
    // обычное "недорезанное" задание без единого сегмента — оно сразу попадает в правую
    // панель ("⚠ Недорезано") и дальше перетаскивается на день как любой другой остаток —
    // никакой отдельной плитки/логики дропа заводить не пришлось.
    // ------------------------------------------------------------------

    private string _urgentSearchText = "";
    public string UrgentSearchText
    {
        get => _urgentSearchText;
        set { _urgentSearchText = value; OnPropertyChanged(); SearchUrgentParts(); }
    }

    public ObservableCollection<DetailSearchResult> UrgentSearchResults { get; set; } = new();

    private DetailSearchResult? _selectedUrgentPart;
    public DetailSearchResult? SelectedUrgentPart
    {
        get => _selectedUrgentPart;
        set
        {
            _selectedUrgentPart = value;
            // Подставляем в поле поиска именно то, что выбрали (а не то, что было напечатано
            // для поиска) — и убираем сам список результатов, он больше не нужен, пока выбор
            // не сброшен явно. Пишем в приватное поле напрямую (не через сеттер UrgentSearchText),
            // чтобы не запускать повторный SearchUrgentParts().
            _urgentSearchText = value?.Title ?? "";
            UrgentSearchResults.Clear();
            // Новая деталь — новая оценка: снимаем отметку "время введено вручную", чтобы для
            // неё снова подставилось расчётное предложение, а не время, оставшееся от прошлой.
            _urgentManualHoursOverridden = false;
            OnPropertyChanged(nameof(UrgentSearchText));
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedUrgentPart));
            OnPropertyChanged(nameof(SelectedUrgentPartInfoText));
            LoadUrgentAmpOptions();
        }
    }

    public bool HasSelectedUrgentPart => SelectedUrgentPart != null;

    /// <summary>Марка стали в ведомости (Details.SteelGrade) хранится составной строкой вида
    /// "Лист г/к 4мм,09Г2С", а не чистым кодом марки — поэтому для поиска по базе АДЖАН
    /// (CncCuttingNorms, ключ — чистый код марки) её сначала нормализуем. Кэшируем результат
    /// здесь же, чтобы не искать по базе повторно ради одного только текста подсказки.</summary>
    private string? _matchedUrgentSteelGrade;

    public string SelectedUrgentPartInfoText
    {
        get
        {
            if (SelectedUrgentPart == null) return "";
            string gradeText = _matchedUrgentSteelGrade != null
                ? $"{SelectedUrgentPart.SteelGrade} → распознано как {_matchedUrgentSteelGrade}"
                : $"{SelectedUrgentPart.SteelGrade} (марка не распознана в базе АДЖАН — впишите время вручную)";
            return $"{gradeText}, {SelectedUrgentPart.Thickness:0.#} мм, длина реза 1 шт — {SelectedUrgentPart.LengthMm:0} мм";
        }
    }

    public ObservableCollection<CncNormOption> UrgentAmpOptions { get; set; } = new();

    private bool _hasUrgentAmpOptions;
    /// <summary>ObservableCollection.Count не биндится напрямую в IsVisible (нужен bool) —
    /// держим отдельный флаг, обновляемый вместе с самим списком.</summary>
    public bool HasUrgentAmpOptions
    {
        get => _hasUrgentAmpOptions;
        private set { _hasUrgentAmpOptions = value; OnPropertyChanged(); }
    }

    private CncNormOption? _selectedUrgentAmpOption;
    public CncNormOption? SelectedUrgentAmpOption
    {
        get => _selectedUrgentAmpOption;
        set { _selectedUrgentAmpOption = value; OnPropertyChanged(); RecomputeUrgentEstimate(); }
    }

    private int _urgentQuantity = 1;
    public int UrgentQuantity
    {
        get => _urgentQuantity;
        set { _urgentQuantity = Math.Max(1, value); OnPropertyChanged(); RecomputeUrgentEstimate(); }
    }

    /// <summary>Посчитанное по формуле время (в часах) — стартовое значение для
    /// UrgentManualHours. Реальная скорость реза на практике отличается от справочной (углы,
    /// качество листа и т.п.), поэтому это ВСЕГДА только предложение, которое технолог видит
    /// и может поправить перед постановкой — не скрытое и не жёстко фиксированное число.</summary>
    private double _urgentComputedHours;

    private double _urgentManualHours;
    /// <summary>Помечается true, как только технолог сам ввёл число в это поле — после этого
    /// RecomputeUrgentEstimate (срабатывает при смене количества/режима) больше не затирает
    /// ручной ввод автоматической подсказкой. Раньше затирал всегда, и ручное значение
    /// пропадало при первом же клике по стрелочке количества.</summary>
    private bool _urgentManualHoursOverridden;

    /// <summary>Итоговое время, которое реально уйдёт в план — предзаполняется расчётом
    /// (RecomputeUrgentEstimate), но технолог может скорректировать вручную перед постановкой,
    /// в том числе на несколько минут — минимум только МинPlaceableHours (6 минут), никакого
    /// дополнительного ограничения снизу нет.</summary>
    public double UrgentManualHours
    {
        get => _urgentManualHours;
        set { _urgentManualHours = Math.Max(0, value); _urgentManualHoursOverridden = true; OnPropertyChanged(); }
    }

    /// <summary>Подставляет предложенное расчётом время, не помечая его как "введённое вручную" —
    /// иначе последующий пересчёт (смена количества и т.п.) переставал бы обновлять подсказку
    /// даже пока технолог её ещё не трогал.</summary>
    private void ApplySuggestedManualHours(double hours)
    {
        _urgentManualHours = Math.Max(0, hours);
        OnPropertyChanged(nameof(UrgentManualHours));
    }

    public string UrgentEstimateText
    {
        get
        {
            if (SelectedUrgentPart == null) return "Сначала выберите деталь.";
            string overhead = $"+{PlanScheduleService.FormatHours(UrgentOverheadHours)} накладных (принести металл, убрать полуфабрикаты и т.п.)";
            if (SelectedUrgentAmpOption == null)
                return $"Нет данных резки в базе АДЖАН для этой марки/толщины — саму резку оценить не по чему, но время на постановку всё равно предложено ({overhead}). Поправьте вручную под фактическую резку.";
            double cuttingPart = Math.Max(0, _urgentComputedHours - UrgentOverheadHours);
            return $"Режим {SelectedUrgentAmpOption.AmpMode}: резка {PlanScheduleService.FormatHours(cuttingPart)} на {UrgentQuantity} шт. {overhead}. Итого {PlanScheduleService.FormatHours(_urgentComputedHours)} — можно поправить перед постановкой.";
        }
    }

    private void SearchUrgentParts()
    {
        UrgentSearchResults.Clear();
        string q = (UrgentSearchText ?? "").Trim();
        if (q.Length < 2) return; // слишком короткий запрос — ждём, пока техник допишет
        foreach (var r in _dbService.SearchDetails(q)) UrgentSearchResults.Add(r);
    }

    private void LoadUrgentAmpOptions()
    {
        UrgentAmpOptions.Clear();
        HasUrgentAmpOptions = false;
        SelectedUrgentAmpOption = null;
        _matchedUrgentSteelGrade = null;
        if (SelectedUrgentPart == null) return;

        // Details.SteelGrade — составная строка ("Лист г/к 4мм,09Г2С"), а не чистый код марки,
        // поэтому ищем среди известных базе АДЖАН марок ту, что входит в эту строку подстрокой
        // (см. DatabaseService.NormalizeSteelGradeForCncNorms).
        _matchedUrgentSteelGrade = _dbService.NormalizeSteelGradeForCncNorms(SelectedUrgentPart.SteelGrade);
        OnPropertyChanged(nameof(SelectedUrgentPartInfoText));

        if (_matchedUrgentSteelGrade != null)
        {
            foreach (var opt in _dbService.GetCncNormOptions(_matchedUrgentSteelGrade, SelectedUrgentPart.Thickness))
                UrgentAmpOptions.Add(opt);
        }
        HasUrgentAmpOptions = UrgentAmpOptions.Count > 0;

        // ПДО режим тока/газа сам не выбирает — на заводе это фиксированное правило по толщине
        // (2-8мм режут на 80А, 10-16мм на 130А, всё толще — на 260А), технологи знают это
        // наизусть, а тонкости самого резака им ни к чему. Поэтому вместо того чтобы отдавать
        // выбор в интерфейс, сразу подставляем вариант с "правильным" по этому правилу током;
        // если у конкретной марки в базе АДЖАН для него данных нет — берём ближайший найденный,
        // чтобы оценка времени всё равно была (технолог видит это по тексту UrgentEstimateText
        // и может поправить время вручную).
        string preferredAmp = PreferredAmpModeForThickness(SelectedUrgentPart.Thickness);
        SelectedUrgentAmpOption =
            UrgentAmpOptions.FirstOrDefault(o => o.AmpMode == preferredAmp && o.IsExactThickness)
            ?? UrgentAmpOptions.FirstOrDefault(o => o.AmpMode == preferredAmp)
            ?? UrgentAmpOptions.FirstOrDefault();
    }

    /// <summary>Заводское правило выбора тока плазмореза по толщине листа (не марке стали) —
    /// см. пояснение технолога: 2,3,4,6,8 мм режут на 80А; 10,12,14,16 мм — на 130А; толще —
    /// на 260А. Ток именно по толщине один и тот же независимо от марки стали.</summary>
    private static string PreferredAmpModeForThickness(double thicknessMm)
    {
        if (thicknessMm <= 8.01) return "80A";
        if (thicknessMm <= 16.01) return "130A";
        return "260A";
    }

    private void RecomputeUrgentEstimate()
    {
        if (SelectedUrgentPart == null)
        {
            _urgentComputedHours = 0;
            OnPropertyChanged(nameof(UrgentEstimateText));
            return;
        }

        double cuttingHoursPerPart = 0;
        double pierceHoursPerPart = 0;
        if (SelectedUrgentAmpOption != null)
        {
            double speedMmPerHour = SelectedUrgentAmpOption.CuttingSpeedMmPerMin * 60.0;
            cuttingHoursPerPart = speedMmPerHour > 0 ? SelectedUrgentPart.LengthMm / speedMmPerHour : 0;
            pierceHoursPerPart = SelectedUrgentAmpOption.PiercingTimeSec / 3600.0;
        }

        // Накладную часть (UrgentOverheadHours) прибавляем ВСЕГДА, даже если резки по базе
        // АДЖАН для этой марки/толщины нет вообще — тогда это единственная часть оценки, но
        // хотя бы не 0, и добавить дорезку можно, сразу же поправив время вручную под реальную
        // резку.
        _urgentComputedHours = (cuttingHoursPerPart + pierceHoursPerPart) * UrgentQuantity + UrgentOverheadHours;

        // Не затираем ручной ввод технолога: подсказку обновляем автоматически только пока он
        // её ещё не трогал (см. _urgentManualHoursOverridden).
        if (!_urgentManualHoursOverridden)
            ApplySuggestedManualHours(Math.Round(_urgentComputedHours, 2));

        OnPropertyChanged(nameof(UrgentEstimateText));
    }

    /// <summary>Ставит срочную дорезку в план — не на конкретный день, а сразу "недорезанным
    /// остатком" в правую панель (ManualNormSeconds = введённое/посчитанное время, сегментов
    /// по дням ещё нет), откуда ПДО перетаскивает её на нужный день точно так же, как любой
    /// другой остаток — со всеми уже существующими проверками вместимости/17:00.</summary>
    public void AddUrgentRecut()
    {
        // Раньше при невыполненном условии метод просто молча выходил — снаружи это выглядело
        // как "кнопка не работает" (см. отчёт технолога). Теперь каждый отказ объясняется в
        // UrgentValidationError, а недостающая причина ещё и подсвечивает своё поле.
        UrgentValidationError = "";
        UrgentReasonMissing = false;

        if (SelectedUrgentPart == null)
        {
            UrgentValidationError = "Сначала выберите деталь из списка.";
            return;
        }
        if (string.IsNullOrWhiteSpace(ReworkReason))
        {
            UrgentReasonMissing = true;
            UrgentValidationError = "Укажите причину дорезки — поле обязательно.";
            return;
        }
        if (UrgentManualHours < MinPlaceableHours)
        {
            UrgentValidationError = "Итоговое время должно быть больше 0 — если расчёт не подставился, впишите время вручную.";
            return;
        }

        string sheetFormat = SelectedSheetIndex switch
        {
            0 => "1500 x 6000 мм",
            1 => "2000 x 6000 мм",
            _ => $"{CustomWidth} x {CustomLength} мм"
        };

        var entry = new PlanEntry
        {
            OrderName = string.IsNullOrEmpty(SelectedUrgentPart.OrderName) ? "Срочная дорезка" : SelectedUrgentPart.OrderName,
            ProductName = string.IsNullOrEmpty(SelectedUrgentPart.ProductName) ? "Срочная дорезка" : SelectedUrgentPart.ProductName,
            UnitName = $"{SelectedUrgentPart.Title} ×{UrgentQuantity}",
            PlannedDate = DateTimeOffset.Now,
            SheetFormat = sheetFormat,
            IsUrgent = true,
            Comment = string.IsNullOrWhiteSpace(ReworkReason) ? "Срочная дорезка" : ReworkReason,
            ManualNormSeconds = UrgentManualHours * 3600.0
        };

        _dbService.SavePlanEntry(entry);

        // Сброс формы — деталь/количество были для ЭТОЙ дорезки, следующую вводим с нуля.
        // SelectedUrgentPart = null уже сам чистит текст поиска/список результатов (см. сеттер).
        SelectedUrgentPart = null;
        UrgentQuantity = 1;
        ReworkReason = "";
        UrgentValidationError = "";
        UrgentReasonMissing = false;
        // Сворачиваем форму галочки — дорезка уже ушла в правую панель "⚠ Недорезано",
        // держать форму открытой значило бы рисковать случайно поставить вторую дорезку.
        IsUrgent = false;

        RefreshAll();
    }

    public PlanViewModel()
    {
        _dbService = new DatabaseService();
        _dbService.InitPlanTable(); // внутри безопасно при оффлайне

        LoadInitialData();
        RefreshPlanTree();
        RefreshTimeline();
        RebuildPool();
    }

    public void RefreshAll()
    {
        LoadInitialData(); // держим старые списки в живых на случай, если где-то ещё используются
        RefreshPlanTree(); // Перезагружает дерево планов
        RefreshTimeline(); // Перезагружает визуальный таймлайн-календарь
        RebuildPool();      // Перезагружает правую панель нераспределённых Заказ/Изделие/Узел
    }

    public void RefreshPlanTree()
    {
        PlanTree.Clear();
        var nodes = _dbService.GetPlanTreeFromDb(); // оффлайн вернёт локальный кэш
        foreach (var node in nodes)
        {
            PlanTree.Add(node);
        }
    }

    /// <summary>
    /// Перестраивает визуальный календарь на TimelineDayCount дней вперёд от TimelineStartDate:
    /// для каждого дня тянет из БД уже упорядоченные (по SortOrder) сегменты заданий, считает
    /// их Start/End через PlanScheduleService.ComputeSchedule (плитки друг за другом с 08:00,
    /// обед пропускается) и суммирует занятость дня для шкалы-гейджа в заголовке колонки. Само
    /// содержимое сегментов уже обрезано по вместимости дня на этапе записи (см.
    /// AddPoolItemToPlanOnDate/ResizeSegment/MoveSegmentToDay) — сюда попадает то, что реально
    /// поместилось.
    /// </summary>
    public void RefreshTimeline()
    {
        TimelineDays.Clear();
        for (int i = 0; i < TimelineDayCount; i++)
        {
            var date = TimelineStartDate.AddDays(i);
            var column = new DayColumnViewModel { Date = date };
            column.Shift2Enabled = _dbService.GetShift2Enabled(date);
            column.ReorderRequested = taskIds => ReorderWithinDay(date, taskIds);
            column.ResizeRequested = (taskId, newDuration) => ResizeSegment(taskId, date, newDuration);
            column.PoolItemDropped = (orderName, productName, unitName) => AddPoolItemToPlanOnDate(orderName, productName, unitName, date);
            column.LeftoverDropped = taskId => AddLeftoverToDate(taskId, date);
            column.MoveTileRequested = (taskId, fromDate, toDate) => MoveSegmentToDay(taskId, fromDate, toDate);
            column.RemoveFromDayRequested = taskId => RemoveSegmentFromDay(taskId, date);
            column.ToggleShift2Requested = () => ToggleShift2(date);

            var segments = _dbService.GetDaySegments(date);
            var tiles = new List<DayTileViewModel>();
            foreach (var seg in segments)
            {
                string title = string.IsNullOrEmpty(seg.UnitName) || seg.UnitName == "Весь узел"
                    ? $"{seg.OrderName} / {seg.ProductName}"
                    : $"{seg.OrderName} / {seg.ProductName} / {seg.UnitName}";

                var tile = new DayTileViewModel
                {
                    TaskId = seg.TaskId,
                    Title = title,
                    IsUrgent = seg.IsUrgent,
                    Duration = seg.Allocated
                };

                // Предел растягивания ЭТОГО сегмента по норме всего задания (см. ResizeSegment
                // и Grip_PointerMoved в DayColumnView) — считаем здесь же, чтобы визуальный
                // грип-драг видел тот же потолок, что и итоговое сохранение, без рассинхрона.
                var norm = _dbService.GetTaskLaborNorm(seg.TaskId);
                if (norm.HasValue)
                {
                    double allocatedOtherDays = _dbService.GetTaskSegments(seg.TaskId)
                        .Where(s => s.Date.Date != date.Date)
                        .Sum(s => s.Duration.TotalHours);
                    tile.MaxTotalNormHours = Math.Max(0, norm.Value.TotalHours - allocatedOtherDays);
                }

                tiles.Add(tile);
            }

            PlanScheduleService.ComputeSchedule(date, tiles);
            foreach (var tile in tiles) column.Tiles.Add(tile);
            column.OccupiedHours = tiles.Sum(t => t.Duration.TotalHours);

            TimelineDays.Add(column);
        }
    }

    /// <summary>
    /// Сохраняет новый визуальный порядок плиток внутри одного дня (свободный drag-and-drop
    /// перетаскивания плитки в другое место в той же колонке) и пересчитывает Start/End всех
    /// плиток дня заново.
    /// </summary>
    public void ReorderWithinDay(DateTime date, List<int> taskIdsInNewOrder)
    {
        _dbService.SetSegmentSortOrders(date, taskIdsInNewOrder);
        RefreshTimeline();
    }

    /// <summary>
    /// Ручное изменение длительности плитки перетаскиванием нижнего края. Сжатие просто
    /// уменьшает время на ЭТОТ день — освободившееся время НИКУДА автоматически не
    /// перекладывается: оно становится недорезанным остатком в правой панели, и ПДО сам
    /// решает, на какой день его перетащить дальше (см. GetLeftoverTasks). Растягивание
    /// ограничено свободным местом ЭТОГО дня (вместимость минус то, что уже занято другими
    /// заданиями) — "одолжить" время у соседних дней больше нельзя.
    /// </summary>
    public void ResizeSegment(int taskId, DateTime date, TimeSpan newDuration)
    {
        if (taskId <= 0 || newDuration < TimeSpan.Zero) return;

        var existing = _dbService.GetTaskSegments(taskId);
        if (!existing.Any(s => s.Date.Date == date.Date)) return;

        double capacity = GetDayCapacityHours(date);
        double occupiedOther = GetDayOccupiedHoursExcluding(date, taskId);
        double maxByDay = Math.Max(0, capacity - occupiedOther);

        // Кроме вместимости дня, растянуть сегмент больше, чем реально осталось резать по
        // норме ВСЕГО задания (норма минус то, что уже расписано на ДРУГИЕ дни), тоже нельзя —
        // иначе получалось (это и был баг): день пустой, свободного места полно, и плитку с
        // фиксированным временем резки можно было растянуть куда сильнее, чем у неё есть работы.
        double maxByNorm = double.MaxValue;
        var norm = _dbService.GetTaskLaborNorm(taskId);
        if (norm.HasValue)
        {
            double allocatedOtherDays = existing.Where(s => s.Date.Date != date.Date).Sum(s => s.Duration.TotalHours);
            maxByNorm = Math.Max(0, norm.Value.TotalHours - allocatedOtherDays);
        }

        double maxAllowed = Math.Min(maxByDay, maxByNorm);
        newDuration = TimeSpan.FromHours(Math.Min(newDuration.TotalHours, maxAllowed));

        var current = existing.First(s => s.Date.Date == date.Date);
        if (newDuration == current.Duration) return;

        var updated = existing
            .Select(s => s.Date.Date == date.Date ? (s.Date, Duration: newDuration) : s)
            .Where(s => s.Duration > TimeSpan.Zero)
            .ToList();

        if (updated.Count == 0)
        {
            // Сжали до нуля — задание полностью снимается с плана (весь узел вернётся
            // обычной незапланированной позицией, а не "остатком").
            _dbService.SetTaskSegments(taskId, updated);
            _dbService.DeletePlannedTask(taskId);
        }
        else
        {
            _dbService.SetTaskSegments(taskId, updated);
        }

        RefreshAll();
    }

    // ------------------------------------------------------------------
    // Списки выбора: уже запланированное СКРЫВАЕМ.
    //  - заказ целиком («Все изделия») -> заказ пропадает
    //  - изделие целиком («Весь узел») ИЛИ все его узлы выставлены -> изделие пропадает
    //  - конкретный узел -> узел пропадает
    // ------------------------------------------------------------------

    private void LoadInitialData()
    {
        var tree = _dbService.GetStatementTreeFromDb();
        var planned = PlanQueryService.GetPlannedKeys();

        Orders.Clear();
        foreach (var item in tree)
        {
            // заказ скрываем, если он запланирован целиком ИЛИ в нём не осталось ни одного незапланированного изделия
            if (PlanQueryService.IsPlanned(planned, item.Title)) continue;

            bool hasAvailableProduct = item.Children.Any(p =>
                !PlanQueryService.IsPlanned(planned, item.Title, p.Title) && !AllUnitsPlanned(planned, item.Title, p));

            if (!hasAvailableProduct) continue;
            Orders.Add(item.Title);
        }
    }

    private void LoadProductsForOrder()
    {
        Products.Clear();
        if (string.IsNullOrEmpty(SelectedOrder)) return;

        var tree = _dbService.GetStatementTreeFromDb();
        var planned = PlanQueryService.GetPlannedKeys();

        var orderNode = tree.FirstOrDefault(x => x.Title == SelectedOrder);
        if (orderNode != null)
        {
            foreach (var child in orderNode.Children)
            {
                // изделие, уже стоящее в плане целиком, скрываем
                if (PlanQueryService.IsPlanned(planned, SelectedOrder, child.Title)) continue;
                // изделие, у которого выставлены ВСЕ узлы, тоже скрываем
                if (AllUnitsPlanned(planned, SelectedOrder, child)) continue;

                Products.Add(child.Title);
            }
        }
    }

    private void LoadUnitsForProduct()
    {
        Units.Clear();
        if (string.IsNullOrEmpty(SelectedOrder) || string.IsNullOrEmpty(SelectedProduct)) return;

        var tree = _dbService.GetStatementTreeFromDb();
        var planned = PlanQueryService.GetPlannedKeys();

        var orderNode = tree.FirstOrDefault(x => x.Title == SelectedOrder);
        var productNode = orderNode?.Children.FirstOrDefault(x => x.Title == SelectedProduct);

        if (productNode != null)
        {
            foreach (var child in productNode.Children)
            {
                // узел, уже стоящий в плане, скрываем из выбора
                if (PlanQueryService.IsPlanned(planned, SelectedOrder, SelectedProduct, child.Title)) continue;
                Units.Add(child.Title);
            }
        }
    }

    /// <summary>Все узлы изделия уже стоят в плане -> изделие считается полностью выставленным.</summary>
    private static bool AllUnitsPlanned(HashSet<string> planned, string order, StatementItem productNode)
    {
        // Узлов нет вообще — изделие не считаем "выставленным по узлам" (его можно поставить целиком)
        if (productNode.Children.Count == 0) return false;

        return productNode.Children.All(u =>
            PlanQueryService.IsPlanned(planned, order, productNode.Title, u.Title));
    }

    /// <summary>
    /// Перестраивает правую панель "плиток" из ещё не запланированных Заказ/Изделие/Узел —
    /// та же логика скрытия уже поставленного, что и в LoadInitialData/LoadProductsForOrder/
    /// LoadUnitsForProduct, только сразу собранная в дерево карточек с учётом текстового
    /// поиска (PoolSearchText). Раскрытые заказы (IsExpanded) стараемся не сворачивать заново
    /// при каждом обновлении, чтобы не сбивать пользователя во время работы.
    /// </summary>
    private void RebuildPool()
    {
        var tree = _dbService.GetStatementTreeFromDb();
        var planned = PlanQueryService.GetPlannedKeys();
        string filter = (PoolSearchText ?? "").Trim().ToLowerInvariant();

        var previouslyExpanded = PoolOrders.ToDictionary(o => o.OrderName, o => o.IsExpanded);

        var newOrders = new List<PlanPoolOrderGroup>();
        foreach (var orderItem in tree)
        {
            if (PlanQueryService.IsPlanned(planned, orderItem.Title)) continue;

            bool orderMatches = filter.Length == 0 || orderItem.Title.ToLowerInvariant().Contains(filter);

            var productGroups = new List<PlanPoolProductGroup>();
            foreach (var productItem in orderItem.Children)
            {
                if (PlanQueryService.IsPlanned(planned, orderItem.Title, productItem.Title)) continue;
                if (AllUnitsPlanned(planned, orderItem.Title, productItem)) continue;

                bool productMatches = orderMatches || productItem.Title.ToLowerInvariant().Contains(filter);

                var units = new List<PlanPoolUnitItem>();
                foreach (var unitItem in productItem.Children)
                {
                    if (PlanQueryService.IsPlanned(planned, orderItem.Title, productItem.Title, unitItem.Title)) continue;

                    bool unitMatches = productMatches || unitItem.Title.ToLowerInvariant().Contains(filter);
                    if (filter.Length > 0 && !unitMatches) continue;

                    units.Add(new PlanPoolUnitItem
                    {
                        OrderName = orderItem.Title,
                        ProductName = productItem.Title,
                        UnitName = unitItem.Title
                    });
                }

                // Изделие остаётся в списке, если само подходит под поиск ИЛИ в нём остался
                // хоть один подходящий узел (иначе искать пришлось бы по всем изделиям вручную)
                if (filter.Length > 0 && !productMatches && units.Count == 0) continue;

                var pg = new PlanPoolProductGroup { OrderName = orderItem.Title, ProductName = productItem.Title };
                foreach (var u in units) pg.Units.Add(u);
                productGroups.Add(pg);
            }

            if (productGroups.Count == 0) continue;

            var og = new PlanPoolOrderGroup { OrderName = orderItem.Title };
            // При активном поиске разворачиваем всё, чтобы результат сразу было видно;
            // без поиска — вспоминаем, что пользователь сам сворачивал/разворачивал.
            og.IsExpanded = filter.Length > 0 || !previouslyExpanded.TryGetValue(orderItem.Title, out var wasExpanded) || wasExpanded;
            foreach (var pg in productGroups) og.Products.Add(pg);
            newOrders.Add(og);
        }

        PoolOrders.Clear();
        foreach (var o in newOrders) PoolOrders.Add(o);

        // "Недорезанные остатки" — не зависят от дерева ведомости, считаются напрямую по
        // разнице норма/расписано, поэтому собираются отдельным запросом.
        var leftovers = _dbService.GetLeftoverTasks()
            .Where(l => filter.Length == 0
                        || l.OrderName.ToLowerInvariant().Contains(filter)
                        || l.ProductName.ToLowerInvariant().Contains(filter)
                        || l.UnitName.ToLowerInvariant().Contains(filter))
            .Select(l => new PlanPoolLeftoverItem
            {
                TaskId = l.TaskId,
                OrderName = l.OrderName,
                ProductName = l.ProductName,
                UnitName = l.UnitName,
                Remaining = l.Remaining
            })
            .ToList();

        PoolLeftovers.Clear();
        foreach (var l in leftovers) PoolLeftovers.Add(l);
        HasLeftovers = PoolLeftovers.Count > 0;
    }

    // ------------------------------------------------------------------
    // Вместимость дня: 8ч (первая смена, до 17:00) по умолчанию, 13ч — если ПДО явно включил
    // вторую смену для этого конкретного дня кнопкой на колонке календаря.
    // ------------------------------------------------------------------

    private double GetDayCapacityHours(DateTime date) =>
        _dbService.GetShift2Enabled(date) ? PlanScheduleService.TotalCapacityHours : PlanScheduleService.Shift1CapacityHours;

    /// <summary>Сколько часов дня уже занято ДРУГИМИ заданиями (не считая текущее) — остаток
    /// показывает, сколько ещё можно добавить/растянуть без пересечения вместимости дня.</summary>
    private double GetDayOccupiedHoursExcluding(DateTime date, int excludeTaskId) =>
        _dbService.GetDaySegments(date).Where(s => s.TaskId != excludeTaskId).Sum(s => s.Allocated.TotalHours);

    /// <summary>
    /// Добавляет в план плитку, перетащенную из правой панели, сразу на день, на который её
    /// бросили в календаре (дата приходит от DayColumnView, а не из отдельного DatePicker).
    /// unitName пустой означает "изделие целиком" — аналог старого выбора без конкретного узла.
    /// Настройки листа и срочности берутся из формы над календарём (общие для любой плитки).
    /// </summary>
    public void AddPoolItemToPlanOnDate(string orderName, string productName, string unitName, DateTime date)
    {
        if (string.IsNullOrEmpty(orderName)) return;

        string sheetFormat = SelectedSheetIndex switch
        {
            0 => "1500 x 6000 мм",
            1 => "2000 x 6000 мм",
            _ => $"{CustomWidth} x {CustomLength} мм"
        };

        var entry = new PlanEntry
        {
            OrderName = orderName,
            ProductName = string.IsNullOrEmpty(productName) ? "Все изделия" : productName,
            UnitName = string.IsNullOrEmpty(unitName) ? "Весь узел" : unitName,
            PlannedDate = new DateTimeOffset(date.Date),
            SheetFormat = sheetFormat,
            IsUrgent = IsUrgent,
            Comment = IsUrgent ? ReworkReason : "Плановое задание"
        };

        int taskId = _dbService.SavePlanEntry(entry);
        if (taskId > 0)
        {
            var total = _dbService.GetPlasmaCuttingTime(entry.OrderName, entry.ProductName, entry.UnitName);
            if (total.HasValue && total.Value > TimeSpan.Zero)
            {
                // Режем ровно по вместимости дня (8ч, либо 13ч, если для этого дня явно
                // включена 2-я смена) — то, что не влезло, НЕ уходит само на день вперёд:
                // оно останется "недорезанным остатком" в правой панели (см. GetLeftoverTasks),
                // и ПДО сам решит, куда его перетащить дальше.
                double capacity = GetDayCapacityHours(date);
                double occupied = GetDayOccupiedHoursExcluding(date, taskId);
                double available = Math.Max(0, capacity - occupied);
                double allocatedHours = Math.Min(total.Value.TotalHours, available);

                // День уже занят целиком (свободного места меньше минимальной осмысленной
                // плитки) — НЕ заводим ни задание, ни "призрачный" остаток без единого
                // сегмента: полностью откатываем, узел остаётся как был, обычной
                // незапланированной позицией в правой панели. Никакой недосказанности —
                // либо влезло видимым куском, либо ничего не произошло.
                if (allocatedHours < MinPlaceableHours)
                {
                    _dbService.DeletePlannedTask(taskId);
                    RefreshAll();
                    return;
                }

                _dbService.SetTaskSegments(taskId, new List<(DateTime, TimeSpan)>
                {
                    (date.Date, TimeSpan.FromHours(allocatedHours))
                });
            }
        }

        RefreshAll();

        IsUrgent = false;
        ReworkReason = "";
    }

    /// <summary>
    /// Перетащили "недорезанный остаток" из правой панели на день — добавляет сегмент к уже
    /// существующему заданию (не создаёт новое), тоже с обрезкой по вместимости дня.
    /// </summary>
    public void AddLeftoverToDate(int taskId, DateTime date)
    {
        if (taskId <= 0) return;

        var leftover = _dbService.GetLeftoverTasks().FirstOrDefault(l => l.TaskId == taskId);
        if (leftover == null || leftover.RemainingSeconds <= 0) return;

        var existing = _dbService.GetTaskSegments(taskId);
        bool hasAlready = existing.Any(s => s.Date.Date == date.Date);
        double alreadyOwnHours = hasAlready ? existing.First(s => s.Date.Date == date.Date).Duration.TotalHours : 0.0;

        // ВАЖНО: "сколько свободно" считаем как вместимость дня минус ДРУГИЕ задания —
        // это сколько места в принципе может занимать ЭТО задание за весь день. Из него ещё
        // нужно вычесть то, что это же задание уже занимает на этот день СЕЙЧАС — иначе
        // (это и был баг) кусок остатка просто добавлялся ПОВЕРХ уже стоящего куска, и день
        // мог в сумме уйти далеко за вместимость (напр. 8ч уже стоит + весь остаток 6.6ч
        // добавляется без оглядки на первые 8ч = 14.6ч на день вместо потолка в 13ч).
        double capacity = GetDayCapacityHours(date);
        double occupiedOther = GetDayOccupiedHoursExcluding(date, taskId);
        double totalAvailableForThisTask = Math.Max(0, capacity - occupiedOther);
        double addHours = Math.Min(leftover.Remaining.TotalHours, Math.Max(0, totalAvailableForThisTask - alreadyOwnHours));
        if (addHours < MinPlaceableHours) return; // день полностью занят — добавить сюда нечего

        var updated = existing.Where(s => s.Date.Date != date.Date).ToList();
        var newDuration = TimeSpan.FromHours(alreadyOwnHours + addHours);
        updated.Add((date.Date, newDuration));

        _dbService.SetTaskSegments(taskId, updated);
        RefreshAll();
    }

    /// <summary>
    /// Перетащили уже стоящую плитку на ДРУГОЙ день — снимает её с дня-источника и добавляет
    /// (с обрезкой по вместимости) на день-получатель. Если на новом дне не хватило места —
    /// нехватка не теряется, а становится недорезанным остатком в правой панели.
    /// </summary>
    public void MoveSegmentToDay(int taskId, DateTime fromDate, DateTime toDate)
    {
        if (taskId <= 0 || fromDate.Date == toDate.Date) return;

        var existing = _dbService.GetTaskSegments(taskId);
        if (!existing.Any(s => s.Date.Date == fromDate.Date)) return;
        var moving = existing.First(s => s.Date.Date == fromDate.Date).Duration;

        bool hasTarget = existing.Any(s => s.Date.Date == toDate.Date);
        double targetOwnHours = hasTarget ? existing.First(s => s.Date.Date == toDate.Date).Duration.TotalHours : 0.0;

        // Как и в AddLeftoverToDate: "свободно на новый день" — это вместимость минус ДРУГИЕ
        // задания, а из этого ещё нужно вычесть то, что задание УЖЕ занимает на день-получатель
        // (если туда переносят кусок того же задания, который там частично уже стоит) — иначе
        // перенесённый кусок добавлялся бы поверх уже стоящего без общего потолка на день.
        double capacity = GetDayCapacityHours(toDate);
        double occupiedOther = GetDayOccupiedHoursExcluding(toDate, taskId);
        double totalAvailableForThisTask = Math.Max(0, capacity - occupiedOther);
        double available = Math.Max(0, totalAvailableForThisTask - targetOwnHours);

        // На новом дне почти совсем нет места — переносить нечего и незачем: полностью
        // отменяем перенос, сегмент остаётся там, где был (а не исчезает без следа с обоих
        // дней, как было бы, если бы "перенесённые" 0 часов просто нигде не осели).
        if (available < MinPlaceableHours) return;

        var movedDuration = TimeSpan.FromHours(Math.Min(moving.TotalHours, available));

        var rest = existing.Where(s => s.Date.Date != fromDate.Date).ToList();
        var updated = rest.Where(s => s.Date.Date != toDate.Date).ToList();
        if (movedDuration > TimeSpan.Zero)
        {
            var totalOnTarget = TimeSpan.FromHours(targetOwnHours) + movedDuration;
            updated.Add((toDate.Date, totalOnTarget));
        }
        else if (hasTarget)
        {
            updated.Add((toDate.Date, TimeSpan.FromHours(targetOwnHours)));
        }

        if (updated.Count == 0)
        {
            // Сегментов совсем не осталось — задание полностью возвращается в обычный пул
            // (не в "недорезанные остатки", там оно смотрелось бы как единственная плитка).
            _dbService.SetTaskSegments(taskId, updated);
            _dbService.DeletePlannedTask(taskId);
        }
        else
        {
            _dbService.SetTaskSegments(taskId, updated);
        }

        RefreshAll();
    }

    /// <summary>
    /// ПКМ на плитке календаря → "Убрать с этого дня": снимает именно этот сегмент. Если у
    /// задания больше не осталось сегментов ни на один день — оно полностью снимается с
    /// плана и узел снова становится обычной незапланированной позицией в правой панели.
    /// </summary>
    public void RemoveSegmentFromDay(int taskId, DateTime date)
    {
        if (taskId <= 0) return;

        var existing = _dbService.GetTaskSegments(taskId);
        var remaining = existing.Where(s => s.Date.Date != date.Date).ToList();
        if (remaining.Count == existing.Count) return; // на этом дне у задания и не было сегмента

        _dbService.SetTaskSegments(taskId, remaining);
        if (remaining.Count == 0)
        {
            _dbService.DeletePlannedTask(taskId);
        }

        RefreshAll();
    }

    /// <summary>Кнопка "Вывести 2-ю смену" на колонке дня — переключает разрешение резать
    /// после 17:00 именно в этот день (не влияет на другие дни).</summary>
    public void ToggleShift2(DateTime date)
    {
        bool current = _dbService.GetShift2Enabled(date);
        _dbService.SetShift2Enabled(date, !current);
        RefreshTimeline();
    }

    public void AddToPlan()
    {
        if (string.IsNullOrEmpty(SelectedOrder)) return;

        string sheetFormat = SelectedSheetIndex switch
        {
            0 => "1500 x 6000 мм",
            1 => "2000 x 6000 мм",
            _ => $"{CustomWidth} x {CustomLength} мм"
        };

        var entry = new PlanEntry
        {
            OrderName = SelectedOrder,
            ProductName = SelectedProduct ?? "Все изделия",
            UnitName = SelectedUnit ?? "Весь узел",
            PlannedDate = PlannedDate,
            SheetFormat = sheetFormat,
            IsUrgent = IsUrgent,
            Comment = IsUrgent ? ReworkReason : "Плановое задание"
        };

        // 1. Сохраняем задание в БД и получаем его TaskId.
        //    Возврат -1 означает: нет связи, задание лежит в локальной очереди и
        //    уйдёт на сервер автоматически (программы раскроя сгенерируются при синхронизации).
        int taskId = _dbService.SavePlanEntry(entry);

        // 2. Если для узла есть норма трудоёмкости на плазму (лист "Трудоёмкость" ведомости) —
        //    сразу заводим один сегмент на выбранный день, на всё время целиком. Дальше его
        //    можно растянуть на сколько угодно дней через SplitTaskAcrossDays — без привязки
        //    к "ровно 2 дня" или "поровну", это просто стартовая точка на день постановки.
        //    Для "Все изделия"/"Весь узел" (агрегированная постановка без конкретного узла)
        //    нормы не существует — метод тихо вернёт null, и это ок, просто не будет сегментов.
        if (taskId > 0)
        {
            var total = _dbService.GetPlasmaCuttingTime(entry.OrderName, entry.ProductName, entry.UnitName);
            if (total.HasValue && total.Value > TimeSpan.Zero)
            {
                _dbService.SetTaskSegments(taskId, new List<(DateTime, TimeSpan)>
                {
                    (PlannedDate.Date, total.Value)
                });
            }
        }

        // 3. Запланированное изделие/узел исчезает из списков выбора
        RefreshAll();

        IsUrgent = false;
        ReworkReason = "";
    }

    /// <summary>
    /// Растягивает уже поставленное задание на переданный набор дней — гибко, любое их
    /// количество (не только "пополам на 2 дня"). Общее время резки берётся из уже
    /// существующей разбивки задания и делится поровну между всеми переданными датами;
    /// остаток от округления уходит в последний день, чтобы сумма точно совпадала
    /// с общим временем.
    /// </summary>
    public void SplitTaskAcrossDays(int taskId, List<DateTime> dates)
    {
        if (taskId <= 0 || dates.Count == 0) return;

        var existing = _dbService.GetTaskSegments(taskId);
        if (existing.Count == 0) return; // у задания нет известного времени резки — распределять нечего

        var total = TimeSpan.FromTicks(existing.Sum(s => s.Duration.Ticks));
        var split = PlanSplitService.EvenSplit(total, dates);

        _dbService.SetTaskSegments(taskId, split);
        RefreshPlanTree();
        RefreshTimeline();
    }

    /// <summary>
    /// Убирает один день из разбивки задания — оставшееся время равномерно
    /// перераспределяется между оставшимися днями (не пропадает и не "зависает" на убранном дне).
    /// </summary>
    public void RemoveDayFromSplit(int taskId, DateTime date)
    {
        var existing = _dbService.GetTaskSegments(taskId);
        var remainingDates = existing
            .Select(s => s.Date)
            .Where(d => d.Date != date.Date)
            .Distinct()
            .ToList();

        if (remainingDates.Count == 0) return;

        SplitTaskAcrossDays(taskId, remainingDates);
    }

    /// <summary>
    /// Тестовый быстрый вызов из дерева (правый клик на узле): добавляет к текущей разбивке
    /// ещё один день сразу после самого позднего уже занятого — время равномерно
    /// пересчитывается на все дни целиком. Если у задания вообще нет разбивки — берёт за
    /// основу его собственную PlannedDate.
    /// </summary>
    public void AddNextDayToSplit(int taskId)
    {
        if (taskId <= 0) return;

        var existing = _dbService.GetTaskSegments(taskId);
        var dates = existing.Select(s => s.Date).ToList();

        DateTime baseDate = dates.Count > 0 ? dates.Max() : PlannedDate.Date;
        if (dates.Count == 0) dates.Add(baseDate);

        var nextDate = baseDate.AddDays(1);
        if (!dates.Contains(nextDate)) dates.Add(nextDate);

        SplitTaskAcrossDays(taskId, dates);
    }

    /// <summary>Тестовый быстрый вызов из дерева: убирает самый поздний день разбивки
    /// (если день всего один — ничего не делает, убирать нечего).</summary>
    public void RemoveLastDayFromSplit(int taskId)
    {
        if (taskId <= 0) return;

        var existing = _dbService.GetTaskSegments(taskId);
        if (existing.Count <= 1) return;

        var lastDate = existing.Max(s => s.Date);
        RemoveDayFromSplit(taskId, lastDate);
    }
}
