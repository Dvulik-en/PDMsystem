using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using BKMsystem.Services;
using BKMsystem.ViewModels;

namespace BKMsystem.Views;

/// <summary>
/// Одна колонка дня в визуальном таймлайне "Плана" (аналог onetask): часовая сетка
/// 08:00–22:00 с закрашенным обедом и подсветкой второй смены, и поверх — плитки заданий.
/// Плитку можно: (1) перетащить вверх/вниз внутри дня — меняет порядок резки; (2) перетащить
/// на ДРУГУЮ колонку дня — переносит задание туда (с обрезкой по вместимости, недостача
/// остаётся недорезанным остатком в правой панели); (3) потянуть за нижний край — сжать или
/// растянуть длительность на этот день (свободное место тоже ограничено вместимостью дня).
/// ПКМ на плитке — снять её с этого дня. Геометрия и вся логика перетаскивания реализованы в
/// коде, а не в XAML/биндингах — Canvas.Top/Height двигаются напрямую при drag, финальный
/// результат уходит во ViewModel только по отпусканию кнопки мыши, чтобы не дёргать БД на
/// каждый пиксель движения.
/// </summary>
public partial class DayColumnView : UserControl
{
    private const double HourLabelWidth = 32;
    private const double TileLeft = 36;
    private const double TileWidth = 168;
    private const double CanvasWidth = 210;

    /// <summary>Метка на элементах "плитки" обеда — чтобы отличать их от плиток заданий
    /// (Tag = DayTileViewModel) при перерисовке и всегда добавлять их ПОСЛЕДНИМИ, поверх
    /// заданий: иначе задание, чья визуальная геометрия перекрывает обеденный час (тайл рисуется
    /// по wall-clock времени, а не по рабочим часам), закрывало собой плитку обеда собой.</summary>
    private static readonly object LunchOverlayTag = new();

    private Canvas? _canvas;
    private DayColumnViewModel? _boundVm;

    // Состояние текущего drag'а (перетаскивание плитки целиком — смена порядка)
    private Border? _dragTile;
    private DayTileViewModel? _dragTileVm;
    private Point _dragStartPointerPos;
    private double _dragStartTop;

    // Состояние текущего resize'а (тянем за нижний грип — меняем длительность)
    private Border? _resizeTile;
    private DayTileViewModel? _resizeTileVm;
    private double _resizeStartPointerY;
    private double _resizeStartHeight;
    /// <summary>Реальная РАБОЧАЯ длительность (в часах), которую сейчас "показывает" грип —
    /// пересчитанная с учётом обеда в Grip_PointerMoved. Отпускание мыши использует именно
    /// её, а не обратный пересчёт из пикселей — иначе пиксели и часы расходятся ровно на
    /// величину обеда для любого сегмента, который через обед перепрыгивает.</summary>
    private double? _pendingResizeWorkHours;

    /// <summary>Формат данных перетаскивания для плиток из правой панели "Не запланировано"
    /// (PlanPoolProductGroup или PlanPoolUnitItem) — общий с PlanView.axaml.cs.</summary>
    public const string PoolDragFormat = "BKM-PlanPoolItem";

    public DayColumnView()
    {
        InitializeComponent();
        _canvas = this.FindControl<Canvas>("RootCanvas");
        DataContextChanged += (_, __) => Rebuild();

        var cardRoot = this.FindControl<Border>("CardRoot");
        if (cardRoot != null)
        {
            DragDrop.SetAllowDrop(cardRoot, true);
            cardRoot.AddHandler(DragDrop.DragOverEvent, OnPoolDragOver);
            cardRoot.AddHandler(DragDrop.DropEvent, OnPoolDrop);
        }
    }

    /// <summary>Кнопка "Вывести 2-ю смену" в заголовке колонки — переключает разрешение
    /// резать после 17:00 именно для этого дня (хранится в БД, PlanDaySettings).</summary>
    private void OnShift2ToggleClick(object? sender, RoutedEventArgs e)
    {
        _boundVm?.ToggleShift2Requested?.Invoke();
    }

