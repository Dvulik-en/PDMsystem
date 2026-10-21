using System;
using System.Collections.ObjectModel;
using BKMsystem.ViewModels;

namespace BKMsystem.Models;

/// <summary>Статус вырезки за день — считается только для сегодняшнего/прошедших дней
/// (будущий день ещё не наступил, спрашивать за него рано, см. NestingRepository.GetDayCutStatus).
/// "Худший" среди заказов дня: если хоть один заказ не вырезан и без причины — весь день
/// красный, даже если остальные заказы уже готовы.</summary>
public enum DayCutStatus
{
    NotApplicable,      // будущий день — статус не считаем
    NoTasks,            // на этот день вообще ничего не запланировано
    Done,               // всё, что запланировано на день, вырезано
    NotDoneNoReason,    // есть невырезанное, причина не указана мастером
    NotDoneWithReason   // есть невырезанное, но причина уже указана мастером
}

// 1. Плитка даты для Таймлайна
public class DateTileModel : ViewModelBase
{
    public DateTime Date { get; set; }
    public string DayNumber => Date.Day.ToString();
    public string MonthName => Date.ToString("MMMM").ToUpper();

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TileBackground));
            OnPropertyChanged(nameof(TileBorder));
        }
    }

    public string TileBackground => IsSelected ? "#444444" : "#1E1E1E";
    public string TileBorder => IsSelected ? "#888888" : "#333333";

    private DayCutStatus _cutStatus = DayCutStatus.NotApplicable;
    /// <summary>Статус на плашке над числом даты — заполняется DashboardViewModel через
    /// NestingRepository.GetDayCutStatus при (пере)загрузке окна дат.</summary>
    public DayCutStatus CutStatus
    {
        get => _cutStatus;
        set
        {
            _cutStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStatusBadge));
            OnPropertyChanged(nameof(StatusBadgeText));
            OnPropertyChanged(nameof(StatusBadgeColor));
        }
    }

    /// <summary>Будущие дни и дни без единого задания плашкой не отмечаем — там пока нечего
    /// показывать (не "не успели", а "ещё не наступило"/"пусто").</summary>
    public bool HasStatusBadge => CutStatus == DayCutStatus.Done
        || CutStatus == DayCutStatus.NotDoneNoReason
        || CutStatus == DayCutStatus.NotDoneWithReason;

    public string StatusBadgeText => CutStatus switch
    {
        DayCutStatus.Done => "✓ Успели",
        DayCutStatus.NotDoneNoReason => "✗ Не успели",
        DayCutStatus.NotDoneWithReason => "⚠ Причина указана",
        _ => ""
    };

    public string StatusBadgeColor => CutStatus switch
    {
        DayCutStatus.Done => "#2E7D32",
        DayCutStatus.NotDoneNoReason => "#C62828",
        DayCutStatus.NotDoneWithReason => "#C49000",
        _ => "Transparent"
    };
}

// 2. Строка изделия в шапке карточки: изделие один раз, под ним его узлы
public class DashboardProductLine
{
    public string ProductTitle { get; set; } = "";
    public ObservableCollection<string> Units { get; set; } = new();
    public string UnitsText => string.Join("\n", Units);
}

// 3. Строка детали в общей таблице карточки
public class DashPartRow : ViewModelBase
{
    public string Cipher { get; set; } = "";      // "ППС10.А.30.00.00.03 — Лист"
    public string Dimensions { get; set; } = "";  // из ведомости (Details.Dimensions), иначе из PDF
    public int Quantity { get; set; }             // суммарно по всем узлам карточки
    public string UpList { get; set; } = "";      // "04S_SL01, 04S_SL03" — все УП, где лежит деталь
    public int SheetsTotal { get; set; }
    public int SheetsDone { get; set; }

    public bool HasUp => SheetsTotal > 0;
    public string StatusText => SheetsTotal == 0 ? "нет УП"
        : SheetsDone >= SheetsTotal ? "вырезано"
        : SheetsDone > 0 ? $"частично {SheetsDone}/{SheetsTotal}" : "не вырезано";
    public string StatusColor => SheetsTotal == 0 ? "#555555"
        : SheetsDone >= SheetsTotal ? "#2E7D32"
        : SheetsDone > 0 ? "#C49000" : "#C62828";
}

