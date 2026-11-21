using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.App;

public sealed record ChartSeries(string Name, Color Color, IReadOnlyList<ProfilePoint> Points, double Thickness = 2);

/// <summary>
/// Графика на профил/разрез: хоризонтално – километраж (или отстояние), вертикално – кота.
/// Оцветява изкопа/насипа между две серии, показва стойностите под курсора.
/// </summary>
public sealed class ProfileChart : FrameworkElement
{
    private const double MarginLeft = 64, MarginRight = 16, MarginTop = 16, MarginBottom = 40;

    private static readonly Brush Back = ColorRamp.Brush(Colors.White);
    private static readonly Brush GridBrush = ColorRamp.Brush(Color.FromRgb(0xE3, 0xE3, 0xE3));
    private static readonly Brush AxisBrush = ColorRamp.Brush(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly Brush TextBrush = ColorRamp.Brush(Color.FromRgb(0x20, 0x20, 0x20));
    private static readonly Brush CutBrush = ColorRamp.Brush(Color.FromArgb(0x70, 0xD7, 0x30, 0x27));
    private static readonly Brush FillBrush = ColorRamp.Brush(Color.FromArgb(0x70, 0x31, 0x6B, 0xD6));
    private static readonly Brush LabelBack = ColorRamp.Brush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

    private double? _hoverS;
    private double _sMin, _sMax, _zMin, _zMax, _kx, _ky;

    public ProfileChart()
    {
        ClipToBounds = true;
        MinHeight = 200;
    }

    public List<ChartSeries> Series { get; } = new();

    /// <summary>Индекси на сериите „съществуващ терен“ и „проект“ за оцветяване на изкоп/насип (-1 – без).</summary>
    public int ExistingIndex { get; set; } = -1;
    public int DesignIndex { get; set; } = -1;

    public string XTitle { get; set; } = "Километраж";
    public Func<double, string> XFormat { get; set; } = s => s.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Вертикално преувеличение; null – автоматично (по размера на прозореца).</summary>
    public double? VerticalExaggeration { get; set; }

    /// <summary>Действително вертикално преувеличение при последното изрисуване.</summary>
    public double ActualExaggeration => _kx > 0 ? _ky / _kx : 0;

    /// <summary>Отстояние/километраж под курсора (null – извън графиката).</summary>
    public event Action<double?>? HoverChanged;

    public void Refresh() => InvalidateVisual();

    // ================= рисуване =================

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(RenderSize);
        dc.DrawRectangle(Back, null, rect);
        var all = Series.SelectMany(s => s.Points).Where(p => p.Z.HasValue).ToList();
        if (all.Count < 2)
        {
            Text(dc, "Няма данни по линията.", new Point(MarginLeft, MarginTop), 13);
            return;
        }

        _sMin = Series.SelectMany(s => s.Points).Min(p => p.Station);
        _sMax = Series.SelectMany(s => s.Points).Max(p => p.Station);
        _zMin = all.Min(p => p.Z!.Value);
        _zMax = all.Max(p => p.Z!.Value);
        if (_sMax - _sMin < 1e-9) _sMax = _sMin + 1;
        double zPad = Math.Max((_zMax - _zMin) * 0.08, 0.1);
        _zMin -= zPad; _zMax += zPad;

        double w = Math.Max(rect.Width - MarginLeft - MarginRight, 10);
        double h = Math.Max(rect.Height - MarginTop - MarginBottom, 10);
        _kx = w / (_sMax - _sMin);
        _ky = h / (_zMax - _zMin);
        if (VerticalExaggeration is double ve && ve > 0)
        {
            // фиксирано преувеличение – центрираме вертикално
            _ky = _kx * ve;
            double zc = (_zMin + _zMax) / 2, half = h / _ky / 2;
            _zMin = zc - half; _zMax = zc + half;
        }

        var plot = new Rect(MarginLeft, MarginTop, w, h);
        DrawGrid(dc, plot);
        dc.PushClip(new RectangleGeometry(plot));
        DrawCutFill(dc);
        foreach (var s in Series) DrawSeries(dc, s);
        dc.Pop();
        dc.DrawRectangle(null, new Pen(AxisBrush, 1), plot);
        DrawLegend(dc, plot);
        DrawHover(dc, plot);
    }