    /// <summary>Плитку из правой панели тащат над колонкой дня — разрешаем бросить, только
    /// если это действительно наш формат (не файл из проводника и т.п.).</summary>
    private void OnPoolDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(PoolDragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Плитку бросили на этот день — вытаскиваем Заказ/Изделие/(Узел) и просим
    /// ViewModel поставить задание сразу на дату этой колонки. Отдельно обрабатываем
    /// "недорезанный остаток" (PlanPoolLeftoverItem, красная плитка) — это уже СУЩЕСТВУЮЩЕЕ
    /// задание, для него нужен не PoolItemDropped (создал бы новое задание), а LeftoverDropped
    /// (добавляет сегмент к уже существующему по TaskId). Раньше этот тип тут не проверялся
    /// вообще, поэтому Drop принимался (Contains(PoolDragFormat) срабатывал), но orderName
    /// оставался пустым и ничего не происходило — плитка просто визуально "не бросалась".</summary>
    private void OnPoolDrop(object? sender, DragEventArgs e)
    {
        if (_boundVm == null) return;
        if (!e.Data.Contains(PoolDragFormat)) return;

        var tag = e.Data.Get(PoolDragFormat);

        if (tag is PlanPoolLeftoverItem leftover)
        {
            _boundVm.LeftoverDropped?.Invoke(leftover.TaskId);
            e.Handled = true;
            return;
        }

        string orderName = "", productName = "", unitName = "";

        if (tag is PlanPoolUnitItem unit)
        {
            orderName = unit.OrderName; productName = unit.ProductName; unitName = unit.UnitName;
        }
        else if (tag is PlanPoolProductGroup product)
        {
            orderName = product.OrderName; productName = product.ProductName; unitName = "";
        }

        if (!string.IsNullOrEmpty(orderName))
            _boundVm.PoolItemDropped?.Invoke(orderName, productName, unitName);

        e.Handled = true;
    }

    private void Rebuild()
    {
        if (_canvas == null) return;
        _canvas.Children.Clear();

        if (_boundVm?.Tiles is INotifyCollectionChanged oldNotify)
            oldNotify.CollectionChanged -= OnTilesChanged;

        _boundVm = DataContext as DayColumnViewModel;
        if (_boundVm == null) return;

        if (_boundVm.Tiles is INotifyCollectionChanged newNotify)
            newNotify.CollectionChanged += OnTilesChanged;

        _canvas.Height = PlanScheduleService.TimelineHeightPx;
        _canvas.Width = CanvasWidth;

        DrawStaticGrid();
        DrawTiles();
    }

    private void OnTilesChanged(object? sender, NotifyCollectionChangedEventArgs e) => DrawTiles();

    /// <summary>Часовые линии с подписями, закрашенный обед и граница второй смены —
    /// геометрия не меняется, пока не пересоздана вся колонка, поэтому рисуется один раз.</summary>
    private void DrawStaticGrid()
    {
        if (_canvas == null) return;

        // Фон второй смены — лёгкий тёплый оттенок, чтобы визуально отличался от первой
        var shift2Band = new Rectangle
        {
            Width = CanvasWidth,
            Height = PlanScheduleService.TimelineHeightPx - PlanScheduleService.Shift2TopPx,
            Fill = new SolidColorBrush(Color.Parse("#1E1A0F"))
        };
        Canvas.SetTop(shift2Band, PlanScheduleService.Shift2TopPx);
        Canvas.SetLeft(shift2Band, 0);
        _canvas.Children.Add(shift2Band);

        for (int hour = (int)PlanScheduleService.DayStartHour; hour <= (int)PlanScheduleService.Shift2EndHour; hour++)
        {
            double y = (hour - PlanScheduleService.DayStartHour) * PlanScheduleService.PixelsPerHour;

            var line = new Rectangle
            {
                Width = CanvasWidth - HourLabelWidth,
                Height = 1,
                Fill = new SolidColorBrush(Color.Parse(hour == (int)PlanScheduleService.Shift1EndHour ? "#FFC107" : "#2E2E2E"))
            };
            Canvas.SetTop(line, y);
            Canvas.SetLeft(line, HourLabelWidth);
            _canvas.Children.Add(line);

            var label = new TextBlock
            {
                Text = $"{hour:00}:00",
                FontSize = 9,
                Foreground = Brushes.Gray
            };
            Canvas.SetTop(label, y - 6);
            Canvas.SetLeft(label, 0);
            _canvas.Children.Add(label);
        }
    }

    /// <summary>Обеденный перерыв — полноценная "плитка"-заглушка со своей рамкой и подписью,
    /// а не просто чуть более тёмная полоса (её путали со свободным местом). Перерисовывается
    /// заново при каждом обновлении плиток заданий и добавляется В САМОМ КОНЦЕ — поверх них —
    /// потому что плитка задания рисуется по wall-clock времени (см. DayTileViewModel.HeightPx)
    /// и её прямоугольник геометрически проходит СКВОЗЬ обеденный час, просто не считая его в
    /// рабочей длительности. Если обед нарисован раньше плиток заданий, такая плитка визуально
    /// перекрывает его собой, и непонятно, что в середине этого времени вообще-то обед.</summary>
    private void DrawLunchOverlay()
    {
        if (_canvas == null) return;

        var lunchBand = new Border
        {
            Width = CanvasWidth - HourLabelWidth,
            Height = PlanScheduleService.LunchHeightPx,
            Background = new SolidColorBrush(Color.Parse("#333333")),
            BorderBrush = new SolidColorBrush(Color.Parse("#4A4A4A")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Tag = LunchOverlayTag,
            IsHitTestVisible = false // визуальная накладка — не должна мешать драгу/ресайзу плитки под ней
        };
        Canvas.SetTop(lunchBand, PlanScheduleService.LunchTopPx);
        Canvas.SetLeft(lunchBand, HourLabelWidth);
        _canvas.Children.Add(lunchBand);

        var lunchLabel = new TextBlock
        {
            Text = "🍽 ОБЕД",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.Gray,
            Width = CanvasWidth - HourLabelWidth,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            Tag = LunchOverlayTag,
            IsHitTestVisible = false // не должна перехватывать клики/драг у плиток под ней
        };
        Canvas.SetTop(lunchLabel, PlanScheduleService.LunchTopPx + PlanScheduleService.LunchHeightPx / 2 - 7);
        Canvas.SetLeft(lunchLabel, HourLabelWidth);
        _canvas.Children.Add(lunchLabel);
    }

    /// <summary>Перерисовывает только плитки заданий поверх статичной сетки.</summary>
    private void DrawTiles()
    {
        if (_canvas == null || _boundVm == null) return;

        // Убираем плитки заданий (Tag = DayTileViewModel) и старую плитку обеда (перерисуем её
        // ниже заново, последней — сетку часов не трогаем).
        var toRemove = _canvas.Children
            .Where(c => c is Control ctrl && (ctrl.Tag is DayTileViewModel || ReferenceEquals(ctrl.Tag, LunchOverlayTag)))
            .ToList();
        foreach (var c in toRemove) _canvas.Children.Remove(c);

        double maxBottomPx = PlanScheduleService.TimelineHeightPx;
        foreach (var tile in _boundVm.Tiles)
        {
            var border = BuildTileBorder(tile);
            _canvas.Children.Add(border);
            maxBottomPx = Math.Max(maxBottomPx, tile.TopPx + tile.HeightPx);
        }

        // Обед добавляем последним — поверх всех плиток заданий (см. DrawLunchOverlay).
        DrawLunchOverlay();

        // Страховка: вместимость дня теперь жёстко проверяется на всех путях записи (постановка,
        // перенос, растягивание, "дорезка остатка"), так что плитка НЕ ДОЛЖНА уходить за 22:00.
        // Но если в базе всё же осталась старая испорченная запись (например, сохранённая до
        // этого фикса) — холст растягивается под неё, чтобы её было видно и можно было
        // прокрутить/поправить/удалить, а не потерять доступ к ней навсегда за нижним краем.
        _canvas.Height = maxBottomPx + 20;
    }

    private Border BuildTileBorder(DayTileViewModel tile)
    {
        var titleText = new TextBlock
        {
            Text = tile.Title,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var timeText = new TextBlock
        {
            Text = $"{tile.TimeRangeText} · {tile.DurationText}",
            FontSize = 9,
            Foreground = Brushes.LightGray
        };

        var content = new StackPanel { Spacing = 2, Margin = new Thickness(6, 3, 6, 8) };
        content.Children.Add(titleText);
        content.Children.Add(timeText);

        // Грип для изменения размера — узкая полоска снизу плитки
        var grip = new Border
        {
            Height = 6,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom
        };
        grip.PointerPressed += (s, e) => Grip_PointerPressed(e, tile, grip);
        grip.PointerMoved += Grip_PointerMoved;
        grip.PointerReleased += Grip_PointerReleased;

        var root = new Grid();
        root.Children.Add(content);
        root.Children.Add(grip);

        // ПКМ на плитке — снять её именно с этого дня (если это был последний сегмент задания,
        // ViewModel сам полностью снимет его с плана и оно вернётся обычной незапланированной
        // позицией в правую панель).
        var removeItem = new MenuItem { Header = "🗑 Убрать с этого дня (вернуть в план)" };
        removeItem.Click += (s, e) => _boundVm?.RemoveFromDayRequested?.Invoke(tile.TaskId);
        var contextMenu = new ContextMenu { ItemsSource = new[] { removeItem } };

        var border = new Border
        {
            Width = TileWidth,
            Height = tile.HeightPx,
            Background = new SolidColorBrush(Color.Parse(tile.TileBackground)),
            BorderBrush = new SolidColorBrush(Color.Parse(tile.TileBorderColor)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            ClipToBounds = true,
            Tag = tile,
            Child = root,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            ContextMenu = contextMenu
        };
        Canvas.SetLeft(border, TileLeft);
        Canvas.SetTop(border, tile.TopPx);

        // Плитка узкая, заголовок обрезается по 2 строкам (см. titleText выше) — при наведении
        // показываем полное название Заказ/Изделие/Узел целиком, иначе на маленькой плитке не
        // понятно, что вообще стоит в плане (жалоба технолога).
        ToolTip.SetTip(border, $"{tile.Title}\n{tile.TimeRangeText} · {tile.DurationText}");

        border.PointerPressed += (s, e) => Tile_PointerPressed(e, border, tile);
        border.PointerMoved += Tile_PointerMoved;
        border.PointerReleased += Tile_PointerReleased;

        return border;
    }

    // ------------------------------------------------------------------
    // Drag плитки целиком — свободная смена порядка резки внутри дня
    // ------------------------------------------------------------------

    private void Tile_PointerPressed(PointerPressedEventArgs e, Border border, DayTileViewModel tile)
    {
        if (_canvas == null) return;
        // ПКМ — открываем контекстное меню (удаление), а не начинаем перетаскивание.
        if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;

        _dragTile = border;
        _dragTileVm = tile;
        _dragStartPointerPos = e.GetPosition(_canvas);
        _dragStartTop = Canvas.GetTop(border);
        border.ZIndex = 100;
        e.Pointer.Capture(border);
        e.Handled = true;
    }

    private void Tile_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTile == null || _canvas == null) return;
        var pos = e.GetPosition(_canvas);
        double deltaY = pos.Y - _dragStartPointerPos.Y;
        double newTop = _dragStartTop + deltaY;
        newTop = Math.Max(0, Math.Min(newTop, PlanScheduleService.TimelineHeightPx - _dragTile.Bounds.Height));
        Canvas.SetTop(_dragTile, newTop);
    }

    private void Tile_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragTile == null || _boundVm == null) { _dragTile = null; return; }

        var releasedTileVm = _dragTileVm;
        _dragTile.ZIndex = 0;
        e.Pointer.Capture(null);

        // Отпустили над ДРУГОЙ колонкой дня? — переносим задание на неё, а не переупорядочиваем
        // (pointer capture продолжает слать нам события, даже когда курсор ушёл за пределы
        // холста этого дня — поэтому координаты пришлось брать через TopLevel-хиттест).
        var targetColumn = FindDayColumnUnderPointer(e);
        if (targetColumn != null && targetColumn != this
            && targetColumn.DataContext is DayColumnViewModel targetVm
            && releasedTileVm != null)
        {
            _boundVm.MoveTileRequested?.Invoke(releasedTileVm.TaskId, _boundVm.Date, targetVm.Date);
        }
        else
        {
            // Тот же день — новый порядок = сортировка всех плиток по их текущему Canvas.Top
            var orderedIds = _canvas!.Children.OfType<Border>()
                .Where(b => b.Tag is DayTileViewModel)
                .OrderBy(Canvas.GetTop)
                .Select(b => ((DayTileViewModel)b.Tag!).TaskId)
                .ToList();

            _boundVm.ReorderRequested?.Invoke(orderedIds);
        }

        _dragTile = null;
        _dragTileVm = null;
    }

