using Avalonia.Controls;
using Avalonia.Input;
using BKMsystem.ViewModels;

namespace BKMsystem.Views;

public partial class NestingView : UserControl
{
    public NestingView()
    {
        InitializeComponent();

        // Ctrl + колесо мыши = масштаб PDF
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, handledEventsToo: true);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (DataContext is not NestingViewModel vm || !vm.IsPdfVisible) return;

        if (e.Delta.Y > 0) vm.ZoomInPdfCommand.Execute(null);
        else vm.ZoomOutPdfCommand.Execute(null);
        e.Handled = true;
    }
}