    private double X(double s) => MarginLeft + (s - _sMin) * _kx;
    private double Y(double z) => MarginTop + (_zMax - z) * _ky;
    private double SAt(double x) => _sMin + (x - MarginLeft) / _kx;

    private static double NiceStep(double range, int target)
    {
        double raw = range / Math.Max(target, 1);
        double pow = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (double k in new[] { 1, 2, 2.5, 5, 10 })
            if (k * pow >= raw) return k * pow;
        return 10 * pow;
    }

    private void DrawGrid(DrawingContext dc, Rect plot)
    {
        var pen = new Pen(GridBrush, 1);
        double sx = NiceStep(_sMax - _sMin, Math.Max(2, (int)(plot.Width / 90)));
        for (double s = Math.Ceiling(_sMin / sx) * sx; s <= _sMax + 1e-9; s += sx)
        {
            double x = X(s);
            dc.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            Text(dc, XFormat(s), new Point(x, plot.Bottom + 4), 11, center: true);
        }
        double sz = NiceStep(_zMax - _zMin, Math.Max(2, (int)(plot.Height / 40)));
        for (double z = Math.Ceiling(_zMin / sz) * sz; z <= _zMax + 1e-9; z += sz)
        {
            double y = Y(z);
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
            Text(dc, z.ToString(sz < 1 ? "0.00" : "0.0", CultureInfo.InvariantCulture), new Point(plot.Left - 6, y - 7), 11, right: true);
        }
        Text(dc, XTitle, new Point(plot.Left + plot.Width / 2, plot.Bottom + 20), 11, center: true);
    }

