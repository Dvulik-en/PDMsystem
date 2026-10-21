using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Npgsql;
using BKMsystem.Models;

namespace BKMsystem.Services;

/// <summary>
/// Репозиторий карт раскроя: таблицы cutting_sheets / cutting_files / nesting_parts.
///
/// Комплект технолога = НЕСКОЛЬКО управляющих программ (.MPG) + ОДИН общий отчёт (.PDF)
/// + ОДНА общая Excel-карта (.XLSX). Каждая .MPG — отдельная карта/слот (04S_SL01, 04S_SL02...),
/// PDF и Excel прикладываются к КАЖДОЙ карте комплекта.
///
/// Все записи оффлайн-безопасны: при обрыве связи уходят в очередь OfflineSyncService.
/// Чтение при оффлайне отдаёт локальный кэш последнего успешного запроса.
/// </summary>
public class NestingRepository
{
    private readonly string _cs;
    private static bool _tablesReady;

    public NestingRepository() : this(DatabaseService.ConnectionString) { }
    public NestingRepository(string connectionString) { _cs = connectionString; }

    private NpgsqlConnection Open()
    {
        var c = new NpgsqlConnection(_cs);
        c.Open();
        return c;
    }
    /// <summary>
    /// Возвращает следующий свободный номер слота для карт раскроя с заданным префиксом.
    /// </summary>
    public int GetNextSlot(string prefix)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();

            // Берём ВСЕ имена с этим префиксом и находим максимум ЧИСЛОМ, а не по алфавиту —
            // иначе "04S_SL09" лексикографически больше "04S_SL10" и слот посчитается неверно.
            cmd.CommandText = "SELECT NestingName FROM cutting_sheets WHERE NestingName LIKE @prefix || '%';";
            cmd.Parameters.AddWithValue("@prefix", prefix);

