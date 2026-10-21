using BKMsystem.Models;

namespace BKMsystem.Services;

public class UserService
{
    /// <summary>Текущий вошедший пользователь (заполняется окном входа).</summary>
    public static UserAccount? CurrentUser { get; set; }

    public static UserRole ParseRole(string? role) =>
        System.Enum.TryParse<UserRole>(role, out var r) ? r : UserRole.Technologist;

    public static string RoleDisplay(string? role) => role switch
    {
        "Pdo" => "ПДО",
        "Technologist" => "Технолог",
        "Accountant" => "Учетник",
        "Operator" => "Оператор плазмы",
        "Master" => "Мастер",
        "Director" => "Директор",
        "Admin" => "Администратор",
        _ => "Без роли"
    };

    //пока поставили роль технолога для удобства разработки
    public UserRole CurrentRole { get; set; } = UserRole.Technologist;

    //кто может планировать заказы (ПДО и Админ)
    public bool CanManagePlan => CurrentRole is UserRole.Pdo or UserRole.Admin;
    //кто может загружать раскрои и работать с файлами (Технолог, Админ)
    public bool CanManageNesting => CurrentRole is UserRole.Technologist or UserRole.Admin;
    //кто может управлять отходами и складом (учетник, админ)
    public bool CanManageStock => CurrentRole is UserRole.Accountant or UserRole.Admin;
    //кто может отпралять раскрой на плазму и запускать резку (плазменщик. технолог, админ)
    public bool CanOperatePlasma => CurrentRole is UserRole.Operator or UserRole.Technologist or UserRole.Admin;
    //кто может просматривать панель (ПДО, оператор, технолог, учетник, админ)
    public bool CanSeeDashBoard => CurrentRole is UserRole.Accountant or UserRole.Admin or UserRole.Operator or UserRole.Pdo or UserRole.Technologist;
    //кто вписывает причины недореза (мастер, админ) — отдельная вкладка "Мастер"
    public bool CanSeeMasterPage => CurrentRole is UserRole.Master or UserRole.Admin;
    //кто видит сводную статистику по срокам/причинам (директор, админ) — вкладка "Метрики", только чтение
    public bool CanSeeMetrics => CurrentRole is UserRole.Director or UserRole.Admin;
    //кто может просматривать базу с dxf для аджан (технолог, админ)
    public bool CanSeeBaseForAjan => CurrentRole is UserRole.Technologist or UserRole.Admin;
    //кто может составлять и редактировать ведомости (технолог, админ)
    public bool CanManageStatement => CurrentRole is UserRole.Technologist or UserRole.Admin;
    //настройки + заявки на регистрацию (технолог — парсинг ведомостей, админ — ещё и пользователи)
    public bool CanSeeSettings => CurrentRole is UserRole.Technologist or UserRole.Admin;
    // кто может удалять планы и карты раскроя (только админ — необратимая операция)
    public bool CanDeleteData => CurrentRole is UserRole.Admin;
}
