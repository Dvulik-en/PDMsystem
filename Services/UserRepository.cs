using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using BKMsystem.Models;

namespace BKMsystem.Services;

/// <summary>
/// Учётные записи: регистрация, вход, подтверждение роли администратором.
/// Пароль хранится как SHA-256 хэш (login + пароль), открытым текстом нигде не пишется.
/// Оффлайн-вход работает по локальному кэшу учёток, которые уже входили онлайн на этом ПК.
/// Первая зарегистрированная учётка автоматически становится Администратором
/// (иначе некому будет подтверждать остальных).
/// </summary>
public class UserRepository
{
    private static bool _tableReady;
    private static readonly string _cacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BKMsystem", "auth_cache.json");

    public static string HashPassword(string login, string password)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(login.Trim().ToLowerInvariant() + ":" + password));
        return Convert.ToHexString(bytes);
    }

    public void EnsureTable()
    {
        if (_tableReady) return;
        using var conn = new NpgsqlConnection(DatabaseService.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS users (
                Id BIGSERIAL PRIMARY KEY,
                Login TEXT UNIQUE NOT NULL,
                FullName TEXT NOT NULL,
                PasswordHash TEXT NOT NULL,
                Role TEXT DEFAULT '',
                IsApproved INTEGER DEFAULT 0,
                CreatedAt TEXT
            );";
        cmd.ExecuteNonQuery();
        _tableReady = true;
    }

    /// <summary>Регистрация: первая учётка -> сразу Админ, остальные ждут назначения роли.</summary>
    public (bool Ok, string Message) Register(string login, string fullName, string password)
    {
        login = login.Trim();
        fullName = fullName.Trim();

        if (string.IsNullOrWhiteSpace(login) || login.Length < 3)
            return (false, "Логин должен быть не короче 3 символов.");
        if (string.IsNullOrWhiteSpace(fullName) || !fullName.Contains(' '))
            return (false, "Укажите фамилию и имя (через пробел).");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
            return (false, "Пароль должен быть не короче 4 символов.");

        try
        {
            EnsureTable();
            using var conn = new NpgsqlConnection(DatabaseService.ConnectionString);
            conn.Open();

            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM users WHERE Login = @l;";
                check.Parameters.AddWithValue("@l", login);
                if ((long)check.ExecuteScalar()! > 0)
                    return (false, "Такой логин уже зарегистрирован.");
            }

            bool isFirst;
            using (var count = conn.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM users;";
                isFirst = (long)count.ExecuteScalar()! == 0;
            }

            using var ins = conn.CreateCommand();
            ins.CommandText = @"
                INSERT INTO users (Login, FullName, PasswordHash, Role, IsApproved, CreatedAt)
                VALUES (@l, @f, @h, @r, @a, @c);";
            ins.Parameters.AddWithValue("@l", login);
            ins.Parameters.AddWithValue("@f", fullName);
            ins.Parameters.AddWithValue("@h", HashPassword(login, password));
            ins.Parameters.AddWithValue("@r", isFirst ? "Admin" : "");
            ins.Parameters.AddWithValue("@a", isFirst ? 1 : 0);
            ins.Parameters.AddWithValue("@c", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            ins.ExecuteNonQuery();

            return (true, isFirst
                ? "Вы зарегистрированы как Администратор — можете входить."
                : "Заявка отправлена. Дождитесь, пока администратор назначит вам роль (вкладка «Настройки»).");
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            return (false, "Нет связи с сервером. Регистрация возможна только при подключении к базе.");
        }
        catch (Exception ex)
        {
            return (false, "Ошибка регистрации: " + ex.Message);
        }
    }

    /// <summary>Вход. Онлайн — проверка по базе (учётка кэшируется для оффлайн-входа).</summary>
    public (UserAccount? User, string Message) TryLogin(string login, string password)
    {
        login = login.Trim();
        string hash = HashPassword(login, password);

        try
        {
            EnsureTable();
            using var conn = new NpgsqlConnection(DatabaseService.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Login, FullName, Role, IsApproved, CreatedAt FROM users WHERE Login = @l AND PasswordHash = @h;";
            cmd.Parameters.AddWithValue("@l", login);
            cmd.Parameters.AddWithValue("@h", hash);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                return (null, "Неверный логин или пароль.");

            var user = ReadUser(r);
            if (!user.IsApproved)
                return (null, "Ваша заявка ещё не подтверждена администратором.");

            CacheCredentials(user, hash);
            return (user, "");
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            // Оффлайн-вход по локальному кэшу (только для тех, кто уже входил на этом ПК)
            var cached = TryLoginOffline(login, hash);
            return cached != null
                ? (cached, "")
                : (null, "Нет связи с сервером. Оффлайн-вход доступен только для учёток, которые уже входили на этом компьютере.");
        }
    }

    public List<UserAccount> GetPending()
    {
        var list = new List<UserAccount>();
        try
        {
            EnsureTable();
            using var conn = new NpgsqlConnection(DatabaseService.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Login, FullName, Role, IsApproved, CreatedAt FROM users WHERE IsApproved = 0 ORDER BY Id;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadUser(r));
        }
        catch { }
        return list;
    }

    public List<UserAccount> GetAllApproved()
    {
        var list = new List<UserAccount>();
        try
        {
            EnsureTable();
            using var conn = new NpgsqlConnection(DatabaseService.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Login, FullName, Role, IsApproved, CreatedAt FROM users WHERE IsApproved = 1 ORDER BY FullName;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadUser(r));
        }
        catch { }
        return list;
    }

    /// <summary>Админ назначает роль — заявка становится активной.</summary>
    public bool Approve(int userId, string role)
    {
        try
        {
            EnsureTable();
            using var conn = new NpgsqlConnection(DatabaseService.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE users SET Role = @r, IsApproved = 1 WHERE Id = @id;";
            cmd.Parameters.AddWithValue("@r", role);
            cmd.Parameters.AddWithValue("@id", userId);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch { return false; }
    }

    private static UserAccount ReadUser(NpgsqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Login = r.GetString(1),
        FullName = r.GetString(2),
        Role = r.IsDBNull(3) ? "" : r.GetString(3),
        IsApproved = r.GetInt32(4) == 1,
        CreatedAt = r.IsDBNull(5) ? "" : r.GetString(5)
    };

    // ---------- локальный кэш учёток для оффлайн-входа ----------

    private class CachedCred
    {
        public string Login { get; set; } = "";
        public string Hash { get; set; } = "";
        public UserAccount User { get; set; } = new();
    }

    private static void CacheCredentials(UserAccount user, string hash)
    {
        try
        {
            var list = LoadCache();
            list.RemoveAll(c => c.Login.Equals(user.Login, StringComparison.OrdinalIgnoreCase));
            list.Add(new CachedCred { Login = user.Login, Hash = hash, User = user });
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            File.WriteAllText(_cacheFile, JsonSerializer.Serialize(list));
        }
        catch { }
    }

    private static UserAccount? TryLoginOffline(string login, string hash)
    {
        return LoadCache()
            .FirstOrDefault(c => c.Login.Equals(login, StringComparison.OrdinalIgnoreCase) && c.Hash == hash)
            ?.User;
    }

    private static List<CachedCred> LoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFile)) return new List<CachedCred>();
            return JsonSerializer.Deserialize<List<CachedCred>>(File.ReadAllText(_cacheFile)) ?? new();
        }
        catch { return new List<CachedCred>(); }
    }
}
