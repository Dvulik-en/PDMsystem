using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia;

namespace BKMsystem.Services;

public class MpgSegment
{
    public Point From, To;
    public bool IsCut;
    public bool IsArc;
    public double I, J;
    public double Sweep;
    public double Length;
}

public class MpgPathResult
{
    public List<MpgSegment> Segments { get; } = new();
    public double SheetW, SheetH;
    public double MinX = double.MaxValue, MinY = double.MaxValue;
    public double MaxX = double.MinValue, MaxY = double.MinValue;
    public void Extend(double x, double y)
    {
        MinX = Math.Min(MinX, x); MaxX = Math.Max(MaxX, x);
        MinY = Math.Min(MinY, y); MaxY = Math.Max(MaxY, y);
    }
}

/// <summary>
/// AJANCAM .MPG (координаты в 0.1 мм):
///   3 — начало детали, 5 — подход к детали, 7..8 — контур (ОТНОСИТЕЛЬНЫЕ шаги).
/// Подход после "5" в разных файлах закодирован по-разному:
///   (А) абсолютная точка от Origin        — 10S_SL01 (Origin 710|710, первый якорь -387|-450)
///   (Б) инкремент от текущей позиции       — 02S_SL01 (Origin 0|0, якоря уходят в минус при абсолюте)
/// Парсер строит ОБЕ интерпретации и выбирает ту, где больше точек реза внутри листа.
/// </summary>
public static class AjanMpgParser
{
    private static readonly Regex RxNum = new(@"[+-]?\d+(?:\.\d+)?", RegexOptions.Compiled);

    private class RawBlock
    {
        public double Ax, Ay;
        public bool HasAnchor;
        public List<double[]> Steps = new();
    }

