using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BKMsystem.Views;

/// <summary>
/// Рисует траекторию управляющей программы AJAN (.MPG — это текстовый G-код, а не видео).
/// Красным — рабочие ходы (G1/G2/G3, рез), серым — холостые перемещения (G0).
/// </summary>
public class MpgPreviewControl : Control
{
    public static readonly StyledProperty<string?> FilePathProperty =
        AvaloniaProperty.Register<MpgPreviewControl, string?>(nameof(FilePath));

    public string? FilePath
    {
        get => GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    private readonly List<(Point A, Point B, bool Cut)> _segments = new();
    private double _minX, _minY, _maxX, _maxY;

    static MpgPreviewControl()
    {
        FilePathProperty.Changed.AddClassHandler<MpgPreviewControl>((c, _) =>
        {
            c.LoadPath();
            c.InvalidateVisual();
        });
    }

    private void LoadPath()
    {
        _segments.Clear();
        _minX = _minY = double.MaxValue;
        _maxX = _maxY = double.MinValue;

        var path = FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        double curX = 0, curY = 0;
        int g = 0;
        var rx = new Regex(@"([GXY])\s*(-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase);

        foreach (var line in File.ReadLines(path))
        {
            bool moved = false;
            double nx = curX, ny = curY;

            foreach (Match m in rx.Matches(line))
            {
                string letter = m.Groups[1].Value.ToUpperInvariant();
                if (!double.TryParse(m.Groups[2].Value.Replace(',', '.'),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
                    continue;

                switch (letter)
                {
                    case "G": g = (int)val; break;
                    case "X": nx = val; moved = true; break;
                    case "Y": ny = val; moved = true; break;
                }
            }

            if (!moved) continue;

            var a = new Point(curX, curY);
            var b = new Point(nx, ny);
            bool isCut = g == 1 || g == 2 || g == 3;

            if (a != b)
            {
                _segments.Add((a, b, isCut));
                _minX = Math.Min(_minX, Math.Min(a.X, b.X));
                _maxX = Math.Max(_maxX, Math.Max(a.X, b.X));
                _minY = Math.Min(_minY, Math.Min(a.Y, b.Y));
                _maxY = Math.Max(_maxY, Math.Max(a.Y, b.Y));
            }

            curX = nx;
            curY = ny;
        }

        if (_segments.Count == 0)
        {
            _minX = 0; _minY = 0; _maxX = 1; _maxY = 1;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width < 10 || bounds.Height < 10) return;

        context.FillRectangle(new SolidColorBrush(Color.Parse("#101014")), bounds);
        if (_segments.Count == 0) return;

        double bw = Math.Max(1e-6, _maxX - _minX);
        double bh = Math.Max(1e-6, _maxY - _minY);
        double scale = Math.Min((bounds.Width - 40) / bw, (bounds.Height - 40) / bh);
        double ox = (bounds.Width - bw * scale) / 2 - _minX * scale;
        double oy = (bounds.Height - bh * scale) / 2 + _maxY * scale; // ось Y вверх, как на станке

        var rapidPen = new Pen(new SolidColorBrush(Color.Parse("#555566")), 1);
        var cutPen = new Pen(new SolidColorBrush(Color.Parse("#FF5252")), 1.6);

        foreach (var (a, b, isCut) in _segments)
        {
            var p1 = new Point(a.X * scale + ox, -a.Y * scale + oy);
            var p2 = new Point(b.X * scale + ox, -b.Y * scale + oy);
            context.DrawLine(isCut ? cutPen : rapidPen, p1, p2);
        }
    }
}
