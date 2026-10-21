using System;
using System.Collections.ObjectModel;
using BKMsystem.Services;

namespace BKMsystem.ViewModels;

/// <summary>
/// Правая панель "Плана" — вместо трёх выпадающих списков показывает ещё не запланированные
/// Заказ/Изделие/Узел деревом сворачиваемых карточек с плитками, которые перетаскиваются
/// мышью прямо на нужный день в календаре (drag-and-drop). Структура специально трёхуровневая
/// (Заказ -> Изделие -> Узел), чтобы не листать длинный плоский список — искать нужно только
/// внутри развёрнутого заказа, плюс есть текстовый поиск (PlanViewModel.PoolSearchText).
/// </summary>
public class PlanPoolUnitItem
{
    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UnitName { get; set; } = "";

    /// <summary>Подпись на плитке узла.</summary>
    public string Title => UnitName;
}

/// <summary>
/// Изделие внутри заказа. Сама карточка изделия тоже перетаскиваемая плитка — "поставить
/// изделие целиком" (аналог старого выбора Order+Product без Unit), а узлы под ней —
/// отдельные более мелкие плитки для точечной постановки одного узла.
/// </summary>
public class PlanPoolProductGroup : ViewModelBase
{
    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Title => ProductName;

    public ObservableCollection<PlanPoolUnitItem> Units { get; set; } = new();
}

/// <summary>Заказ — сворачиваемая группа-заголовок, сам по себе не перетаскивается
/// (слишком грубая единица постановки), просто группирует свои изделия.</summary>
public class PlanPoolOrderGroup : ViewModelBase
{
    public string OrderName { get; set; } = "";
    public string Title => OrderName;

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public ObservableCollection<PlanPoolProductGroup> Products { get; set; } = new();
}

/// <summary>
/// "Недорезанный остаток" — задание, которому по норме положено больше времени резки, чем
/// сейчас реально расписано по дням (обрезали плиткой, упёрлись в 17:00, при переносе на
/// другой день не влезло целиком). Плитка того же вида, что и обычная непоставленная позиция,
/// но с готовым TaskId — при перетаскивании на день просто ДОБАВЛЯЕТ сегмент к уже
/// существующему заданию, а не создаёт новое.
/// </summary>
public class PlanPoolLeftoverItem
{
    public int TaskId { get; set; }
    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UnitName { get; set; } = "";
    public TimeSpan Remaining { get; set; }

    public string Title => string.IsNullOrEmpty(UnitName)
        ? $"{OrderName} / {ProductName} — остаток {PlanScheduleService.FormatHours(Remaining.TotalHours)}"
        : $"{OrderName} / {ProductName} / {UnitName} — остаток {PlanScheduleService.FormatHours(Remaining.TotalHours)}";
}
