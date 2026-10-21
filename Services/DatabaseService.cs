using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Threading;
using Npgsql;
using BKMsystem.Models;

namespace BKMsystem.Services;

public class DatabaseService
{
    private static readonly string _connectionString = LoadConnectionString();
    internal static string ConnectionString => _connectionString;

    /// <summary>Строка подключения не хранится в коде (чтобы пароль не попадал в git).
    /// Порядок поиска: 1) переменная окружения BKM_DB_CONNECTION;
    /// 2) файл appsettings.local.json рядом с программой, поле "ConnectionString".</summary>
    private static string LoadConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("BKM_DB_CONNECTION");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("ConnectionString", out var cs)
                && cs.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(cs.GetString()))
                return cs.GetString()!;
        }

        throw new InvalidOperationException(
            "Не задана строка подключения к базе. Создайте файл appsettings.local.json рядом с программой " +
            "(см. appsettings.example.json) или задайте переменную окружения BKM_DB_CONNECTION.");
    }

    public static event Action<string>? OnDatabaseUpdated;
    private static bool _isListeningStarted = false;
    private static readonly object _listenerLock = new();

    public DatabaseService()
    {
        // Автоматически запускаем прослушиватель и оффлайн-синхронизацию при первом обращении
        StartGlobalListener();
        OfflineSyncService.EnsureStarted();
    }
    /// <summary>
    /// Вызывает локальное событие обновления таблиц для всех ViewModel
    /// </summary>
    public static void NotifyDatabaseUpdated(string tableName)
    {
        OnDatabaseUpdated?.Invoke(tableName);
    }
    // Проверка, что ошибка — это именно проблема с сетью/соединением, а не логическая ошибка SQL
    /// <summary>
    /// ВАЖНО: PostgresException (реальная ошибка САМОГО запроса — опечатка в SQL, нехватка
    /// нормы, несовпадение типов и т.п.) — это НЕ обрыв связи с сервером. PostgresException
    /// является подклассом NpgsqlException, поэтому раньше он тоже подпадал под общее правило
    /// и тихо трактовался как "нет сети": метод-обёртка просто возвращал пустой/старый
    /// результат, как будто мы оффлайн, а настоящая причина (баг в запросе) терялась
    /// НАСОВСЕМ и не оставляла следа — например, недорезанные остатки могли не показываться
    /// из-за реальной ошибки запроса, а не из-за того, что их действительно нет. Поэтому
    /// PostgresException явно исключаем из "обрыва связи" — такие ошибки должны быть видны
    /// (см. try/catch в конкретных методах, которые их логируют), а не маскироваться под офлайн.
    /// </summary>
    internal static bool IsConnectionError(Exception ex)
    {
        if (ex is PostgresException) return false;

        return ex is NpgsqlException
            or TimeoutException
            or System.Net.Sockets.SocketException
            or InvalidOperationException
            or EndOfStreamException
            or IOException;
    }

    // ------------------------------------------------------------------
    //  LISTEN / NOTIFY — глобальный прослушиватель обновлений таблиц
    // ------------------------------------------------------------------
    private static void StartGlobalListener()
    {
        lock (_listenerLock)
        {
            if (_isListeningStarted) return;
            _isListeningStarted = true;
        }

        Task.Run(async () =>
        {
            var builder = new NpgsqlConnectionStringBuilder(_connectionString)
            {
                CommandTimeout = 0,
                KeepAlive = 15
            };

            while (true)
            {
                try
                {
                    using var listenConn = new NpgsqlConnection(builder.ConnectionString);
                    await listenConn.OpenAsync();

                    listenConn.Notification += (sender, e) =>
                    {
                        _ = Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            OnDatabaseUpdated?.Invoke(e.Payload);
                        });
                    };

                    using (var cmd = new NpgsqlCommand("LISTEN table_updated;", listenConn))
                    {
                        cmd.CommandTimeout = 0;
                        await cmd.ExecuteNonQueryAsync();
                    }

                    while (listenConn.State == System.Data.ConnectionState.Open)
                    {
                        await listenConn.WaitAsync();
                    }
                }
                catch
                {
                    // Обрыв сети / сервер недоступен — переподключаемся
                    OfflineSyncService.MarkOffline();
                    await Task.Delay(3000);
                }
            }
        });
    }

    public NpgsqlConnection CreateConnection()
    {
        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    // ------------------------------------------------------------------
    //  Инициализация: таблицы + функция и триггеры NOTIFY
    //  (без триггеров LISTEN никогда не получал уведомлений!)
    // ------------------------------------------------------------------
    public void InitializeDatabase()
    {
        try
        {
            using (var connection = CreateConnection())
            {
                var command = connection.CreateCommand();
                command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Details (
                    Id BIGSERIAL PRIMARY KEY,
                    DrawingNumber TEXT, Name TEXT, SteelGrade TEXT, Thickness DOUBLE PRECISION,
                    Dimensions TEXT, Quantity INTEGER, OrderName TEXT, ProductName TEXT,
                    UnitName TEXT, DetailType TEXT, Profile TEXT, Length DOUBLE PRECISION
                );

                CREATE TABLE IF NOT EXISTS PlannedTasks (
                    Id BIGSERIAL PRIMARY KEY,
                    OrderName TEXT NOT NULL, ProductName TEXT, UnitName TEXT,
                    PlannedDate TEXT NOT NULL, SheetFormat TEXT, IsUrgent INTEGER, Comment TEXT
                );

                -- Норма трудоёмкости, введённая/посчитанная ВРУЧНУЮ прямо при постановке
                -- (используется для ""срочной дорезки"" — там нет строки в ведомости и, значит,
                -- нет соответствующей записи в UnitLaborNorms). NULL для обычных заданий —
                -- у них норма по-прежнему берётся из UnitLaborNorms по Заказ/Изделие/Узел.
                ALTER TABLE PlannedTasks ADD COLUMN IF NOT EXISTS ManualNormSeconds DOUBLE PRECISION;

                CREATE TABLE IF NOT EXISTS CuttingPrograms (
                    Id BIGSERIAL PRIMARY KEY,
                    TaskId INTEGER NOT NULL,
                    Material TEXT NOT NULL,
                    Thickness DOUBLE PRECISION DEFAULT 0,
                    Cipher TEXT,
                    Length DOUBLE PRECISION,
                    Width DOUBLE PRECISION,
                    ProgramCode TEXT NOT NULL,
                    Quantity INTEGER DEFAULT 1,
                    IsReady INTEGER DEFAULT 0
                );

                -- Трудоёмкость плазменной резки на узел (Заказ/Изделие/Узел) — из листа
                -- ""Трудоёмкость"" ведомости. Отдельная таблица, а не колонка в Details,
                -- потому что относится к узлу ЦЕЛИКОМ, а не к отдельной детали.
                CREATE TABLE IF NOT EXISTS UnitLaborNorms (
                    OrderName TEXT NOT NULL,
                    ProductName TEXT NOT NULL,
                    UnitName TEXT NOT NULL,
                    PlasmaCuttingSeconds DOUBLE PRECISION NOT NULL,
                    PRIMARY KEY (OrderName, ProductName, UnitName)
                );

                -- Разбивка одного планового задания (PlannedTasks) по нескольким дням.
                -- Гибко: любое число дней, любое распределение часов между ними (не только
                -- ""пополам на 2 дня"") — задаётся списком (дата, выделенные секунды) целиком
                -- через SetTaskSegments. Если у задания нет строк здесь — оно целиком стоит
                -- на своей PlannedDate (старое поведение, как до этой фичи).
                CREATE TABLE IF NOT EXISTS PlanTaskSegments (
                    Id BIGSERIAL PRIMARY KEY,
                    TaskId INTEGER NOT NULL REFERENCES PlannedTasks(Id) ON DELETE CASCADE,
                    PlannedDate TEXT NOT NULL,
                    AllocatedSeconds DOUBLE PRECISION NOT NULL,
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    UNIQUE (TaskId, PlannedDate)
                );

                -- На случай, если таблица уже существовала до добавления колонки
                -- (старые базы, поднятые до этой правки).
                ALTER TABLE PlanTaskSegments ADD COLUMN IF NOT EXISTS SortOrder INTEGER NOT NULL DEFAULT 0;

                -- Разрешение на вторую смену (17:00–22:00) для КОНКРЕТНОГО дня. По умолчанию
                -- рабочий день режется ровно по 17:00 — то, что не влезло, остаётся
                -- ""недорезанным остатком"" в правой панели плиток, а не расползается само по
                -- себе на вторую смену. ПДО явно включает смену кнопкой на колонке дня.
                CREATE TABLE IF NOT EXISTS PlanDaySettings (
                    PlannedDate TEXT PRIMARY KEY,
                    Shift2Enabled INTEGER NOT NULL DEFAULT 0
                );

                -- Нормы резки на плазме из собственной базы АДЖАН (скорость реза и время
                -- прожига по марке стали / толщине / режиму тока-газа) — используются, чтобы
                -- прикинуть время СРОЧНОЙ дорезки детали, у которой ещё нет готовой нормы в
                -- ведомости (UnitLaborNorms). Наполняется один раз при первом запуске из
                -- CncCuttingNormsSeedData (см. SeedCncCuttingNormsIfEmpty) — дальше её можно
                -- будет донабрать/поправить прямо в БД, ничего не пересобирая.
                CREATE TABLE IF NOT EXISTS CncCuttingNorms (
                    Id BIGSERIAL PRIMARY KEY,
                    SteelGrade TEXT NOT NULL,
                    AmpMode TEXT NOT NULL,
                    ThicknessMm DOUBLE PRECISION NOT NULL,
                    CuttingSpeedMmPerMin DOUBLE PRECISION NOT NULL,
                    PiercingTimeSec DOUBLE PRECISION NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_cncnorms_grade_thickness ON CncCuttingNorms (SteelGrade, ThicknessMm);

                -- Функция, отправляющая NOTIFY с именем изменённой таблицы
                CREATE OR REPLACE FUNCTION notify_table_update() RETURNS trigger AS $func$
                BEGIN
                    PERFORM pg_notify('table_updated', lower(TG_TABLE_NAME));
                    RETURN NULL;
                END;
                $func$ LANGUAGE plpgsql;

                -- Триггеры на все рабочие таблицы (FOR EACH STATEMENT — одно уведомление на запрос)
                DROP TRIGGER IF EXISTS trg_notify_details ON Details;
                CREATE TRIGGER trg_notify_details
                    AFTER INSERT OR UPDATE OR DELETE ON Details
                    FOR EACH STATEMENT EXECUTE FUNCTION notify_table_update();

                DROP TRIGGER IF EXISTS trg_notify_planned ON PlannedTasks;
                CREATE TRIGGER trg_notify_planned
                    AFTER INSERT OR UPDATE OR DELETE ON PlannedTasks
                    FOR EACH STATEMENT EXECUTE FUNCTION notify_table_update();

                DROP TRIGGER IF EXISTS trg_notify_cutting ON CuttingPrograms;
                CREATE TRIGGER trg_notify_cutting
                    AFTER INSERT OR UPDATE OR DELETE ON CuttingPrograms
                    FOR EACH STATEMENT EXECUTE FUNCTION notify_table_update();

                DROP TRIGGER IF EXISTS trg_notify_labornorms ON UnitLaborNorms;
                CREATE TRIGGER trg_notify_labornorms
                    AFTER INSERT OR UPDATE OR DELETE ON UnitLaborNorms
                    FOR EACH STATEMENT EXECUTE FUNCTION notify_table_update();

                DROP TRIGGER IF EXISTS trg_notify_segments ON PlanTaskSegments;
                CREATE TRIGGER trg_notify_segments
                    AFTER INSERT OR UPDATE OR DELETE ON PlanTaskSegments
                    FOR EACH STATEMENT EXECUTE FUNCTION notify_table_update();

                DROP TRIGGER IF EXISTS trg_notify_daysettings ON PlanDaySettings;
                CREATE TRIGGER trg_notify_daysettings
                    AFTER INSERT OR UPDATE OR DELETE ON PlanDaySettings
                    FOR EACH STATEMENT EXECUTE FUNCTION notify_table_update();";
                command.ExecuteNonQuery();
            }
            OfflineSyncService.SetOnline(true);
            SeedCncCuttingNormsIfEmpty();
            BackfillDetailsCuttingLength();
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            // Нет связи — таблицы создадутся при следующем удачном запуске/синхронизации
            OfflineSyncService.MarkOffline();
        }
    }

    /// <summary>Разово заполняет CncCuttingNorms из встроенного набора данных (снят с реальной
    /// базы CNC-раскроя завода, машина "Plasma") — только если таблица ещё пустая, чтобы не
    /// плодить дубликаты при каждом запуске и не затирать ручные правки/довнесённые строки.</summary>
    private void SeedCncCuttingNormsIfEmpty()
    {
        try
        {
            using var connection = CreateConnection();

            var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM CncCuttingNorms;";
            long existing = (long)checkCmd.ExecuteScalar()!;
            if (existing > 0) return;

            using var transaction = connection.BeginTransaction();
            foreach (var row in CncCuttingNormsSeedData.Rows)
            {
                var ins = connection.CreateCommand();
                ins.Transaction = transaction;
                ins.CommandText = @"
                    INSERT INTO CncCuttingNorms (SteelGrade, AmpMode, ThicknessMm, CuttingSpeedMmPerMin, PiercingTimeSec)
                    VALUES (@grade, @amp, @thickness, @speed, @pierce);";
                ins.Parameters.AddWithValue("@grade", row.SteelGrade);
                ins.Parameters.AddWithValue("@amp", row.AmpMode);
                ins.Parameters.AddWithValue("@thickness", row.ThicknessMm);
                ins.Parameters.AddWithValue("@speed", row.CuttingSpeedMmPerMin);
                ins.Parameters.AddWithValue("@pierce", row.PiercingTimeSec);
                ins.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            // Нет связи — досеется при следующем удачном запуске.
        }
    }

    /// <summary>Пересчитывает Details.Length из уже сохранённого текста Details.Dimensions по
    /// актуальной формуле (см. ExcelParserService.ComputeCuttingLength): раньше при импорте
    /// длина реза бралась как ПОСЛЕДНЕЕ число в строке размеров, из-за чего для круглых деталей
    /// вида "Ø115" (одно число) она всегда получалась 0 — это и заметил технолог на детали
    /// "Заглушка" при расчёте срочной дорезки. Формула детерминирована и зависит только от уже
    /// сохранённого Dimensions, поэтому безопасно гонять на каждом запуске (самолечение при
    /// доработке формулы в будущем), а не только один раз — лишних дублей это не создаёт,
    /// строки не добавляются, только числится колонка Length. Обновляем только реально
    /// изменившиеся строки, чтобы не поднимать зря триггеры NOTIFY на всей таблице Details.</summary>
    private void BackfillDetailsCuttingLength()
    {
        try
        {
            using var connection = CreateConnection();

            var selectCmd = connection.CreateCommand();
            selectCmd.CommandText = "SELECT Id, Dimensions, Length FROM Details;";
            var updates = new List<(long Id, double NewLength)>();
            using (var reader = selectCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    long id = reader.GetInt64(0);
                    string dims = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    double oldLength = reader.IsDBNull(2) ? 0 : reader.GetDouble(2);
                    double newLength = ExcelParserService.ComputeCuttingLength(dims);
                    if (Math.Abs(newLength - oldLength) > 0.01)
                        updates.Add((id, newLength));
                }
            }
            if (updates.Count == 0) return;

            using var transaction = connection.BeginTransaction();
            foreach (var (id, newLength) in updates)
            {
                var upd = connection.CreateCommand();
                upd.Transaction = transaction;
                upd.CommandText = "UPDATE Details SET Length = @len WHERE Id = @id;";
                upd.Parameters.AddWithValue("@len", newLength);
                upd.Parameters.AddWithValue("@id", id);
                upd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            // Нет связи — пересчитается при следующем удачном запуске.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[План] BackfillDetailsCuttingLength: ошибка запроса — {ex}");
            Console.Error.WriteLine($"[План] BackfillDetailsCuttingLength: ошибка запроса — {ex}");
        }
    }

    public void InitPlanTable() => InitializeDatabase();
    public void InitCuttingProgramsTable() => InitializeDatabase();

    // ------------------------------------------------------------------
    //  ЗАПИСЬ: публичные методы с оффлайн-поддержкой
    //  Возвращают true — записано на сервер сразу, false — ушло в локальную очередь.
    // ------------------------------------------------------------------

    public bool ClearOrderDetails(string orderName)
    {
        if (string.IsNullOrWhiteSpace(orderName)) return true;

        if (OfflineSyncService.IsOnline)
        {
            try
            {
                ClearOrderDetailsDirect(orderName);
                return true;
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                OfflineSyncService.MarkOffline();
            }
        }

        OfflineSyncService.Enqueue(new PendingOperation
        {
            OpType = OpTypes.ClearOrder,
            PayloadJson = JsonSerializer.Serialize(orderName)
        });
        return false;
    }

    /// <summary>Старая сигнатура — обёртка для совместимости.</summary>
    public bool SaveDetails(List<Detail> details, string orderName, string productName, string unitName)
    {
        var rows = details.Select(d => new DetailRowDto
        {
            Detail = d,
            OrderName = orderName,
            ProductName = productName,
            UnitName = unitName
        }).ToList();
        return SaveRows(rows);
    }

    /// <summary>Пакетное сохранение деталей (одна транзакция вместо транзакции на каждую деталь).</summary>
    public bool SaveRows(List<DetailRowDto> rows)
    {
        if (rows.Count == 0) return true;

        if (OfflineSyncService.IsOnline)
        {
            try
            {
                SaveRowsDirect(rows);
                OfflineSyncService.SetOnline(true);
                return true;
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                OfflineSyncService.MarkOffline();
            }
        }

        OfflineSyncService.Enqueue(new PendingOperation
        {
            OpType = OpTypes.SaveDetails,
            PayloadJson = JsonSerializer.Serialize(rows)
        });
        return false;
    }

    /// <summary>
    /// Атомарная замена ведомостей: очистка указанных заказов + вставка всех деталей
    /// (+ норм трудоёмкости по узлам) в ОДНОЙ транзакции. При сбое — не остаётся
    /// «полупустых» заказов.
    /// </summary>
    public bool ReplaceStatement(List<string> ordersToClear, List<DetailRowDto> rows, List<UnitLaborNormDto> laborNorms)
    {
        if (OfflineSyncService.IsOnline)
        {
            try
            {
                ReplaceStatementDirect(ordersToClear, rows, laborNorms);
                OfflineSyncService.SetOnline(true);
                return true;
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                OfflineSyncService.MarkOffline();
            }
        }

        OfflineSyncService.Enqueue(new PendingOperation
        {
            OpType = OpTypes.ReplaceStatement,
            PayloadJson = JsonSerializer.Serialize(new ReplaceStatementPayload
            {
                OrdersToClear = ordersToClear,
                Rows = rows,
                LaborNorms = laborNorms
            })
        });
        return false;
    }

    /// <summary>Старая сигнатура (без норм трудоёмкости) — оставлена для совместимости
    /// с уже скомпилированным кодом/локальной очередью старого формата.</summary>
    public bool ReplaceStatement(List<string> ordersToClear, List<DetailRowDto> rows)
        => ReplaceStatement(ordersToClear, rows, new List<UnitLaborNormDto>());

    /// <summary>
    /// Сохранение задания планирования.
    /// Возвращает Id задания (> 0) при записи на сервер или -1, если задание
    /// сохранено локально и будет отправлено при восстановлении связи
    /// (программы раскроя при этом сгенерируются автоматически на этапе синхронизации).
    /// </summary>
    public int SavePlanEntry(PlanEntry entry)
    {
        if (OfflineSyncService.IsOnline)
        {
            try
            {
                int id = SavePlanEntryDirect(entry);
                OfflineSyncService.SetOnline(true);
                return id;
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                OfflineSyncService.MarkOffline();
            }
        }

        OfflineSyncService.Enqueue(new PendingOperation
        {
            OpType = OpTypes.SavePlan,
            PayloadJson = JsonSerializer.Serialize(entry)
        });
        return -1;
    }

    public bool SaveCuttingProgramsForTask(int taskId, string orderName, string productName, string unitName)
    {
        if (OfflineSyncService.IsOnline)
        {
            try
            {
                SaveCuttingProgramsForTaskDirect(taskId, orderName, productName, unitName);
                return true;
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                OfflineSyncService.MarkOffline();
            }
        }

        OfflineSyncService.Enqueue(new PendingOperation
        {
            OpType = OpTypes.GenPrograms,
            PayloadJson = JsonSerializer.Serialize(new GenProgramsPayload
            {
                TaskId = taskId,
                OrderName = orderName,
                ProductName = productName,
                UnitName = unitName
            })
        });
        return false;
    }

    // ------------------------------------------------------------------
    //  ЗАПИСЬ: прямые методы (вызываются при живом соединении и при синхронизации)
    // ------------------------------------------------------------------

    internal void ClearOrderDetailsDirect(string orderName)
    {
        using (var connection = CreateConnection())
        {
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Details WHERE OrderName = @order;";
            command.Parameters.AddWithValue("@order", orderName);
            command.ExecuteNonQuery();
        }
    }

    internal void SaveRowsDirect(List<DetailRowDto> rows)
    {
        using (var connection = CreateConnection())
        {
            InsertRows(connection, null, rows);
        }
    }

    internal void ReplaceStatementDirect(List<string> ordersToClear, List<DetailRowDto> rows, List<UnitLaborNormDto> laborNorms)
    {
        using (var connection = CreateConnection())
        {
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var order in ordersToClear.Distinct())
                {
                    var del = connection.CreateCommand();
                    del.Transaction = transaction;
                    del.CommandText = "DELETE FROM Details WHERE OrderName = @order;";
                    del.Parameters.AddWithValue("@order", order);
                    del.ExecuteNonQuery();

                    var delNorms = connection.CreateCommand();
                    delNorms.Transaction = transaction;
                    delNorms.CommandText = "DELETE FROM UnitLaborNorms WHERE OrderName = @order;";
                    delNorms.Parameters.AddWithValue("@order", order);
                    delNorms.ExecuteNonQuery();
                }

                InsertRows(connection, transaction, rows);
                UpsertLaborNormsRows(connection, transaction, laborNorms);

                transaction.Commit();
            }
        }
    }

    /// <summary>Старая сигнатура (без норм трудоёмкости) — для обратной совместимости
    /// с уже сериализованными в локальной очереди операциями старого формата.</summary>
    internal void ReplaceStatementDirect(List<string> ordersToClear, List<DetailRowDto> rows)
        => ReplaceStatementDirect(ordersToClear, rows, new List<UnitLaborNormDto>());

    private static void InsertRows(NpgsqlConnection connection, NpgsqlTransaction? transaction, List<DetailRowDto> rows)
    {
        foreach (var row in rows)
        {
            var detail = row.Detail;
            var command = connection.CreateCommand();
            command.Transaction = transaction;

            command.CommandText = @"
                INSERT INTO Details (
                    DrawingNumber, Name, SteelGrade, Thickness, Dimensions,
                    Quantity, OrderName, ProductName, UnitName, DetailType, Profile, Length
                )
                VALUES (
                    @drawing, @name, @steel, @thickness, @dims,
                    @qty, @order, @product, @unit, @type, @profile, @length
                );";

            command.Parameters.AddWithValue("@drawing", detail.DrawingNumber ?? "");
            command.Parameters.AddWithValue("@name", detail.Name ?? "");
            command.Parameters.AddWithValue("@steel", detail.SteelGrade ?? "");
            command.Parameters.AddWithValue("@thickness", detail.Thickness);
            command.Parameters.AddWithValue("@dims", detail.Dimensions ?? "");
            command.Parameters.AddWithValue("@qty", detail.Quantity);
            command.Parameters.AddWithValue("@order", string.IsNullOrWhiteSpace(row.OrderName) ? "Без заказа" : row.OrderName);
            command.Parameters.AddWithValue("@product", string.IsNullOrWhiteSpace(row.ProductName) ? "Без категории" : row.ProductName);
            command.Parameters.AddWithValue("@unit", string.IsNullOrWhiteSpace(row.UnitName) ? "Общий узел" : row.UnitName);
            command.Parameters.AddWithValue("@type", detail.DetailType ?? "Лист");
            command.Parameters.AddWithValue("@profile", detail.Profile ?? "");
            command.Parameters.AddWithValue("@length", detail.Length);

            command.ExecuteNonQuery();
        }
    }

    /// <summary>Пишет (или обновляет) нормы трудоёмкости на плазменную резку по узлам.
    /// ON CONFLICT — потому что в одном заказе теоретически может встретиться повтор
    /// ключа (Заказ/Изделие/Узел), а падать транзакцией из-за этого не нужно.</summary>
    private static void UpsertLaborNormsRows(NpgsqlConnection connection, NpgsqlTransaction? transaction, List<UnitLaborNormDto> norms)
    {
        foreach (var n in norms)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO UnitLaborNorms (OrderName, ProductName, UnitName, PlasmaCuttingSeconds)
                VALUES (@order, @product, @unit, @seconds)
                ON CONFLICT (OrderName, ProductName, UnitName)
                DO UPDATE SET PlasmaCuttingSeconds = EXCLUDED.PlasmaCuttingSeconds;";

            command.Parameters.AddWithValue("@order", string.IsNullOrWhiteSpace(n.OrderName) ? "Без заказа" : n.OrderName);
            command.Parameters.AddWithValue("@product", string.IsNullOrWhiteSpace(n.ProductName) ? "Без категории" : n.ProductName);
            command.Parameters.AddWithValue("@unit", string.IsNullOrWhiteSpace(n.UnitName) ? "Общий узел" : n.UnitName);
            command.Parameters.AddWithValue("@seconds", n.PlasmaCuttingSeconds);
            command.ExecuteNonQuery();
        }
    }

    internal int SavePlanEntryDirect(PlanEntry entry)
    {
        using (var connection = CreateConnection())
        {
            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO PlannedTasks (OrderName, ProductName, UnitName, PlannedDate, SheetFormat, IsUrgent, Comment, ManualNormSeconds)
                VALUES (@order, @product, @unit, @date, @sheet, @urgent, @comment, @manualNorm)
                RETURNING Id;";

            command.Parameters.AddWithValue("@order", entry.OrderName);
            command.Parameters.AddWithValue("@product", entry.ProductName);
            command.Parameters.AddWithValue("@unit", entry.UnitName);
            command.Parameters.AddWithValue("@date", entry.PlannedDate.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("@sheet", entry.SheetFormat);
            command.Parameters.AddWithValue("@urgent", entry.IsUrgent ? 1 : 0);
            command.Parameters.AddWithValue("@comment", entry.Comment);
            command.Parameters.AddWithValue("@manualNorm", (object?)entry.ManualNormSeconds ?? DBNull.Value);

            long newId = (long)command.ExecuteScalar()!;
            return (int)newId;
        }
    }

    internal void SaveCuttingProgramsForTaskDirect(int taskId, string orderName, string productName, string unitName)
    {
        using (var connection = CreateConnection())
        {
            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT SteelGrade, Thickness, DrawingNumber, Length, Quantity
                FROM Details
                WHERE OrderName = @order
                AND (@product = 'Все изделия' OR ProductName = @product)
                AND (@unit = 'Весь узел' OR UnitName = @unit);";

            command.Parameters.AddWithValue("@order", orderName);
            command.Parameters.AddWithValue("@product", productName);
            command.Parameters.AddWithValue("@unit", unitName);

            var programsToInsert = new List<(string Material, double Thickness, string Cipher, double Length, double Width, string Code, int Qty)>();

            using (var reader = command.ExecuteReader())
            {
                int codeIndex = 1;
                while (reader.Read())
                {
                    string steel = reader.IsDBNull(0) ? "Ст3" : reader.GetString(0);
                    double thickness = reader.IsDBNull(1) ? 4.0 : reader.GetDouble(1);
                    string cipher = reader.IsDBNull(2) ? "ДЕТ-01" : reader.GetString(2);
                    double length = reader.IsDBNull(3) ? 1000 : reader.GetDouble(3);
                    int qty = reader.IsDBNull(4) ? 1 : reader.GetInt32(4);

                    programsToInsert.Add((
                        Material: steel,
                        Thickness: thickness,
                        Cipher: cipher,
                        Length: length,
                        Width: 500,
                        Code: $"NC-{taskId:D3}-{codeIndex++:D2}",
                        Qty: qty
                    ));
                }
            }

            if (programsToInsert.Count == 0)
            {
                programsToInsert.Add(("09Г2С", 4.0, "ПЛ-01-4мм", 1200, 600, $"NC-{taskId:D3}-01", 12));
                programsToInsert.Add(("Ст3", 8.0, "ПЛ-02-8мм", 850, 400, $"NC-{taskId:D3}-02", 4));
                programsToInsert.Add(("Ст3", 8.0, "ПЛ-03-8мм", 500, 300, $"NC-{taskId:D3}-03", 8));
            }

            using (var transaction = connection.BeginTransaction())
            {
                foreach (var item in programsToInsert)
                {
                    var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = @"
                        INSERT INTO CuttingPrograms (TaskId, Material, Thickness, Cipher, Length, Width, ProgramCode, Quantity, IsReady)
                        VALUES (@taskId, @mat, @thick, @cipher, @len, @width, @code, @qty, 0);";

                    insertCmd.Parameters.AddWithValue("@taskId", taskId);
                    insertCmd.Parameters.AddWithValue("@mat", item.Material);
                    insertCmd.Parameters.AddWithValue("@thick", item.Thickness);
                    insertCmd.Parameters.AddWithValue("@cipher", item.Cipher);
                    insertCmd.Parameters.AddWithValue("@len", item.Length);
                    insertCmd.Parameters.AddWithValue("@width", item.Width);
                    insertCmd.Parameters.AddWithValue("@code", item.Code);
                    insertCmd.Parameters.AddWithValue("@qty", item.Qty);

                    insertCmd.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }
    }

    // ------------------------------------------------------------------
    //  ЧТЕНИЕ: при отсутствии связи возвращают локальный кэш последнего
    //  успешного запроса, чтобы интерфейс продолжал работать оффлайн.
    // ------------------------------------------------------------------

    public List<StatementItem> GetStatementTreeFromDb()
    {
        try
        {
            var result = ReadStatementTreeDirect();
            OfflineSyncService.SetOnline(true);
            OfflineSyncService.SaveSnapshot("statement_tree", result);
            return result;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            OfflineSyncService.MarkOffline();
            return OfflineSyncService.LoadSnapshot<List<StatementItem>>("statement_tree")
                   ?? new List<StatementItem>();
        }
    }

    public ObservableCollection<PlanTreeNode> GetPlanTreeFromDb()
    {
        try
        {
            var result = ReadPlanTreeDirect();
            OfflineSyncService.SetOnline(true);
            OfflineSyncService.SaveSnapshot("plan_tree", result);
            return result;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            OfflineSyncService.MarkOffline();
            var cached = OfflineSyncService.LoadSnapshot<List<PlanTreeNode>>("plan_tree");
            return cached != null
                ? new ObservableCollection<PlanTreeNode>(cached)
                : new ObservableCollection<PlanTreeNode>();
        }
    }

    public ObservableCollection<DashboardOrderItem> GetDashboardDataForDate(DateTime date)
    {
        try
        {
            var result = ReadDashboardDirect(date);
            OfflineSyncService.SetOnline(true);
            OfflineSyncService.SaveSnapshot("dashboard_last", result);
            return result;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            OfflineSyncService.MarkOffline();
            var cached = OfflineSyncService.LoadSnapshot<List<DashboardOrderItem>>("dashboard_last");
            return cached != null
                ? new ObservableCollection<DashboardOrderItem>(cached)
                : new ObservableCollection<DashboardOrderItem>();
        }
    }

    /// <summary>
    /// Норма времени на плазменную резку узла (из листа "Трудоёмкость" ведомости), для
    /// шкалы заполнения в календаре плана. null, если для узла норма не сохранена (ведомость
    /// без листа "Трудоёмкость" либо узел ещё не импортирован).
    /// </summary>
    public TimeSpan? GetPlasmaCuttingTime(string orderName, string productName, string unitName)
    {
        try
        {
            using var connection = CreateConnection();
            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT PlasmaCuttingSeconds FROM UnitLaborNorms
                WHERE OrderName = @order AND ProductName = @product AND UnitName = @unit
                LIMIT 1;";
            command.Parameters.AddWithValue("@order", orderName);
            command.Parameters.AddWithValue("@product", productName);
            command.Parameters.AddWithValue("@unit", unitName);

            var result = command.ExecuteScalar();
            return result is double seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Поиск детали для срочной дорезки — прямо по уже импортированной ведомости (Details),
    /// по названию или номеру чертежа. Возвращает марку стали/толщину/суммарную длину реза —
    /// этого достаточно, чтобы прикинуть время резки по CncCuttingNorms, не вводя ничего
    /// заново вручную.
    /// </summary>
    public List<DetailSearchResult> SearchDetails(string query, int limit = 25)
    {
        try
        {
            using var connection = CreateConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT DrawingNumber, Name, SteelGrade, Thickness, Length, OrderName, ProductName, UnitName
                FROM Details
                WHERE (@q = '' OR Name ILIKE '%' || @q || '%' OR DrawingNumber ILIKE '%' || @q || '%')
                ORDER BY Name
                LIMIT @limit;";
            cmd.Parameters.AddWithValue("@q", query ?? "");
            cmd.Parameters.AddWithValue("@limit", limit);

            var result = new List<DetailSearchResult>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new DetailSearchResult
                {
                    DrawingNumber = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    SteelGrade = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Thickness = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                    LengthMm = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                    OrderName = reader.IsDBNull(5) ? "" : reader.GetString(5),
                    ProductName = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    UnitName = reader.IsDBNull(7) ? "" : reader.GetString(7)
                });
            }
            return result;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            return new List<DetailSearchResult>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[План] SearchDetails: ошибка запроса — {ex}");
            Console.Error.WriteLine($"[План] SearchDetails: ошибка запроса — {ex}");
            return new List<DetailSearchResult>();
        }
    }

    /// <summary>
    /// Марка стали в Details записана НЕ чистым обозначением, а составной строкой вида
    /// "Лист г/к 4мм,09Г2С" (формат листа + толщина + марка через запятую) — так она пришла
    /// из ведомости. Искать по такой строке в CncCuttingNorms напрямую бессмысленно, там марка
    /// хранится чисто ("09Г2С"). Ищем среди уже известных в CncCuttingNorms марок ту, что
    /// целиком входит в эту составную строку — и если совпадений несколько (напр. "Сталь2" внутри
    /// "Сталь20"), берём самое длинное, чтобы не промахнуться на более короткой подстроке.
    /// Возвращает null, если ни одна известная марка не нашлась — тогда время придётся вписать
    /// вручную, ничего не подставляя наугад.
    /// </summary>
    public string? NormalizeSteelGradeForCncNorms(string rawSteelGrade)
    {
        if (string.IsNullOrWhiteSpace(rawSteelGrade)) return null;

        try
        {
            using var connection = CreateConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT SteelGrade FROM CncCuttingNorms;";

            var grades = new List<string>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read()) grades.Add(reader.GetString(0));
            }

            string raw = rawSteelGrade.Trim();
            return grades
                .Where(g => raw.IndexOf(g, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(g => g.Length)
                .FirstOrDefault();
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[План] NormalizeSteelGradeForCncNorms: ошибка запроса — {ex}");
            Console.Error.WriteLine($"[План] NormalizeSteelGradeForCncNorms: ошибка запроса — {ex}");
            return null;
        }
    }

    /// <summary>
    /// Варианты нормы резки для марки стали и толщины — из CncCuttingNorms (заводская база
    /// АДЖАН). Может вернуть НЕСКОЛЬКО вариантов (разный ток/газ) для одной и той же толщины —
    /// какой из них реально стоит на станке сейчас, решает технолог при постановке дорезки, а
    /// не автоматика (см. CncNormOption). Если точной толщины нет вообще, возвращает ближайшие
    /// найденные (IsExactThickness = false), чтобы не оставлять технолога совсем без ориентира.
    /// </summary>
    public List<CncNormOption> GetCncNormOptions(string steelGrade, double thicknessMm)
    {
        try
        {
            using var connection = CreateConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT AmpMode, ThicknessMm, CuttingSpeedMmPerMin, PiercingTimeSec
                FROM CncCuttingNorms
                WHERE SteelGrade = @grade
                ORDER BY ABS(ThicknessMm - @thickness), AmpMode;";
            cmd.Parameters.AddWithValue("@grade", steelGrade ?? "");
            cmd.Parameters.AddWithValue("@thickness", thicknessMm);

            var all = new List<(string Amp, double Thickness, double Speed, double Pierce)>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    all.Add((reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3)));
                }
            }

            if (all.Count == 0) return new List<CncNormOption>();

            const double epsilonMm = 0.05;
            double closestThickness = all.Min(r => Math.Abs(r.Thickness - thicknessMm));
            bool hasExact = closestThickness <= epsilonMm;

            return all
                .Where(r => hasExact ? Math.Abs(r.Thickness - thicknessMm) <= epsilonMm
                                      : Math.Abs(r.Thickness - thicknessMm) <= closestThickness + epsilonMm)
                .Select(r => new CncNormOption
                {
                    AmpMode = r.Amp,
                    ThicknessMm = r.Thickness,
                    CuttingSpeedMmPerMin = r.Speed,
                    PiercingTimeSec = r.Pierce,
                    IsExactThickness = hasExact
                })
                .ToList();
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            return new List<CncNormOption>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[План] GetCncNormOptions: ошибка запроса — {ex}");
            Console.Error.WriteLine($"[План] GetCncNormOptions: ошибка запроса — {ex}");
            return new List<CncNormOption>();
        }
    }

    /// <summary>Норма трудоёмкости резки уже СУЩЕСТВУЮЩЕГО задания по его TaskId (та же норма,
    /// что и GetPlasmaCuttingTime по Заказ/Изделие/Узел, только не нужно заново знать эти три
    /// строки — просто джойн PlannedTasks к UnitLaborNorms по Id). Используется при
    /// растягивании плитки: сжать/растянуть НА ЭТОТ день можно не только в пределах вместимости
    /// дня, но и в пределах того, что реально осталось резать по норме ВСЕГО задания (норма
    /// минус уже расписанное на другие дни) — иначе можно было бы растянуть плитку с фиксированным
    /// временем резки сильнее, чем у неё есть работы, просто потому что в дне есть свободное место.</summary>
    public TimeSpan? GetTaskLaborNorm(int taskId)
    {
        try
        {
            using var connection = CreateConnection();
            var cmd = connection.CreateCommand();
            // COALESCE с ManualNormSeconds — та же логика, что и в GetLeftoverTasks: у срочной
            // дорезки нет строки в ведомости/UnitLaborNorms, норма введена технологом вручную
            // прямо на задании.
            cmd.CommandText = @"
                SELECT COALESCE(uln.PlasmaCuttingSeconds, pt.ManualNormSeconds)
                FROM PlannedTasks pt
                LEFT JOIN UnitLaborNorms uln
                    ON uln.OrderName = pt.OrderName AND uln.ProductName = pt.ProductName AND uln.UnitName = pt.UnitName
                WHERE pt.Id = @id
                LIMIT 1;";
            cmd.Parameters.AddWithValue("@id", taskId);

            var result = cmd.ExecuteScalar();
            return result is double seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[План] GetTaskLaborNorm: ошибка запроса — {ex}");
            Console.Error.WriteLine($"[План] GetTaskLaborNorm: ошибка запроса — {ex}");
            return null;
        }
    }

    // ------------------------------------------------------------------
    //  Разбивка задания по дням (гибкая — любое число дней, любые пропорции)
    // ------------------------------------------------------------------

    /// <summary>Полностью заменяет распределение задания по дням заданным списком
    /// (дата, выделенное время). Ничего не навязывает по числу дней или пропорциям —
    /// список формирует вызывающая сторона (например, PlanSplitService.EvenSplit для
    /// равномерного распределения, либо руками — под будущий drag&amp;drop в календаре).</summary>
    public bool SetTaskSegments(int taskId, List<(DateTime Date, TimeSpan Duration)> segments)
    {
        if (OfflineSyncService.IsOnline)
        {
            try
            {
                SetTaskSegmentsDirect(taskId, segments);
                OfflineSyncService.SetOnline(true);
                return true;
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                OfflineSyncService.MarkOffline();
            }
        }
        // Разбивка — это правка уже существующего задания, а не первичная запись,
        // поэтому в офлайн-очередь пока не ставим: при отсутствии связи ПДО увидит
        // явное сообщение и сможет повторить действие, когда сеть вернётся.
        return false;
    }

    internal void SetTaskSegmentsDirect(int taskId, List<(DateTime Date, TimeSpan Duration)> segments)
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();

        var del = connection.CreateCommand();
        del.Transaction = transaction;
        del.CommandText = "DELETE FROM PlanTaskSegments WHERE TaskId = @taskId;";
        del.Parameters.AddWithValue("@taskId", taskId);
        del.ExecuteNonQuery();

        foreach (var seg in segments)
        {
            string dateStr = seg.Date.ToString("yyyy-MM-dd");

            // Новый сегмент этого задания встаёт В КОНЕЦ очереди дня (после уже стоящих там
            // плиток других заданий), а не всегда на первое место — иначе при каждом
            // растягивании/сжатии задание перепрыгивало бы вперёд всех соседей по дню.
            var maxOrderCmd = connection.CreateCommand();
            maxOrderCmd.Transaction = transaction;
            maxOrderCmd.CommandText = "SELECT COALESCE(MAX(SortOrder), -1) FROM PlanTaskSegments WHERE PlannedDate = @date;";
            maxOrderCmd.Parameters.AddWithValue("@date", dateStr);
            int nextOrder = Convert.ToInt32(maxOrderCmd.ExecuteScalar()) + 1;

            var ins = connection.CreateCommand();
            ins.Transaction = transaction;
            ins.CommandText = @"
                INSERT INTO PlanTaskSegments (TaskId, PlannedDate, AllocatedSeconds, SortOrder)
                VALUES (@taskId, @date, @seconds, @order);";
            ins.Parameters.AddWithValue("@taskId", taskId);
            ins.Parameters.AddWithValue("@date", dateStr);
            ins.Parameters.AddWithValue("@seconds", seg.Duration.TotalSeconds);
            ins.Parameters.AddWithValue("@order", nextOrder);
            ins.ExecuteNonQuery();
        }

        // PlannedTasks.PlannedDate держим равной самому раннему дню разбивки — старый код
        // (FindTaskId, генерация программ раскроя и т.п.) про сегменты не знает и продолжает
        // читать эту колонку как "день задания".
        if (segments.Count > 0)
        {
            string minDate = segments.Min(s => s.Date).ToString("yyyy-MM-dd");
            var upd = connection.CreateCommand();
            upd.Transaction = transaction;
            upd.CommandText = "UPDATE PlannedTasks SET PlannedDate = @date WHERE Id = @taskId;";
            upd.Parameters.AddWithValue("@date", minDate);
            upd.Parameters.AddWithValue("@taskId", taskId);
            upd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Текущая разбивка задания по дням. Пустой список = задание ещё не растянуто
    /// (стоит целиком на своей PlannedDate).</summary>
    public List<(DateTime Date, TimeSpan Duration)> GetTaskSegments(int taskId)
    {
        try
        {
            using var connection = CreateConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT PlannedDate, AllocatedSeconds FROM PlanTaskSegments WHERE TaskId = @taskId ORDER BY PlannedDate;";
            cmd.Parameters.AddWithValue("@taskId", taskId);

            var result = new List<(DateTime, TimeSpan)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                DateTime.TryParse(reader.GetString(0), out var date);
                result.Add((date, TimeSpan.FromSeconds(reader.GetDouble(1))));
            }
            return result;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            return new List<(DateTime, TimeSpan)>();
        }
    }

    /// <summary>Все плитки одного дня — по всем заказам, в порядке SortOrder (сверху вниз на
    /// шкале). На этом строится визуальный дневной таймлайн (DayColumnView).</summary>
    public List<DaySegmentInfo> GetDaySegments(DateTime date)
    {
        try
        {
            using var connection = CreateConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT s.TaskId, pt.OrderName, pt.ProductName, pt.UnitName, s.AllocatedSeconds, s.SortOrder, pt.IsUrgent
                FROM PlanTaskSegments s
                JOIN PlannedTasks pt ON pt.Id = s.TaskId
                WHERE s.PlannedDate = @date
                ORDER BY s.SortOrder, s.Id;";
            cmd.Parameters.AddWithValue("@date", date.ToString("yyyy-MM-dd"));

            var result = new List<DaySegmentInfo>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new DaySegmentInfo
                {
                    TaskId = reader.GetInt32(0),
                    OrderName = reader.GetString(1),
                    ProductName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    UnitName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Allocated = TimeSpan.FromSeconds(reader.GetDouble(4)),
                    SortOrder = reader.GetInt32(5),
                    IsUrgent = reader.GetInt32(6) == 1
                });
            }
            return result;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            return new List<DaySegmentInfo>();
        }
    }

    /// <summary>Перезаписывает порядок плиток дня целиком (после перетаскивания в UI).
    /// taskIdsInNewOrder — список TaskId в новом порядке сверху вниз.</summary>
    public void SetSegmentSortOrders(DateTime date, List<int> taskIdsInNewOrder)
    {
        try
        {
            using var connection = CreateConnection();
            using var transaction = connection.BeginTransaction();

            string dateStr = date.ToString("yyyy-MM-dd");
            for (int i = 0; i < taskIdsInNewOrder.Count; i++)
            {
                var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE PlanTaskSegments SET SortOrder = @order WHERE TaskId = @taskId AND PlannedDate = @date;";
                cmd.Parameters.AddWithValue("@order", i);
                cmd.Parameters.AddWithValue("@taskId", taskIdsInNewOrder[i]);
                cmd.Parameters.AddWithValue("@date", dateStr);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            // Порядок плиток — второстепенная правка, при обрыве связи просто не сохраняем;
            // UI откатит визуальный порядок на следующем обновлении из БД.
        }
    }

    /// <summary>Разрешена ли вторая смена (17:00–22:00) на конкретный день. По умолчанию
    /// (нет строки в PlanDaySettings) — false: день режется ровно по 17:00.</summary>
    public bool GetShift2Enabled(DateTime date)
    {
        try
        {
            using var connection = CreateConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Shift2Enabled FROM PlanDaySettings WHERE PlannedDate = @date;";
            cmd.Parameters.AddWithValue("@date", date.ToString("yyyy-MM-dd"));
            var result = cmd.ExecuteScalar();
            return result != null && result != DBNull.Value && Convert.ToInt32(result) == 1;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            return false;
        }
    }

    /// <summary>Включает/выключает вторую смену для конкретного дня — кнопка на колонке
    /// календаря ("Вывести 2-ю смену"). Пока не нажата, добавить время после 17:00 на этот
    /// день нельзя — избыток остаётся недорезанным остатком в правой панели.</summary>
    public void SetShift2Enabled(DateTime date, bool enabled)
    {
        try
        {
            using var connection = CreateConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO PlanDaySettings (PlannedDate, Shift2Enabled) VALUES (@date, @enabled)
                ON CONFLICT (PlannedDate) DO UPDATE SET Shift2Enabled = @enabled;";
            cmd.Parameters.AddWithValue("@date", date.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            // Второстепенная настройка — при обрыве связи просто не сохраняем.
        }
    }

    /// <summary>Есть ли хоть одна запись плана на эту дату — нужен WorkTimeCalculator, чтобы
    /// решить, считать ли субботу/воскресенье рабочим днём: будни рабочие всегда, а выходной —
    /// только если на него реально что-то запланировано (ПДО перетащил плитку на календарь).
    /// Никакой отдельной галочки "рабочий выходной" заводить не пришлось — этот же факт уже
    /// виден по наличию плана. Статический метод (без экземпляра DatabaseService) — вызывается
    /// и из NestingRepository, у которого своего DatabaseService нет.</summary>
    public static bool HasPlanOnDateStatic(DateTime date)
    {
        try
        {
            using var connection = new NpgsqlConnection(ConnectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM PlannedTasks WHERE PlannedDate = @date LIMIT 1;";
            cmd.Parameters.AddWithValue("@date", date.ToString("yyyy-MM-dd"));
            return cmd.ExecuteScalar() != null;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            return false;
        }
    }

    /// <summary>Статический двойник GetShift2Enabled — та же таблица/логика, но без экземпляра
    /// DatabaseService, для тех же внешних вызовов (WorkTimeCalculator из NestingRepository).</summary>
    public static bool GetShift2EnabledStatic(DateTime date)
    {
        try
        {
            using var connection = new NpgsqlConnection(ConnectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Shift2Enabled FROM PlanDaySettings WHERE PlannedDate = @date;";
            cmd.Parameters.AddWithValue("@date", date.ToString("yyyy-MM-dd"));
            var result = cmd.ExecuteScalar();
            return result != null && result != DBNull.Value && Convert.ToInt32(result) == 1;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Задания, у которых норма трудоёмкости (лист "Трудоёмкость") больше, чем сейчас реально
    /// расписано по дням (PlanTaskSegments) — то есть время где-то "обрезали" (сжали плитку,
    /// упёрлись в 17:00, перенесли на другой день не влезло целиком) и остаток ещё не пристроен
    /// никуда. Показываются в правой панели отдельным разделом "Недорезано" — их можно
    /// перетащить на любой день, как и обычную ещё не запланированную позицию.
    /// </summary>
    public List<LeftoverTaskInfo> GetLeftoverTasks()
    {
        try
        {
            using var connection = CreateConnection();
            var cmd = connection.CreateCommand();
            // LEFT JOIN (не JOIN) на UnitLaborNorms — у срочной дорезки нет строки в
            // ведомости, а значит и в UnitLaborNorms, но у нас есть введённая технологом
            // норма прямо на самом задании (PlannedTasks.ManualNormSeconds). COALESCE
            // берёт норму из ведомости, если она есть, иначе — введённую вручную; WHERE
            // отсекает задания, у которых нет вообще никакой нормы (им нечего показывать
            // как остаток — это старое поведение для заданий без "Трудоёмкости").
            cmd.CommandText = @"
                SELECT pt.Id, pt.OrderName, pt.ProductName, pt.UnitName,
                       COALESCE(uln.PlasmaCuttingSeconds, pt.ManualNormSeconds) AS Norm,
                       COALESCE(SUM(s.AllocatedSeconds), 0) AS Allocated
                FROM PlannedTasks pt
                LEFT JOIN UnitLaborNorms uln
                    ON uln.OrderName = pt.OrderName AND uln.ProductName = pt.ProductName AND uln.UnitName = pt.UnitName
                LEFT JOIN PlanTaskSegments s ON s.TaskId = pt.Id
                WHERE COALESCE(uln.PlasmaCuttingSeconds, pt.ManualNormSeconds) IS NOT NULL
                GROUP BY pt.Id, pt.OrderName, pt.ProductName, pt.UnitName, uln.PlasmaCuttingSeconds, pt.ManualNormSeconds
                HAVING COALESCE(uln.PlasmaCuttingSeconds, pt.ManualNormSeconds) > COALESCE(SUM(s.AllocatedSeconds), 0) + 1;";

            var result = new List<LeftoverTaskInfo>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                double norm = reader.GetDouble(4);
                double allocated = reader.GetDouble(5);
                result.Add(new LeftoverTaskInfo
                {
                    TaskId = reader.GetInt32(0),
                    OrderName = reader.GetString(1),
                    ProductName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    UnitName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    RemainingSeconds = norm - allocated
                });
            }
            return result;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            return new List<LeftoverTaskInfo>();
        }
        catch (Exception ex)
        {
            // Настоящая ошибка запроса (не обрыв связи) — раньше она пряталась под "оффлайн"
            // и остатки просто не показывались без единого следа. Теперь текст ошибки идёт
            // и в Debug-вывод (видно при отладке из Visual Studio), и в стандартный поток
            // ошибок консоли — чтобы при следующей жалобе можно было сразу увидеть причину,
            // а не гадать по описанию симптомов.
            System.Diagnostics.Debug.WriteLine($"[План] GetLeftoverTasks: ошибка запроса — {ex}");
            Console.Error.WriteLine($"[План] GetLeftoverTasks: ошибка запроса — {ex}");
            return new List<LeftoverTaskInfo>();
        }
    }

    /// <summary>Полностью удаляет плановое задание (каскадно удалит и все его
    /// PlanTaskSegments) — узел возвращается в правую панель как обычная незапланированная
    /// позиция. Используется, когда с дня убирают последний/единственный сегмент задания.</summary>
    public void DeletePlannedTask(int taskId)
    {
        try
        {
            using var connection = CreateConnection();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM PlannedTasks WHERE Id = @id;";
            cmd.Parameters.AddWithValue("@id", taskId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            // Нет связи — задание останется висеть пустым (без сегментов) до следующей
            // успешной попытки; не критично, просто не покажет "полностью снятым" сразу.
        }
    }

    // ------------------------------------------------------------------
    //  ЧТЕНИЕ: прямые запросы
    // ------------------------------------------------------------------

    private List<StatementItem> ReadStatementTreeDirect()
    {
        var rootNodes = new List<StatementItem>();

        using (var connection = new NpgsqlConnection(_connectionString))
        {
            connection.Open();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT DrawingNumber, Name, SteelGrade, Thickness, Dimensions, Quantity,
                        OrderName, ProductName, UnitName, DetailType, Profile, Length
                    FROM Details;";

                var orderNodes = new Dictionary<string, StatementItem>();
                var productNodes = new Dictionary<string, StatementItem>();
                var unitNodes = new Dictionary<string, StatementItem>();

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string drawing = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        string name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        string steel = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        double thickness = reader.IsDBNull(3) ? 0 : reader.GetDouble(3);
                        string dims = reader.IsDBNull(4) ? "" : reader.GetString(4);
                        int qty = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);

                        string orderName = reader.IsDBNull(6) || string.IsNullOrWhiteSpace(reader.GetString(6)) ? "Заказ" : reader.GetString(6);
                        string productName = reader.IsDBNull(7) || string.IsNullOrWhiteSpace(reader.GetString(7)) ? "Без категории" : reader.GetString(7);
                        string unitName = reader.IsDBNull(8) || string.IsNullOrWhiteSpace(reader.GetString(8)) ? "Общий узел" : reader.GetString(8);

                        string detailType = reader.IsDBNull(9) ? "Лист" : reader.GetString(9);
                        string profile = reader.IsDBNull(10) ? "" : reader.GetString(10);
                        double length = reader.IsDBNull(11) ? 0 : reader.GetDouble(11);

                        var detail = new Detail
                        {
                            DrawingNumber = drawing,
                            Name = name,
                            SteelGrade = steel,
                            Dimensions = dims,
                            Quantity = qty,
                            DetailType = detailType,
                            Profile = profile,
                            Length = length
                        };

                        if (!orderNodes.TryGetValue(orderName, out var orderNode))
                        {
                            orderNode = new StatementItem { Title = orderName, Icon = "📁", IsGroup = true };
                            orderNodes[orderName] = orderNode;
                            rootNodes.Add(orderNode);
                        }

                        string productKey = $"{orderName}_{productName}";
                        if (!productNodes.TryGetValue(productKey, out var productNode))
                        {
                            productNode = new StatementItem { Title = productName, Icon = "📦", IsGroup = true };
                            productNodes[productKey] = productNode;
                            orderNode.Children.Add(productNode);
                        }

                        string unitKey = $"{productKey}_{unitName}";
                        if (!unitNodes.TryGetValue(unitKey, out var unitNode))
                        {
                            unitNode = new StatementItem { Title = unitName, Icon = "🧩", IsGroup = true };
                            unitNodes[unitKey] = unitNode;
                            productNode.Children.Add(unitNode);
                        }

                        string itemIcon = detailType switch
                        {
                            "Труба" => "⭕",
                            "Швеллер" or "Профиль" => "📐",
                            "Плазма" => "🔥",
                            _ => "🔧"
                        };

                        string displayTitle = string.IsNullOrWhiteSpace(detail.DrawingNumber)
                            ? detail.Name
                            : $"[{detail.DrawingNumber}]  {detail.Name}";

                        double displayThickness = thickness > 0 ? thickness : detail.Thickness;

                        unitNode.Children.Add(new StatementItem
                        {
                            Title = displayTitle,
                            Icon = itemIcon,
                            IsGroup = false,
                            DrawingNumber = detail.DrawingNumber,
                            Thickness = displayThickness > 0 ? displayThickness.ToString() : "",
                            SteelGrade = detail.SteelGrade,
                            Quantity = detail.Quantity,
                            DetailType = detailType,
                            Profile = profile,
                            Length = length,
                            DetailData = detail,
                            ProductGroupName = productName
                        });
                    }
                }

                // Подтягиваем нормы трудоёмкости на каждый узел одним запросом,
                // чтобы дерево ведомости и после перезагрузки из БД несло время резки.
                var laborCmd = connection.CreateCommand();
                laborCmd.CommandText = "SELECT OrderName, ProductName, UnitName, PlasmaCuttingSeconds FROM UnitLaborNorms;";
                using (var laborReader = laborCmd.ExecuteReader())
                {
                    while (laborReader.Read())
                    {
                        string orderName = laborReader.GetString(0);
                        string productName = laborReader.GetString(1);
                        string unitName = laborReader.GetString(2);
                        double seconds = laborReader.GetDouble(3);

                        string unitKey = $"{orderName}_{productName}_{unitName}";
                        if (unitNodes.TryGetValue(unitKey, out var unitNode))
                            unitNode.PlasmaCuttingTime = TimeSpan.FromSeconds(seconds);
                    }
                }
            }
        }

        return rootNodes;
    }

    private ObservableCollection<PlanTreeNode> ReadPlanTreeDirect()
    {
        var rootNodes = new ObservableCollection<PlanTreeNode>();

        using (var connection = CreateConnection())
        {
            // LEFT JOIN на PlanTaskSegments: если задание растянуто на несколько дней —
            // получаем ПО СТРОКЕ НА КАЖДЫЙ ДЕНЬ разбивки (и оно появится в дереве под каждой
            // датой, с указанием выделенных на неё часов). Если разбивки ещё нет — одна строка
            // с EffectiveDate = PlannedTasks.PlannedDate, как было раньше.
            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT pt.Id, pt.OrderName, pt.ProductName, pt.UnitName,
                       COALESCE(s.PlannedDate, pt.PlannedDate) AS EffectiveDate,
                       pt.SheetFormat, pt.IsUrgent, pt.Comment, s.AllocatedSeconds
                FROM PlannedTasks pt
                LEFT JOIN PlanTaskSegments s ON s.TaskId = pt.Id
                ORDER BY EffectiveDate DESC, pt.OrderName, pt.ProductName;";

            var rawRows = new List<(int TaskId, string OrderName, string ProductName, string UnitName, string PlannedDate, string SheetFormat, int IsUrgent, string Comment, double? AllocatedSeconds)>();

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    rawRows.Add((
                        reader.GetInt32(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? "" : reader.GetString(2),
                        reader.IsDBNull(3) ? "" : reader.GetString(3),
                        reader.GetString(4),
                        reader.IsDBNull(5) ? "" : reader.GetString(5),
                        reader.GetInt32(6),
                        reader.IsDBNull(7) ? "" : reader.GetString(7),
                        reader.IsDBNull(8) ? (double?)null : reader.GetDouble(8)
                    ));
                }
            }

            var dateGroups = rawRows.GroupBy(r => r.PlannedDate);

            foreach (var dateGroup in dateGroups)
            {
                var dateNode = new PlanTreeNode { Title = $"📅 {dateGroup.Key}", NodeType = "Date" };

                var orderGroups = dateGroup.GroupBy(r => r.OrderName);
                foreach (var orderGroup in orderGroups)
                {
                    var orderNode = new PlanTreeNode { Title = $"📦 Заказ: {orderGroup.Key}", NodeType = "Order" };

                    var productGroups = orderGroup.GroupBy(r => r.ProductName);
                    foreach (var productGroup in productGroups)
                    {
                        var productNode = new PlanTreeNode { Title = $"⚙️ Изделие: {productGroup.Key}", NodeType = "Product" };

                        foreach (var item in productGroup)
                        {
                            string urgentMark = item.IsUrgent == 1 ? "⚠️ [СРОЧНО] " : "";

                            // Если задание растянуто на несколько дней — показываем, сколько
                            // часов из скольких выделено именно на этот день.
                            string timeMark = "";
                            if (item.AllocatedSeconds.HasValue)
                            {
                                var allocated = TimeSpan.FromSeconds(item.AllocatedSeconds.Value);
                                timeMark = $" | {(int)allocated.TotalHours}ч {allocated.Minutes}м на этот день";
                            }

                            var unitNode = new PlanTreeNode
                            {
                                Title = $"🧩 Узел: {item.UnitName} | {urgentMark}Лист: {item.SheetFormat}{timeMark}",
                                NodeType = "Unit",
                                TaskId = item.TaskId
                            };
                            productNode.Children.Add(unitNode);
                        }
                        orderNode.Children.Add(productNode);
                    }
                    dateNode.Children.Add(orderNode);
                }
                rootNodes.Add(dateNode);
            }
        }

        return rootNodes;
    }

    private ObservableCollection<DashboardOrderItem> ReadDashboardDirect(DateTime date)
    {
        var result = new ObservableCollection<DashboardOrderItem>();
        string dateSql = date.ToString("yyyy-MM-dd");

        using (var connection = CreateConnection())
        {
            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Id, OrderName, ProductName, UnitName, Comment
                FROM PlannedTasks
                WHERE PlannedDate = @date;";
            command.Parameters.AddWithValue("@date", dateSql);

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    int taskId = reader.GetInt32(0);

                    var orderItem = new DashboardOrderItem
                    {
                        DateStr = date.ToString("dd.MM.yyyy"),
                        CustomerSolution = reader.GetString(1),
                        ProductTitle = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        SerialNumber = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        Quantity = 1,
                        Note = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        IsExpanded = true
                    };

                    var rawPrograms = new List<CuttingProgramItem>();

                    var progCmd = connection.CreateCommand();
                    progCmd.CommandText = @"
                        SELECT Material, Thickness, Cipher, Length, Width, ProgramCode, Quantity, IsReady
                        FROM CuttingPrograms
                        WHERE TaskId = @taskId;";
                    progCmd.Parameters.AddWithValue("@taskId", taskId);

                    using (var progReader = progCmd.ExecuteReader())
                    {
                        while (progReader.Read())
                        {
                            rawPrograms.Add(new CuttingProgramItem
                            {
                                Material = progReader.GetString(0),
                                Thickness = progReader.IsDBNull(1) ? 0 : progReader.GetDouble(1),
                                Cipher = progReader.IsDBNull(2) ? "" : progReader.GetString(2),
                                Length = progReader.IsDBNull(3) ? 0 : progReader.GetDouble(3),
                                Width = progReader.IsDBNull(4) ? 0 : progReader.GetDouble(4),
                                ProgramCode = progReader.GetString(5),
                                Quantity = progReader.IsDBNull(6) ? 1 : progReader.GetInt32(6),
                                IsReady = progReader.GetInt32(7) == 1
                            });
                        }
                    }

                    var grouped = rawPrograms
                        .GroupBy(p => new { p.Material, p.Thickness })
                        .OrderBy(g => g.Key.Thickness);

                    foreach (var group in grouped)
                    {
                        var groupModel = new ThicknessGroupModel
                        {
                            Material = group.Key.Material,
                            Thickness = group.Key.Thickness
                        };

                        foreach (var prog in group)
                        {
                            groupModel.Items.Add(prog);
                        }

                        orderItem.ThicknessGroups.Add(groupModel);
                    }

                    result.Add(orderItem);
                }
            }
        }

        return result;
    }

    public ObservableCollection<StatementItem> GetTechnologistPlanTree()
    {
        var rootNodes = new ObservableCollection<StatementItem>();

        using var connection = CreateConnection();

        // Выбираем детали, которые попали в плановые задания ПДО
        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT
                pt.PlannedDate,
                pt.OrderName,
                pt.ProductName,
                pt.UnitName,
                d.DrawingNumber,
                d.Name,
                d.SteelGrade,
                d.Thickness,
                d.Quantity
            FROM PlannedTasks pt
            JOIN Details d ON d.OrderName = pt.OrderName
                AND (pt.ProductName = 'Все изделия' OR d.ProductName = pt.ProductName)
                AND (pt.UnitName = 'Весь узел' OR d.UnitName = pt.UnitName)
            ORDER BY pt.PlannedDate DESC, pt.OrderName, pt.ProductName, pt.UnitName, d.Thickness;";

        using var reader = command.ExecuteReader();

        var dateNodes = new Dictionary<string, StatementItem>();
        var orderNodes = new Dictionary<string, StatementItem>();
        var productNodes = new Dictionary<string, StatementItem>();
        var unitNodes = new Dictionary<string, StatementItem>();

        while (reader.Read())
        {
            string dateStr = reader.GetString(0);
            string orderName = reader.GetString(1);
            string productName = reader.IsDBNull(2) || string.IsNullOrWhiteSpace(reader.GetString(2)) ? "Все изделия" : reader.GetString(2);
            string unitName = reader.IsDBNull(3) || string.IsNullOrWhiteSpace(reader.GetString(3)) ? "Весь узел" : reader.GetString(3);

            string drawing = reader.IsDBNull(4) ? "" : reader.GetString(4);
            string partName = reader.IsDBNull(5) ? "" : reader.GetString(5);
            string steel = reader.IsDBNull(6) ? "" : reader.GetString(6);
            double thickness = reader.IsDBNull(7) ? 0 : reader.GetDouble(7);
            int qty = reader.IsDBNull(8) ? 0 : reader.GetInt32(8);

            // 1. Уровень Даты
            if (!dateNodes.TryGetValue(dateStr, out var dateNode))
            {
                dateNode = new StatementItem { Title = $"📅 План на {dateStr}", Icon = "📅", IsGroup = true };
                dateNodes[dateStr] = dateNode;
                rootNodes.Add(dateNode);
            }

            // 2. Уровень Заказа
            string orderKey = $"{dateStr}_{orderName}";
            if (!orderNodes.TryGetValue(orderKey, out var orderNode))
            {
                orderNode = new StatementItem { Title = $"📦 Заказ: {orderName}", Icon = "📦", IsGroup = true };
                orderNodes[orderKey] = orderNode;
                dateNode.Children.Add(orderNode);
            }

            // 3. Уровень Изделия
            string productKey = $"{orderKey}_{productName}";
            if (!productNodes.TryGetValue(productKey, out var productNode))
            {
                productNode = new StatementItem { Title = $"Изделие: {productName}", Icon = "⚙️", IsGroup = true };
                productNodes[productKey] = productNode;
                orderNode.Children.Add(productNode);
            }

            // 4. Уровень Узла
            string unitKey = $"{productKey}_{unitName}";
            if (!unitNodes.TryGetValue(unitKey, out var unitNode))
            {
                unitNode = new StatementItem { Title = $"Узел: {unitName}", Icon = "🧩", IsGroup = true };
                unitNodes[unitKey] = unitNode;
                productNode.Children.Add(unitNode);
            }

            // 5. Уровень Детали
            unitNode.Children.Add(new StatementItem
            {
                Title = partName,
                DrawingNumber = drawing,
                SteelGrade = steel,
                Thickness = thickness > 0 ? thickness.ToString() : "",
                Quantity = qty,
                Icon = "📄",
                IsGroup = false
            });
        }

        return rootNodes;
    }
    public int? FindTaskId(string order, string product, string unit)
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            // ВАЖНО: order/product/unit сюда приходят уже очищенными через CleanTextFromEmoji()
            // (эмодзи/префиксы "Заказ: " убраны), а PlannedTasks хранит "сырые" названия из
            // ведомости как есть — точное сравнение строк в SQL никогда бы не совпало, и TaskId
            // молча оставался бы NULL. Поэтому забираем кандидатов и сравниваем в C#, очищая
            // обе стороны одинаково.
            cmd.CommandText = "SELECT Id, OrderName, ProductName, UnitName FROM PlannedTasks ORDER BY Id DESC;";
            using var r = cmd.ExecuteReader();

            string cleanOrder = CleanForCompare(order);
            string cleanProduct = CleanForCompare(product);
            string cleanUnit = CleanForCompare(unit);

            while (r.Read())
            {
                string rowOrder = r.IsDBNull(1) ? "" : r.GetString(1);
                string rowProduct = r.IsDBNull(2) ? "" : r.GetString(2);
                string rowUnit = r.IsDBNull(3) ? "" : r.GetString(3);

                bool orderMatches = CleanForCompare(rowOrder).Equals(cleanOrder, StringComparison.OrdinalIgnoreCase);
                bool productMatches = string.IsNullOrEmpty(cleanProduct)
                    || rowProduct.Equals("Все изделия", StringComparison.OrdinalIgnoreCase)
                    || CleanForCompare(rowProduct).Equals(cleanProduct, StringComparison.OrdinalIgnoreCase);
                bool unitMatches = string.IsNullOrEmpty(cleanUnit)
                    || rowUnit.Equals("Весь узел", StringComparison.OrdinalIgnoreCase)
                    || CleanForCompare(rowUnit).Equals(cleanUnit, StringComparison.OrdinalIgnoreCase);

                if (orderMatches && productMatches && unitMatches)
                    return (int)r.GetInt64(0);
            }

            return null;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            return null;
        }
    }

    /// <summary>Та же очистка, что и CleanTextFromEmoji() в NestingTehnologistManageViewModel —
    /// используется здесь для сравнения "сырых" названий из PlannedTasks с уже очищенными.</summary>
    private static string CleanForCompare(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        string text = input
            .Replace("Заказ:", "")
            .Replace("Заказ", "")
            .Replace("Изделие:", "")
            .Replace("Изделие", "")
            .Replace("Узел:", "")
            .Replace("Узел", "")
            .Trim();

        string clean = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"[^\w\d\s\-\.\(\)\№\/\,\«\»]",
            "",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        return clean.Trim();
    }

}

