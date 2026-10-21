using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Npgsql;

using BKMsystem.Models;
using BKMsystem.Services;

namespace BKMsystem.ViewModels;

public partial class NestingTehnologistManageViewModel : ViewModelBase
{
    private readonly DatabaseService _dbService;
    private readonly DxfCollectorService _dxfCollector;
    private readonly NestGeneratorService _nestGenerator;
    private readonly NestingRepository _repo;

    public ICommand CreateDxfPackageCommand { get; }
    public ObservableCollection<StatementItem> PlanTree { get; set; } = new();

    // Левая колонка: плановые "листы" (Дата -> Заказ -> Изделие -> Лист по толщине),
    // по которым УП ещё НЕ загружены. Загруженные автоматически исчезают.
    public ObservableCollection<PlanTaskGroupNode> PlanTaskTree { get; } = new();

    private PlanTaskGroupNode? _selectedTaskNode;
    public PlanTaskGroupNode? SelectedTaskNode
    {
        get => _selectedTaskNode;
        set { _selectedTaskNode = value; OnPropertyChanged(); }
    }

    /// <summary>"Один ПК — один состав": резервация листов привязана к имени машины,
    /// а не к логину — чтобы двое на одном ПК не путались, а разные ПК не пересекались.</summary>
    public string CurrentMachineId { get; } = Environment.MachineName;