    public static MpgPathResult Parse(string path)
    {
        double originX = 0, originY = 0, sheetW = 0, sheetH = 0;
        var blocks = new List<RawBlock>();
        RawBlock? cur = null;
        int mode = 0;

        foreach (var raw in File.ReadLines(path))
        {
            string t = raw.Trim();
            if (t.Length == 0) continue;

            if (t.StartsWith("Sheet ", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(t, @"Sheet\s+(\d+(?:\.\d+)?)[XxХх](\d+(?:\.\d+)?)");
                if (m.Success) { sheetW = P(m.Groups[1].Value); sheetH = P(m.Groups[2].Value); }
                continue;
            }
            if (t.StartsWith("Origin ", StringComparison.OrdinalIgnoreCase))
            {
                var p = t[7..].Split('|');
                if (p.Length == 2) { originX = P(p[0]); originY = P(p[1]); }
                continue;
            }

            if (t == "3") { cur = new RawBlock(); blocks.Add(cur); continue; }
            if (t == "5") { mode = 5; cur ??= NewBlock(blocks); continue; }
            if (t == "7") { mode = 7; cur ??= NewBlock(blocks); continue; }
            if (t == "8") { mode = 0; cur = null; continue; }
            if (t is "4" or "6") continue;

            var mm = RxNum.Matches(t);
            if (mm.Count == 0 || cur == null) continue;
            var nums = new double[mm.Count];
            for (int i = 0; i < mm.Count; i++) nums[i] = P(mm[i].Value) * 0.1; // 0.1 мм -> мм

            if (mode == 5 && nums.Length >= 2)
            {
                cur.Ax = nums[0]; cur.Ay = nums[1]; cur.HasAnchor = true;
                mode = 0;
            }
            else if (mode == 7)
            {
                cur.Steps.Add(nums);
            }
        }

        // Две интерпретации подхода: (А) все якоря абсолютные, (Б) первый абсолютный, дальше — инкременты
        var resA = Build(blocks, originX, originY, deltas: false);
        var resB = Build(blocks, originX, originY, deltas: true);
        resA.SheetW = resB.SheetW = sheetW;
        resA.SheetH = resB.SheetH = sheetH;

        double scoreA = InsideScore(resA, sheetW, sheetH);
        double scoreB = InsideScore(resB, sheetW, sheetH);
        var best = scoreB > scoreA ? resB : resA;

        best.Extend(0, 0);
        if (best.SheetW > 0) best.Extend(best.SheetW, best.SheetH);
        return best;
    }

    private static RawBlock NewBlock(List<RawBlock> blocks)
    {
        var b = new RawBlock();
        blocks.Add(b);
        return b;
    }

    private static MpgPathResult Build(List<RawBlock> blocks, double ox, double oy, bool deltas)
    {
        var r = new MpgPathResult();
        var cur = new Point(ox, oy);
        bool firstAnchor = true;

        foreach (var b in blocks)
        {
            if (b.HasAnchor)
            {
                // Первый подход всегда абсолютный от Origin (резак стартует из машинного нуля),
                // дальше — либо все абсолютные (режим А), либо инкременты (режим Б)
                Point target = (!deltas || firstAnchor)
                    ? new Point(ox + b.Ax, oy + b.Ay)
                    : new Point(cur.X + b.Ax, cur.Y + b.Ay);
                firstAnchor = false;

                if (Dist(cur, target) > 0.001)
                    AddLine(r, cur, target, cut: false);
                cur = target;
            }

            foreach (var nums in b.Steps)
            {
                if (nums.Length == 2)
                {
                    var to = new Point(cur.X + nums[0], cur.Y + nums[1]);
                    if (Dist(cur, to) > 0.001) AddLine(r, cur, to, cut: true);
                    cur = to;
                }
                else if (nums.Length >= 4)
                {
                    double dx = nums[0], dy = nums[1], ci = nums[2], cj = nums[3];
                    // 5-е число со знаком — направление дуги; '+' = против часовой (unconfirmed: если дуги вывернуты, поменяйте на nums[4] < 0)
                    bool ccw = nums.Length < 5 || nums[4] >= 0;

                    double r1 = Math.Sqrt(ci * ci + cj * cj);
                    double r2 = Math.Sqrt((dx - ci) * (dx - ci) + (dy - cj) * (dy - cj));
                    var to = new Point(cur.X + dx, cur.Y + dy);

                    if (r1 > 0.005 && Math.Abs(r1 - r2) / r1 < 0.05)
                    {
                        double a0 = Math.Atan2(-cj, -ci);
                        double a1 = Math.Atan2(dy - cj, dx - ci);
                        double sweep = a1 - a0;
                        if (ccw) { if (sweep < 0) sweep += 2 * Math.PI; }
                        else { if (sweep > 0) sweep -= 2 * Math.PI; }

                        var seg = new MpgSegment
                        {
                            From = cur, To = to, IsCut = true, IsArc = true,
                            I = ci, J = cj, Sweep = sweep, Length = Math.Abs(sweep) * r1
                        };
                        r.Segments.Add(seg);
                        for (int k = 0; k <= 24; k++)
                        {
                            var pt = Interpolate(seg, k / 24.0);
                            r.Extend(pt.X, pt.Y);
                        }
                    }
                    else if (Dist(cur, to) > 0.001)
                    {
                        AddLine(r, cur, to, cut: true);
                    }
                    cur = to;
                }
            }
        }
        return r;
    }

    /// <summary>Доля середин резовых сегментов, попадающих внутрь листа (с запасом 10%).</summary>
    private static double InsideScore(MpgPathResult r, double w, double h)
    {
        int cut = 0, inside = 0;
        double x0 = -0.1 * (w > 0 ? w : 1), y0 = -0.1 * (h > 0 ? h : 1);
        double x1 = w > 0 ? w * 1.1 : double.MaxValue;
        double y1 = h > 0 ? h * 1.1 : double.MaxValue;

        foreach (var s in r.Segments)
        {
            if (!s.IsCut) continue;
            cut++;
            double mx = (s.From.X + s.To.X) / 2, my = (s.From.Y + s.To.Y) / 2;
            if (mx >= x0 && mx <= x1 && my >= y0 && my <= y1) inside++;
        }
        return cut == 0 ? 0 : (double)inside / cut;
    }

    public static Point Interpolate(MpgSegment s, double t)
    {
        if (!s.IsArc)
            return new Point(s.From.X + (s.To.X - s.From.X) * t,
                             s.From.Y + (s.To.Y - s.From.Y) * t);
        double cx = s.From.X + s.I, cy = s.From.Y + s.J;
        double r = Math.Sqrt(s.I * s.I + s.J * s.J);
        double a0 = Math.Atan2(-s.J, -s.I);
        double a = a0 + s.Sweep * t;
        return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
    }

    private static void AddLine(MpgPathResult r, Point a, Point b, bool cut)
    {
        r.Segments.Add(new MpgSegment { From = a, To = b, IsCut = cut, Length = Dist(a, b) });
        r.Extend(a.X, a.Y); r.Extend(b.X, b.Y);
    }
    private static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static double P(string s) =>
        double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
}
