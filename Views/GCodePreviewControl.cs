using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using BKMsystem.Services;

namespace BKMsystem.Views;

public class GCodePreviewControl : Control
{
    public static readonly StyledProperty<string?> FilePathProperty =
        AvaloniaProperty.Register<GCodePreviewControl, string?>(nameof(FilePath));
    public string? FilePath { get => GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }

    public static readonly StyledProperty<double> SpeedProperty =
        AvaloniaProperty.Register<GCodePreviewControl, double>(nameof(Speed), 3000);
    public double Speed { get => GetValue(SpeedProperty); set => SetValue(SpeedProperty, value); }

    private MpgPathResult? _parsed;
    private double _totalLen;   // рез + холостые (для непрерывного движения кружка)
    private double _elapsed;
    private DateTime _lastTick;
    private readonly DispatcherTimer _timer;

    // Пользовательский зум/панорама
    private double _userZoom = 1.0;
    private Point _pan;
    private bool _dragging;
    private Point _dragStart;

    public GCodePreviewControl()
    {
        Focusable = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) =>
        {
            if (_totalLen <= 0) return;
            var now = DateTime.Now;
            _elapsed += Speed * (now - _lastTick).TotalSeconds;
            _lastTick = now;
            if (_elapsed > _totalLen) _elapsed = 0;
            InvalidateVisual();
        };
        _timer.Start();

        PointerWheelChanged += (_, e) =>
        {
            double f = e.Delta.Y > 0 ? 1.2 : 1 / 1.2;
            _userZoom = Math.Clamp(_userZoom * f, 0.3, 12);
            InvalidateVisual();
            e.Handled = true;
        };
        PointerPressed += (_, e) =>
        {
            _dragStart = e.GetPosition(this);
            _dragging = true;
        };
        PointerReleased += (_, _) => _dragging = false;
        PointerMoved += (_, e) =>
        {
            if (!_dragging) return;
            var p = e.GetPosition(this);
            _pan = new Point(_pan.X + (p.X - _dragStart.X), _pan.Y + (p.Y - _dragStart.Y));
            _dragStart = p;
            InvalidateVisual();
        };
    }

    static GCodePreviewControl()
    {
        AffectsRender<GCodePreviewControl>(FilePathProperty, BoundsProperty);
        FilePathProperty.Changed.AddClassHandler<GCodePreviewControl>((o, _) => o.Reload());
    }

    private void Reload()
    {
        _parsed = null; _totalLen = 0; _elapsed = 0; _userZoom = 1; _pan = new Point();
        if (!string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath))
        {
            try { _parsed = AjanMpgParser.Parse(FilePath); } catch { }
        }
        if (_parsed != null)
        {
            foreach (var s in _parsed.Segments)
            {
                // Холостые ускоряем в таймлайне (короче по времени), но кружок всё равно едет по ним
                _totalLen += s.IsCut ? s.Length : s.Length / 4.0;
            }
        }
        _lastTick = DateTime.Now;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var rect = new Rect(Bounds.Size);
        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#101014")), rect);
        if (_parsed == null || _parsed.Segments.Count == 0) return;

        // Viewport = bounding box всей траектории (подходы часто вне габарита листа)
        double minX = _parsed.MinX, minY = _parsed.MinY, maxX = _parsed.MaxX, maxY = _parsed.MaxY;
        if (maxX <= minX || maxY <= minY)
        {
            if (_parsed.SheetW > 0 && _parsed.SheetH > 0)
            { minX = 0; minY = 0; maxX = _parsed.SheetW; maxY = _parsed.SheetH; }
            else return;
        }

        double bw = Math.Max(maxX - minX, 1);
        double bh = Math.Max(maxY - minY, 1);
        double m = 20;
        double baseScale = Math.Min((rect.Width - m * 2) / bw, (rect.Height - m * 2) / bh);
        double scale = baseScale * _userZoom;
        double offX = (rect.Width - bw * scale) / 2 - minX * scale + _pan.X;
        double offY = (rect.Height + bh * scale) / 2 + minY * scale + _pan.Y;
        // ось Y станка вверх → инвертируем
        Point Tr(Point p) => new(p.X * scale + offX, offY - p.Y * scale);

        // Контур листа (справочно)
        if (_parsed.SheetW > 0 && _parsed.SheetH > 0)
        {
            var p1 = Tr(new Point(0, 0));
            var p2 = Tr(new Point(_parsed.SheetW, _parsed.SheetH));
            var lr = new Rect(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y),
                              Math.Abs(p2.X - p1.X), Math.Abs(p2.Y - p1.Y));
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#18181E")),
                              new Pen(new SolidColorBrush(Color.Parse("#3A3A48")), 1.2), lr);
        }

        var cutPen = new Pen(new SolidColorBrush(Color.Parse("#C8C8C8")), 1.3);
        var rapidPen = new Pen(new SolidColorBrush(Color.Parse("#4A4A4A")), 0.6)
            { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
        var donePen = new Pen(new SolidColorBrush(Color.Parse("#66BB6A")), 1.8);
        var doneRapidPen = new Pen(new SolidColorBrush(Color.Parse("#5A7A5A")), 0.7)
            { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };

        double acc = 0;
        Point? head = null;
        foreach (var s in _parsed.Segments)
        {
            double weight = s.IsCut ? s.Length : s.Length / 4.0;
            if (weight < 1e-9) { Draw(ctx, s, s.IsCut ? cutPen : rapidPen, Tr); continue; }

            if (acc + weight <= _elapsed)
            {
                Draw(ctx, s, s.IsCut ? donePen : doneRapidPen, Tr);
                acc += weight;
                head = Tr(s.To);
                continue;
            }
            if (acc >= _elapsed)
            {
                Draw(ctx, s, s.IsCut ? cutPen : rapidPen, Tr);
                continue;
            }

            double t = (_elapsed - acc) / weight;
            head = Tr(AjanMpgParser.Interpolate(s, t));
            DrawPart(ctx, s, 0, t, s.IsCut ? donePen : doneRapidPen, Tr);
            DrawPart(ctx, s, t, 1, s.IsCut ? cutPen : rapidPen, Tr);
            acc += weight;
        }

        if (head.HasValue)
        {
            ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#FF6A00")), null, head.Value, 5, 5);
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#FF6A00")), 1), head.Value, 9, 9);
        }
    }

    private static void Draw(DrawingContext ctx, MpgSegment s, Pen pen, Func<Point, Point> tr)
    {
        if (!s.IsArc) { ctx.DrawLine(pen, tr(s.From), tr(s.To)); return; }
        const int n = 24;
        var prev = tr(AjanMpgParser.Interpolate(s, 0));
        for (int i = 1; i <= n; i++)
        {
            var cur = tr(AjanMpgParser.Interpolate(s, i / (double)n));
            ctx.DrawLine(pen, prev, cur);
            prev = cur;
        }
    }
    private static void DrawPart(DrawingContext ctx, MpgSegment s, double a, double b, Pen pen, Func<Point, Point> tr)
    {
        const int n = 14;
        var prev = tr(AjanMpgParser.Interpolate(s, a));
        for (int i = 1; i <= n; i++)
        {
            double t = a + (b - a) * i / n;
            var cur = tr(AjanMpgParser.Interpolate(s, t));
            ctx.DrawLine(pen, prev, cur);
            prev = cur;
        }
    }
}
