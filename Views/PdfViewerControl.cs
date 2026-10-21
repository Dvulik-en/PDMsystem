using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PDFtoImage;
using SkiaSharp;

namespace BKMsystem.Views;

/// <summary>
/// PDF-просмотрщик. Ключевые отличия:
///  - масштаб реально меняет размер контрола (обёрнут ScrollViewer'ом → скроллбары как в Adobe);
///  - DPI рендера растёт вместе с Zoom, чтобы не было пикселизации;
///  - страницы рендерятся по одной, PageIndex ограничивается PageCount;
///  - bitmap кэшируется, EnsureBitmap не открывает файл повторно без нужды.
/// </summary>
public class PdfViewerControl : Control
{
    public static readonly StyledProperty<string?> FilePathProperty =
        AvaloniaProperty.Register<PdfViewerControl, string?>(nameof(FilePath));
    public string? FilePath { get => GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }

    public static readonly StyledProperty<int> PageIndexProperty =
        AvaloniaProperty.Register<PdfViewerControl, int>(nameof(PageIndex));
    public int PageIndex { get => GetValue(PageIndexProperty); set => SetValue(PageIndexProperty, value); }

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<PdfViewerControl, double>(nameof(Zoom), 1.0);
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    private string? _loadedPath;
    private int _loadedPage = -1;
    private double _loadedZoom = -1;
    private Bitmap? _bitmap;
    private static readonly Dictionary<string, int> _pageCache = new();

    static PdfViewerControl()
    {
        AffectsMeasure<PdfViewerControl>(FilePathProperty, PageIndexProperty, ZoomProperty);
        AffectsRender<PdfViewerControl>(FilePathProperty, PageIndexProperty, ZoomProperty);
    }

    public static int GetPageCount(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;
        if (_pageCache.TryGetValue(path, out int c)) return c;

        int n = 0;
        try
        {
            // Один проход: читаем файл в память и считаем страницы без 50 открытий с диска
            byte[] bytes = File.ReadAllBytes(path);
            for (int i = 0; i < 50; i++)
            {
                try
                {
                    using var ms = new MemoryStream(bytes);
                    using var img = Conversion.ToImage(ms, page: i, options: new(Dpi: 12));
                    n++;
                }
                catch { break; }
            }
        }
        catch { return 0; }

        _pageCache[path] = n;
        return n;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureBitmap();
        if (_bitmap == null) return new Size(400, 300);
        return new Size(_bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
    }

    public override void Render(DrawingContext ctx)
    {
        var rect = new Rect(Bounds.Size);
        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#1E1E1E")), rect);
        if (_bitmap == null) EnsureBitmap();
        if (_bitmap == null) return;

        var dest = new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
        ctx.DrawRectangle(Brushes.White, null, dest);
        ctx.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height), dest);
    }

    private void EnsureBitmap()
    {
        if (_loadedPath == FilePath && _loadedPage == PageIndex &&
            Math.Abs(_loadedZoom - Zoom) < 0.001 && _bitmap != null) return;

        _loadedPath = FilePath; _loadedPage = PageIndex; _loadedZoom = Zoom;
        _bitmap = null;
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath)) return;

        try
        {
            int dpi = (int)Math.Clamp(110 * Zoom, 60, 400);
            using var fs = File.OpenRead(FilePath);
            using var img = Conversion.ToImage(fs, page: Math.Max(0, PageIndex), options: new(Dpi: dpi));
            using var ms = new MemoryStream();
            img.Encode(ms, SKEncodedImageFormat.Png, 92);
            ms.Position = 0;
            _bitmap = new Bitmap(ms);
        }
        catch { }
    }
}
