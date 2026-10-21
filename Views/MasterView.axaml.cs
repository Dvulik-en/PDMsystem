using Avalonia.Controls;
using Avalonia.Interactivity;
using BKMsystem.Models;
using BKMsystem.ViewModels;

namespace BKMsystem.Views;

public partial class MasterView : UserControl
{
    public MasterView()
    {
        InitializeComponent();
        DataContext = new MasterViewModel();
    }

    private void OnCardToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is DashboardCardModel card &&
            DataContext is MasterViewModel vm)
        {
            vm.ToggleExpand(card);
        }
    }

    /// <summary>Кнопка "💾 Сохранить" в поле причины недореза внутри карточки заказа —
    /// достаём саму карточку из DataContext кнопки (она внутри DataTemplate DashboardCardModel)
    /// и передаём в MasterViewModel.SaveReason, который пишет в CuttingDelayReasons.</summary>
    private void OnSaveReasonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is DashboardCardModel card &&
            DataContext is MasterViewModel vm)
        {
            vm.SaveReason(card);
        }
    }

    // ------------------------------------------------------------------
    // Листание окна дат таймлайна — как в "Панели"/"Плане".
    // ------------------------------------------------------------------
    private void OnTimelineBackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MasterViewModel vm) vm.TimelineShiftBack();
    }

    private void OnTimelineForwardClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MasterViewModel vm) vm.TimelineShiftForward();
    }

    private void OnTimelineTodayClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MasterViewModel vm) vm.TimelineJumpToToday();
    }
}
