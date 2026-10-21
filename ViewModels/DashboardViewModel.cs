using System;
using System.Collections.ObjectModel;
using System.Linq;
using BKMsystem.Models;
using BKMsystem.Services;

namespace BKMsystem.ViewModels;

public class DashboardViewModel : ViewModelBase
{
    private readonly NestingRepository _repo;

    /// <summary>Сколько дней показываем на таймлайне одновременно (окно можно листать) —
    /// то же число, что и в "Плане" (PlanViewModel.TimelineDayCount), для единообразия.</summary>
    private const int TimelineDayCount = 10;
    /// <summary>На сколько дней сдвигается окно по кнопкам "Раньше"/"Позже".</summary>
    private const int TimelineShiftStep = 5;

    public ObservableCollection<DateTileModel> DateTiles { get; set; } = new();
    public ObservableCollection<DashboardCardModel> Cards { get; set; } = new();

    private DateTime _timelineStartDate = DateTime.Today.AddDays(-3);
    /// <summary>Первый день видимого окна таймлайна — НЕ привязан к "сегодня", листается в обе
    /// стороны сколько угодно (раньше окно всегда пересоздавалось от "сегодня-3", и вернуться
    /// к более старым дням было нельзя вообще).</summary>
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

    public DashboardViewModel()
    {
        _repo = new NestingRepository();
        _repo.EnsureTables();
        LoadDatesFromDb();

        DatabaseService.OnDatabaseUpdated += (tableName) =>
        {
            if (tableName.Equals("plannedtasks", StringComparison.OrdinalIgnoreCase) ||
                tableName.Equals("cutting_sheets", StringComparison.OrdinalIgnoreCase) ||
                tableName.Equals("details", StringComparison.OrdinalIgnoreCase))
            {
                LoadDataForSelectedDate();
                LoadDayStatuses(); // могло измениться "вырезано/не вырезано" по всему окну дат
            }
            else if (tableName.Equals("cuttingdelayreasons", StringComparison.OrdinalIgnoreCase))
            {
                // Причину вписал мастер в своей вкладке — обновляем и плашки дат, и баннер
                // над карточкой заказа, не дожидаясь, пока технолог сам перезайдёт во вкладку.
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
        // При листании стараемся остаться на выбранном дне, если он всё ещё виден в новом окне —
        // иначе (или при самом первом запуске) выбираем сегодня, а если и его не видно — первый день окна.
        var keepDate = SelectedDateTile?.Date;
        SelectedDateTile = (keepDate.HasValue ? DateTiles.FirstOrDefault(d => d.Date.Date == keepDate.Value.Date) : null)
            ?? DateTiles.FirstOrDefault(d => d.Date.Date == DateTime.Today)
            ?? DateTiles.FirstOrDefault();
    }

    /// <summary>Статус "успели/не успели" на плашках над числом даты — считается для всего
    /// видимого окна сразу (см. NestingRepository.GetDayCutStatus).</summary>
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
            Cards.Add(card);
        }
    }

    public void ToggleExpand(DashboardCardModel card)
    {
        if (card != null) card.IsExpanded = !card.IsExpanded;
    }
}
