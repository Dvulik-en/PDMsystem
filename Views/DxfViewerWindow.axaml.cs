using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;

namespace BKMsystem.Views;

public partial class DxfViewerWindow : Window
{
    // Порог, ниже которого считаем компоненту вектора нулевой (для алгоритма Arbitrary Axis)
    private const double AxisEpsilon = 1.0 / 64.0;

    public DxfViewerWindow()
    {
        InitializeComponent();
    }

    public DxfViewerWindow(string filePath) : this()
    {
        FileNameText.Text = Path.GetFileName(filePath);
        RenderDxf(filePath);
    }

    private void RenderDxf(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var file = DxfFile.Load(stream);

            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };

            // Базовая матрица: инвертируем ось Y для системы координат UI (в DXF Y растёт вверх)
            Matrix baseTransform = Matrix.CreateScale(1, -1);

            foreach (var entity in file.Entities)
            {
                ProcessEntity(entity, file, group, baseTransform);
            }

            Rect rawBounds = group.Bounds;

            if (rawBounds.Width > 0 && rawBounds.Height > 0 &&
                !double.IsInfinity(rawBounds.Width) && !double.IsInfinity(rawBounds.Height))
            {
                const double margin = 10.0;

                double offsetX = -rawBounds.X + margin;
                double offsetY = -rawBounds.Y + margin;

                group.Transform = new TranslateTransform(offsetX, offsetY);

                DxfCanvas.Width = rawBounds.Width + (margin * 2);
                DxfCanvas.Height = rawBounds.Height + (margin * 2);
            }

            var pathControl = new Avalonia.Controls.Shapes.Path
            {
                Data = group,
                Stroke = new SolidColorBrush(Color.Parse("#00E5FF")),
                StrokeThickness = 0.8,
                StrokeJoin = PenLineJoin.Round,
                StrokeLineCap = PenLineCap.Round
            };