/// <summary>Одна плитка дневного таймлайна (задание + сколько часов у него на этот
/// конкретный день). Заполняется из PlanTaskSegments JOIN PlannedTasks.</summary>
public class DaySegmentInfo
{
    public int TaskId { get; set; }
    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UnitName { get; set; } = "";
    public TimeSpan Allocated { get; set; }
    public int SortOrder { get; set; }
    public bool IsUrgent { get; set; }
}

/// <summary>Задание, у которого норма трудоёмкости больше, чем сейчас реально расписано по
/// дням — "недорезанный остаток", ожидающий, чтобы его перетащили на какой-то день.</summary>
public class LeftoverTaskInfo
{
    public int TaskId { get; set; }
    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UnitName { get; set; } = "";
    public double RemainingSeconds { get; set; }
    public TimeSpan Remaining => TimeSpan.FromSeconds(RemainingSeconds);
}

public class PlanEntry
{
    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UnitName { get; set; } = "";
    public DateTimeOffset PlannedDate { get; set; }
    public string SheetFormat { get; set; } = "";
    public bool IsUrgent { get; set; }
    public string Comment { get; set; } = "";

    /// <summary>Норма трудоёмкости, введённая/посчитанная ВРУЧНУЮ — только для срочной
    /// дорезки детали, у которой нет собственной строки в UnitLaborNorms (нет в ведомости).
    /// null для обычных заданий — их норма как и раньше берётся из ведомости.</summary>
    public double? ManualNormSeconds { get; set; }
}