    private void DrawSeries(DrawingContext dc, ChartSeries s)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            bool open = false;
            foreach (var p in s.Points)
            {
                if (p.Z is not double z) { open = false; continue; }
                var pt = new Point(X(p.Station), Y(z));
                if (!open) { ctx.BeginFigure(pt, false, false); open = true; }
                else ctx.LineTo(pt, true, true);
            }
        }
        g.Freeze();
        dc.DrawGeometry(null, new Pen(ColorRamp.Brush(s.Color), s.Thickness) { LineJoin = PenLineJoin.Round }, g);
    }

    private void DrawCutFill(DrawingContext dc)
    {
        if (ExistingIndex < 0 || DesignIndex < 0 || ExistingIndex >= Series.Count || DesignIndex >= Series.Count) return;
        var eg = Series[ExistingIndex].Points;
        var de = Series[DesignIndex].Points;
        // всички четириъгълници от един вид в една геометрия – без видими шевове между тях
        var fillGeo = new StreamGeometry { FillRule = FillRule.Nonzero };
        var cutGeo = new StreamGeometry { FillRule = FillRule.Nonzero };
        var fillCtx = fillGeo.Open(); // затварят се изрично по-долу, преди Freeze
        var cutCtx = cutGeo.Open();
        void Quad(double s0, double e0, double d0, double s1, double e1, double d1, bool fill)
        {
            var ctx = fill ? fillCtx : cutCtx;
            // обхождане в една и съща посока за всички – Nonzero ги обединява
            bool up = fill;
            ctx.BeginFigure(new Point(X(s0), Y(up ? e0 : d0)), true, true);
            ctx.PolyLineTo(new[]
            {
                new Point(X(s1), Y(up ? e1 : d1)),
                new Point(X(s1), Y(up ? d1 : e1)),
                new Point(X(s0), Y(up ? d0 : e0)),
            }, false, false);
        }
        var stations = eg.Select(p => p.Station).Concat(de.Select(p => p.Station)).Distinct().OrderBy(s => s).ToList();
        for (int i = 1; i < stations.Count; i++)
        {
            double s0 = stations[i - 1], s1 = stations[i];
            if (ProfileSampler.ElevationAtStation(eg, (s0 + s1) / 2) is null || ProfileSampler.ElevationAtStation(de, (s0 + s1) / 2) is null) continue;
            var e0 = ProfileSampler.ElevationAtStation(eg, s0); var e1 = ProfileSampler.ElevationAtStation(eg, s1);
            var d0 = ProfileSampler.ElevationAtStation(de, s0); var d1 = ProfileSampler.ElevationAtStation(de, s1);
            if (e0 is null || e1 is null || d0 is null || d1 is null) continue;
            double h0 = d0.Value - e0.Value, h1 = d1.Value - e1.Value;
            if (h0 * h1 >= 0)
                Quad(s0, e0.Value, d0.Value, s1, e1.Value, d1.Value, h0 + h1 > 0);
            else
            {
                double sx = s0 + (s1 - s0) * h0 / (h0 - h1);
                double ex = e0.Value + (e1.Value - e0.Value) * (sx - s0) / (s1 - s0);
                Quad(s0, e0.Value, d0.Value, sx, ex, ex, h0 > 0);
                Quad(sx, ex, ex, s1, e1.Value, d1.Value, h1 > 0);
            }
        }
        fillCtx.Close();
        cutCtx.Close();
        fillGeo.Freeze();
        cutGeo.Freeze();
        dc.DrawGeometry(FillBrush, null, fillGeo);
        dc.DrawGeometry(CutBrush, null, cutGeo);
    }

    private void DrawLegend(DrawingContext dc, Rect plot)
    {
        double x = plot.Left + 8, y = plot.Top + 6;
        foreach (var s in Series)
        {
            dc.DrawRectangle(LabelBack, null, new Rect(x - 4, y - 2, 150, 18));
            dc.DrawLine(new Pen(ColorRamp.Brush(s.Color), 3), new Point(x, y + 7), new Point(x + 22, y + 7));
            Text(dc, s.Name, new Point(x + 28, y), 11);
            y += 18;
        }
    }

    private void DrawHover(DrawingContext dc, Rect plot)
    {
        if (_hoverS is not double s || s < _sMin || s > _sMax) return;
        double x = X(s);
        dc.DrawLine(new Pen(AxisBrush, 1) { DashStyle = DashStyles.Dash }, new Point(x, plot.Top), new Point(x, plot.Bottom));
        var lines = new List<string> { XFormat(s) };
        double? ze = null, zd = null;
        for (int i = 0; i < Series.Count; i++)
        {
            var z = ProfileSampler.ElevationAtStation(Series[i].Points, s);
            if (i == ExistingIndex) ze = z;
            if (i == DesignIndex) zd = z;
            lines.Add($"{Series[i].Name}: {(z.HasValue ? z.Value.ToString("0.000", CultureInfo.InvariantCulture) : "—")}");
            if (z.HasValue) dc.DrawEllipse(ColorRamp.Brush(Series[i].Color), null, new Point(x, Y(z.Value)), 3.5, 3.5);
        }
        if (ze.HasValue && zd.HasValue)
            lines.Add($"h = {(zd.Value - ze.Value).ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture)}");

        var ft = Formatted(string.Join("\n", lines), 11);
        double bx = x + 10 + ft.Width > plot.Right ? x - 14 - ft.Width : x + 10;
        dc.DrawRoundedRectangle(LabelBack, new Pen(AxisBrush, 0.5), new Rect(bx - 4, plot.Top + 4, ft.Width + 8, ft.Height + 6), 3, 3);
        dc.DrawText(ft, new Point(bx, plot.Top + 7));
    }

    private FormattedText Formatted(string text, double size) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, TextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void Text(DrawingContext dc, string text, Point at, double size, bool center = false, bool right = false)
    {
        var ft = Formatted(text, size);
        double x = center ? at.X - ft.Width / 2 : right ? at.X - ft.Width : at.X;
        dc.DrawText(ft, new Point(x, at.Y));
    }

    // ================= мишка =================

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_kx <= 0) return;
        var p = e.GetPosition(this);
        double s = SAt(p.X);
        _hoverS = s >= _sMin && s <= _sMax ? s : null;
        HoverChanged?.Invoke(_hoverS);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverS = null;
        HoverChanged?.Invoke(null);
        InvalidateVisual();
    }
}