            DxfCanvas.Children.Clear();
            DxfCanvas.Children.Add(pathControl);
        }
        catch (Exception ex)
        {
            FileNameText.Text += $" (Ошибка чтения DXF: {ex.Message})";
        }
    }

    // ================================================================
    // OCS -> WCS: Arbitrary Axis Algorithm из спецификации DXF.
    // Для сущностей CIRCLE / ARC / LWPOLYLINE / ELLIPSE (для 2D-сущностей)
    // координаты хранятся в собственной OCS. Чтобы получить корректные WCS-координаты,
    // нужно построить базис (Ax, Ay, Az) из вектора экструзии Az и повернуть точку.
    // ================================================================
    private static (DxfVector ax, DxfVector ay, DxfVector az) BuildOcsBasis(DxfVector extrusion)
    {
        // Нормализуем Az
        double len = Math.Sqrt(extrusion.X * extrusion.X + extrusion.Y * extrusion.Y + extrusion.Z * extrusion.Z);
        if (len < 1e-12)
        {
            // Некорректный вектор — используем стандартный
            return (new DxfVector(1, 0, 0), new DxfVector(0, 1, 0), new DxfVector(0, 0, 1));
        }
        DxfVector az = new DxfVector(extrusion.X / len, extrusion.Y / len, extrusion.Z / len);

        DxfVector wy = new DxfVector(0, 1, 0);
        DxfVector wz = new DxfVector(0, 0, 1);

        DxfVector ax;
        if (Math.Abs(az.X) < AxisEpsilon && Math.Abs(az.Y) < AxisEpsilon)
            ax = Cross(wy, az);
        else
            ax = Cross(wz, az);

        ax = Normalize(ax);
        DxfVector ay = Normalize(Cross(az, ax));
        return (ax, ay, az);
    }

    private static DxfVector Cross(DxfVector a, DxfVector b) =>
        new DxfVector(a.Y * b.Z - a.Z * b.Y,
                      a.Z * b.X - a.X * b.Z,
                      a.X * b.Y - a.Y * b.X);

    private static DxfVector Normalize(DxfVector v)
    {
        double l = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return l < 1e-12 ? v : new DxfVector(v.X / l, v.Y / l, v.Z / l);
    }

    /// <summary>
    /// Переводит точку из OCS (в которой лежат X,Y CIRCLE/ARC/LWPOLYLINE) в WCS.
    /// Elevation (Z в OCS) берём как есть.
    /// </summary>
    private static Point OcsToWcs2D(double ocsX, double ocsY, double ocsZ, DxfVector extrusion)
    {
        var (ax, ay, az) = BuildOcsBasis(extrusion);
        double wx = ocsX * ax.X + ocsY * ay.X + ocsZ * az.X;
        double wy = ocsX * ax.Y + ocsY * ay.Y + ocsZ * az.Y;
        return new Point(wx, wy);
    }

    /// <summary>
    /// Определитель матрицы: отрицательное значение = зеркальная трансформация,
    /// в такой системе направление обхода дуги нужно инвертировать.
    /// </summary>
    private static bool IsFlipped(Matrix m) =>
        (m.M11 * m.M22 - m.M12 * m.M21) < 0;

    /// <summary>
    /// Средний масштаб трансформации (для радиуса дуг/окружностей).
    /// Для аффинной без сдвига это |sqrt(|det|)|.
    /// </summary>
    private static double UniformScale(Matrix m) =>
        Math.Sqrt(Math.Abs(m.M11 * m.M22 - m.M12 * m.M21));

    private void ProcessEntity(DxfEntity entity, DxfFile file, GeometryGroup group, Matrix transform)
    {
        // Служебные / вспомогательные сущности пропускаем
        if (entity is DxfXLine || entity is DxfRay) return;
        if (entity is DxfDimensionBase) return;               // размеры — не часть контура
        if (entity is DxfHatch) return;                       // штриховка не нужна для контура

        switch (entity)
        {
            // --- 1. ОТРЕЗОК (LINE) --- координаты в WCS
            case DxfLine line:
            {
                var p1 = transform.Transform(new Point(line.P1.X, line.P1.Y));
                var p2 = transform.Transform(new Point(line.P2.X, line.P2.Y));
                group.Children.Add(new LineGeometry(p1, p2));
                break;
            }

            // --- 2. ДУГА (ARC) --- координаты в OCS
            case DxfArc arc:
                AddArc(arc, group, transform);
                break;

            // --- 3. ОКРУЖНОСТЬ (CIRCLE) --- координаты в OCS
            case DxfCircle circle:
                AddCircle(circle, group, transform);
                break;

            // --- 4. LWPOLYLINE --- координаты в OCS
            case DxfLwPolyline lw when lw.Vertices.Count > 0:
            {
                var verts = lw.Vertices
                    .Select(v => (X: v.X, Y: v.Y, Bulge: v.Bulge))
                    .ToList();
                ProcessPolylineVertices(verts, lw.IsClosed, lw.Elevation, lw.ExtrusionDirection, group, transform);
                break;
            }

            // --- 5. POLYLINE (2D/3D) ---
            case DxfPolyline pl when pl.Vertices.Count > 0:
            {
                // 3D POLYLINE в модельспейсе хранит координаты уже в WCS,
                // 2D POLYLINE (флаг Is3DPolyline=false) — в OCS.
                bool is3D = pl.Is3DPolyline;
                var verts = pl.Vertices
                    .Select(v => (X: v.Location.X, Y: v.Location.Y, Bulge: v.Bulge))
                    .ToList();

                if (is3D)
                {
                    // Считаем плоской проекцией XY, extrusion=(0,0,1)
                    ProcessPolylineVertices(verts, pl.IsClosed, 0.0,
                        new DxfVector(0, 0, 1), group, transform);
                }
                else
                {
                    ProcessPolylineVertices(verts, pl.IsClosed, pl.Elevation,
                        pl.Normal, group, transform);
                }
                break;
            }

            // --- 6. ЭЛЛИПС (ELLIPSE) --- Center и MajorAxis в WCS, но extrusion задаёт плоскость
            case DxfEllipse ellipse:
                AddEllipse(ellipse, group, transform);
                break;

            // --- 7. СПЛАЙН (SPLINE) --- координаты в WCS
            case DxfSpline spline when spline.ControlPoints.Count > 1:
            {
                AddSpline(spline, group, transform);
                break;
            }

            // --- 8. POINT --- маленький крестик? Пропускаем.
            case DxfModelPoint:
                break;

            // --- 9. INSERT (вставка блока), в т.ч. массивы ---
            case DxfInsert insert:
                AddInsert(insert, file, group, transform);
                break;

            // --- 10. SOLID / TRACE --- 4-угольник, координаты в OCS
            case DxfSolid solid:
            {
                var (ax, ay, az) = BuildOcsBasis(solid.ExtrusionDirection);
                Point Convert(DxfPoint p)
                {
                    double wx = p.X * ax.X + p.Y * ay.X + p.Z * az.X;
                    double wy = p.X * ax.Y + p.Y * ay.Y + p.Z * az.Y;
                    return transform.Transform(new Point(wx, wy));
                }
                var p1 = Convert(solid.FirstCorner);
                var p2 = Convert(solid.SecondCorner);
                var p3 = Convert(solid.ThirdCorner);
                var p4 = Convert(solid.FourthCorner);

                var geo = new PathGeometry();
                var fig = new PathFigure { StartPoint = p1, IsClosed = true };
                fig.Segments!.Add(new LineSegment { Point = p2 });
                fig.Segments!.Add(new LineSegment { Point = p4 });   // порядок DXF: 1,2,4,3
                fig.Segments!.Add(new LineSegment { Point = p3 });
                geo.Figures!.Add(fig);
                group.Children.Add(geo);
                break;
            }
        }
    }

    // ------------------------------------------------------------------
    //                     ГЕОМЕТРИЯ: CIRCLE / ARC / ELLIPSE
    // ------------------------------------------------------------------
    private void AddCircle(DxfCircle circle, GeometryGroup group, Matrix transform)
    {
        // Центр OCS -> WCS
        var wcsCenter = OcsToWcs2D(circle.Center.X, circle.Center.Y, circle.Center.Z, circle.Normal);
        var center = transform.Transform(wcsCenter);
        double radius = circle.Radius * UniformScale(transform);

        group.Children.Add(new EllipseGeometry
        {
            Center = center,
            RadiusX = radius,
            RadiusY = radius
        });
    }

    private void AddArc(DxfArc arc, GeometryGroup group, Matrix transform)
    {
        // Углы задаются в плоскости OCS, отсчитываются против часовой стрелки от +X OCS.
        double startRad = arc.StartAngle * Math.PI / 180.0;
        double endRad = arc.EndAngle * Math.PI / 180.0;

        // Точки старта/конца в OCS
        double sxOcs = arc.Center.X + arc.Radius * Math.Cos(startRad);
        double syOcs = arc.Center.Y + arc.Radius * Math.Sin(startRad);
        double exOcs = arc.Center.X + arc.Radius * Math.Cos(endRad);
        double eyOcs = arc.Center.Y + arc.Radius * Math.Sin(endRad);

        // OCS -> WCS
        var wcsStart = OcsToWcs2D(sxOcs, syOcs, arc.Center.Z, arc.Normal);
        var wcsEnd = OcsToWcs2D(exOcs, eyOcs, arc.Center.Z, arc.Normal);

        // WCS -> UI (через основную матрицу)
        var startPt = transform.Transform(wcsStart);
        var endPt = transform.Transform(wcsEnd);

        double radius = arc.Radius * UniformScale(transform);

        // Разница углов (всегда положительная, DXF идёт CCW от Start к End в OCS)
        double sweep = arc.EndAngle - arc.StartAngle;
        while (sweep < 0) sweep += 360;
        while (sweep >= 360) sweep -= 360;

        bool isLargeArc = sweep > 180.0;

        // Направление: в OCS дуга рисуется CCW. Если суммарная матрица (transform + переход OCS->WCS)
        // содержит нечётное количество зеркал — направление в UI инвертируется.
        // Определитель transform уже включает инверсию Y. Для OCS учитываем знак Az.z ниже.
        bool flippedByTransform = IsFlipped(transform);
        bool flippedByOcs = arc.Normal.Z < 0;                    // «взгляд снизу»
        bool flipped = flippedByTransform ^ flippedByOcs;        // XOR

        var sweepDir = flipped ? SweepDirection.CounterClockwise : SweepDirection.Clockwise;

        var pathGeo = new PathGeometry();
        var figure = new PathFigure { StartPoint = startPt, IsClosed = false };
        figure.Segments!.Add(new ArcSegment
        {
            Point = endPt,
            Size = new Size(radius, radius),
            IsLargeArc = isLargeArc,
            SweepDirection = sweepDir
        });
        pathGeo.Figures!.Add(figure);
        group.Children.Add(pathGeo);
    }

    private void AddEllipse(DxfEllipse ellipse, GeometryGroup group, Matrix transform)
    {
        // MajorAxis задаётся в WCS (относительно Center), Center в WCS (для 2D-плоскости XY).
        double majorLength = Math.Sqrt(ellipse.MajorAxis.X * ellipse.MajorAxis.X +
                                       ellipse.MajorAxis.Y * ellipse.MajorAxis.Y);
        double minorLength = majorLength * ellipse.MinorAxisRatio;
        double rotationAngle = Math.Atan2(ellipse.MajorAxis.Y, ellipse.MajorAxis.X);

        var ellipseGeo = new EllipseGeometry
        {
            Center = new Point(0, 0),
            RadiusX = majorLength,
            RadiusY = minorLength
        };

        var localTransform = Matrix.CreateRotation(rotationAngle) *
                             Matrix.CreateTranslation(ellipse.Center.X, ellipse.Center.Y) *
                             transform;

        group.Children.Add(new GeometryGroup
        {
            Transform = new MatrixTransform(localTransform),
            Children = { ellipseGeo }
        });
    }

    // ------------------------------------------------------------------
    //                              SPLINE (кубический)
    // ------------------------------------------------------------------
    private void AddSpline(DxfSpline spline, GeometryGroup group, Matrix transform)
    {
        // Аппроксимация линиями по FitPoints (если есть) — они лежат точно на кривой,
        // иначе — по ControlPoints (это грубее, но лучше чем ничего).
        List<Point> pts;
        if (spline.FitPoints != null && spline.FitPoints.Count > 1)
        {
            pts = spline.FitPoints
                .Select(p => transform.Transform(new Point(p.X, p.Y)))
                .ToList();
        }
        else
        {
            pts = spline.ControlPoints
                .Select(cp => transform.Transform(new Point(cp.Point.X, cp.Point.Y)))
                .ToList();
        }

        if (pts.Count < 2) return;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(pts[0], isFilled: false);
            for (int i = 1; i < pts.Count; i++)
                ctx.LineTo(pts[i]);
            ctx.EndFigure(isClosed: spline.IsClosed);
        }
        group.Children.Add(geo);
    }

    // ------------------------------------------------------------------
    //                              INSERT (в т.ч. массивы)
    // ------------------------------------------------------------------
    private void AddInsert(DxfInsert insert, DxfFile file, GeometryGroup group, Matrix transform)
    {
        var block = file.Blocks.FirstOrDefault(b => b.Name == insert.Name);
        if (block == null) return;

        double scaleX = insert.XScaleFactor == 0 ? 1 : insert.XScaleFactor;
        double scaleY = insert.YScaleFactor == 0 ? 1 : insert.YScaleFactor;
        double rotationRad = insert.Rotation * Math.PI / 180.0;

        // Матрица одиночной вставки блока
        Matrix singleInsertMatrix =
            Matrix.CreateTranslation(-block.BasePoint.X, -block.BasePoint.Y) *
            Matrix.CreateScale(scaleX, scaleY) *
            Matrix.CreateRotation(rotationRad);

        // Учитываем сетку: ColumnCount x RowCount с шагами ColumnSpacing / RowSpacing
        int cols = Math.Max(1, (int)insert.ColumnCount);
        int rows = Math.Max(1, (int)insert.RowCount);
        double colSpacing = insert.ColumnSpacing;
        double rowSpacing = insert.RowSpacing;

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                double offsetX = insert.Location.X + col * colSpacing;
                double offsetY = insert.Location.Y + row * rowSpacing;

                Matrix insertMatrix = singleInsertMatrix *
                                      Matrix.CreateTranslation(offsetX, offsetY);

                Matrix combinedMatrix = insertMatrix * transform;

                foreach (var blockEntity in block.Entities)
                {
                    ProcessEntity(blockEntity, file, group, combinedMatrix);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    //                          POLYLINE (общий обработчик)
    // ------------------------------------------------------------------
    private void ProcessPolylineVertices(
        List<(double X, double Y, double Bulge)> vertices,
        bool isClosed,
        double elevation,
        DxfVector extrusion,
        GeometryGroup group,
        Matrix transform)
    {
        if (vertices.Count == 0) return;

        // Преобразуем каждую вершину: OCS(x,y,elevation) -> WCS -> UI
        Point ToUi(double x, double y)
        {
            var wcs = OcsToWcs2D(x, y, elevation, extrusion);
            return transform.Transform(wcs);
        }

        var pathGeo = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = ToUi(vertices[0].X, vertices[0].Y),
            IsClosed = isClosed
        };

        bool flippedByTransform = IsFlipped(transform);
        bool flippedByOcs = extrusion.Z < 0;
        bool isFlipped = flippedByTransform ^ flippedByOcs;

        double uniScale = UniformScale(transform);

        int count = vertices.Count;
        int lastIndex = isClosed ? count : count - 1;

        for (int i = 0; i < lastIndex; i++)
        {
            var vCurrent = vertices[i];
            var vNext = vertices[(i + 1) % count];
            var pNext = ToUi(vNext.X, vNext.Y);

            if (Math.Abs(vCurrent.Bulge) > 1e-9)
            {
                double bulge = vCurrent.Bulge;

                // Хорда — в OCS (масштаб OCS==WCS для стандартного базиса; для радиуса
                // используем OCS-хорду, а затем умножаем на uniScale, чтобы получить радиус в UI).
                double dx = vNext.X - vCurrent.X;
                double dy = vNext.Y - vCurrent.Y;
                double chordLen = Math.Sqrt(dx * dx + dy * dy);
                if (chordLen < 1e-12) continue;

                // Радиус дуги через bulge: R = chord * (1 + b^2) / (4 * |b|)
                double absB = Math.Abs(bulge);
                double radiusOcs = chordLen * (1.0 + bulge * bulge) / (4.0 * absB);
                double radius = radiusOcs * uniScale;

                // Угол дуги: 4 * atan(|bulge|). При |bulge|=1 угол=180°, при >1 — большая дуга.
                double includedAngle = 4.0 * Math.Atan(absB);
                bool isLargeArc = includedAngle > Math.PI;

                // В OCS: bulge > 0 => CCW от Current к Next, bulge < 0 => CW.
                SweepDirection sweepDir;
                if (bulge > 0)
                    sweepDir = isFlipped ? SweepDirection.CounterClockwise : SweepDirection.Clockwise;
                else
                    sweepDir = isFlipped ? SweepDirection.Clockwise : SweepDirection.CounterClockwise;

                figure.Segments!.Add(new ArcSegment
                {
                    Point = pNext,
                    Size = new Size(radius, radius),
                    IsLargeArc = isLargeArc,
                    SweepDirection = sweepDir
                });
            }
            else
            {
                figure.Segments!.Add(new LineSegment { Point = pNext });
            }
        }

        pathGeo.Figures!.Add(figure);
        group.Children.Add(pathGeo);
    }
}
