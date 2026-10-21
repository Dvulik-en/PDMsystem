using Avalonia.Controls;
using Avalonia.Interactivity;
using BKMsystem.Models;
using BKMsystem.ViewModels;

namespace BKMsystem.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        DataContext = new DashboardViewModel();
    }

    private void OnCardToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is DashboardCardModel card &&
            DataContext is DashboardViewModel vm)
        {
            vm.ToggleExpand(card);
        }
    }

    // ------------------------------------------------------------------
    // Листание окна дат таймлайна — как в "Плане" (PlanView.OnTimelineBack/Forward/TodayClick).
    // ------------------------------------------------------------------
    private void OnTimelineBackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DashboardViewModel vm) vm.TimelineShiftBack();
    }

    private void OnTimelineForwardClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DashboardViewModel vm) vm.TimelineShiftForward();
    }

    private void OnTimelineTodayClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DashboardViewModel vm) vm.TimelineJumpToToday();
    }
}
