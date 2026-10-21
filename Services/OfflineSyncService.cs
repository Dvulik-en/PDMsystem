using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using Npgsql;
using BKMsystem.Models;

namespace BKMsystem.Services;

// Типы отложенных операций для локальной очереди
public static class OpTypes
{
    public const string ClearOrder = "clear_order";
    public const string SaveDetails = "save_details";
    public const string ReplaceStatement = "replace_statement";
    public const string SavePlan = "save_plan";
    public const string GenPrograms = "gen_programs";
    public const string NestingUpload = "nesting_upload";   // комплект раскроя (УП/PDF/XLSX)
    public const string NestingStatus = "nesting_status";   // смена статуса карты (Выполнен/Новый)
}

// Одна отложенная операция записи в БД
public class PendingOperation
{
    public string OpType { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// Деталь + её положение в иерархии (заказ/изделие/узел)
public class DetailRowDto
{
    public Detail Detail { get; set; } = new();
    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UnitName { get; set; } = "";
}

/// <summary>Норма трудоёмкости на плазменную резку для одного узла (Заказ/Изделие/Узел) —
/// из листа "Трудоёмкость" ведомости. Относится к узлу целиком, не к отдельной детали.</summary>
public class UnitLaborNormDto
{
    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UnitName { get; set; } = "";
    public double PlasmaCuttingSeconds { get; set; }
}

public class ReplaceStatementPayload
{
    public List<string> OrdersToClear { get; set; } = new();
    public List<DetailRowDto> Rows { get; set; } = new();
    public List<UnitLaborNormDto> LaborNorms { get; set; } = new();
}

public class GenProgramsPayload
{
    public int TaskId { get; set; }
    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UnitName { get; set; } = "";
}

/// <summary>
/// Локальная очередь записей + фоновая синхронизация с сервером.
/// Если сеть пропала — операции записи складываются в JSON-файл на диске,
/// при восстановлении связи автоматически проигрываются на сервер в исходном порядке.
/// Также хранит локальный кэш результатов чтения, чтобы деревья открывались оффлайн.
/// </summary>
public static class OfflineSyncService
{
    private static readonly string _dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BKMsystem");
    private static readonly string _queueFile = Path.Combine(_dataDir, "pending_ops.json");
    private static readonly string _cacheDir = Path.Combine(_dataDir, "cache");

    private static readonly object _fileLock = new();
    private static bool _started;
    private static volatile bool _isOnline = true;

    /// <summary>Текущее состояние связи с сервером БД.</summary>
    public static bool IsOnline => _isOnline;

    /// <summary>Срабатывает при смене состояния связи (true = онлайн). Вызывается в UI-потоке.</summary>
    public static event Action<bool>? OnConnectionStateChanged;

    /// <summary>Срабатывает при изменении числа отложенных операций. Вызывается в UI-потоке.</summary>
    public static event Action<int>? OnPendingCountChanged;

    /// <summary>Информационные сообщения синхронизации (для статус-строки). Вызывается в UI-потоке.</summary>
    public static event Action<string>? OnSyncMessage;

    /// <summary>Запускает фоновый цикл синхронизации (однократно, потокобезопасно).</summary>
    public static void EnsureStarted()
    {
        lock (_fileLock)
        {
            if (_started) return;
            _started = true;
        }

        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_cacheDir);

        Task.Run(SyncLoopAsync);
    }

    public static int PendingCount
    {
        get { lock (_fileLock) { return LoadQueueUnsafe().Count; } }
    }

    /// <summary>Помечает соединение как разорванное (вызывается из методов записи при сбое).</summary>
    public static void MarkOffline() => SetOnline(false);

    public static void SetOnline(bool value)
    {
        if (_isOnline == value) return;
        _isOnline = value;
        RaiseOnUi(() => OnConnectionStateChanged?.Invoke(value));
    }

    /// <summary>Добавляет операцию в локальную очередь (переживает перезапуск приложения).</summary>
    public static void Enqueue(PendingOperation op)
    {
        int count;
        lock (_fileLock)
        {
            var queue = LoadQueueUnsafe();
            queue.Add(op);
            SaveQueueUnsafe(queue);
            count = queue.Count;
        }
        RaiseOnUi(() => OnPendingCountChanged?.Invoke(count));
    }

    // ---------- Локальный кэш чтения (чтобы UI работал без сети) ----------

    public static void SaveSnapshot<T>(string key, T data)
    {
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(_cacheDir);
                File.WriteAllText(Path.Combine(_cacheDir, key + ".json"), JsonSerializer.Serialize(data));
            }
        }
        catch { /* кэш — необязательная штука, ошибки игнорируем */ }
    }

    public static T? LoadSnapshot<T>(string key)
    {
        try
        {
            lock (_fileLock)
            {
                string path = Path.Combine(_cacheDir, key + ".json");
                if (!File.Exists(path)) return default;
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            }
        }
        catch { return default; }
    }

    // ---------- Фоновый цикл синхронизации ----------

    private static async Task SyncLoopAsync()
    {
        while (true)
        {
            try
            {
                bool canConnect = await CanConnectAsync();
                SetOnline(canConnect);

                if (canConnect)
                {
                    List<PendingOperation> queue;
                    lock (_fileLock) { queue = LoadQueueUnsafe(); }

                    if (queue.Count > 0)
                    {
                        RaiseOnUi(() => OnSyncMessage?.Invoke($"Синхронизация: отправка {queue.Count} отложенных операций..."));

                        var db = new DatabaseService();
                        var repo = new NestingRepository();
                        var remaining = new List<PendingOperation>();
                        bool failed = false;

                        foreach (var op in queue)
                        {
                            if (failed)
                            {
                                remaining.Add(op);
                                continue;
                            }
                            try
                            {
                                await Task.Run(() => ExecuteOp(db, repo, op));
                            }
                            catch
                            {
                                // Связь оборвалась посреди синхронизации — операцию и хвост очереди оставляем
                                failed = true;
                                remaining.Add(op);
                                SetOnline(false);
                            }
                        }

                        lock (_fileLock) { SaveQueueUnsafe(remaining); }

                        if (remaining.Count == 0)
                        {
                            RaiseOnUi(() =>
                            {
                                OnPendingCountChanged?.Invoke(0);
                                OnSyncMessage?.Invoke("Все локально сохранённые данные отправлены на сервер.");
                            });
                        }
                        else
                        {
                            RaiseOnUi(() => OnPendingCountChanged?.Invoke(remaining.Count));
                        }
                    }
                }
            }
            catch { /* цикл не должен умирать */ }

            await Task.Delay(10000); // проверка связи каждые 10 секунд
        }
    }

    private static void ExecuteOp(DatabaseService db, NestingRepository repo, PendingOperation op)
    {
        switch (op.OpType)
        {
            case OpTypes.ClearOrder:
                db.ClearOrderDetailsDirect(JsonSerializer.Deserialize<string>(op.PayloadJson) ?? "");
                break;

            case OpTypes.SaveDetails:
                db.SaveRowsDirect(JsonSerializer.Deserialize<List<DetailRowDto>>(op.PayloadJson) ?? new());
                break;

            case OpTypes.ReplaceStatement:
                var payload = JsonSerializer.Deserialize<ReplaceStatementPayload>(op.PayloadJson) ?? new();
                db.ReplaceStatementDirect(payload.OrdersToClear, payload.Rows, payload.LaborNorms);
                break;

            case OpTypes.SavePlan:
                var entry = JsonSerializer.Deserialize<PlanEntry>(op.PayloadJson) ?? new PlanEntry();
                int taskId = db.SavePlanEntryDirect(entry);
                db.SaveCuttingProgramsForTaskDirect(taskId, entry.OrderName, entry.ProductName, entry.UnitName);
                break;

            case OpTypes.GenPrograms:
                var gen = JsonSerializer.Deserialize<GenProgramsPayload>(op.PayloadJson) ?? new();
                db.SaveCuttingProgramsForTaskDirect(gen.TaskId, gen.OrderName, gen.ProductName, gen.UnitName);
                break;

            case OpTypes.NestingUpload:
                var dto = JsonSerializer.Deserialize<NestingSheetDto>(op.PayloadJson) ?? new();
                repo.SavePackageDirect(dto);
                break;

            case OpTypes.NestingStatus:
                var st = JsonSerializer.Deserialize<NestingStatusPayload>(op.PayloadJson) ?? new();
                repo.SetStatusDirect(st.SheetId, st.Status);
                break;
        }
    }

    private static async Task<bool> CanConnectAsync()
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(DatabaseService.ConnectionString)
            {
                Timeout = 3
            };
            using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- Работа с файлом очереди (только под _fileLock) ----------

    private static List<PendingOperation> LoadQueueUnsafe()
    {
        try
        {
            if (!File.Exists(_queueFile)) return new List<PendingOperation>();
            return JsonSerializer.Deserialize<List<PendingOperation>>(File.ReadAllText(_queueFile))
                   ?? new List<PendingOperation>();
        }
        catch
        {
            return new List<PendingOperation>();
        }
    }

    private static void SaveQueueUnsafe(List<PendingOperation> queue)
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(_queueFile, JsonSerializer.Serialize(queue));
    }

    private static void RaiseOnUi(Action action)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) action();
            else Dispatcher.UIThread.Post(action);
        }
        catch { /* приложение может быть ещё не инициализировано */ }
    }
}