// 4. Карточка дня: один заказ, изделия по одному разу, под ними узлы, ниже таблица деталей
public class DashboardCardModel : ViewModelBase
{
    public string DateStr { get; set; } = "";
    public string OrderName { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>Дата, на которую эта карточка построена — нужна, чтобы мастер мог сохранить
    /// причину недореза именно по этой дате (CuttingDelayReasons ключуется Заказ+Дата, а
    /// DateStr — уже отформатированная строка, для записи в БД нужен настоящий DateTime).
    /// Ставится вьюмоделью (Dashboard/MasterViewModel) сразу после загрузки карточки.</summary>
    public DateTime PlanDate { get; set; }

    public ObservableCollection<DashboardProductLine> Products { get; set; } = new();
    public ObservableCollection<DashPartRow> Parts { get; set; } = new();

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToggleText));
        }
    }

    public string ToggleText => IsExpanded ? "▲ свернуть описание" : "▼ развернуть описание";

    public int DoneCount
    {
        get
        {
            int n = 0;
            foreach (var p in Parts) if (p.SheetsTotal > 0 && p.SheetsDone >= p.SheetsTotal) n++;
            return n;
        }
    }
    public string ProgressText => Parts.Count == 0 ? "" : $"вырезано {DoneCount} из {Parts.Count}";

    public bool AllPartsDone => Parts.Count > 0 && DoneCount == Parts.Count;

    /// <summary>Ставит DashboardViewModel при загрузке карточек — только для сегодняшнего или
    /// прошедшего дня есть смысл показывать "не успели"/причину; для будущей даты это ещё рано.</summary>
    public bool IsPastOrToday { get; set; }

    private string? _delayReason;
    /// <summary>Причина недореза от мастера (таблица CuttingDelayReasons, ключ — Заказ+Дата) —
    /// заполняется отдельной вкладкой мастера; здесь только читаем и показываем технологу.</summary>
    public string? DelayReason
    {
        get => _delayReason;
        set
        {
            _delayReason = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDelayReason));
            OnPropertyChanged(nameof(ShowStatusBanner));
            OnPropertyChanged(nameof(StatusBannerText));
            OnPropertyChanged(nameof(StatusBannerColor));
        }
    }
    public bool HasDelayReason => !string.IsNullOrWhiteSpace(DelayReason);

    /// <summary>Плашка над всей карточкой заказа — только для прошедшего/сегодняшнего дня и
    /// только пока не всё вырезано (нажали на дату — сразу видно, что и почему не успели).</summary>
    public bool ShowStatusBanner => IsPastOrToday && !AllPartsDone;

    public string StatusBannerText => HasDelayReason
        ? $"⚠ Не вырезано — причина: {DelayReason}"
        : "✗ Не вырезано — причина не указана";

    public string StatusBannerColor => HasDelayReason ? "#3A2E00" : "#3A0000";
    public string StatusBannerBorder => HasDelayReason ? "#C49000" : "#C62828";

    private string _reasonDraft = "";
    /// <summary>Черновик причины в поле ввода вкладки "Мастер" — предзаполняется уже сохранённой
    /// DelayReason (если она есть), сохраняется по кнопке (MasterViewModel.SaveReason), после
    /// чего переносится в саму DelayReason. Технологам в "Панели" это поле не нужно (там причина
    /// только читается, банер уже строится из DelayReason).</summary>
    public string ReasonDraft
    {
        get => _reasonDraft;
        set { _reasonDraft = value; OnPropertyChanged(); }
    }

    // ВОТ ЭТОТ МЕТОД ДОЛЖЕН БЫТЬ ЗДЕСЬ, А НЕ В REPOSITORY:
    public void OnPropertyChangedPublic()
    {
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(AllPartsDone));
        OnPropertyChanged(nameof(ShowStatusBanner));
    }
}

// 5. Классы для совместимости с существующим кодом DatabaseService
public class CuttingProgramItem
{
    public string Material { get; set; } = "";
    public double Thickness { get; set; }
    public string Cipher { get; set; } = "";
    public double Length { get; set; }
    public double Width { get; set; }
    public string ProgramCode { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public bool IsReady { get; set; }
    public string StatusText => IsReady ? "готово" : "не готово";
    public string StatusColor => IsReady ? "#2E7D32" : "#C62828";
}

public class ThicknessGroupModel
{
    public double Thickness { get; set; }
    public string Material { get; set; } = "";
    public string ThicknessTitle => $"Толщина: {Thickness} мм";
    public ObservableCollection<CuttingProgramItem> Items { get; set; } = new();
}

public class DashboardOrderItem : ViewModelBase
{
    public string DateStr { get; set; } = "";
    public string CustomerSolution { get; set; } = "";
    public string ProductTitle { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public int Quantity { get; set; }
    public string Note { get; set; } = "";

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set 
        { 
            _isExpanded = value; 
            OnPropertyChanged(); 
            OnPropertyChanged(nameof(ToggleText));
        }
    }

    public string ToggleText => IsExpanded ? "▲ свернуть состав" : "▼ развернуть состав";

    private bool _showParts;
    public bool ShowParts
    {
        get => _showParts;
        set
        {
            _showParts = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowCards));
            OnPropertyChanged(nameof(ModeButtonText));
        }
    }

    public bool ShowCards => !ShowParts;
    public string ModeButtonText => ShowParts ? "📋 По деталям" : "🗂 По картам";

    public ObservableCollection<ThicknessGroupModel> ThicknessGroups { get; set; } = new();

    // Коллекция деталей узла, на которую ругается компилятор:
    public ObservableCollection<PartStatusRow> PartRows { get; set; } = new();
    public bool HasPartRows => PartRows.Count > 0;
}

public class PartStatusRow : ViewModelBase
{
    public string DrawingNumber { get; set; } = "";
    public string PartName { get; set; } = "";
    public int SheetsTotal { get; set; }
    public int SheetsDone { get; set; }
}