    /// <summary>Хиттест по TopLevel в точке, где отпустили плитку — ищем, над колонкой КАКОГО
    /// дня сейчас находится курсор (может быть не эта колонка, если плитку тащили дальше её
    /// собственного холста — Canvas обрезает содержимое, но pointer capture всё равно
    /// продолжает присылать координаты).</summary>
    private DayColumnView? FindDayColumnUnderPointer(PointerEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return null;

        var pointOnTopLevel = e.GetPosition(topLevel);
        var hit = topLevel.InputHitTest(pointOnTopLevel);
        return (hit as Visual)?.FindAncestorOfType<DayColumnView>(includeSelf: true);
    }

    // ------------------------------------------------------------------
    // Drag за нижний грип — изменение длительности (сжать/растянуть плитку)
    // ------------------------------------------------------------------

    private void Grip_PointerPressed(PointerPressedEventArgs e, DayTileViewModel tile, Border grip)
    {
        if (_canvas == null) return;
        _resizeTile = grip.Parent as Border ?? FindParentTileBorder(grip);
        _resizeTileVm = tile;
        _resizeStartPointerY = e.GetPosition(_canvas).Y;
        _resizeStartHeight = _resizeTile?.Bounds.Height ?? tile.HeightPx;
        _pendingResizeWorkHours = null;
        e.Pointer.Capture(grip);
        e.Handled = true; // не даём событию всплыть на саму плитку и запустить drag-перенос
    }

