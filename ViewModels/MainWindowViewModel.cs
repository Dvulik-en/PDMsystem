using Avalonia;
using BKMsystem.Models;
using BKMsystem.Services;

namespace BKMsystem.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private string _currentPageTitle = "Панель";
    private string _userName = "";
    private string _userRole = "";
    private bool _isSidebarCollapsed;
    private double _sidebarWidth = 220;
    private Thickness _sidebarPadding = new Thickness(15);

    public string UserName
    {
        get => _userName;
        set { _userName = value; OnPropertyChanged(); }
    }
    public string UserRole
    {
        get => _userRole;
        set { _userRole = value; OnPropertyChanged(); }
    }
    public string CurrentPageTitle
    {
        get => _currentPageTitle;
        set { _currentPageTitle = value; OnPropertyChanged(); }
    }
    public bool IsSidebarCollapsed
    {
        get => _isSidebarCollapsed;
        set { _isSidebarCollapsed = value; OnPropertyChanged(); }
    }
    public double SidebarWidth
    {
        get => _sidebarWidth;
        set { _sidebarWidth = value; OnPropertyChanged(); }
    }
    public Thickness SidebarPadding
    {
        get => _sidebarPadding;
        set { _sidebarPadding = value; OnPropertyChanged(); }
    }

    // --- Видимость пунктов меню по роли вошедшего пользователя ---
    public bool ShowDashboard => _userService.CanSeeDashBoard;
    public bool ShowPlan => _userService.CanManagePlan;
    public bool ShowNesting => _userService.CanOperatePlasma;
    public bool ShowNestingManage => _userService.CanManageNesting;
    public bool ShowStatement => _userService.CanManageStatement;
    public bool ShowStock => _userService.CanManageStock;
    public bool ShowBaseForAjan => _userService.CanSeeBaseForAjan;
    public bool ShowSettings => _userService.CanSeeSettings;
    // Вкладка мастера — вписывает причины недореза (см. MasterViewModel)
    public bool ShowMaster => _userService.CanSeeMasterPage;
    // Вкладка директора — сводная статистика, только чтение (см. MetricsViewModel)
    public bool ShowMetrics => _userService.CanSeeMetrics;

    // Создаем объекты страниц
    public ViewModelBase DashboardPage { get; } = new DashboardViewModel();
    public ViewModelBase MasterPage { get; } = new MasterViewModel();
    public ViewModelBase MetricsPage { get; } = new MetricsViewModel();
    public ViewModelBase StockPage { get; } = new StockViewModel();
    public ViewModelBase NestingPage { get; } = new NestingViewModel();
    public ViewModelBase PlanPage { get; } = new PlanViewModel();
    public ViewModelBase SettingsPage { get; } = new SettingsViewModel();
    public ViewModelBase BaseForAjanPage { get; } = new BaseForAjanViewModel();
    public ViewModelBase StatementPage { get; } = new StatementViewModel();
    public ViewModelBase NestingTehnologistManagePage { get; } = new NestingTehnologistManageViewModel();

    private readonly UserService _userService;
    private ViewModelBase _currentPage;

    public ViewModelBase CurrentPage
    {
        get => _currentPage;
        set { _currentPage = value; OnPropertyChanged(); }
    }

    public MainWindowViewModel() : this(null) { }

    /// <summary>Создание окна под конкретного вошедшего пользователя.</summary>
    public MainWindowViewModel(UserAccount? user)
    {
        _userService = new UserService();

        if (user != null)
        {
            UserService.CurrentUser = user;
            _userService.CurrentRole = UserService.ParseRole(user.Role);
            UserName = user.FullName;
            UserRole = UserService.RoleDisplay(user.Role);
        }
        else
        {
            // Режим разработки без входа
            UserName = "Погорельский В. А.";
            UserRole = "Технолог";
        }

        _currentPage = DashboardPage;
    }

    public void TestSwichUser()
    {
        UserName = "Иванов И.";
        UserRole = "Оператор плазмы";
    }

    public void OpenDashboard()
    {
        if (!_userService.CanSeeDashBoard) return;
        CurrentPage = DashboardPage;
        CurrentPageTitle = "Панель";
    }
    public void OpenStock()
    {
        if (!_userService.CanManageStock) return;
        CurrentPage = StockPage;
        CurrentPageTitle = "Склад";
    }
    public void OpenNesting()
    {
        if (!_userService.CanOperatePlasma) return;
        CurrentPage = NestingPage;
        CurrentPageTitle = "Раскрои";
    }
    public void OpenPlan()
    {
        if (!_userService.CanManagePlan) return;
        CurrentPage = PlanPage;
        CurrentPageTitle = "План";
    }
    public void OpenSettings()
    {
        if (!_userService.CanSeeSettings) return;
        CurrentPage = SettingsPage;
        CurrentPageTitle = "Настройки";
    }
    public void OpenBaseForAjan()
    {
        if (!_userService.CanSeeBaseForAjan) return;
        CurrentPage = BaseForAjanPage;
        CurrentPageTitle = "База";
    }
    public void OpenStatement()
    {
        if (!_userService.CanManageStatement) return;
        CurrentPage = StatementPage;
        CurrentPageTitle = "Ведомость";
    }
    public void OpenNestingTehnologistManage()
    {
        if (!_userService.CanManageNesting) return;
        CurrentPage = NestingTehnologistManagePage;
        CurrentPageTitle = "Учет УП";
    }
    public void OpenMaster()
    {
        if (!_userService.CanSeeMasterPage) return;
        CurrentPage = MasterPage;
        CurrentPageTitle = "Мастер";
    }
    public void OpenMetrics()
    {
        if (!_userService.CanSeeMetrics) return;
        CurrentPage = MetricsPage;
        CurrentPageTitle = "Метрики";
    }
    public void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        SidebarWidth = IsSidebarCollapsed ? 60 : 220;
        SidebarPadding = IsSidebarCollapsed ? new Thickness(8, 15, 8, 15) : new Thickness(15);
    }
}

// Заглушки для оставшихся страниц
public class StockViewModel : ViewModelBase { }
public class SettingsViewModel : ViewModelBase { }
public class StatementViewModel : ViewModelBase { }