/// <summary>Найденная по поиску деталь (для срочной дорезки) — берётся напрямую из уже
/// импортированной ведомости (Details), т.к. там уже есть марка стали, толщина и суммарная
/// длина реза одной штуки (используется и для генерации программ раскроя).</summary>
public class DetailSearchResult
{
    public string DrawingNumber { get; set; } = "";
    public string Name { get; set; } = "";
    public string SteelGrade { get; set; } = "";
    public double Thickness { get; set; }
    public double LengthMm { get; set; }
    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UnitName { get; set; } = "";

    public string Title => string.IsNullOrEmpty(DrawingNumber)
        ? Name
        : $"{DrawingNumber} - {Name}";
}

/// <summary>Один вариант нормы резки (марка/толщина/режим тока-газа) из CncCuttingNorms —
/// для конкретной толщины детали может быть несколько режимов (разный ток/газ), выбор
/// какой из них реально используется на станке — решение технолога, не автоматика.</summary>
public class CncNormOption
{
    public string AmpMode { get; set; } = "";
    public double ThicknessMm { get; set; }
    public double CuttingSpeedMmPerMin { get; set; }
    public double PiercingTimeSec { get; set; }

    /// <summary>Толщина нормы совпадает с толщиной детали ТОЧНО — если нет ни одной точной,
    /// показываем ближайшую по толщине норму, но явно помечаем её как приблизительную.</summary>
    public bool IsExactThickness { get; set; }

    public string Title => IsExactThickness
        ? $"{AmpMode} ({ThicknessMm:0.#} мм)"
        : $"{AmpMode} (≈{ThicknessMm:0.#} мм, ближайшая — точной нормы на эту толщину нет)";
}
