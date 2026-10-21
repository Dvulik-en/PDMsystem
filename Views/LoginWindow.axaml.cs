using Avalonia.Controls;
using Avalonia.Interactivity;
using BKMsystem.ViewModels;
using BKMsystem.Services;

namespace BKMsystem.Views;

public partial class LoginWindow : Window
{
    private readonly UserRepository _users = new();
    private bool _isRegisterMode;

    public LoginWindow()
    {
        InitializeComponent();
    }

    private void OnToggleModeClick(object? sender, RoutedEventArgs e)
    {
        _isRegisterMode = !_isRegisterMode;
        FullNamePanel.IsVisible = _isRegisterMode;
        ModeTitle.Text = _isRegisterMode ? "Регистрация (роль назначит администратор)" : "Вход в систему";
        ActionButton.Content = _isRegisterMode ? "Отправить заявку" : "Войти";
        ToggleModeButton.Content = _isRegisterMode ? "Уже есть аккаунт? Войти" : "Нет аккаунта? Регистрация";
        MessageText.Text = "";
    }

    private void OnActionClick(object? sender, RoutedEventArgs e)
    {
        if (_isRegisterMode)
        {
            var (ok, message) = _users.Register(LoginBox.Text ?? "", FullNameBox.Text ?? "", PasswordBox.Text ?? "");
            MessageText.Text = message;
            MessageText.Foreground = ok
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#A5D6A7"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#EF9A9A"));

            if (ok && message.Contains("Администратор"))
            {
                // Первая учётка — сразу входим
                TryEnter();
            }
            else if (ok)
            {
                OnToggleModeClick(sender, e); // возвращаем в режим входа
                MessageText.Text = message;
                MessageText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#A5D6A7"));
            }
        }
        else
        {
            TryEnter();
        }
    }

    private void TryEnter()
    {
        var (user, message) = _users.TryLogin(LoginBox.Text ?? "", PasswordBox.Text ?? "");
        if (user == null)
        {
            MessageText.Text = message;
            MessageText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#EF9A9A"));
            return;
        }

        var main = new MainWindow
        {
            DataContext = new MainWindowViewModel(user)
        };

        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = main; // критично: иначе TopLevel/StorageProvider во всех VM
                                        // продолжат резолвиться от уже закрытого LoginWindow
        }

        main.Show();
        Close(); // ShutdownMode = OnLastWindowClose, приложение продолжит работу в MainWindow
    }
}