            int maxSlot = 0;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string name = r.GetString(0);
                if (name.Length >= prefix.Length + 2 &&
                    int.TryParse(name.Substring(prefix.Length, 2), out int slot))
                {
                    maxSlot = Math.Max(maxSlot, slot);
                }
            }
            return maxSlot + 1;
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            OfflineSyncService.MarkOffline();
        }

        return 1; // Если карт с таким префиксом ещё не было
    }
    public bool TryReserveSheet(string reservationKey, string orderName, string productName,
    string sheetTitle, string thickness, string steelGrade, string reservedBy)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO sheet_reservations (ReservationKey, OrderName, ProductName, SheetTitle, Thickness, SteelGrade, ReservedBy)
                VALUES (@key, @o, @p, @s, @t, @g, @by)
                ON CONFLICT (ReservationKey) DO NOTHING;";
            cmd.Parameters.AddWithValue("@key", reservationKey);
            cmd.Parameters.AddWithValue("@o", orderName ?? "");
            cmd.Parameters.AddWithValue("@p", productName ?? "");
            cmd.Parameters.AddWithValue("@s", sheetTitle ?? "");
            cmd.Parameters.AddWithValue("@t", thickness ?? "");
            cmd.Parameters.AddWithValue("@g", steelGrade ?? "");
            cmd.Parameters.AddWithValue("@by", reservedBy);

            int affected = cmd.ExecuteNonQuery();
            return affected > 0; // 0 = ключ уже занят кем-то другим (ON CONFLICT сработал)
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            return false;
        }
    }

    public bool ReleaseSheet(string reservationKey, string reservedBy)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM sheet_reservations WHERE ReservationKey = @key AND ReservedBy = @by;";
            cmd.Parameters.AddWithValue("@key", reservationKey);
            cmd.Parameters.AddWithValue("@by", reservedBy);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            return false;
        }
    }

    public HashSet<string> GetAllReservedKeys()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ReservationKey FROM sheet_reservations;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(0));
        }
        catch { /* оффлайн — просто не фильтруем, чем рисковать сборкой */ }
        return set;
    }

    public List<SheetReservation> GetMyReservations(string reservedBy)
    {
        var list = new List<SheetReservation>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT ReservationKey, OrderName, ProductName, SheetTitle, Thickness, SteelGrade
                                FROM sheet_reservations WHERE ReservedBy = @by ORDER BY ReservedAt;";
            cmd.Parameters.AddWithValue("@by", reservedBy);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new SheetReservation
                {
                    ReservationKey = r.GetString(0),
                    OrderName = r.IsDBNull(1) ? "" : r.GetString(1),
                    ProductName = r.IsDBNull(2) ? "" : r.GetString(2),
                    SheetTitle = r.IsDBNull(3) ? "" : r.GetString(3),
                    Thickness = r.IsDBNull(4) ? "" : r.GetString(4),
                    SteelGrade = r.IsDBNull(5) ? "" : r.GetString(5)
                });
            }
        }
        catch { }
        return list;
    }

    // ------------------------------------------------------------------
    // Таблицы + триггер NOTIFY (вкладки Раскрои/База/Панель обновляются сами)
    // ------------------------------------------------------------------
    public void EnsureTables()
    {
        if (_tablesReady) return;
        try
        {
            using var conn = Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS cutting_sheets (
                    Id BIGSERIAL PRIMARY KEY,
                    NestingName TEXT UNIQUE,
                    Material TEXT, Thickness DOUBLE PRECISION, SheetSize TEXT, TotalTime TEXT,
                    TaskId INTEGER, OrderName TEXT, ProductName TEXT, UnitName TEXT, PlannedDate TEXT,
                    Status TEXT DEFAULT 'Новый', CreatedAt TEXT,
                    UsbExported BOOLEAN DEFAULT FALSE
                );

                CREATE TABLE IF NOT EXISTS cutting_files (
                    Id BIGSERIAL PRIMARY KEY,
                    SheetId INTEGER, FileName TEXT, FileType TEXT, FileData TEXT
                );

                CREATE TABLE IF NOT EXISTS nesting_parts (
                    Id BIGSERIAL PRIMARY KEY,
                    SheetId INTEGER, PartNumber INTEGER, PartName TEXT, Quantity INTEGER, WeightKg DOUBLE PRECISION,
                    OrderName TEXT, ProductName TEXT, UnitName TEXT
                );

                CREATE TABLE IF NOT EXISTS sheet_reservations (
                    Id BIGSERIAL PRIMARY KEY,
                    ReservationKey TEXT UNIQUE NOT NULL,
                    OrderName TEXT, ProductName TEXT, SheetTitle TEXT,
                    Thickness TEXT, SteelGrade TEXT,
                    ReservedBy TEXT NOT NULL,
                    ReservedAt TIMESTAMP DEFAULT now()
                );

                -- Таблица связей карты раскроя с узлами (для сборных раскроев из разных узлов)
                CREATE TABLE IF NOT EXISTS cutting_sheet_tasks (
                    Id BIGSERIAL PRIMARY KEY,
                    SheetId BIGINT NOT NULL,
                    OrderName TEXT NOT NULL,
                    ProductName TEXT NOT NULL,
                    UnitName TEXT NOT NULL,
                    CONSTRAINT uq_sheet_task UNIQUE (SheetId, OrderName, ProductName, UnitName)
                );

                CREATE INDEX IF NOT EXISTS idx_cst_lookup ON cutting_sheet_tasks(OrderName, ProductName, UnitName);

                -- Причина недореза от мастера (вкладка мастера, ещё не сделана) — один заказ на
                -- одну плановую дату может иметь ровно одну причину; технологи в Панели её
                -- только читают. OrderName хранится СЫРЫМ (как в PlannedTasks.OrderName), без
                -- очистки — сравниваем один к одному с тем, что уже лежит в DashboardCardModel.OrderName.
                CREATE TABLE IF NOT EXISTS CuttingDelayReasons (
                    Id BIGSERIAL PRIMARY KEY,
                    OrderName TEXT NOT NULL,
                    PlannedDate TEXT NOT NULL,
                    Reason TEXT NOT NULL,
                    CreatedAt TEXT,
                    CONSTRAINT uq_delay_reason UNIQUE (OrderName, PlannedDate)
                );

                -- Добавляем колонки в существующие таблицы на случай, если они уже были созданы в БД:
                ALTER TABLE cutting_sheets ADD COLUMN IF NOT EXISTS UsbExported BOOLEAN DEFAULT FALSE;
                ALTER TABLE cutting_sheets ADD COLUMN IF NOT EXISTS PackageId TEXT;
                -- Метки времени для вкладки Метрики (директор): когда УП создана — уже есть в
                -- CreatedAt; UsbExportedAt — момент, когда оператор реально забрал УП на резку
                -- (выгрузка на флешку = взято в работу); CompletedAt — момент отметки Выполнено.
                ALTER TABLE cutting_sheets ADD COLUMN IF NOT EXISTS UsbExportedAt TIMESTAMP;
                ALTER TABLE cutting_sheets ADD COLUMN IF NOT EXISTS CompletedAt TIMESTAMP;
                ALTER TABLE nesting_parts ADD COLUMN IF NOT EXISTS OrderName TEXT;
                ALTER TABLE nesting_parts ADD COLUMN IF NOT EXISTS ProductName TEXT;
                ALTER TABLE nesting_parts ADD COLUMN IF NOT EXISTS UnitName TEXT;
                ALTER TABLE nesting_parts ADD COLUMN IF NOT EXISTS Length DOUBLE PRECISION DEFAULT 0;
                ALTER TABLE nesting_parts ADD COLUMN IF NOT EXISTS Width DOUBLE PRECISION DEFAULT 0;

                CREATE OR REPLACE FUNCTION notify_table_update() RETURNS trigger AS $func$
                BEGIN
                    PERFORM pg_notify('table_updated', lower(TG_TABLE_NAME));
                    RETURN NULL;
                END;
                $func$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS trg_notify_sheets ON cutting_sheets;
                CREATE TRIGGER trg_notify_sheets
                    AFTER INSERT OR UPDATE OR DELETE ON cutting_sheets
                    FOR EACH STATEMENT EXECUTE FUNCTION notify_table_update();

                DROP TRIGGER IF EXISTS trg_notify_reservations ON sheet_reservations;
                CREATE TRIGGER trg_notify_reservations
                    AFTER INSERT OR UPDATE OR DELETE ON sheet_reservations
                    FOR EACH STATEMENT EXECUTE FUNCTION notify_table_update();

                DROP TRIGGER IF EXISTS trg_notify_delay_reasons ON CuttingDelayReasons;
                CREATE TRIGGER trg_notify_delay_reasons
                    AFTER INSERT OR UPDATE OR DELETE ON CuttingDelayReasons
                    FOR EACH STATEMENT EXECUTE FUNCTION notify_table_update();";

            cmd.ExecuteNonQuery();
            _tablesReady = true;
            OfflineSyncService.SetOnline(true);
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            OfflineSyncService.MarkOffline();
        }
    }

    // ------------------------------------------------------------------
    // ПРОВЕРКА ФАЙЛОВ КОМПЛЕКТА (подписи/структура, а не только расширение)
    // ------------------------------------------------------------------
    public static string? ValidatePackage(List<string> mpgPaths, string? pdfPath, string? xlsxPath)
    {
        if (mpgPaths.Count == 0)
            return "В комплекте должен быть хотя бы один файл управляющей программы (.MPG).";

        foreach (var mpg in mpgPaths)
        {
            try
            {
                using var reader = new StreamReader(mpg);
                string head = "";
                for (int i = 0; i < 12 && !reader.EndOfStream; i++)
                    head += reader.ReadLine() + "\n";

                if (!head.Contains("AJANCAM"))
                    return $"Файл {Path.GetFileName(mpg)} не похож на УП AJANCAM (нет заголовка AJANCAM).";
            }
            catch (Exception ex)
            {
                return $"Не удалось прочитать {Path.GetFileName(mpg)}: {ex.Message}";
            }
        }

        if (pdfPath != null)
        {
            try
            {
                var bytes = new byte[5];
                using var fs = File.OpenRead(pdfPath);
                if (fs.Read(bytes, 0, 5) < 5 || Encoding_Ascii(bytes) != "%PDF-")
                    return $"Файл {Path.GetFileName(pdfPath)} не является PDF-документом.";
            }
            catch (Exception ex)
            {
                return $"Не удалось прочитать {Path.GetFileName(pdfPath)}: {ex.Message}";
            }
        }

        if (xlsxPath != null)
        {
            try
            {
                using var fs = File.OpenRead(xlsxPath);
                int b0 = fs.ReadByte(), b1 = fs.ReadByte();
                bool isZip = b0 == 0x50 && b1 == 0x4B;                 // .xlsx — ZIP
                bool isOle = b0 == 0xD0 && b1 == 0xCF;                 // .xls — OLE compound
                if (!isZip && !isOle)
                    return $"Файл {Path.GetFileName(xlsxPath)} не является таблицей Excel.";
            }
            catch (Exception ex)
            {
                return $"Не удалось прочитать {Path.GetFileName(xlsxPath)}: {ex.Message}";
            }
        }

        return null; // всё в порядке
    }

    private static string Encoding_Ascii(byte[] b) =>
        System.Text.Encoding.ASCII.GetString(b);

    // ------------------------------------------------------------------
    // СОХРАНЕНИЕ КОМПЛЕКТА: одна запись cutting_sheets НА КАЖДУЮ УП,
    // общие PDF и Excel прикладываются ко всем картам комплекта.
    // ------------------------------------------------------------------

    /// <summary>true — записано на сервер сразу; false — в локальную очередь (доедет при сети).</summary>
    public bool SavePackageSafe(NestingUploadModel model)
    {
        var dto = ToDto(model);

        if (OfflineSyncService.IsOnline)
        {
            try
            {
                SavePackageDirect(dto);
                OfflineSyncService.SetOnline(true);
                
                // Явно оповещаем систему об обновлении таблиц!
                DatabaseService.NotifyDatabaseUpdated("cutting_sheets");
                return true;
            }
            catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
            {
                OfflineSyncService.MarkOffline();
            }
        }

        OfflineSyncService.Enqueue(new PendingOperation
        {
            OpType = OpTypes.NestingUpload,
            PayloadJson = JsonSerializer.Serialize(dto)
        });
        return false;
    }

    private static NestingSheetDto ToDto(NestingUploadModel model)
    {
        var dto = new NestingSheetDto
        {
            PackageId = model.PackageId,
            NestingName = model.NestingName,
            Material = model.Material,
            Thickness = model.Thickness,
            SheetSize = model.SheetSize,
            TotalTime = model.TotalTime,
            TaskId = model.TaskId,
            OrderName = model.OrderName,
            ProductName = model.ProductName,
            UnitName = model.UnitName,
            PlannedDate = model.PlannedDate,
            Parts = model.Parts,
            TargetUnits = model.TargetUnits
        };

        foreach (var pair in model.FilePaths)
        {
            string path = pair.Value;
            if (!File.Exists(path)) continue;
            // Имя берём из ключа словаря (каноническое 04S_SL01.MPG), а не из пути на диске
            string logicalName = string.IsNullOrWhiteSpace(pair.Key)
                ? Path.GetFileName(path)
                : pair.Key;
            string ext = Path.GetExtension(logicalName).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext))
                ext = Path.GetExtension(path).ToLowerInvariant();
            dto.Files.Add(new NestingFileDto
            {
                FileName = logicalName,
                FileType = ext switch
                {
                    ".mpg" => "CNC",
                    ".pdf" => "PDF",
                    ".dxf" => "DXF",
                    ".xlsx" or ".xls" => "EXCEL",
                    _ => "OTHER"
                },
                Base64Data = Convert.ToBase64String(File.ReadAllBytes(path))
            });
        }
        return dto;
    }

    /// <summary>Прямая запись: одна карта на каждый CNC-файл.
    /// PDF и Excel прикладываются только к ПЕРВОЙ карте комплекта (в дереве оператора PDF один).</summary>
    internal List<int> SavePackageDirect(NestingSheetDto dto)
    {
        EnsureTables();
        var sheetIds = new List<int>();

        var mpgFiles = dto.Files.Where(f => f.FileType == "CNC").ToList();
        var sharedFiles = dto.Files.Where(f => f.FileType != "CNC").ToList();
        if (mpgFiles.Count == 0 && dto.Files.Count > 0)
            mpgFiles.Add(dto.Files[0]); // комплект без УП — сохраняем как одну карту с тем, что есть

        int slot = 1;
        for (int i = 0; i < mpgFiles.Count; i++)
        {
            var mpg = mpgFiles[i];
            // Имя карты = имя файла УП (уже проверено/сгенерировано снаружи), предохранитель 8 символов
            string sheetName = NamingService.EnforceAjanLimit(
                NamingService.SanitizeFileName(Path.GetFileNameWithoutExtension(mpg.FileName)));
            if (string.IsNullOrWhiteSpace(sheetName))
                sheetName = NamingService.BuildSheetName((double)dto.Thickness,
                    PdfTextExtractor.IsChequeredSteel(dto.Material), slot);
            slot++;

            var filesForSheet = new List<NestingFileDto> { mpg };
            // Общий PDF/Excel — только у первой УП, чтобы в «Раскрои» не плодились копии отчёта
            if (i == 0)
                filesForSheet.AddRange(sharedFiles);

            sheetIds.Add(UpsertOneSheet(dto, sheetName, filesForSheet));
        }

        return sheetIds;
    }

    private int UpsertOneSheet(NestingSheetDto dto, string sheetName, List<NestingFileDto> files)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        long sheetId;
        using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT Id FROM cutting_sheets WHERE NestingName = @n;";
            find.Parameters.AddWithValue("@n", sheetName);
            var existing = find.ExecuteScalar();

            if (existing == null)
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"
                    INSERT INTO cutting_sheets
                        (NestingName, Material, Thickness, SheetSize, TotalTime, TaskId, OrderName, ProductName, UnitName, PlannedDate, Status, CreatedAt)
                    VALUES
                        (@n, @mat, @th, @size, @time, @task, @ord, @prod, @unit, @date, 'Новый', @created)
                    RETURNING Id;";
                    
                ins.Parameters.AddWithValue("@n", sheetName);
                ins.Parameters.AddWithValue("@mat", dto.Material ?? "");
                ins.Parameters.AddWithValue("@th", (double)dto.Thickness);
                ins.Parameters.AddWithValue("@size", dto.SheetSize ?? "");
                ins.Parameters.AddWithValue("@time", dto.TotalTime ?? "");
                ins.Parameters.AddWithValue("@task", (object?)dto.TaskId ?? DBNull.Value);
                ins.Parameters.AddWithValue("@ord", dto.OrderName ?? "");
                ins.Parameters.AddWithValue("@prod", dto.ProductName ?? "");
                ins.Parameters.AddWithValue("@unit", dto.UnitName ?? "");
                ins.Parameters.AddWithValue("@date", dto.PlannedDate ?? "");
                ins.Parameters.AddWithValue("@created", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                ins.Parameters.AddWithValue("@pkgId", string.IsNullOrEmpty(dto.PackageId) ? (object)DBNull.Value : dto.PackageId);
                sheetId = (long)ins.ExecuteScalar()!;
            }
            else
            {
                sheetId = (long)existing;
                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = @"
                    UPDATE cutting_sheets SET Material=@mat, Thickness=@th, SheetSize=@size, TotalTime=@time,
                        TaskId=@task, OrderName=@ord, ProductName=@prod, UnitName=@unit, PlannedDate=@date
                    WHERE Id=@id;
                    DELETE FROM cutting_files WHERE SheetId=@id;
                    DELETE FROM nesting_parts WHERE SheetId=@id;";
                upd.Parameters.AddWithValue("@id", sheetId);
                upd.Parameters.AddWithValue("@mat", dto.Material ?? "");
                upd.Parameters.AddWithValue("@th", (double)dto.Thickness);
                upd.Parameters.AddWithValue("@size", dto.SheetSize ?? "");
                upd.Parameters.AddWithValue("@time", dto.TotalTime ?? "");
                upd.Parameters.AddWithValue("@task", (object?)dto.TaskId ?? DBNull.Value);
                upd.Parameters.AddWithValue("@ord", dto.OrderName ?? "");
                upd.Parameters.AddWithValue("@prod", dto.ProductName ?? "");
                upd.Parameters.AddWithValue("@unit", dto.UnitName ?? "");
                upd.Parameters.AddWithValue("@date", dto.PlannedDate ?? "");
                upd.ExecuteNonQuery();
            }
        }

        // Сохранение файлов УП, PDF и т.д.
        foreach (var f in files)
        {
            using var cmdF = conn.CreateCommand();
            cmdF.Transaction = tx;
            cmdF.CommandText = "INSERT INTO cutting_files (SheetId, FileName, FileType, FileData) VALUES (@s, @n, @t, @d);";
            cmdF.Parameters.AddWithValue("@s", sheetId);
            cmdF.Parameters.AddWithValue("@n", f.FileName);
            cmdF.Parameters.AddWithValue("@t", f.FileType);
            cmdF.Parameters.AddWithValue("@d", f.Base64Data);
            cmdF.ExecuteNonQuery();
        }

        // 1. Сохранение деталей с указанием их конкретного узла
        foreach (var p in dto.Parts)
        {
            using var cmdP = conn.CreateCommand();
            cmdP.Transaction = tx;

            cmdP.CommandText = @"
                INSERT INTO nesting_parts (SheetId, PartNumber, PartName, Quantity, WeightKg, OrderName, ProductName, UnitName, Length, Width) 
                VALUES (@s, @num, @n, @q, @w, @ord, @prod, @unit, @len, @wid);";
            cmdP.Parameters.AddWithValue("@s", sheetId);
            cmdP.Parameters.AddWithValue("@num", p.PartNumber);
            cmdP.Parameters.AddWithValue("@n", p.PartName);
            cmdP.Parameters.AddWithValue("@q", p.Quantity);
            cmdP.Parameters.AddWithValue("@w", (double)p.WeightKg);
            cmdP.Parameters.AddWithValue("@ord", string.IsNullOrEmpty(p.OrderName) ? (object)DBNull.Value : p.OrderName);
            cmdP.Parameters.AddWithValue("@prod", string.IsNullOrEmpty(p.ProductName) ? (object)DBNull.Value : p.ProductName);
            cmdP.Parameters.AddWithValue("@unit", string.IsNullOrEmpty(p.UnitName) ? (object)DBNull.Value : p.UnitName);
            cmdP.Parameters.AddWithValue("@len", p.Length);
            cmdP.Parameters.AddWithValue("@wid", p.Width);
            cmdP.ExecuteNonQuery();
        }

        // 2. Очищаем старые связки листа с узлами (при перезаписи)
        using (var delCst = conn.CreateCommand())
        {
            delCst.Transaction = tx;
            delCst.CommandText = "DELETE FROM cutting_sheet_tasks WHERE SheetId = @id;";
            delCst.Parameters.AddWithValue("@id", sheetId);
            delCst.ExecuteNonQuery();
        }

        // 3. Создаем связки карты со всеми узлами, чьи детали есть на этом листе
        // ГЛАВНЫЙ ИСТОЧНИК: список изделий/узлов уже собран правильно в ImportKitAsync
        // (dto.TargetUnits) — используем его напрямую. Раньше здесь угадывали привязку по
        // чертёжным номерам деталей (dto.Parts[i].OrderName) — это ломается, когда несколько
        // изделий (например, разные зав.№ одной сборки) используют ОДИНАКОВЫЕ чертежи: словарь
        // "номер детали -> изделие" может хранить только одно значение на номер, поэтому карта
        // привязывалась только к одному изделию из четырёх, а не ко всем.
        var unitsToLink = dto.TargetUnits
            .Where(u => !string.IsNullOrEmpty(u.OrderName))
            .Select(u => (Order: u.OrderName, Product: u.ProductName, Unit: u.UnitName))
            .Distinct()
            .ToList();

        // Запасной вариант — если TargetUnits почему-то не прислали (старый вызов/офлайн-очередь
        // из версии до этого фикса), угадываем по деталям, как раньше.
        if (unitsToLink.Count == 0)
        {
            unitsToLink = dto.Parts
                .Where(p => !string.IsNullOrEmpty(p.OrderName))
                .Select(p => (Order: p.OrderName, Product: p.ProductName, Unit: p.UnitName))
                .Distinct()
                .ToList();
        }

        if (unitsToLink.Count == 0 && !string.IsNullOrEmpty(dto.OrderName))
        {
            unitsToLink.Add((dto.OrderName ?? "", dto.ProductName ?? "", dto.UnitName ?? ""));
        }

        // ВРЕМЕННАЯ ДИАГНОСТИКА — уберите после проверки.
        System.Diagnostics.Debug.WriteLine($"[UpsertOneSheet] Карта '{dto.NestingName}' (SheetId={sheetId}): деталей всего={dto.Parts.Count}, из них с непустым OrderName={dto.Parts.Count(p => !string.IsNullOrEmpty(p.OrderName))}, уникальных unitsToLink={unitsToLink.Count}");
        foreach (var u in unitsToLink)
        {
            System.Diagnostics.Debug.WriteLine($"[UpsertOneSheet]    -> Order='{u.Order}', Product='{u.Product}', Unit='{u.Unit}'");
        }

        foreach (var u in unitsToLink)
        {
            using var cmdU = conn.CreateCommand();
            cmdU.Transaction = tx;
            cmdU.CommandText = @"
                INSERT INTO cutting_sheet_tasks (SheetId, OrderName, ProductName, UnitName)
                VALUES (@s, @o, @p, @u)
                ON CONFLICT (SheetId, OrderName, ProductName, UnitName) DO NOTHING;";
            cmdU.Parameters.AddWithValue("@s", sheetId);
            cmdU.Parameters.AddWithValue("@o", u.Order);
            cmdU.Parameters.AddWithValue("@p", u.Product);
            cmdU.Parameters.AddWithValue("@u", u.Unit);
            cmdU.ExecuteNonQuery();
        }

        tx.Commit();
        return (int)sheetId;
    }

    // ------------------------------------------------------------------
    // СТАТУС КАРТЫ: Новый / Выполнен
    // ------------------------------------------------------------------
    public bool SetStatusSafe(int sheetId, string status)
    {
        if (OfflineSyncService.IsOnline)
        {
            try
            {
                SetStatusDirect(sheetId, status);
                OfflineSyncService.SetOnline(true);
                return true;
            }
            catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
            {
                OfflineSyncService.MarkOffline();
            }
        }

        OfflineSyncService.Enqueue(new PendingOperation
        {
            OpType = OpTypes.NestingStatus,
            PayloadJson = JsonSerializer.Serialize(new NestingStatusPayload { SheetId = sheetId, Status = status })
        });
        return false;
    }

    internal void SetStatusDirect(int sheetId, string status)
    {
        EnsureTables();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // CompletedAt ставим только при переходе именно в "Выполнен", и только один раз —
        // повторное нажатие (или пересинхронизация из офлайн-очереди) не должно сдвигать
        // уже зафиксированное время выполнения вперёд.
        cmd.CommandText = @"
            UPDATE cutting_sheets
            SET Status = @st,
                CompletedAt = CASE WHEN @st = 'Выполнен' AND CompletedAt IS NULL THEN now() ELSE CompletedAt END
            WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@st", status);
        cmd.Parameters.AddWithValue("@id", sheetId);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------
    // Какие "листы" (задание+толщина) уже имеют загруженные УП —
    // чтобы скрыть их у технолога во вкладке "Учет УП"
    // ------------------------------------------------------------------
    public HashSet<string> GetCoveredSheetKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            // ВАЖНО: раньше читали cutting_sheets.OrderName/ProductName напрямую — но там
            // ОДНО значение на карту (то, что было первым в списке отмеченных при сохранении).
            // Когда одна карта относится СРАЗУ к нескольким изделиям/узлам (несколько галочек
            // за раз), это и происходит с этой связью — cutting_sheet_tasks: там по строке на
            // КАЖДОЕ изделие/узел, к которому реально привязана карта. Без этого только первое
            // из отмеченных считалось "покрытым", остальные возвращались в правое дерево.
            cmd.CommandText = @"
                SELECT cst.OrderName, cst.ProductName, s.Thickness
                FROM cutting_sheet_tasks cst
                JOIN cutting_sheets s ON s.Id = cst.SheetId
                WHERE cst.OrderName IS NOT NULL AND cst.OrderName <> '';";

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string order = r.IsDBNull(0) ? "" : r.GetString(0);
                string product = r.IsDBNull(1) ? "" : r.GetString(1);
                double th = r.IsDBNull(2) ? 0 : r.GetDouble(2);

                // ВРЕМЕННАЯ ДИАГНОСТИКА — уберите после проверки.
                System.Diagnostics.Debug.WriteLine($"[GetCoveredSheetKeys] Из cutting_sheet_tasks: Order='{order}', Product='{product}', Thickness={th}");
                // OrderName/ProductName в БД уже очищены через CleanTextFromEmoji() при сохранении —
                // здесь НЕ применяем другую очистку (старый Regex [^\w\d\s\-\.] вырезал скобки/№/,
                // которые CleanTextFromEmoji специально оставляет) — иначе ключи снова разъедутся.
                string cleanThickness = th.ToString("0.##", CultureInfo.InvariantCulture);
                keys.Add($"{order}|{product}|{cleanThickness}".ToLowerInvariant());
            }
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            OfflineSyncService.MarkOffline();
        }
        return keys;
    }

    // ------------------------------------------------------------------
    // ДЕРЕВО: Заказ -> Изделие/Узел -> Карта раскроя -> Файлы (УП, PDF)
    // statusFilter: "active" = всё кроме Выполнен, "done" = только Выполнен
    // includeExcel: Excel технологу (База) — да, оператору (Раскрои) — нет
    // ------------------------------------------------------------------
    public List<NestingTreeNode> GetSheetsTree(string statusFilter, bool includeExcel = false)
    {
        EnsureTables();
        string cacheKey = $"nest_tree_{statusFilter}_{includeExcel}";
        try
        {
            var roots = new List<NestingTreeNode>();
            var orderNodes = new Dictionary<string, NestingTreeNode>(StringComparer.OrdinalIgnoreCase);
            var productNodes = new Dictionary<string, NestingTreeNode>(StringComparer.OrdinalIgnoreCase);

            var sheets = new List<(long Id, string Name, string Mat, double Th, string Size, string Time,
                                string Status, string Order, string Product, string Unit, bool UsbExported,
                                DateTime? UsbExportedAt)>();

            // 1. Читаем списки карт и ЗАКРЫВАЕМ соединение/reader
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = statusFilter == "done"
                    ? @"SELECT Id, NestingName, Material, Thickness, SheetSize, TotalTime, Status, OrderName, ProductName, UnitName, COALESCE(UsbExported, FALSE), UsbExportedAt
                        FROM cutting_sheets WHERE Status = 'Выполнен' ORDER BY OrderName, ProductName, Thickness, NestingName;"
                    : @"SELECT Id, NestingName, Material, Thickness, SheetSize, TotalTime, Status, OrderName, ProductName, UnitName, COALESCE(UsbExported, FALSE), UsbExportedAt
                        FROM cutting_sheets WHERE Status IS DISTINCT FROM 'Выполнен' ORDER BY OrderName, ProductName, Thickness, NestingName;";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    sheets.Add((r.GetInt64(0),
                        r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2),
                        r.IsDBNull(3) ? 0 : r.GetDouble(3), r.IsDBNull(4) ? "" : r.GetString(4),
                        r.IsDBNull(5) ? "" : r.GetString(5), r.IsDBNull(6) ? "" : r.GetString(6),
                        r.IsDBNull(7) ? "" : r.GetString(7), r.IsDBNull(8) ? "" : r.GetString(8),
                        r.IsDBNull(9) ? "" : r.GetString(9),
                        !r.IsDBNull(10) && r.GetBoolean(10),
                        r.IsDBNull(11) ? (DateTime?)null : r.GetDateTime(11)));
                }
            }

            // 2. Формируем дерево: Заказ → Изделие → Лист N мм → УП (+ один PDF на папку листа)
            var sheetFolderNodes = new Dictionary<string, NestingTreeNode>(StringComparer.OrdinalIgnoreCase);

            using (var conn = Open())
            {
                foreach (var s in sheets)
                {
                    // 1. Уровень Заказа
                    string orderTitle = string.IsNullOrWhiteSpace(s.Order) ? "Без привязки к плану" : s.Order;
                    if (!orderNodes.TryGetValue(orderTitle, out var orderNode))
                    {
                        orderNode = new NestingTreeNode { Title = $"Заказ: {orderTitle}", NodeType = "Order" };
                        orderNodes[orderTitle] = orderNode;
                        roots.Add(orderNode);
                    }

                    // 2. Уровень Изделия / Узла
                    string productTitle = string.IsNullOrWhiteSpace(s.Product)
                        ? "—"
                        : (string.IsNullOrWhiteSpace(s.Unit) || s.Unit == "Весь узел" ? s.Product : $"{s.Product} / {s.Unit}");
                    string productKey = $"{orderTitle}|{productTitle}";
                    if (!productNodes.TryGetValue(productKey, out var productNode))
                    {
                        productNode = new NestingTreeNode { Title = productTitle, NodeType = "Product" };
                        productNodes[productKey] = productNode;
                        orderNode.Children.Add(productNode);
                    }

                    // 3. Уровень Листа (например: "Лист 4 мм (Ст3)")
                    string matStr = string.IsNullOrWhiteSpace(s.Mat) ? "" : $" ({s.Mat})";
                    string sheetFolderTitle = $"Лист {s.Th:0.#} мм{matStr}";
                    string sheetFolderKey = $"{productKey}|{sheetFolderTitle}";

                    if (!sheetFolderNodes.TryGetValue(sheetFolderKey, out var sheetFolderNode))
                    {
                        sheetFolderNode = new NestingTreeNode
                        {
                            Title = sheetFolderTitle,
                            NodeType = "MaterialFolder",
                            UsbExported = true // AND по картам ниже
                        };
                        sheetFolderNodes[sheetFolderKey] = sheetFolderNode;
                        productNode.Children.Add(sheetFolderNode);
                    }

                    sheetFolderNode.RelatedSheetIds.Add((int)s.Id);
                    sheetFolderNode.UsbExported = sheetFolderNode.UsbExported && s.UsbExported;

                    // 4. Файлы: каждая УП отдельно; PDF — только один на папку листа
                    using (var cmdF = conn.CreateCommand())
                    {
                        cmdF.CommandText = "SELECT Id, FileName, FileType FROM cutting_files WHERE SheetId = @s ORDER BY CASE WHEN FileType='CNC' THEN 0 WHEN FileType='PDF' THEN 1 ELSE 2 END, Id;";
                        cmdF.Parameters.AddWithValue("@s", s.Id);
                        using var rf = cmdF.ExecuteReader();
                        while (rf.Read())
                        {
                            string type = rf.IsDBNull(2) ? "" : rf.GetString(2);
                            if (type == "EXCEL" && !includeExcel) continue;

                            if (type == "PDF")
                            {
                                // Один отчёт на весь лист (толщину) — не дублируем
                                if (sheetFolderNode.Children.Any(c => c.NodeType == "Pdf"))
                                    continue;
                            }

                            string mpgTitle = rf.IsDBNull(1) ? "" : rf.GetString(1);

                            var fileNode = new NestingTreeNode
                            {
                                FileId = rf.GetInt32(0),
                                SheetId = (int)s.Id,
                                SheetName = s.Name,
                                FileName = rf.IsDBNull(1) ? "" : rf.GetString(1),
                                FileType = type,
                                Title = mpgTitle,
                                Status = s.Status,
                                UsbExported = s.UsbExported,
                                UsbExportedAt = s.UsbExportedAt,
                                NodeType = type switch
                                {
                                    "CNC" => "Mpg",
                                    "PDF" => "Pdf",
                                    "EXCEL" => "Excel",
                                    _ => "Other"
                                }
                            };
                            // Бейдж "⏱ в работе: Xч Yм" — отдельное поле TimeInWorkText (не часть
                            // Title), считается по рабочему времени (см. WorkTimeCalculator) от
                            // выгрузки на флешку до сейчас. NestingViewModel периодически вызывает
                            // RecomputeTimeInWork() заново на уже построенном дереве, чтобы бейдж
                            // "тикал" без похода в базу.
                            fileNode.RecomputeTimeInWork();
                            sheetFolderNode.Children.Add(fileNode);
                        }
                    }
                }
            }

            OfflineSyncService.SetOnline(true);
            OfflineSyncService.SaveSnapshot(cacheKey, roots);
            return roots;
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            OfflineSyncService.MarkOffline();
            return OfflineSyncService.LoadSnapshot<List<NestingTreeNode>>(cacheKey) ?? new List<NestingTreeNode>();
        }
    }

    // ------------------------------------------------------------------
    // ФАЙЛЫ: чтение из базы и выгрузка каноническими именами AJAN
    // ------------------------------------------------------------------
    public (string FileName, byte[] Data)? GetFile(int fileId)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT FileName, FileData FROM cutting_files WHERE Id = @id;";
            cmd.Parameters.AddWithValue("@id", fileId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return (r.GetString(0), Convert.FromBase64String(r.GetString(1)));
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            OfflineSyncService.MarkOffline();
            return null;
        }
    }

    /// <summary>Выгружает на флешку ТОЛЬКО УП (.MPG). PDF/Excel оператору не нужны.</summary>
    public int ExportSheetMpgOnly(IEnumerable<int> sheetIds, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        int written = 0;
        using var conn = Open();

        foreach (int sheetId in sheetIds.Distinct())
        {
            string sheetName;
            using (var cmdS = conn.CreateCommand())
            {
                cmdS.CommandText = "SELECT NestingName FROM cutting_sheets WHERE Id = @id;";
                cmdS.Parameters.AddWithValue("@id", sheetId);
                sheetName = cmdS.ExecuteScalar()?.ToString() ?? $"SHEET_{sheetId}";
            }
            sheetName = NamingService.EnforceAjanLimit(NamingService.SanitizeFileName(sheetName));

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT FileData FROM cutting_files WHERE SheetId = @s AND FileType = 'CNC' LIMIT 1;";
                cmd.Parameters.AddWithValue("@s", sheetId);
                var data = cmd.ExecuteScalar() as string;
                if (string.IsNullOrEmpty(data)) continue;

                File.WriteAllBytes(Path.Combine(targetDir, sheetName + ".MPG"), Convert.FromBase64String(data));
                written++;
            }
        }

        return written;
    }

    /// <summary>Помечает карты как выгруженные на флешку (открывает кнопку «Выполнено»).</summary>
    public void MarkUsbExported(IEnumerable<int> sheetIds)
    {
        EnsureTables();
        var ids = sheetIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return;

        try
        {
            using var conn = Open();
            foreach (int id in ids)
            {
                using var cmd = conn.CreateCommand();
                // UsbExportedAt — тоже только один раз: комплект листа может выгружаться на
                // флешку повторно (докинули ещё одну карту в тот же лист), но "взято в работу"
                // по факту произошло в первый раз.
                cmd.CommandText = @"
                    UPDATE cutting_sheets
                    SET UsbExported = TRUE,
                        UsbExportedAt = CASE WHEN UsbExportedAt IS NULL THEN now() ELSE UsbExportedAt END
                    WHERE Id = @id;";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
            DatabaseService.NotifyDatabaseUpdated("cutting_sheets");
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            OfflineSyncService.MarkOffline();
        }
    }

    /// <summary>Выгружает файлы карты в папку. Имена — строго канонические (04S_SL01.MPG и т.д.).</summary>
    public int ExportSheet(int sheetId, string targetDir)
    {
        using var conn = Open();

        string sheetName;
        using (var cmdS = conn.CreateCommand())
        {
            cmdS.CommandText = "SELECT NestingName FROM cutting_sheets WHERE Id = @id;";
            cmdS.Parameters.AddWithValue("@id", sheetId);
            sheetName = cmdS.ExecuteScalar()?.ToString() ?? $"SHEET_{sheetId}";
        }
        sheetName = NamingService.EnforceAjanLimit(NamingService.SanitizeFileName(sheetName));

        var files = new List<(string Name, string Type, string Data)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT FileName, FileType, FileData FROM cutting_files WHERE SheetId = @s;";
            cmd.Parameters.AddWithValue("@s", sheetId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                files.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetString(2)));
        }

        Directory.CreateDirectory(targetDir);
        int written = 0;
        foreach (var f in files)
        {
            string ext = Path.GetExtension(f.Name);
            string outName = f.Type == "CNC"
                ? sheetName + ".MPG"
                : sheetName + ext.ToLowerInvariant();
            File.WriteAllBytes(Path.Combine(targetDir, outName), Convert.FromBase64String(f.Data));
            written++;
        }
        return written;
    }

    /// <summary>Детали карты (для сверки с ведомостью).</summary>
    public List<NestingPartModel> GetParts(int sheetId)
    {
        var parts = new List<NestingPartModel>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT PartNumber, PartName, Quantity, WeightKg FROM nesting_parts WHERE SheetId = @s ORDER BY PartNumber;";
            cmd.Parameters.AddWithValue("@s", sheetId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                parts.Add(new NestingPartModel
                {
                    PartNumber = r.GetInt32(0),
                    PartName = r.IsDBNull(1) ? "" : r.GetString(1),
                    Quantity = r.IsDBNull(2) ? 1 : r.GetInt32(2),
                    WeightKg = r.IsDBNull(3) ? 0 : (decimal)r.GetDouble(3)
                });
            }
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            OfflineSyncService.MarkOffline();
        }
        return parts;
    }

    // ------------------------------------------------------------------
    // ПАНЕЛЬ (Dashboard): задания на дату + загруженные карты раскроя
    // ------------------------------------------------------------------
    public ObservableCollection<DashboardOrderItem> GetDashboardForDate(DateTime date)
    {
        EnsureTables();
        string dateSql = date.ToString("yyyy-MM-dd");
        string cacheKey = "dash_" + dateSql;
        var result = new ObservableCollection<DashboardOrderItem>();

        try
        {
            using var conn = Open();

            // 1. Читаем все плановые задания на дату
            var tasks = new List<(long Id, string Order, string Product, string Unit, string Comment)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT Id, OrderName, ProductName, UnitName, Comment FROM PlannedTasks WHERE PlannedDate = @d ORDER BY OrderName, ProductName;";
                cmd.Parameters.AddWithValue("@d", dateSql);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    tasks.Add((r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2),
                        r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4)));
                }
            }

            // 2. Читаем все карты раскроя с их PackageId и связями
            var allSheets = new List<(long Id, string Name, string Mat, double Th, string Status, string PkgId)>();
            using (var cmdS = conn.CreateCommand())
            {
                cmdS.CommandText = @"
                    SELECT Id, NestingName, Material, Thickness, Status, COALESCE(PackageId, NestingName)
                    FROM cutting_sheets
                    WHERE PlannedDate = @d OR PlannedDate IS NULL OR PlannedDate = '';";
                cmdS.Parameters.AddWithValue("@d", dateSql);
                using var rs = cmdS.ExecuteReader();
                while (rs.Read())
                {
                    allSheets.Add((rs.GetInt64(0), rs.GetString(1), rs.GetString(2), rs.GetDouble(3), rs.GetString(4), rs.GetString(5)));
                }
            }

            // 3. Читаем связи карт с узлами
            var sheetTasks = new List<(long SheetId, string Order, string Product, string Unit)>();
            using (var cmdCst = conn.CreateCommand())
            {
                cmdCst.CommandText = "SELECT SheetId, OrderName, ProductName, UnitName FROM cutting_sheet_tasks;";
                using var rc = cmdCst.ExecuteReader();
                while (rc.Read())
                {
                    sheetTasks.Add((rc.GetInt64(0), rc.GetString(1), rc.GetString(2), rc.GetString(3)));
                }
            }

            // Множество задач, которые уже попали в сгруппированные карточки
            var handledTaskIds = new HashSet<long>();

            // Группируем карты по PackageId (комплектам раскроев)
            var packageGroups = allSheets.GroupBy(s => s.PkgId);

            foreach (var pkg in packageGroups)
            {
                var sheetIds = pkg.Select(s => s.Id).ToList();
                var linkedTasks = sheetTasks.Where(st => sheetIds.Contains(st.SheetId)).ToList();

                // Находим задания, которые закрывает этот комплект
                var matchedTasks = tasks.Where(t => 
                    linkedTasks.Any(lt => lt.Order.Equals(t.Order, StringComparison.OrdinalIgnoreCase) && 
                                          lt.Product.Equals(t.Product, StringComparison.OrdinalIgnoreCase))
                ).ToList();

                if (matchedTasks.Count == 0 && linkedTasks.Count > 0)
                {
                    // Если в плане точных записей нет, берем данные из связей карт
                    matchedTasks = linkedTasks.Select(lt => (0L, lt.Order, lt.Product, lt.Unit, "Плановое задание")).Distinct().ToList();
                }

                if (matchedTasks.Count == 0) continue;

                foreach (var mt in matchedTasks) 
                    if (mt.Id > 0) handledTaskIds.Add(mt.Id);

                // ОБЪЕДИНЕНИЕ: заказы, изделия и узлы через перенос строки
                var distinctOrders = matchedTasks.Select(m => m.Order).Distinct().ToList();
                var distinctProducts = matchedTasks.Select(m => m.Product).Distinct().ToList();
                var distinctUnits = matchedTasks.Select(m => m.Unit).Distinct().ToList();

                var item = new DashboardOrderItem
                {
                    DateStr = date.ToString("dd.MM.yyyy"),
                    CustomerSolution = string.Join("\n", distinctOrders),
                    ProductTitle = string.Join("\n", distinctProducts),
                    SerialNumber = string.Join("\n", distinctUnits),
                    Quantity = matchedTasks.Count,
                    Note = matchedTasks[0].Comment,
                    IsExpanded = true
                };

                // Читаем РЕАЛЬНЫЕ ДЕТАЛИ из nesting_parts для этих листов
                var partsList = new List<(string Name, double Length, double Width, int Qty, string SheetName, string SheetStatus)>();
                using (var cmdP = conn.CreateCommand())
                {
                    cmdP.CommandText = @"
                        SELECT p.PartName, p.Length, p.Width, p.Quantity, s.NestingName, s.Status
                        FROM nesting_parts p
                        JOIN cutting_sheets s ON s.Id = p.SheetId
                        WHERE s.Id = ANY(@ids)
                        ORDER BY p.PartNumber;";
                    cmdP.Parameters.AddWithValue("@ids", sheetIds.ToArray());
                    using var rp = cmdP.ExecuteReader();
                    while (rp.Read())
                    {
                        partsList.Add((
                            rp.IsDBNull(0) ? "" : rp.GetString(0),
                            rp.IsDBNull(1) ? 0 : rp.GetDouble(1),
                            rp.IsDBNull(2) ? 0 : rp.GetDouble(2),
                            rp.IsDBNull(3) ? 1 : rp.GetInt32(3),
                            rp.GetString(4),
                            rp.GetString(5)
                        ));
                    }
                }

                // Группируем по толщине и материалу
                var firstSheet = pkg.First();
                var groupModel = new ThicknessGroupModel
                {
                    Material = $"Толщина: {firstSheet.Th:0.#} мм ({firstSheet.Mat})",
                    Thickness = firstSheet.Th
                };

                // Заполняем СТРОКИ ДЕТАЛЯМИ ИЗ PDF
                foreach (var p in partsList)
                {
                    groupModel.Items.Add(new CuttingProgramItem
                    {
                        Cipher = p.Name,               // Название детали из PDF
                        Length = p.Length,             // Длина детали
                        Width = p.Width,               // Ширина детали
                        ProgramCode = p.SheetName,     // УП (Код) — 04S_SL01, 04S_SL02
                        Quantity = p.Qty,
                        IsReady = p.SheetStatus == "Выполнен"
                    });
                }

                item.ThicknessGroups.Add(groupModel);

                // Заполняем детали для режима "По деталям"
                foreach (var mt in matchedTasks)
                {
                    var pStatuses = GetPartStatuses(mt.Order, mt.Product, mt.Unit);
                    foreach (var ps in pStatuses)
                    {
                        if (!item.PartRows.Any(r => r.DrawingNumber == ps.DrawingNumber && r.PartName == ps.PartName))
                            item.PartRows.Add(ps);
                    }
                }

                result.Add(item);
            }

            // 4. Оставшиеся плановые задания, под которые УП еще не загружены
            foreach (var t in tasks.Where(t => !handledTaskIds.Contains(t.Id)))
            {
                var emptyItem = new DashboardOrderItem
                {
                    DateStr = date.ToString("dd.MM.yyyy"),
                    CustomerSolution = t.Order,
                    ProductTitle = t.Product,
                    SerialNumber = t.Unit,
                    Quantity = 1,
                    Note = t.Comment,
                    IsExpanded = true
                };

                var emptyGroup = new ThicknessGroupModel { Material = "Ожидает загрузки УП", Thickness = 0 };
                emptyGroup.Items.Add(new CuttingProgramItem
                {
                    Cipher = "УП не загружена", Length = 0, Width = 0, ProgramCode = "-", IsReady = false
                });
                emptyItem.ThicknessGroups.Add(emptyGroup);
                result.Add(emptyItem);
            }

            OfflineSyncService.SetOnline(true);
            OfflineSyncService.SaveSnapshot(cacheKey, result.ToList());
            return result;
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            OfflineSyncService.MarkOffline();
            var cached = OfflineSyncService.LoadSnapshot<List<DashboardOrderItem>>(cacheKey);
            return cached != null ? new ObservableCollection<DashboardOrderItem>(cached) : result;
        }
    }
    public bool DeleteSheetSafe(int sheetId)
    {
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            void Exec(string sql)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@id", sheetId);
                cmd.ExecuteNonQuery();
            }

            Exec("DELETE FROM nesting_parts WHERE SheetId = @id;");
            Exec("DELETE FROM cutting_files WHERE SheetId = @id;");
            Exec("DELETE FROM cutting_sheets WHERE Id = @id;");

            tx.Commit();
            return true;
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            return false;
        }
    }


    /// <summary>Статусы деталей по узлу задания: деталь вырезана, когда выполнены ВСЕ содержащие её карты.</summary>
    public List<PartStatusRow> GetPartStatuses(string order, string product, string unit)
    {
        var rows = new List<PartStatusRow>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT p.PartName,
                COUNT(DISTINCT s.Id) AS total,
                COUNT(DISTINCT CASE WHEN s.Status = 'Выполнен' THEN s.Id END) AS done
            FROM nesting_parts p
            JOIN cutting_sheets s ON s.Id = p.SheetId
            WHERE (
                -- Детали, привязанные именно к этому узлу
                (p.OrderName = @o AND 
                    (p.ProductName = @p OR @p = 'Все изделия' OR @p = '') AND 
                    (p.UnitName = @u OR @u = 'Весь узел' OR @u = ''))
                OR
                -- Резерв для старых карт, где у деталей не было OrderName
                (p.OrderName IS NULL AND 
                    (s.OrderName = @o OR EXISTS (SELECT 1 FROM cutting_sheet_tasks cst WHERE cst.SheetId = s.Id AND cst.OrderName = @o)) AND
                    (s.ProductName = @p OR @p = 'Все изделия' OR @p = '') AND
                    (s.UnitName = @u OR @u = 'Весь узел' OR @u = ''))
            )
            GROUP BY p.PartName
            ORDER BY p.PartName;";

        cmd.Parameters.AddWithValue("@o", order ?? "");
        cmd.Parameters.AddWithValue("@p", product ?? "");
        cmd.Parameters.AddWithValue("@u", unit ?? "");

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string partName = r.IsDBNull(0) ? "" : r.GetString(0);
            rows.Add(new PartStatusRow
            {
                PartName = partName,
                DrawingNumber = NestingPackageParser.ExtractDrawingNumber(partName),
                SheetsTotal = r.GetInt32(1),
                SheetsDone = r.GetInt32(2)
            });
        }
        return rows;
    }

    /// <summary>Обратный поиск: на каких картах (УП) лежит конкретная деталь.</summary>
    public List<(int SheetId, string SheetName, string Status)> GetSheetsForPart(string drawingNumber)
    {
        var list = new List<(int, string, string)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT s.Id, s.NestingName, s.Status
            FROM nesting_parts p 
            JOIN cutting_sheets s ON s.Id = p.SheetId
            WHERE p.PartName ILIKE @pat
            ORDER BY s.NestingName;";
        cmd.Parameters.AddWithValue("@pat", "%" + drawingNumber + "%");

        using var r = cmd.ExecuteReader();
        while (r.Read()) 
        {
            list.Add((r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)));
        }
        return list;
    }
    // ======================= ПАНЕЛЬ (объединённые карточки) =======================

    // ======================= ПАНЕЛЬ (объединённые карточки) =======================

    // ======================= ПАНЕЛЬ (объединённые карточки) =======================

    public ObservableCollection<DashboardCardModel> GetDashboardCards(DateTime date)
    {
        EnsureTables();
        string dateSql = date.ToString("yyyy-MM-dd");
        string cacheKey = "dash_cards_" + dateSql;
        var result = new ObservableCollection<DashboardCardModel>();

        try
        {
            using var conn = Open();

            // 1. Задания на дату из PlannedTasks
            var tasks = new List<(string Order, string Product, string Unit, string SheetFormat, string Comment)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT OrderName, ProductName, UnitName, SheetFormat, Comment FROM PlannedTasks WHERE PlannedDate = @d ORDER BY OrderName, ProductName, UnitName;";
                cmd.Parameters.AddWithValue("@d", dateSql);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    tasks.Add((
                        r.IsDBNull(0) ? "" : r.GetString(0),
                        r.IsDBNull(1) ? "" : r.GetString(1),
                        r.IsDBNull(2) ? "" : r.GetString(2),
                        r.IsDBNull(3) ? "" : r.GetString(3),
                        r.IsDBNull(4) ? "" : r.GetString(4)
                    ));
                }
            }

            if (tasks.Count == 0)
                return result;

            // 2. Группируем задания: один заказ = одна карточка
            foreach (var orderGroup in tasks.GroupBy(t => t.Order, StringComparer.OrdinalIgnoreCase))
            {
                string orderName = orderGroup.Key;
                var card = new DashboardCardModel
                {
                    DateStr = date.ToString("dd.MM.yyyy"),
                    OrderName = orderName,
                    Note = orderGroup.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Comment)).Comment ?? "",
                    IsExpanded = true
                };

                // Изделия и узлы строго этой карточки
                var cardProducts = orderGroup
                    .Select(t => t.Product)
                    .Where(p => !string.IsNullOrWhiteSpace(p) && p != "Все изделия")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var cardUnits = orderGroup
                    .Select(t => t.Unit)
                    .Where(u => !string.IsNullOrWhiteSpace(u) && u != "Весь узел")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var prodGroup in orderGroup.GroupBy(t => t.Product, StringComparer.OrdinalIgnoreCase))
                {
                    var line = new DashboardProductLine { ProductTitle = prodGroup.Key };
                    foreach (var u in prodGroup.Select(t => t.Unit).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(u))
                            line.Units.Add(u);
                    }
                    card.Products.Add(line);
                }

                // 3. Выбираем детали ТОЛЬКО из загруженных карт ЭТИХ изделий (из nesting_parts)
                //    ВАЖНО: PlannedTasks.OrderName хранит СЫРОЕ название из ведомости (с эмодзи/
                //    префиксами "Заказ: " и т.п.), а nesting_parts.OrderName уже очищено через
                //    CleanTextFromEmoji() в момент сохранения УП. Без нормализации здесь строки
                //    никогда не совпадают, и UpList/SheetsTotal остаются пустыми молча —
                //    поэтому чистим сторону PlannedTasks той же логикой перед сравнением.
                string cleanOrderName = CleanForCompare(orderName);
                var cleanCardUnits = cardUnits.Select(CleanForCompare).ToArray();

                // ВРЕМЕННАЯ ДИАГНОСТИКА — уберите после проверки.
                System.Diagnostics.Debug.WriteLine($"[Dashboard] Ищем: OrderName='{cleanOrderName}' (сырое='{orderName}'), Units=[{string.Join(", ", cleanCardUnits)}]");

                using (var cmdN = conn.CreateCommand())
                {
                    // ВАЖНО: фильтр по ProductName убран — в текущей цепочке резервации
                    // (ToggleReservation -> ImportKitAsync: "string unit = prod;") поле
                    // ProductName в nesting_parts всегда дублирует значение UnitName
                    // ("Узел"), а не настоящее "Изделие". Фильтровать по нему бессмысленно —
                    // он никогда не совпадёт с настоящим ProductName из PlannedTasks. UnitName
                    // и так однозначно определяет конкретный завод.№, этого достаточно.
                    // TODO: когда дойдут руки — научить резервацию различать все 3 уровня
                    // (Заказ -> Изделие -> Узел), а не 2, и вернуть фильтр по Product тоже.
                    cmdN.CommandText = @"
                        SELECT 
                            p.PartName,
                            MAX(CASE WHEN p.Length > 0 AND p.Width > 0 THEN p.Length || 'x' || p.Width ELSE '' END) AS Dims,
                            SUM(p.Quantity) AS TotalQty,
                            STRING_AGG(DISTINCT s.NestingName, ', ' ORDER BY s.NestingName) AS UpList,
                            COUNT(DISTINCT s.Id) AS SheetsTotal,
                            COUNT(DISTINCT CASE WHEN s.Status = 'Выполнен' THEN s.Id END) AS SheetsDone
                        FROM nesting_parts p
                        JOIN cutting_sheets s ON s.Id = p.SheetId
                        WHERE p.OrderName = @o
                          AND (cardUnitsCount = 0 OR p.UnitName = ANY(@units))
                        GROUP BY p.PartName
                        ORDER BY p.PartName;";

                    cmdN.CommandText = cmdN.CommandText
                        .Replace("cardUnitsCount = 0", cardUnits.Count == 0 ? "TRUE" : "FALSE");

                    cmdN.Parameters.AddWithValue("@o", cleanOrderName);
                    cmdN.Parameters.AddWithValue("@units", cleanCardUnits);

                    using var rn = cmdN.ExecuteReader();
                    while (rn.Read())
                    {
                        string partName = rn.IsDBNull(0) ? "" : rn.GetString(0);
                        string dims = rn.IsDBNull(1) ? "" : rn.GetString(1);
                        int qty = rn.IsDBNull(2) ? 1 : Convert.ToInt32(rn.GetInt64(2));
                        string upList = rn.IsDBNull(3) ? "" : rn.GetString(3);
                        int sheetsTotal = rn.IsDBNull(4) ? 0 : Convert.ToInt32(rn.GetInt64(4));
                        int sheetsDone = rn.IsDBNull(5) ? 0 : Convert.ToInt32(rn.GetInt64(5));

                        card.Parts.Add(new DashPartRow
                        {
                            Cipher = partName,
                            Dimensions = dims,
                            Quantity = qty,
                            UpList = upList,
                            SheetsTotal = sheetsTotal,
                            SheetsDone = sheetsDone
                        });
                    }
                }

                // 4. Fallback: если УП для этих изделий ещё НЕ загружались — берём детали ТОЛЬКО этих изделий из ведомости
                if (card.Parts.Count == 0)
                {
                    using (var cmdD = conn.CreateCommand())
                    {
                        cmdD.CommandText = @"
                            SELECT 
                                DrawingNumber, 
                                Name, 
                                Dimensions, 
                                SUM(Quantity)
                            FROM Details
                            WHERE OrderName = @o 
                              AND (cardProductsCount = 0 OR ProductName = ANY(@prods))
                              AND (cardUnitsCount = 0 OR UnitName = ANY(@units))
                              AND (DetailType = 'Плазма' OR DetailType = 'Лист' OR DetailType IS NULL)
                            GROUP BY DrawingNumber, Name, Dimensions
                            ORDER BY DrawingNumber;";

                        cmdD.CommandText = cmdD.CommandText
                            .Replace("cardProductsCount = 0", cardProducts.Count == 0 ? "TRUE" : "FALSE")
                            .Replace("cardUnitsCount = 0", cardUnits.Count == 0 ? "TRUE" : "FALSE");

                        cmdD.Parameters.AddWithValue("@o", orderName);
                        cmdD.Parameters.AddWithValue("@prods", cardProducts.ToArray());
                        cmdD.Parameters.AddWithValue("@units", cardUnits.ToArray());

                        using var rd = cmdD.ExecuteReader();
                        while (rd.Read())
                        {
                            string drawing = rd.IsDBNull(0) ? "" : rd.GetString(0);
                            string name = rd.IsDBNull(1) ? "" : rd.GetString(1);
                            string dims = rd.IsDBNull(2) ? "" : rd.GetString(2);
                            int qty = rd.IsDBNull(3) ? 1 : Convert.ToInt32(rd.GetInt64(3));

                            string cipher = string.IsNullOrWhiteSpace(drawing) ? name : $"{drawing} — {name}";

                            card.Parts.Add(new DashPartRow
                            {
                                Cipher = cipher,
                                Dimensions = dims,
                                Quantity = qty,
                                UpList = "",
                                SheetsTotal = 0,
                                SheetsDone = 0
                            });
                        }
                    }
                }

                card.OnPropertyChangedPublic();
                result.Add(card);
            }

            OfflineSyncService.SetOnline(true);
            OfflineSyncService.SaveSnapshot(cacheKey, result.ToList());
            return result;
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            OfflineSyncService.MarkOffline();
            var cached = OfflineSyncService.LoadSnapshot<List<DashboardCardModel>>(cacheKey);
            return cached != null ? new ObservableCollection<DashboardCardModel>(cached) : result;
        }
    }


    // ======================= ПРИЧИНЫ НЕДОРЕЗА (для мастера/Панели) =======================

    /// <summary>Все причины недореза на дату разом — одним запросом на весь таймлайн/карточку,
    /// а не по одной на заказ (иначе загрузка 10 плашек таймлайна дала бы 10×N запросов).
    /// Ключ — сырое OrderName, как оно лежит в PlannedTasks/DashboardCardModel.OrderName.</summary>
    public Dictionary<string, string> GetDelayReasonsForDate(DateTime date)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            EnsureTables();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT OrderName, Reason FROM CuttingDelayReasons WHERE PlannedDate = @d;";
            cmd.Parameters.AddWithValue("@d", date.ToString("yyyy-MM-dd"));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                dict[r.IsDBNull(0) ? "" : r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            OfflineSyncService.MarkOffline();
        }
        return dict;
    }

    public string? GetDelayReason(string orderName, DateTime date)
        => GetDelayReasonsForDate(date).TryGetValue(orderName ?? "", out var reason) ? reason : null;

    /// <summary>Записывает/обновляет причину недореза — вызывается из будущей вкладки мастера.
    /// Технологи в "Панели" эту причину только читают (GetDelayReasonsForDate).</summary>
    public bool SetDelayReasonSafe(string orderName, DateTime date, string reason)
    {
        try
        {
            EnsureTables();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO CuttingDelayReasons (OrderName, PlannedDate, Reason, CreatedAt)
                VALUES (@o, @d, @r, @c)
                ON CONFLICT (OrderName, PlannedDate)
                DO UPDATE SET Reason = EXCLUDED.Reason, CreatedAt = EXCLUDED.CreatedAt;";
            cmd.Parameters.AddWithValue("@o", orderName ?? "");
            cmd.Parameters.AddWithValue("@d", date.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@r", reason ?? "");
            cmd.Parameters.AddWithValue("@c", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            cmd.ExecuteNonQuery();
            DatabaseService.NotifyDatabaseUpdated("cuttingdelayreasons");
            return true;
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            OfflineSyncService.MarkOffline();
            return false;
        }
    }

    // ======================= СТАТУС ДНЯ ЦЕЛИКОМ (плашка над датой в Панели) =======================

    /// <summary>Статус вырезки за день "в целом" — для плашки над числом даты в таймлайне
    /// Панели. Правило: будущие дни не оцениваем (день ещё не наступил — рано спрашивать);
    /// день без единого задания — NoTasks; иначе берём ХУДШИЙ статус среди заказов дня — если
    /// хоть один заказ не вырезан и без причины, весь день "не успели", даже если остальные уже
    /// готовы; если у всех невырезанных заказов причина от мастера уже есть — день жёлтый; и
    /// только если вырезано вообще всё — день зелёный.
    /// Переиспользует GetDashboardCards (уже проверенная логика сопоставления УП/деталей), а не
    /// дублирует её здесь заново — цена лишних 10 вызовов при (пере)загрузке окна дат приемлема.</summary>
    public DayCutStatus GetDayCutStatus(DateTime date)
    {
        if (date.Date > DateTime.Today) return DayCutStatus.NotApplicable;
        try
        {
            var cards = GetDashboardCards(date);
            if (cards.Count == 0) return DayCutStatus.NoTasks;

            var reasons = GetDelayReasonsForDate(date);
            bool anyNotDone = false;
            bool anyNotDoneMissingReason = false;
            foreach (var card in cards)
            {
                if (!card.AllPartsDone)
                {
                    anyNotDone = true;
                    if (!reasons.ContainsKey(card.OrderName))
                        anyNotDoneMissingReason = true;
                }
            }

            if (!anyNotDone) return DayCutStatus.Done;
            return anyNotDoneMissingReason ? DayCutStatus.NotDoneNoReason : DayCutStatus.NotDoneWithReason;
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            return DayCutStatus.NotApplicable;
        }
    }

    /// <summary>Статусы всех дней месяца разом — для квадратиков в "Метриках" (директор).
    /// Один вызов GetDayCutStatus на каждый день месяца — те же 28-31 запросов, что и таймлайн
    /// Панели/Мастера делает на 10 дней, так что цена та же по духу, просто на месяц шире.</summary>
    public Dictionary<DateTime, DayCutStatus> GetDayCutStatusesForMonth(int year, int month)
    {
        var result = new Dictionary<DateTime, DayCutStatus>();
        int daysInMonth = DateTime.DaysInMonth(year, month);
        for (int d = 1; d <= daysInMonth; d++)
        {
            var date = new DateTime(year, month, d);
            result[date] = GetDayCutStatus(date);
        }
        return result;
    }

    // ======================= ВРЕМЕННАЯ СТАТИСТИКА (вкладка "Метрики", директор) =======================

    /// <summary>Одна карта раскроя с её временными метками — сырьё для графиков "Метрик":
    /// когда УП создана (загружена технологом), когда взята оператором в работу (выгрузка на
    /// флешку) и когда отмечена выполненной. Время до появления плана здесь ЕЩЁ НЕТ — оно
    /// зависит от PlannedTasks.CreatedAt, которого пока нет в схеме (см. DatabaseService).</summary>
    public class SheetTimingRow
    {
        public string NestingName { get; set; } = "";
        public string OrderName { get; set; } = "";
        public DateTime? CreatedAt { get; set; }
        public DateTime? UsbExportedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        /// <summary>Часы от загрузки УП до взятия в работу оператором.</summary>
        public double? HoursToTakenInWork =>
            CreatedAt.HasValue && UsbExportedAt.HasValue ? (UsbExportedAt.Value - CreatedAt.Value).TotalHours : null;

        /// <summary>Часы от взятия в работу до фактического выполнения (резки на станке).</summary>
        public double? HoursToCompleted =>
            UsbExportedAt.HasValue && CompletedAt.HasValue ? (CompletedAt.Value - UsbExportedAt.Value).TotalHours : null;
    }

    /// <summary>Все карты раскроя, у которых CreatedAt попадает в диапазон [from, to] — для
    /// построения графиков за период в "Метриках".</summary>
    public List<SheetTimingRow> GetSheetTimingStats(DateTime from, DateTime to)
    {
        var rows = new List<SheetTimingRow>();
        try
        {
            EnsureTables();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT NestingName, OrderName, CreatedAt, UsbExportedAt, CompletedAt
                FROM cutting_sheets
                WHERE CreatedAt IS NOT NULL
                  AND CreatedAt::timestamp >= @from AND CreatedAt::timestamp < @to
                ORDER BY CreatedAt;";
            cmd.Parameters.AddWithValue("@from", from.Date);
            cmd.Parameters.AddWithValue("@to", to.Date.AddDays(1));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new SheetTimingRow
                {
                    NestingName = r.IsDBNull(0) ? "" : r.GetString(0),
                    OrderName = r.IsDBNull(1) ? "" : r.GetString(1),
                    CreatedAt = ParseNullableTimestamp(r, 2),
                    UsbExportedAt = ParseNullableTimestamp(r, 3),
                    CompletedAt = ParseNullableTimestamp(r, 4)
                });
            }
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            OfflineSyncService.MarkOffline();
        }
        return rows;
    }

    /// <summary>Строка сравнения "норма из ведомости vs факт по времени резки" для одного узла
    /// заказа — то, что просил технолог для директора во вкладке "Метрики": не бейдж времени
    /// оператору, а сводка, по какому узлу реально перерасходовали время против ведомости.</summary>
    public class UnitTimeComparisonRow
    {
        public string OrderName { get; set; } = "";
        public string ProductName { get; set; } = "";
        public string UnitName { get; set; } = "";

        /// <summary>Норма из ведомости (UnitLaborNorms.PlasmaCuttingSeconds, переведено в часы).
        /// Null — на этот узел ведомость ещё не загружали, сравнивать не с чем.</summary>
        public double? NormHours { get; set; }

        /// <summary>Факт — сумма РАБОЧЕГО времени (WorkTimeCalculator, без ночей и выходных без
        /// плана) от выгрузки на флешку до отметки "Выполнено", по всем УП этого узла, которые
        /// уже полностью прошли этот путь. Порядок реза внутри узла не важен — суммируем в целом
        /// по узлу, как и предлагал технолог.</summary>
        public double FactHours { get; set; }

        /// <summary>Сколько УП этого узла вошло в факт (уже выгружены и отмечены выполненными).</summary>
        public int SheetsCounted { get; set; }

        public bool HasNorm => NormHours.HasValue;
        public double? DeltaHours => HasNorm ? FactHours - NormHours!.Value : null;
        /// <summary>Факт в процентах от нормы — 100% = уложились ровно по ведомости.</summary>
        public double? PercentOfNorm => HasNorm && NormHours!.Value > 0 ? 100.0 * FactHours / NormHours.Value : null;
    }

    /// <summary>Сравнение нормы (ведомость, UnitLaborNorms) и факта (реальное рабочее время резки
    /// по cutting_sheets.UsbExportedAt→CompletedAt) по каждому узлу заказа, у которого в диапазоне
    /// [from, to] есть хотя бы одна полностью выполненная УП. Диапазон — по CompletedAt, так же,
    /// как остальная месячная статистика на вкладке "Метрики".</summary>
    public List<UnitTimeComparisonRow> GetUnitTimeComparison(DateTime from, DateTime to)
    {
        var result = new List<UnitTimeComparisonRow>();
        try
        {
            EnsureTables();

            var sheets = new List<(string Order, string Product, string Unit, DateTime UsbAt, DateTime Done)>();
            using (var conn = Open())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT OrderName, ProductName, UnitName, UsbExportedAt, CompletedAt
                    FROM cutting_sheets
                    WHERE UsbExportedAt IS NOT NULL AND CompletedAt IS NOT NULL
                      AND CompletedAt >= @from AND CompletedAt < @to;";
                cmd.Parameters.AddWithValue("@from", from.Date);
                cmd.Parameters.AddWithValue("@to", to.Date.AddDays(1));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var usbAt = ParseNullableTimestamp(r, 3);
                    var done = ParseNullableTimestamp(r, 4);
                    if (!usbAt.HasValue || !done.HasValue) continue;
                    sheets.Add((
                        r.IsDBNull(0) ? "" : r.GetString(0),
                        r.IsDBNull(1) ? "" : r.GetString(1),
                        r.IsDBNull(2) ? "" : r.GetString(2),
                        usbAt.Value, done.Value));
                }
            }

            // Суммируем факт по узлу — не по отдельной УП: одному узлу может принадлежать
            // несколько листов/карт раскроя.
            var factByUnit = new Dictionary<(string Order, string Product, string Unit), (double Hours, int Count)>();
            foreach (var s in sheets)
            {
                double hours = WorkTimeCalculator.WorkingHoursElapsed(s.UsbAt, s.Done);
                var key = (s.Order, s.Product, s.Unit);
                factByUnit.TryGetValue(key, out var agg);
                factByUnit[key] = (agg.Hours + hours, agg.Count + 1);
            }

            var norms = new Dictionary<(string Order, string Product, string Unit), double>();
            using (var conn = Open())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT OrderName, ProductName, UnitName, PlasmaCuttingSeconds FROM UnitLaborNorms;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var key = (r.IsDBNull(0) ? "" : r.GetString(0),
                               r.IsDBNull(1) ? "" : r.GetString(1),
                               r.IsDBNull(2) ? "" : r.GetString(2));
                    norms[key] = r.GetDouble(3) / 3600.0;
                }
            }

            foreach (var kv in factByUnit)
            {
                norms.TryGetValue(kv.Key, out var normHours);
                result.Add(new UnitTimeComparisonRow
                {
                    OrderName = kv.Key.Order,
                    ProductName = kv.Key.Product,
                    UnitName = kv.Key.Unit,
                    NormHours = norms.ContainsKey(kv.Key) ? normHours : (double?)null,
                    FactHours = kv.Value.Hours,
                    SheetsCounted = kv.Value.Count
                });
            }

            // Сначала самые проблемные узлы (наибольший перерасход против нормы) — директору
            // важнее увидеть их первыми, а не по алфавиту.
            result = result.OrderByDescending(r => r.PercentOfNorm ?? -1).ToList();
        }
        catch (Exception ex) when (DatabaseService.IsConnectionError(ex))
        {
            OfflineSyncService.MarkOffline();
        }
        return result;
    }

    /// <summary>CreatedAt хранится как TEXT (историческая причина — совместимость со старым
    /// кодом), а UsbExportedAt/CompletedAt — уже настоящий TIMESTAMP; читаем оба варианта
    /// без падения на формате.</summary>
    private static DateTime? ParseNullableTimestamp(NpgsqlDataReader r, int col)
    {
        if (r.IsDBNull(col)) return null;
        try
        {
            var val = r.GetValue(col);
            if (val is DateTime dt) return dt;
            if (DateTime.TryParse(val?.ToString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                return parsed;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Та же очистка, что и CleanTextFromEmoji() в NestingTehnologistManageViewModel — используется
    /// здесь, чтобы сравнивать "сырое" PlannedTasks.OrderName с уже очищенным nesting_parts.OrderName
    /// на равных, а не гадать, в какую сторону разъехались строки.
    /// </summary>
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

public class NestingStatusPayload
{
    public int SheetId { get; set; }
    public string Status { get; set; } = "";
}