    private string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public NestingTehnologistManageViewModel()
    {
        _dbService = new DatabaseService();
        _dxfCollector = new DxfCollectorService();
        _nestGenerator = new NestGeneratorService();
        _repo = new NestingRepository();
        _repo.EnsureTables();

        CreateDxfPackageCommand = new AsyncRelayCommand(async () => await CreateDxfPackageAsync());
        LoadData();
        LoadPlanTaskTree();

        // Автообновление: новый план / новая загруженная УП — оба дерева перечитываем
        DatabaseService.OnDatabaseUpdated += (tableName) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (tableName.Equals("plannedtasks", StringComparison.OrdinalIgnoreCase) ||
                            tableName.Equals("details", StringComparison.OrdinalIgnoreCase))
                        {
                            LoadData();
                        }
                        else if (tableName.Equals("cutting_sheets", StringComparison.OrdinalIgnoreCase))
                        {
                            LoadPlanTaskTree();
                        }
                        else if (tableName.Equals("sheet_reservations", StringComparison.OrdinalIgnoreCase))
                        {
                            ApplyReservationState();
                        }
                    });
                };
    }

    // ==================================================================
    //  ЗАГРУЗКА КОМПЛЕКТА УП — ОДНА кнопка.
    //  Комплект: НЕСКОЛЬКО .MPG (несколько раскроев одного листа/толщины)
    //  + ОДИН общий .PDF (отчёт) + ОДИН общий .XLSX (карта деталей, для сверки).
    //  Каждая .MPG становится отдельной картой (слотом); PDF и Excel
    //  прикладываются к каждой карте комплекта.
    // ==================================================================
    [RelayCommand]
    private async Task ImportKitAsync()
    {
        Debug.WriteLine("========== [ImportKit] КОМАНДА ВЫЗВАНА ==========");
        StatusMessage = "▶ Кнопка нажата, начинаю обработку...";

        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var mainWindow = desktop?.MainWindow;

        if (mainWindow is null)
        {
            StatusMessage = "⛔ Ошибка: не удалось определить главное окно приложения.";
            return;
        }

        var topLevel = TopLevel.GetTopLevel(mainWindow);
        if (topLevel?.StorageProvider is null)
        {
            StatusMessage = "⛔ Ошибка: StorageProvider недоступен.";
            return;
        }

        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выберите комплект: УП (.MPG) + отчёт (.PDF) + карта (.XLSX)",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Комплект УП (*.mpg, *.pdf, *.xlsx, *.xls)")
                    {
                        Patterns = new[] { "*.mpg", "*.pdf", "*.xlsx", "*.xls" }
                    }
                }
            });

            if (files == null || files.Count == 0)
            {
                StatusMessage = "Файлы не выбраны.";
                return;
            }

            var mpgPaths = new List<string>();
            var pdfPaths = new List<string>();
            string? xlsx = null;
            foreach (var f in files)
            {
                string p = f.Path.LocalPath;
                switch (Path.GetExtension(p).ToLowerInvariant())
                {
                    case ".mpg": mpgPaths.Add(p); break;
                    case ".pdf": pdfPaths.Add(p); break;
                    case ".xlsx": case ".xls": xlsx = p; break;
                }
            }

            mpgPaths.Sort(StringComparer.OrdinalIgnoreCase);

            // ValidatePackage сейчас проверяет один PDF — для базовой sanity-проверки (есть ли
            // вообще PDF, MPG) достаточно первого; полноценная проверка на нехватку конкретных
            // карт всё равно случится ниже, когда будем сопоставлять MPG с разобранными картами.
            string? validationError = NestingRepository.ValidatePackage(mpgPaths, pdfPaths.FirstOrDefault(), xlsx);
            if (validationError != null)
            {
                StatusMessage = "⛔ " + validationError;
                return;
            }

            if (pdfPaths.Count == 0)
            {
                StatusMessage = "⛔ В комплекте отсутствует PDF-отчёт AJANCAM. Выберите один или несколько .PDF файлов.";
                return;
            }

            try
            {
                StatusMessage = "Парсинг PDF отчёта AJAN...";

                // 1. Разбираем КАЖДЫЙ выбранный PDF отдельно и объединяем результаты — так
                //    одинаково работает и один общий многостраничный PDF (несколько карт внутри
                //    одного файла), и несколько отдельных однострочных PDF (по одному на карту).
                //    Раньше тут был один string? pdf — при выборе нескольких PDF выживал только
                //    последний, остальные два тихо терялись.
                var parsedSheets = new List<ParsedAjanSheet>();
                foreach (var onePdfPath in pdfPaths)
                {
                    var sheetsFromThisPdf = await Task.Run(() => AjanPdfParser.ParsePdf(onePdfPath));
                    parsedSheets.AddRange(sheetsFromThisPdf);
                }

                if (parsedSheets.Count == 0)
                {
                    StatusMessage = "⛔ Не удалось найти карты раскроя в PDF отчёте.";
                    return;
                }

                var leftSheets = GetLeftThicknessNodes();
                if (leftSheets.Count == 0)
                {
                    StatusMessage = "⛔ Сначала отметьте листы в правом дереве — они появятся слева.";
                    return;
                }

                // 2. Собираем карту чертежей всех выбранных узлов из базы
                var drawingToUnitMap = new Dictionary<string, (string Order, string Product, string Unit)>(StringComparer.OrdinalIgnoreCase);
                var targetUnits = new List<SheetTargetUnit>();

                foreach (var ws in leftSheets)
                {
                    string ord = CleanTextFromEmoji(ws.OrderName);
                    string prod = CleanTextFromEmoji(ws.ProductName);
                    string unit = prod;
                    int? tid = _dbService.FindTaskId(ord, prod, unit);

                    targetUnits.Add(new SheetTargetUnit
                    {
                        TaskId = tid,
                        OrderName = ord,
                        ProductName = prod,
                        UnitName = unit
                    });

                    var details = CollectDetailsFromPlan(ws.OrderName, ws.ProductName, ws.Thickness, ws.SteelGrade);
                    foreach (var d in details)
                    {
                        string key = NestingPackageParser.NormalizeDrawingKey(d.DrawingNumber);
                        if (!string.IsNullOrEmpty(key) && !drawingToUnitMap.ContainsKey(key))
                        {
                            drawingToUnitMap[key] = (ord, prod, unit);
                        }
                    }
                }

                int savedCount = 0;
                string packageId = Guid.NewGuid().ToString("N");

                // 3. Сохраняем каждую УП со своими деталями из PDF
                for (int i = 0; i < mpgPaths.Count; i++)
                {
                    string mpgPath = mpgPaths[i];
                    string mpgBase = Path.GetFileNameWithoutExtension(mpgPath);

                    // Сопоставляем УП с секцией в PDF: сначала по имени, иначе по порядку
                    var ps = parsedSheets.FirstOrDefault(s =>
                                 Path.GetFileNameWithoutExtension(s.MpgFileName).Equals(mpgBase, StringComparison.OrdinalIgnoreCase))
                             ?? (i < parsedSheets.Count ? parsedSheets[i] : null);

                    if (ps == null) continue;

                    // Привязываем каждую деталь этой УП к её узлу
                    foreach (var part in ps.Parts)
                    {
                        string drawKey = NestingPackageParser.NormalizeDrawingKey(
                            NestingPackageParser.ExtractDrawingNumber(part.PartName));

                        if (!string.IsNullOrEmpty(drawKey) && drawingToUnitMap.TryGetValue(drawKey, out var unitInfo))
                        {
                            part.OrderName = unitInfo.Order;
                            part.ProductName = unitInfo.Product;
                            part.UnitName = unitInfo.Unit;
                        }
                        else
                        {
                            part.OrderName = targetUnits[0].OrderName;
                            part.ProductName = targetUnits[0].ProductName;
                            part.UnitName = targetUnits[0].UnitName;
                        }
                    }

                    string material = ps.Material;
                    try
                    {
                        var mpgLines = File.ReadLines(mpgPath).Take(4).ToList();
                        if (mpgLines.Count >= 4 && !string.IsNullOrWhiteSpace(mpgLines[3]) &&
                            !mpgLines[3].StartsWith("%") && !mpgLines[3].StartsWith("G"))
                        {
                            string rawMat = mpgLines[3].Trim();
                            if (rawMat.Length <= 20) material = rawMat;
                        }
                    }
                    catch { }

                    // Какой именно PDF прикладывать к этой карте: тот, из которого она реально
                    // разобрана (SourcePdfPath) — при нескольких отдельных PDF у каждой карты
                    // будет СВОЙ файл; при одном общем многостраничном PDF у всех будет один и
                    // тот же файл, и это тоже верно (это буквально их общий отчёт).
                    string sourcePdf = !string.IsNullOrEmpty(ps.SourcePdfPath) ? ps.SourcePdfPath : pdfPaths[0];
                    string pdfLogicalName = pdfPaths.Count > 1
                        ? Path.GetFileName(sourcePdf) // несколько разных PDF — сохраняем их различимыми
                        : "REPORT.pdf";

                    var uploadModel = new NestingUploadModel
                    {
                        PackageId = packageId,
                        NestingName = mpgBase,
                        Material = material,
                        Thickness = (decimal)ps.Thickness,
                        SheetSize = ps.SheetSize,
                        TotalTime = ps.TotalTime,
                        PlannedDate = DateTime.Today.ToString("yyyy-MM-dd"),
                        TaskId = targetUnits[0].TaskId,
                        OrderName = targetUnits[0].OrderName,
                        ProductName = targetUnits[0].ProductName,
                        UnitName = targetUnits[0].UnitName,
                        TargetUnits = targetUnits,
                        Parts = ps.Parts, // Детали ИМЕННО этой УП из PDF
                        FilePaths = new Dictionary<string, string>
                        {
                            { Path.GetFileName(mpgPath), mpgPath },
                            { pdfLogicalName, sourcePdf }
                        }
                    };

                    await Task.Run(() => _repo.SavePackageSafe(uploadModel));
                    savedCount++;
                }

                if (savedCount == 0)
                {
                    StatusMessage = "⛔ Не удалось сопоставить .MPG файлы с картами из PDF.";
                    return;
                }

                // 4. Освобождаем резервации листов
                foreach (var s in leftSheets)
                {
                    string key = !string.IsNullOrEmpty(s.ReservationKey)
                        ? s.ReservationKey
                        : MakeReservationKey(s.OrderName, s.ProductName, s.Thickness.ToString(CultureInfo.InvariantCulture), s.SteelGrade);
                    _repo.ReleaseSheet(key, CurrentMachineId);
                }

                LoadData();
                StatusMessage = $"Успешно загружено карт: {savedCount}. Детали распределены по узлам.";
            }
            catch (Exception ex)
            {
                StatusMessage = "Ошибка загрузки комплекта: " + ex.Message;
                Debug.WriteLine($"[ImportKit] ИСКЛЮЧЕНИЕ (внутренний блок): {ex}");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "⛔ Ошибка вызова диалога: " + ex.Message;
            Debug.WriteLine($"[ImportKit] ИСКЛЮЧЕНИЕ (внешний блок): {ex}");
        }
    }

    /// <summary>Все детали листов из левого дерева должны быть в Excel-карте.</summary>
    private string? ValidateLeftTreePartsInNest(NestingUploadModel model, List<PlanTaskGroupNode> workSheets)
    {
        if (model.Parts.Count == 0)
            return "В комплекте нет Excel-карты деталей — сверка невозможна. Добавьте .XLSX.";

        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in workSheets)
        {
            foreach (var d in CollectDrawingNumbersFromPlan(
                         sheet.OrderName, sheet.ProductName, sheet.Thickness, sheet.SteelGrade))
                required.Add(d);
        }

        if (required.Count == 0)
            return null;

        var inNest = model.Parts
            .Select(p => NestingPackageParser.NormalizeDrawingKey(
                NestingPackageParser.ExtractDrawingNumber(p.PartName)))
            .Where(d => !string.IsNullOrEmpty(d))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = required
            .Select(d => NestingPackageParser.NormalizeDrawingKey(d))
            .Where(d => !string.IsNullOrEmpty(d) && !inNest.Contains(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count == 0) return null;

        return $"В раскрой не попали детали из левого дерева ({missing.Count}): " +
               $"{string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? "…" : "")}. Загрузка отменена.";
    }

    /// <summary>Чертежи деталей листа из сырого плана (даже если лист уже убран из правого дерева).</summary>
    private HashSet<string> CollectDrawingNumbersFromPlan(string orderName, string productName, double thickness, string steel)
    {
        return CollectDetailsFromPlan(orderName, productName, thickness, steel)
            .Select(d => d.DrawingNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private List<(string DrawingNumber, int Quantity, double Thickness, string SteelGrade)> CollectDetailsFromPlan(
        string orderName, string productName, double thickness, string steel)
    {
        var result = new List<(string, int, double, string)>();
        var rawTree = _dbService.GetTechnologistPlanTree();
        string wantOrder = CleanTextFromEmoji(orderName).ToLowerInvariant();
        string wantProduct = CleanTextFromEmoji(productName).ToLowerInvariant();

        foreach (var dateNode in rawTree)
        foreach (var orderNode in dateNode.Children)
        {
            if (CleanTextFromEmoji(orderNode.Title).ToLowerInvariant() != wantOrder) continue;
            foreach (var productNode in GetAllNodes(orderNode))
            {
                if (!productNode.IsGroup || !productNode.Children.Any(c => !c.IsGroup)) continue;
                if (CleanTextFromEmoji(productNode.Title).ToLowerInvariant() != wantProduct) continue;

                foreach (var d in productNode.Children.Where(c => !c.IsGroup))
                {
                    if (Math.Abs(ParseThickness(d.Thickness) - thickness) > 1e-3) continue;
                    if (!string.IsNullOrEmpty(steel) &&
                        !string.Equals(d.SteelGrade, steel, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.IsNullOrWhiteSpace(d.DrawingNumber)) continue;
                    result.Add((d.DrawingNumber.Trim(), d.Quantity, ParseThickness(d.Thickness), d.SteelGrade ?? steel));
                }
            }
        }
        return result;
    }

    private List<PlanTaskGroupNode> GetLeftThicknessNodes()
    {
        var list = new List<PlanTaskGroupNode>();
        foreach (var d in PlanTaskTree)
        foreach (var o in d.Children)
        foreach (var p in o.Children)
        foreach (var t in p.Children)
            if (t.NodeType == "Thickness") list.Add(t);
        return list;
    }

    // ==================================================================
    //  Левая колонка: строится из МОИХ резерваций (листы, убранные справа)
    // ==================================================================
    private void LoadPlanTaskTree()
    {
        var expandedKeys = CaptureExpandedKeys(PlanTaskTree);

        PlanTaskTree.Clear();
        var covered = _repo.GetCoveredSheetKeys();
        var mine = _repo.GetMyReservations(CurrentMachineId);
        if (mine.Count == 0) return;

        var root = new PlanTaskGroupNode
        {
            Title = "В работе на этом ПК",
            NodeType = "Date",
            PlannedDate = DateTime.Today.ToString("yyyy-MM-dd")
        };

        foreach (var byOrder in mine.GroupBy(r => r.OrderName, StringComparer.OrdinalIgnoreCase))
        {
            var orderNode = new PlanTaskGroupNode
            {
                Title = byOrder.Key,
                NodeType = "Order",
                OrderName = byOrder.Key,
                PlannedDate = root.PlannedDate
            };

            foreach (var byProduct in byOrder.GroupBy(r => r.ProductName, StringComparer.OrdinalIgnoreCase))
            {
                var productNode = new PlanTaskGroupNode
                {
                    Title = byProduct.Key,
                    NodeType = "Product",
                    OrderName = byOrder.Key,
                    ProductName = byProduct.Key,
                    PlannedDate = root.PlannedDate
                };

                foreach (var r in byProduct)
                {
                    double th = ParseThickness(r.Thickness);
                    string coverKey = MakeCoverKey(r.OrderName, r.ProductName, th);
                    if (covered.Contains(coverKey)) continue;

                    productNode.Children.Add(new PlanTaskGroupNode
                    {
                        Title = string.IsNullOrWhiteSpace(r.SheetTitle)
                            ? $"{r.Thickness}мм, {r.SteelGrade}"
                            : r.SheetTitle,
                        NodeType = "Thickness",
                        OrderName = r.OrderName,
                        ProductName = r.ProductName,
                        PlannedDate = root.PlannedDate,
                        Thickness = th,
                        SteelGrade = r.SteelGrade ?? "",
                        ReservationKey = r.ReservationKey
                    });
                }

                if (productNode.Children.Count > 0)
                    orderNode.Children.Add(productNode);
            }

            if (orderNode.Children.Count > 0)
                root.Children.Add(orderNode);
        }

        if (root.Children.Count > 0)
            PlanTaskTree.Add(root);

        RestoreExpandedKeys(PlanTaskTree, expandedKeys);
    }

    private static HashSet<string> CaptureExpandedKeys(IEnumerable<PlanTaskGroupNode> nodes)
    {
        var keys = new HashSet<string>();
        void Walk(IEnumerable<PlanTaskGroupNode> level, string parentPath)
        {
            foreach (var n in level)
            {
                string path = parentPath + "/" + n.NodeType + ":" + n.Title;
                if (n.IsExpanded) keys.Add(path);
                if (n.Children.Count > 0) Walk(n.Children, path);
            }
        }
        Walk(nodes, "");
        return keys;
    }

    private static void RestoreExpandedKeys(IEnumerable<PlanTaskGroupNode> nodes, HashSet<string> expandedKeys)
    {
        void Walk(IEnumerable<PlanTaskGroupNode> level, string parentPath)
        {
            foreach (var n in level)
            {
                string path = parentPath + "/" + n.NodeType + ":" + n.Title;
                if (expandedKeys.Contains(path)) n.IsExpanded = true;
                if (n.Children.Count > 0) Walk(n.Children, path);
            }
        }
        Walk(nodes, "");
    }

    [RelayCommand]
    private void ReleaseTaskReservation(PlanTaskGroupNode? node)
    {
        if (node == null || string.IsNullOrEmpty(node.ReservationKey)) return;
        _repo.ReleaseSheet(node.ReservationKey, CurrentMachineId);
        StatusMessage = $"Лист «{node.Title}» возвращён в правое дерево.";
        LoadData();
    }


    // ==================================================================
    //  Существующий функционал (привязка Excel к дереву, комплект DXF для AJANCAM)
    // ==================================================================
    public void LoadData()
    {
        var covered = _repo.GetCoveredSheetKeys();
        var reserved = _repo.GetAllReservedKeys();
        var rawTree = _dbService.GetTechnologistPlanTree();

        var freshRoots = new List<StatementItem>();
        foreach (var dateNode in rawTree)
        {
            // Фильтр: загруженные УП + зарезервированные (чужие и свои) — убираем из правого дерева
            ProcessNodeAndGroupLeafs(dateNode, dateNode, covered, reserved);

            if (dateNode.Children.Any())
                freshRoots.Add(dateNode);
        }

        // Обновляем СУЩЕСТВУЮЩЕЕ дерево на месте, а не пересоздаём — иначе IsExpanded (и любое
        // другое состояние, привязанное к конкретному объекту) неизбежно теряется при каждом
        // обновлении, независимо от того, как оно связано в XAML.
        MergeTree(PlanTree, freshRoots, n => n.Title);

        foreach (var dateNode in PlanTree)
            SubscribeToSelection(dateNode);

        // Левое дерево — мои резервации
        LoadPlanTaskTree();
    }

    /// <summary>
    /// Аккуратно обновляет дерево на месте вместо Clear()+Add(): переиспользует уже
    /// существующие узлы (совпавшие по ключу), просто обновляя их поля — сам объект
    /// остаётся тем же, поэтому IsExpanded/прочее состояние UI не сбрасывается. Новые узлы
    /// добавляются, пропавшие — убираются. Рекурсивно спускается по Children.
    /// </summary>
    private static void MergeTree(ObservableCollection<StatementItem> existing, List<StatementItem> fresh, Func<StatementItem, string> keySelector)
    {
        var freshKeys = new HashSet<string>(fresh.Select(keySelector));

        for (int i = existing.Count - 1; i >= 0; i--)
        {
            if (!freshKeys.Contains(keySelector(existing[i])))
                existing.RemoveAt(i);
        }

        for (int i = 0; i < fresh.Count; i++)
        {
            var freshNode = fresh[i];
            string key = keySelector(freshNode);
            var oldNode = existing.FirstOrDefault(n => keySelector(n) == key);

            if (oldNode != null)
            {
                CopyStatementFields(oldNode, freshNode);
                MergeTree(oldNode.Children, freshNode.Children.ToList(), keySelector);

                int curIndex = existing.IndexOf(oldNode);
                if (curIndex != i)
                    existing.Move(curIndex, Math.Min(i, existing.Count - 1));
            }
            else
            {
                existing.Insert(Math.Min(i, existing.Count), freshNode);
            }
        }
    }

    /// <summary>Копирует "содержательные" поля свежего узла в уже существующий — специально
    /// НЕ трогает IsExpanded (то самое состояние UI, которое хотим сохранить) и Children
    /// (их сливает MergeTree рекурсивно отдельно).</summary>
    private static void CopyStatementFields(StatementItem target, StatementItem source)
    {
        target.IsGroup = source.IsGroup;
        target.DrawingNumber = source.DrawingNumber;
        target.Thickness = source.Thickness;
        target.SteelGrade = source.SteelGrade;
        target.Quantity = source.Quantity;
        target.DetailType = source.DetailType;
        target.Profile = source.Profile;
        target.Length = source.Length;
        target.DetailData = source.DetailData;
        target.ProductGroupName = source.ProductGroupName;
        target.ExcelFilePath = source.ExcelFilePath;
        target.IsSelected = source.IsSelected;
        target.IsEnabled = source.IsEnabled;
    }

    /// <summary>Собирает ключи развёрнутых узлов дерева ведомости — чтобы восстановить их
    /// после перестроения (LoadData() создаёт новые объекты StatementItem каждый раз, и без
    /// этого дерево сворачивалось бы обратно при каждом обновлении/резервации).</summary>
    private static HashSet<string> CaptureExpandedKeys(IEnumerable<StatementItem> nodes)
    {
        var keys = new HashSet<string>();
        void Walk(IEnumerable<StatementItem> level, string parentPath)
        {
            foreach (var n in level)
            {
                string path = parentPath + "/" + n.Title;
                if (n.IsExpanded) keys.Add(path);
                if (n.Children.Count > 0) Walk(n.Children, path);
            }
        }
        Walk(nodes, "");
        return keys;
    }

    private static void RestoreExpandedKeys(IEnumerable<StatementItem> nodes, HashSet<string> expandedKeys)
    {
        void Walk(IEnumerable<StatementItem> level, string parentPath)
        {
            foreach (var n in level)
            {
                string path = parentPath + "/" + n.Title;
                if (expandedKeys.Contains(path)) n.IsExpanded = true;
                if (n.Children.Count > 0) Walk(n.Children, path);
            }
        }
        Walk(nodes, "");
    }

    [RelayCommand]
    private void ToggleReservation(StatementItem? node)
    {
        if (node == null || !node.IsGroup || string.IsNullOrEmpty(node.Thickness)) return;

        string key = BuildReservationKey(node);

        if (node.IsSelected)
        {
            _repo.ReleaseSheet(key, CurrentMachineId);
            node.IsSelected = false;
        }
        else
        {
            var (orderName, productName) = GetOrderAndProductNames(node);
            bool ok = _repo.TryReserveSheet(key, orderName, productName, node.Title,
                node.Thickness, node.SteelGrade, CurrentMachineId);

            if (ok)
            {
                node.IsSelected = true;
                StatusMessage = $"Лист «{node.DisplayTitle}» взят в работу — убран из правого дерева.";
            }
            else
            {
                StatusMessage = $"Лист «{node.DisplayTitle}» уже взят в работу на другом ПК.";
            }
        }

        // Перестраиваем оба дерева: зарезервированный лист исчезает справа / появляется слева
        LoadData();
    }

    /// <summary>Раньше красили/серели занятые листы. Теперь они полностью убираются из правого дерева.</summary>
    private void ApplyReservationState()
    {
        LoadPlanTaskTree();
    }

    private string BuildReservationKey(StatementItem node)
    {
        var (orderName, productName) = GetOrderAndProductNames(node);
        return MakeReservationKey(orderName, productName, node.Thickness, node.SteelGrade);
    }

    private static string MakeReservationKey(string orderName, string productName, string thickness, string? steelGrade)
    {
        return $"{CleanTextFromEmoji(orderName)}|{CleanTextFromEmoji(productName)}|{thickness}|{steelGrade ?? ""}"
            .ToLowerInvariant();
    }

    private (string orderName, string productName) GetOrderAndProductNames(StatementItem node)
    {
        var directParent = FindParentNode(PlanTree, node);
        StatementItem? orderParent = directParent;
        while (orderParent != null)
        {
            var upper = FindParentNode(PlanTree, orderParent);
            if (upper == null || PlanTree.Contains(upper))
                break;
            orderParent = upper;
        }
        return (CleanNodeTitle(orderParent?.Title ?? ""), CleanNodeTitle(directParent?.Title ?? ""));
    }

    private void ProcessNodeAndGroupLeafs(StatementItem node, StatementItem rootDateNode,
        HashSet<string> covered, HashSet<string> reserved)
    {
        if (!node.IsGroup) return;

        if (node.Children.Any(c => !c.IsGroup))
        {
            string orderName = "";
            string productName = "";

            var orderNode = rootDateNode.Children.FirstOrDefault(c => ContainsChild(c, node));
            if (orderNode != null)
                orderName = CleanNodeTitle(orderNode.Title);

            productName = CleanNodeTitle(node.Title);

            var rawDetails = node.Children.Where(c => !c.IsGroup).ToList();
            node.Children.Clear();

            var sheetGroups = rawDetails
                .GroupBy(d => new { d.Thickness, d.SteelGrade })
                .OrderBy(g => GetMaterialSortOrder(g.FirstOrDefault()))
                .ThenBy(g => ParseThickness(g.Key.Thickness));

            foreach (var group in sheetGroups)
            {
                double th = ParseThickness(group.Key.Thickness);
                string coverKey = MakeCoverKey(orderName, productName, th);
                Debug.WriteLine($"[ProcessNodeAndGroupLeafs] Ищем coverKey='{coverKey}' (orderName='{orderName}', productName='{productName}', th={th}) -> {(covered.Contains(coverKey) ? "НАЙДЕН, скрываем" : "не найден, показываем")}");
                if (covered.Contains(coverKey))
                    continue;

                string resKey = MakeReservationKey(orderName, productName, group.Key.Thickness, group.Key.SteelGrade);
                // Зарезервированные (своим или чужим ПК) полностью убираем из правого дерева
                if (reserved.Contains(resKey))
                    continue;

                string profileName = !string.IsNullOrEmpty(group.First().Profile)
                    ? group.First().Profile
                    : group.First().Title;

                var sheetNode = new StatementItem
                {
                    Title = $"{group.Key.Thickness}мм, {group.Key.SteelGrade} ({group.Count()} поз.)",
                    Thickness = group.Key.Thickness,
                    SteelGrade = group.Key.SteelGrade,
                    Profile = profileName,
                    IsGroup = true
                };

                foreach (var detail in group)
                    sheetNode.Children.Add(detail);

                node.Children.Add(sheetNode);
            }
        }
        else
        {
            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                var child = node.Children[i];
                ProcessNodeAndGroupLeafs(child, rootDateNode, covered, reserved);

                if (child.IsGroup && !child.Children.Any())
                    node.Children.RemoveAt(i);
            }
        }
    }

    /// <summary> Проверяет, содержится ли искомый узел внутри родительского </summary>
    private bool ContainsChild(StatementItem parent, StatementItem target)
    {
        if (parent == target) return true;
        foreach (var child in parent.Children)
        {
            if (ContainsChild(child, target)) return true;
        }
        return false;
    }

    private int GetMaterialSortOrder(StatementItem? item)
    {
        if (item == null) return 1;

        string text = (item.Title + " " + item.Profile + " " + item.SteelGrade).ToLower();

        if (text.Contains("труба")) return 2;
        if (text.Contains("швеллер")) return 3;
        if (text.Contains("уголок")) return 4;
        if (text.Contains("балка") || text.Contains("двутавр")) return 5;

        return 1;
    }

    private double ParseThickness(string thickness)
    {
        if (double.TryParse(thickness?.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double result))
        {
            return result;
        }
        return 0;
    }

    private readonly HashSet<StatementItem> _subscribedNodes = new();

    private void SubscribeToSelection(StatementItem item)
    {
        // MergeTree теперь переиспользует объекты между обновлениями — без этой проверки
        // подписка накапливалась бы заново при каждом LoadData() на одном и том же узле.
        if (item.IsGroup && !string.IsNullOrEmpty(item.Thickness) && _subscribedNodes.Add(item))
        {
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(StatementItem.IsSelected))
                {
                    foreach (var child in item.Children)
                        child.IsSelected = item.IsSelected;
                }
            };
        }

        foreach (var child in item.Children)
            SubscribeToSelection(child);
    }

    private void ApplyThicknessFilter()
    {
        // Ищем ПЕРВЫЙ выбранный лист в правом дереве
        var selectedSheet = GetAllNodes(PlanTree)
            .FirstOrDefault(n => n.IsGroup && !string.IsNullOrEmpty(n.Thickness) && n.IsSelected);

        if (selectedSheet == null)
        {
            ResetFilter(PlanTree);
            return;
        }

        string activeThickness = selectedSheet.Thickness;
        string activeSteel = selectedSheet.SteelGrade;

        FilterTree(PlanTree, activeThickness, activeSteel);
    }

    private bool FilterTree(IEnumerable<StatementItem> nodes, string thickness, string steel)
    {
        bool anyMatch = false;

        foreach (var node in nodes)
        {
            if (node.IsGroup && !string.IsNullOrEmpty(node.Thickness))
            {
                // Если это узел листа/толщины
                bool isMatch = node.Thickness == thickness && string.Equals(node.SteelGrade, steel, StringComparison.OrdinalIgnoreCase);
                
                // Если лист НЕ совпадает с выбранным — прячем его и снимаем выбор
                if (!isMatch)
                {
                    node.IsEnabled = false;
                    node.IsSelected = false;
                }
                else
                {
                    node.IsEnabled = true;
                    anyMatch = true;
                }
            }
            else
            {
                bool childMatch = FilterTree(node.Children, thickness, steel);
                node.IsEnabled = childMatch;
                if (childMatch) anyMatch = true;
            }
        }

        return anyMatch;
    }
    private PlanTaskGroupNode? FindTaskNodeByThickness(string thickness, string steel)
    {
        double th = ParseThickness(thickness);
        foreach (var d in PlanTaskTree)
        foreach (var o in d.Children)
        foreach (var p in o.Children)
        foreach (var t in p.Children)
            if (Math.Abs(t.Thickness - th) < 1e-3 &&
                string.Equals(t.SteelGrade, steel, StringComparison.OrdinalIgnoreCase))
                return t;
        return null;
    }

    private void ResetFilter(IEnumerable<StatementItem> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsEnabled = true;
            if (node.IsGroup)
            {
                node.IsExpanded = true;
                ResetFilter(node.Children);
            }
        }
    }

    private async Task<string?> GetOrFindAjanCamPathAsync()
    {
        string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BKMsystem");
        string configFilePath = Path.Combine(appDataDir, "ajan_path.txt");

        if (File.Exists(configFilePath))
        {
            string savedPath = (await File.ReadAllTextAsync(configFilePath)).Trim();
            if (File.Exists(savedPath))
            {
                return savedPath;
            }
        }

        string[] commonPaths = new[]
        {
            @"C:\Ajan\AJANCAM.exe",
            @"C:\Ajan\AjanCAM.exe",
            @"C:\Program Files (x86)\AJAN ELECTRONICS\AJANCAM V8.0\AJANCAM.exe",
            @"C:\Program Files\AJAN ELECTRONICS\AJANCAM V8.0\AJANCAM.exe",
            @"D:\Ajan\AJANCAM.exe"
        };

        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
            {
                await SaveAjanPathAsync(configFilePath, path);
                return path;
            }
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow);
            if (topLevel != null)
            {
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Укажите исполняемый файл AJANCAM.exe",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("Исполняемые файлы (*.exe)") { Patterns = new[] { "*.exe" } }
                    }
                });

                if (files.Count > 0)
                {
                    string selectedPath = files[0].Path.LocalPath;
                    await SaveAjanPathAsync(configFilePath, selectedPath);
                    return selectedPath;
                }
            }
        }

        return null;
    }

    private async Task SaveAjanPathAsync(string configPath, string exePath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllTextAsync(configPath, exePath);
        }
        catch
        {
        }
    }

    public async Task CreateDxfPackageAsync()
    {
        try
        {
            // Комплект DXF собирается строго по левому дереву (листы, взятые в работу)
            var leftSheets = GetLeftThicknessNodes();
            if (leftSheets.Count == 0)
            {
                await ShowMessageAsync("Внимание", "Сначала отметьте лист(ы) в правом дереве — они появятся слева.");
                return;
            }

            var selectedDetails = leftSheets
                .SelectMany(s => CollectDetailsFromPlan(s.OrderName, s.ProductName, s.Thickness, s.SteelGrade))
                .Where(x => !string.IsNullOrWhiteSpace(x.DrawingNumber))
                .GroupBy(x => x.DrawingNumber, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    DrawingNumber = g.Key,
                    Quantity = g.Sum(x => x.Quantity),
                    Thickness = g.First().Thickness,
                    SteelGrade = g.First().SteelGrade
                })
                .ToList();

            if (!selectedDetails.Any())
            {
                await ShowMessageAsync("Внимание", "В левом дереве не найдено деталей для формирования раскроя!");
                return;
            }

            string targetFolder = @"C:\AJAN_Temp_DXF";
            string networkPath = @"Y:\";

            if (Directory.Exists(targetFolder))
            {
                var oldFiles = Directory.GetFiles(targetFolder, "*.dxf");
                foreach (var file in oldFiles)
                {
                    try { File.Delete(file); } catch { }
                }
            }
            else
            {
                Directory.CreateDirectory(targetFolder);
            }

            var dxfItems = selectedDetails
                .Select(d => (DrawingNumber: d.DrawingNumber, Quantity: d.Quantity))
                .ToList();

            await _dxfCollector.CollectDxfFilesAsync(dxfItems, networkPath, targetFolder);

            var nestParts = new List<NestPartItem>();
            foreach (var item in selectedDetails)
            {
                string? foundFilePath = Directory.GetFiles(targetFolder, $"*{item.DrawingNumber}*.dxf")
                                                .FirstOrDefault();

                if (!string.IsNullOrEmpty(foundFilePath) && File.Exists(foundFilePath))
                {
                    nestParts.Add(new NestPartItem
                    {
                        DxfPath = foundFilePath,
                        PartId = item.DrawingNumber,
                        Quantity = item.Quantity
                    });
                }
            }

            if (!nestParts.Any())
            {
                await ShowMessageAsync("Ошибка DXF", "Ни один DXF файл не был найден!");
                return;
            }

            string? ajanExePath = await GetOrFindAjanCamPathAsync();

            if (string.IsNullOrEmpty(ajanExePath) || !File.Exists(ajanExePath))
            {
                await ShowMessageAsync("Ошибка запуска", "Не найден исполняемый файл AJANCAM.exe!");
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ajanExePath,
                WorkingDirectory = targetFolder,
                UseShellExecute = true
            };

            Process? ajanProcess = Process.Start(startInfo);

            if (ajanProcess != null)
            {
                var automationService = new AjanUIAutomationService();

                var firstPart = selectedDetails.FirstOrDefault();
                double thicknessVal = firstPart != null ? Convert.ToDouble(firstPart.Thickness) : 0;
                string steelGradeVal = firstPart?.SteelGrade ?? "09Г2С";

                StatusMessage = $"Собран комплект DXF ({nestParts.Count} шт.) по левому дереву, запускаю AJANCAM…";

                _ = Task.Run(async () =>
                {
                    await automationService.AutomateFullAjanFlowAsync(
                        ajanProcess,
                        nestParts,
                        thickness: thicknessVal,
                        steelGrade: steelGradeVal
                    );
                });
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Критическая ошибка", $"Произошла ошибка при выполнении операции:\n{ex.Message}");
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        Debug.WriteLine($"[{title}] {message}");
        StatusMessage = $"{title}: {message}";
        await Task.CompletedTask;
    }

    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<Task> execute)
        {
            _execute = execute;
        }

        public bool CanExecute(object? parameter) => !_isExecuting;

        public async void Execute(object? parameter)
        {
            if (_isExecuting) return;
            _isExecuting = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);

            try
            {
                await _execute();
            }
            finally
            {
                _isExecuting = false;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? CanExecuteChanged;
    }

    private IEnumerable<StatementItem> GetAllNodes(IEnumerable<StatementItem> nodes)
    {
        var list = new List<StatementItem>();
        foreach (var node in nodes)
        {
            list.Add(node);
            if (node.Children.Any())
                list.AddRange(GetAllNodes(node.Children));
        }
        return list;
    }

    private IEnumerable<StatementItem> GetAllNodes(StatementItem parent)
    {
        yield return parent;
        foreach (var child in parent.Children)
        {
            foreach (var relative in GetAllNodes(child))
            {
                yield return relative;
            }
        }
    }

    /// <summary>Вспомогательный метод поиска родительского узла в дереве PlanTree.</summary>
    private StatementItem? FindParentNode(IEnumerable<StatementItem> nodes, StatementItem targetChild)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Contains(targetChild))
                return node;

            var parent = FindParentNode(node.Children, targetChild);
            if (parent != null)
                return parent;
        }
        return null;
    }

    /// <summary>Убирает служебные префиксы из заголовков элементов дерева.</summary>
    private string CleanNodeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        return title
            .Replace("Заказ: ", "")
            .Replace("Изделие: ", "")
            .Replace("Узел: ", "")
            .Trim();
    }

    private static string MakeCoverKey(string order, string product, double thickness)
    {
        string cleanOrder = CleanTextFromEmoji(order).ToLower();
        string cleanProduct = CleanTextFromEmoji(product).ToLower();
        
        string cleanThickness = thickness.ToString("0.##", CultureInfo.InvariantCulture);

        return $"{cleanOrder}|{cleanProduct}|{cleanThickness}";
    }

    private static string CleanTextFromEmoji(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        // 1. Убираем служебные префиксы
        string text = input
            .Replace("Заказ:", "")
            .Replace("Заказ", "")
            .Replace("Изделие:", "")
            .Replace("Изделие", "")
            .Replace("Узел:", "")
            .Replace("Узел", "")
            .Trim();

        // 2. Оставляем только кириллицу, латиницу, цифры и базовую пунктуацию (без спецсимволов и эмодзи)
        string clean = System.Text.RegularExpressions.Regex.Replace(
            text, 
            @"[^\w\d\s\-\.\(\)\№\/\,\«\»]", 
            "", 
            System.Text.RegularExpressions.RegexOptions.Compiled
        );

        return clean.Trim();
    }
}