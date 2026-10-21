using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BKMsystem.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string message) : this()
    {
        MessageText.Text = message;
    }

    private void OnYesClick(object? sender, RoutedEventArgs e) => Close(true);
    private void OnNoClick(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>Показывает диалог поверх owner-окна и возвращает true/false по нажатой кнопке.</summary>
    public static async System.Threading.Tasks.Task<bool> Show(Window owner, string message)
    {
        var dialog = new ConfirmDialog(message);
        return await dialog.ShowDialog<bool>(owner);
    }
}