    private static Border? FindParentTileBorder(Control c)
    {
        var p = c.Parent;
        while (p != null && !(p is Border b && b.Tag is DayTileViewModel)) p = p.Parent;
        return p as Border;
    }

    private void Grip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_resizeTile == null || _canvas == null || _boundVm == null || _resizeTileVm == null) return;
        double y = e.GetPosition(_canvas).Y;
        double deltaY = y - _resizeStartPointerY;
        double candidateHeightPx = Math.Max(22, _resizeStartHeight + deltaY);

        // "Сырая" высота в пикселях — это положение курсора на НАСТЕННОЙ (wall-clock) сетке,
        // где час обеда тоже занимает место. Переводим её в реальную РАБОЧУЮ длительность так
        // же, как это делает AddWorkTime/GetTaskSegments — иначе, если новая граница переезжает
        // через обед, часы и пиксели расходятся ровно на 1 час (тот самый "отскок к 17:00").
        var candidateEnd = _resizeTileVm.Start.AddHours(candidateHeightPx / PlanScheduleService.PixelsPerHour);
        double candidateWorkHours = PlanScheduleService.WallClockSpanToWorkHours(_resizeTileVm.Start, candidateEnd);

        // Жёсткие пределы В РАБОЧИХ ЧАСАХ — растянуть плитку прямо во время перетаскивания
        // нельзя ни больше свободного места дня (вместимость минус другие плитки), ни больше
        // того, что реально осталось резать по норме всего задания (см. MaxTotalNormHours) —
        // иначе задание с фиксированным временем резки можно было растянуть на пустой день
        // сильнее, чем у него вообще есть работы.
        double capacityHours = _boundVm.Shift2Enabled
            ? PlanScheduleService.TotalCapacityHours
            : PlanScheduleService.Shift1CapacityHours;
        double occupiedOtherHours = _boundVm.Tiles
            .Where(t => t.TaskId != _resizeTileVm.TaskId)
            .Sum(t => t.Duration.TotalHours);
        double maxByDay = Math.Max(0, capacityHours - occupiedOtherHours);
        double maxAllowedWorkHours = Math.Min(maxByDay, _resizeTileVm.MaxTotalNormHours);

        double finalWorkHours = Math.Max(0.05, Math.Min(candidateWorkHours, maxAllowedWorkHours));
        _pendingResizeWorkHours = finalWorkHours;

        // Обратно в пиксели для отрисовки — через AddWorkTime, чтобы низ плитки визуально
        // доходил ровно до границы на сетке (с учётом обеда), а не "недотягивал" на его час.
        var finalEnd = PlanScheduleService.AddWorkTime(_resizeTileVm.Start, TimeSpan.FromHours(finalWorkHours));
        double finalHeightPx = Math.Max(22, (finalEnd - _resizeTileVm.Start).TotalHours * PlanScheduleService.PixelsPerHour);

        _resizeTile.Height = finalHeightPx;
    }

    private void Grip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizeTile == null || _resizeTileVm == null || _boundVm == null)
        {
            _resizeTile = null; _resizeTileVm = null; return;
        }

        (sender as Control)?.GetType(); // no-op, keeps analyzer quiet about unused param
        e.Pointer.Capture(null);

        // Берём уже посчитанную В Grip_PointerMoved рабочую длительность (с учётом обеда и
        // всех потолков), а НЕ пересчитываем заново из итоговых пикселей — обратный пересчёт
        // из пикселей в часы (h = px / PixelsPerHour) не знает про обед и для любого сегмента,
        // пересекающего 12:00–13:00, дал бы на час больше, чем реально показано на сетке.
        double newHours = _pendingResizeWorkHours ?? (_resizeTile.Height / PlanScheduleService.PixelsPerHour);
        var newDuration = TimeSpan.FromHours(Math.Max(0.05, newHours)); // минимум ~3 минуты, чтобы не обнулить задание случайно

        int taskId = _resizeTileVm.TaskId;
        _resizeTile = null;
        _resizeTileVm = null;
        _pendingResizeWorkHours = null;

        _boundVm.ResizeRequested?.Invoke(taskId, newDuration);
    }
}
