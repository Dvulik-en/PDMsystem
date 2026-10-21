using System;
using System.Collections.ObjectModel;
using System.Linq;
using BKMsystem.Models;
using BKMsystem.Services;

namespace BKMsystem.ViewModels;

/// <summary>
/// Вкладка "Мастер" — тот же таймлайн и те же карточки заказов, что и в "Панели"
/// (DashboardViewModel/GetDashboardCards), но для мастера участка: он не просто смотрит на
/// статус "вырезано/не вырезано", а вписывает ПРИЧИНУ, почему конкретный заказ на конкретный
/// день не успели дорезать (таблица CuttingDelayReasons, см. NestingRepository). Технологи потом
/// видят эту причину на вкладке "Панель" как баннер над карточкой — без лишнего похода к мастеру
/// узнавать, что случилось.
/// </summary>
public class MasterViewModel : ViewModelBase
{
    private readonly NestingRepository _repo;

    private const int TimelineDayCount = 10;
    private const int TimelineShiftStep = 5;

    public ObservableCollection<DateTileModel> DateTiles { get; set; } = new();
    public ObservableCollection<DashboardCardModel> Cards { get; set; } = new();

    private DateTime _timelineStartDate = DateTime.Today.AddDays(-3);
    public DateTime TimelineStartDate
    {
        get => _timelineStartDate;
        set { _timelineStartDate = value.Date; OnPropertyChanged(); OnPropertyChanged(nameof(TimelineRangeText)); LoadDatesFromDb(); }
    }

    public string TimelineRangeText =>
        $"{TimelineStartDate:dd.MM.yyyy} – {TimelineStartDate.AddDays(TimelineDayCount - 1):dd.MM.yyyy}";

    public void TimelineShiftBack() => TimelineStartDate = TimelineStartDate.AddDays(-TimelineShiftStep);
    public void TimelineShiftForward() => TimelineStartDate = TimelineStartDate.AddDays(TimelineShiftStep);
    public void TimelineJumpToToday() => TimelineStartDate = DateTime.Today.AddDays(-3);

    private DateTileModel? _selectedDateTile;
    public DateTileModel? SelectedDateTile
    {
        get => _selectedDateTile;
        set
        {
            if (_selectedDateTile != value)
            {
                if (_selectedDateTile != null) _selectedDateTile.IsSelected = false;
                _selectedDateTile = value;
                if (_selectedDateTile != null) _selectedDateTile.IsSelected = true;
                OnPropertyChanged();
                LoadDataForSelectedDate();
            }
        }
    }

    private string _saveMessage = "";
    /// <summary>Короткое подтверждение/ошибка после сохранения причины — показывается под
    /// карточкой, чтобы мастер видел, что запись реально ушла (или не ушла, если офлайн).</summary>
    public string SaveMessage
    {
        get => _saveMessage;
        set { _saveMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSaveMessage)); }
    }

    /// <summary>Вместо биндинга на StringConverters (лишний xmlns в axaml) — простой bool,
    /// который просто и надёжно скрывает TextBlock, пока сообщения ещё не было.</summary>
    public bool HasSaveMessage => !string.IsNullOrEmpty(_saveMessage);

    public MasterViewModel()
    {
        _repo = new NestingRepository();
        _repo.EnsureTables();
        LoadDatesFromDb();

        DatabaseService.OnDatabaseUpdated += (tableName) =>
        {
            if (tableName.Equals("plannedtasks", StringComparison.OrdinalIgnoreCase) ||
                tableName.Equals("cutting_sheets", StringComparison.OrdinalIgnoreCase) ||
                tableName.Equals("details", StringComparison.OrdinalIgnoreCase) ||
                tableName.Equals("cuttingdelayreasons", StringComparison.OrdinalIgnoreCase))
            {
                LoadDataForSelectedDate();
                LoadDayStatuses();
            }
        };
    }

    private void LoadDatesFromDb()
    {
        DateTiles.Clear();
        for (int i = 0; i < TimelineDayCount; i++)
            DateTiles.Add(new DateTileModel { Date = TimelineStartDate.AddDays(i) });
        LoadDayStatuses();

        var keepDate = SelectedDateTile?.Date;
        SelectedDateTile = (keepDate.HasValue ? DateTiles.FirstOrDefault(d => d.Date.Date == keepDate.Value.Date) : null)
            ?? DateTiles.FirstOrDefault(d => d.Date.Date == DateTime.Today)
            ?? DateTiles.FirstOrDefault();
    }

    private void LoadDayStatuses()
    {
        foreach (var tile in DateTiles)
            tile.CutStatus = _repo.GetDayCutStatus(tile.Date);
    }

    private void LoadDataForSelectedDate()
    {
        Cards.Clear();
        if (SelectedDateTile == null) return;

        var date = SelectedDateTile.Date;
        bool isPastOrToday = date.Date <= DateTime.Today;
        var reasons = _repo.GetDelayReasonsForDate(date);

        foreach (var card in _repo.GetDashboardCards(date))
        {
            card.PlanDate = date;
            card.IsPastOrToday = isPastOrToday;
            if (reasons.TryGetValue(card.OrderName, out var reason))
                card.DelayReason = reason;
            // Предзаполняем поле ввода уже сохранённой причиной — мастер правит, а не пишет
            // с нуля каждый раз, когда просто заходит посмотреть.
            card.ReasonDraft = card.DelayReason ?? "";
            Cards.Add(card);
        }
    }

    public void ToggleExpand(DashboardCardModel card)
    {
        if (card != null) card.IsExpanded = !card.IsExpanded;
    }

    /// <summary>Сохраняет причину недореза по заказу этой карточки на её дату. Пустой текст —
    /// это тоже осознанное действие (стереть ранее вписанную причину), поэтому не блокируем
    /// сохранение пустой строки отдельной проверкой — мастер сам решает, что там должно быть.</summary>
    public void SaveReason(DashboardCardModel card)
    {
        if (card == null) return;

        bool ok = _repo.SetDelayReasonSafe(card.OrderName, card.PlanDate, card.ReasonDraft ?? "");
        card.DelayReason = card.ReasonDraft;
        SaveMessage = ok
            ? $"Причина по «{card.OrderName}» сохранена."
            : "Нет связи с сервером — причина сохранится, когда появится сеть.";

        LoadDayStatuses(); // могла поменяться жёлтая/красная плашка над датой
    }
}
