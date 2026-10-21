using Avalonia.Controls;
using Avalonia.Interactivity;
using BKMsystem.ViewModels;

namespace BKMsystem.Views;

public partial class MetricsView : UserControl
{
    public MetricsView()
    {
        InitializeComponent();
        DataContext = new MetricsViewModel();
    }

    private void OnMonthBackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MetricsViewModel vm) vm.MonthBack();
    }

    private void OnMonthForwardClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MetricsViewModel vm) vm.MonthForward();
    }

    private void OnMonthTodayClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MetricsViewModel vm) vm.JumpToCurrentMonth();
    }
}
