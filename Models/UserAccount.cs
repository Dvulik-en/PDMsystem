namespace BKMsystem.Models;

/// <summary>Учётная запись пользователя (таблица users).</summary>
public class UserAccount
{
    public int Id { get; set; }
    public string Login { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Role { get; set; } = "";      // Pdo / Technologist / Accountant / Operator / Admin ("" = роль ещё не назначена)
    public bool IsApproved { get; set; }
    public string CreatedAt { get; set; } = "";
}
