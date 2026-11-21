using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SurfaceDesigner.Core;
using SurfaceDesigner.Core.Geometry;
using SurfaceDesigner.Core.Grading;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.App;

public enum ToolMode
{
    Pan,
    AddPoint,
    MovePoint,
    DeletePoint,
    SwapEdge,
    DrawBreakline,
    DrawBoundary,
    DrawProfile,
    DrawPlatform,
    EditPlatform,
    DrawSelection,
}

public enum ColorMode
{
    None,
    Elevation,
    Slope,
}

/// <summary>
/// 2D план: повърхнина (оцветени триъгълници, ребра, хоризонтали, точки), платформи,
/// картограма на земните маси и интерактивни инструменти.
/// Геометрията се кешира в локални координати (спрямо _origin) – така не се губи точност
/// при големи геодезически координати, а мащабирането/преместването е само трансформация.
/// </summary>
public sealed class SurfaceView : FrameworkElement
{
    private const int ElevationBins = 48;
    private const double PickRadiusPx = 10;

    private static readonly Brush Background = ColorRamp.Brush(Color.FromRgb(0xF7, 0xF7, 0xF4));
    private static readonly Brush PointBrush = ColorRamp.Brush(Color.FromRgb(0x20, 0x20, 0x20));
    private static readonly Brush EdgeBrush = ColorRamp.Brush(Color.FromArgb(0x70, 0x40, 0x40, 0x40));
    private static readonly Brush MinorContourBrush = ColorRamp.Brush(Color.FromArgb(0xB0, 0x6B, 0x3E, 0x1F));
    private static readonly Brush MajorContourBrush = ColorRamp.Brush(Color.FromRgb(0x5A, 0x2E, 0x10));

    // проектна повърхнина – червени ребра и хоризонтали
    private static readonly Brush DesignEdgeBrush = ColorRamp.Brush(Color.FromArgb(0x90, 0xE5, 0x39, 0x35));
    private static readonly Brush DesignMinorBrush = ColorRamp.Brush(Color.FromRgb(0xE5, 0x39, 0x35));
    private static readonly Brush DesignMajorBrush = ColorRamp.Brush(Color.FromRgb(0xB7, 0x1C, 0x1C));
    private static readonly Brush PlatformFill = ColorRamp.Brush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
    private static readonly Brush PlatformStroke = ColorRamp.Brush(Color.FromRgb(0x10, 0x10, 0x10));
    private static readonly Brush SelectedPlatformStroke = ColorRamp.Brush(Color.FromRgb(0xE0, 0x7B, 0x00));
    private static readonly Brush SelectionStroke = ColorRamp.Brush(Color.FromRgb(0x00, 0x99, 0xCC));
    private static readonly Brush SelectionFill = ColorRamp.Brush(Color.FromArgb(0x25, 0x00, 0x99, 0xCC));
    private static readonly Brush DraftStroke = ColorRamp.Brush(Color.FromRgb(0xCC, 0x00, 0x66));
    private static readonly Brush HighlightBrush = ColorRamp.Brush(Color.FromRgb(0xE0, 0x00, 0x00));
    private static readonly Brush LabelBrush = ColorRamp.Brush(Color.FromRgb(0x10, 0x10, 0x10));
    private static readonly Brush BreaklineBrush = ColorRamp.Brush(Color.FromRgb(0xB0, 0x12, 0x6B));
    private static readonly Brush SelectedBreaklineBrush = ColorRamp.Brush(Color.FromRgb(0x00, 0xB4, 0xD8));
    private static readonly Brush OuterBoundaryBrush = ColorRamp.Brush(Color.FromRgb(0x1B, 0x7A, 0x2E));
    private static readonly Brush HideBoundaryBrush = ColorRamp.Brush(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly Brush ShowBoundaryBrush = ColorRamp.Brush(Color.FromRgb(0x15, 0x65, 0xC0));
    private static readonly Brush SnapBrush = ColorRamp.Brush(Color.FromRgb(0x00, 0xA0, 0x40));
    private static readonly Brush LabelBack = ColorRamp.Brush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF));

    // ---- изглед ----
    private double _scale = 1;

    // ---- изглед ----
    private double _panX;

    // ---- изглед ----
    private double _panY;
    private Point2 _origin;

    // ---- кеширана геометрия ----
    private readonly List<(Brush Brush, Geometry Geometry)> _fills = new();
    private readonly List<(Brush Brush, Geometry Geometry)> _cutFillCells = new();

    /// <summary>Слой за изобразяване на една видима повърхнина (триъгълници + хоризонтали).</summary>
    private sealed record SurfaceLayer(string Name, bool Design, bool Active, double Interval, Geometry? Edges, Geometry? Minor, Geometry? Major);

    /// <summary>Видимите повърхнини в реда на рисуване: теренни отдолу, проектни отгоре; при равни – активната последна.</summary>
    private readonly List<SurfaceLayer> _layers = new();
    private bool _activeIsDesign;
    private double _zMin;
    private double _zMax;
    private double _cutFillMaxAbs;

    // ---- взаимодействие ----
    private ToolMode _tool = ToolMode.Pan;
    private readonly List<Point2> _draft = new();
    private bool _panning;
    private Point _panStart;
    private double _panStartX;
    private double _panStartY;
    private int _dragPoint = -1;

    /// <summary>Влачене на връх (или маркер в средата на страна) на платформа.</summary>
    private (Platform Platform, int Index, bool Midpoint)? _dragVertex;
    private bool _platformDragStarted;
    private Point2 _dragPos;
    private Point _dragStartScreen;
    private Point2 _mouseWorld;
    private bool _mouseInside;

    public SurfaceView()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    // ================= публично API =================
    public Project? Project { get; set; }
    public Surface? ActiveSurface { get; set; }
    public Platform? SelectedPlatform { get; set; }
    public Breakline? SelectedBreakline { get; set; }
    public SurfaceBoundary? SelectedBoundary { get; set; }

    /// <summary>Трасе на надлъжния профил (за показване в плана).</summary>
    public List<Point2>? ProfileLine { get; set; }

    /// <summary>Линии на напречните разрези и индекс на текущия.</summary>
    public List<(Point2 Left, Point2 Right)>? SectionLines { get; set; }
    public int CurrentSection { get; set; } = -1;

    /// <summary>Маркер в плана (напр. позиция под курсора в профила).</summary>
    public Point2? Marker { get; set; }
    public CutFillGrid? CutFill { get; set; }
    public List<Point2>? SelectionPolygon { get; set; }

    public bool ShowPoints { get; set; } = true;
    public bool ShowTriangles { get; set; } = true;
    public bool ShowContours { get; set; } = true;

    /// <summary>Надписи на котите по хоризонталите (главните; ако няма главни – второстепенните).</summary>
    public bool ShowContourLabels { get; set; } = true;

    /// <summary>Надписи и по хоризонталите на другите видими повърхнини (напр. терена под проекта).</summary>
    public bool ShowBackgroundLabels { get; set; } = true;

    /// <summary>Други видими повърхнини – рисуват се със своите триъгълници и хоризонтали (без оцветяване и точки).</summary>
    public List<Surface> BackgroundSurfaces { get; set; } = new();

    /// <summary>Дали активната повърхнина се изобразява (може да е скрита).</summary>
    private bool ActiveVisible => ActiveSurface is { IsVisible: true };
    public bool ShowCutFill { get; set; } = true;
    public ColorMode ColorMode { get; set; } = ColorMode.Elevation;

    public ToolMode Tool
    {
        get
        {
            return _tool;
        }

        set
        {
            _tool = value;
            _draft.Clear();
            Cursor = value switch
            {
                ToolMode.Pan => Cursors.Hand,
                ToolMode.AddPoint or ToolMode.DrawBreakline or ToolMode.DrawBoundary or ToolMode.DrawProfile or ToolMode.DrawPlatform or ToolMode.DrawSelection => Cursors.Cross,
                _ => Cursors.Arrow,
            };
            InvalidateVisual();
        }
    }

    public event Action<Point2>? AddPointRequested;

    public event Action<int, Point2>? MovePointRequested;

    public event Action<int>? DeletePointRequested;

    public event Action<int>? PointEditRequested;

    public event Action<int, int>? SwapEdgeRequested;

    public event Action<ToolMode, List<Point2>>? PolygonCompleted;

    /// <summary>Преди първата промяна на контура на платформа (за „Отмени“).</summary>
    public event Action<Platform>? PlatformEditStarting;

    /// <summary>Контурът на платформа е променен; final = true при пускане на бутона / изтриване на връх.</summary>
    public event Action<Platform, bool>? PlatformGeometryChanged;

    public event Action<Platform>? PlatformClicked;

    public event Action<Point2>? CursorMoved;

    /// <summary>Преизчислява кешираната геометрия (след промяна на данните или опциите).</summary>
    public void RebuildScene()
    {
        _fills.Clear();
        _cutFillCells.Clear();
        _layers.Clear();
        _labelLines.Clear();

        var s = ActiveSurface;
        _activeIsDesign = s?.IsDesign == true;
        bool contours = ShowContours; // стъпката е собствена за всяка повърхнина

        // Активната повърхнина: оцветяване + надписи с предимство
        if (s != null && s.IsVisible && s.TriangleCount > 0)
        {
            var st = s.GetStatistics();
            _zMin = st.MinZ;
            _zMax = st.MaxZ;
            if (ColorMode != ColorMode.None) BuildFills(s);
            if (contours && ShowContourLabels)
                BuildLabelLines(s, s.IsDesign ? DesignMajorBrush : ExistingLabelBrush, phase: 0.5);
        }

        // Всички видими повърхнини (вкл. активната) – със своите триъгълници и хоризонтали
        var visible = BackgroundSurfaces.Where(o => !ReferenceEquals(o, s)).Select(o => (Surface: o, Active: false)).ToList();
        if (s != null) visible.Add((s, true));
        foreach (var (o, active) in visible
                     .Where(v => v.Surface.IsVisible && v.Surface.TriangleCount > 0)
                     .OrderBy(v => v.Surface.IsDesign).ThenBy(v => v.Active))
        {
            Geometry? mi = null, ma = null;
            if (contours) (mi, ma) = BuildContours(o);
            _layers.Add(new SurfaceLayer(o.Name, o.IsDesign, active, o.EffectiveContourInterval(), ShowTriangles ? BuildEdges(o) : null, mi, ma));

            // надписите на другите повърхнини – разместени с половин интервал спрямо активната
            if (!active && contours && ShowContourLabels && ShowBackgroundLabels)
                BuildLabelLines(o, o.IsDesign ? DesignMajorBrush : ExistingLabelBrush, phase: 1.0);
        }

        if (CutFill != null && ShowCutFill) BuildCutFill(CutFill);
        InvalidateVisual();
    }

    /// <summary>Показва всички данни в прозореца.</summary>
    public void ZoomExtents()
    {
        var b = Bounds2.Empty;
        if (Project != null)
        {
            foreach (var s in Project.Surfaces)
                if (s.Points.Count > 0) b = b.Union(s.Bounds);
            foreach (var p in Project.Platforms) b = b.Union(Bounds2.FromPoints(p.Boundary));
        }

        if (b.IsEmpty) b = new Bounds2(0, 0, 100, 100);
        if (b.Width < 1e-6 || b.Height < 1e-6) b = b.Inflate(10);

        _origin = new Point2(Math.Round(b.MinX), Math.Round(b.MinY));
        double w = Math.Max(ActualWidth, 100), h = Math.Max(ActualHeight, 100);
        _scale = Math.Min(w / b.Width, h / b.Height) * 0.92;
        var c = ToLocal(b.Center);
        _panX = w / 2 - c.X * _scale;
        _panY = h / 2 + c.Y * _scale;
        RebuildScene();
    }

    // ================= координати =================
    private Point ToLocal(Point2 p) => new(p.X - _origin.X, p.Y - _origin.Y);

    private Point ToLocal(double x, double y) => new(x - _origin.X, y - _origin.Y);

    private Point ToScreen(Point2 w) => new((w.X - _origin.X) * _scale + _panX, -(w.Y - _origin.Y) * _scale + _panY);

    private Point2 ToWorld(Point s) =>
        new((s.X - _panX) / _scale + _origin.X, (_panY - s.Y) / _scale + _origin.Y);

    // ================= изграждане на геометрията =================
    private void BuildFills(Surface s)
    {
        var pts = s.Vertices;
        var t = s.Triangles;
        int triCount = t.Length / 3;
        var bins = new List<int>[ElevationBins];
        for (int i = 0; i < bins.Length; i++) bins[i] = new List<int>();
        double range = Math.Max(_zMax - _zMin, 1e-9);

        for (int k = 0; k < triCount; k++)
        {
            double v;
            if (ColorMode == ColorMode.Slope)
                v = Math.Min(s.TriangleSlopePercent(k), ColorRamp.MaxSlopePercent) / ColorRamp.MaxSlopePercent;
            else
                v = ((pts[t[3 * k]].Z + pts[t[3 * k + 1]].Z + pts[t[3 * k + 2]].Z) / 3 - _zMin) / range;
            int bin = Math.Clamp((int)(v * ElevationBins), 0, ElevationBins - 1);
            bins[bin].Add(k);
        }

        for (int b = 0; b < ElevationBins; b++)
        {
            if (bins[b].Count == 0) continue;
            double tv = (b + 0.5) / ElevationBins;
            var color = ColorMode == ColorMode.Slope
                ? ColorRamp.Slope(tv * ColorRamp.MaxSlopePercent)
                : ColorRamp.Elevation(tv);
            var g = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var ctx = g.Open())
            {
                foreach (int k in bins[b])
                {
                    var a = pts[t[3 * k]];
                    ctx.BeginFigure(ToLocal(a.X, a.Y), true, true);
                    ctx.PolyLineTo(
                        new[]
                        {
                            ToLocal(pts[t[3 * k + 1]].X, pts[t[3 * k + 1]].Y),
                            ToLocal(pts[t[3 * k + 2]].X, pts[t[3 * k + 2]].Y),
                        },
                        true,
                        false);
                }
            }

            g.Freeze();
            _fills.Add((ColorRamp.Brush(color), g));
        }
    }

    private Geometry BuildEdges(Surface s)
    {
        var pts = s.Vertices;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            foreach (var (a, b) in s.GetEdges())
            {
                ctx.BeginFigure(ToLocal(pts[a].X, pts[a].Y), false, false);
                ctx.LineTo(ToLocal(pts[b].X, pts[b].Y), true, false);
            }
        }

        g.Freeze();
        return g;
    }

    private (Geometry Minor, Geometry Major) BuildContours(Surface s)
    {
        var segs = ContourGenerator.Generate(s, s.EffectiveContourInterval(), s.MajorContourEvery);
        var minor = new StreamGeometry();
        var major = new StreamGeometry();
        using (var cMinor = minor.Open())
        using (var cMajor = major.Open())
        {
            foreach (var seg in segs)
            {
                var ctx = seg.IsMajor ? cMajor : cMinor;
                ctx.BeginFigure(ToLocal(seg.A), false, false);
                ctx.LineTo(ToLocal(seg.B), true, false);
            }
        }

        minor.Freeze();
        major.Freeze();
        return (minor, major);
    }

    private void BuildCutFill(CutFillGrid grid)
    {
        const int binsPerSide = 12;
        _cutFillMaxAbs = 0;
        foreach (var d in grid.MeanDepth)
            if (!double.IsNaN(d)) _cutFillMaxAbs = Math.Max(_cutFillMaxAbs, Math.Abs(d));
        if (_cutFillMaxAbs < 1e-6) return;

        var bins = new List<int>[2 * binsPerSide];
        for (int i = 0; i < bins.Length; i++) bins[i] = new List<int>();
        for (int iy = 0; iy < grid.Ny; iy++)
        {
            for (int ix = 0; ix < grid.Nx; ix++)
            {
                double d = grid.MeanDepth[grid.Index(ix, iy)];
                if (double.IsNaN(d) || Math.Abs(d) < 0.005) continue;
                int b = Math.Clamp((int)(Math.Abs(d) / _cutFillMaxAbs * binsPerSide), 0, binsPerSide - 1);
                bins[(d < 0 ? 0 : binsPerSide) + b].Add(grid.Index(ix, iy));
            }
        }

        for (int i = 0; i < bins.Length; i++)
        {
            if (bins[i].Count == 0) continue;
            bool cut = i < binsPerSide;
            double depth = ((i % binsPerSide) + 0.5) / binsPerSide * _cutFillMaxAbs * (cut ? -1 : 1);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                foreach (int idx in bins[i])
                {
                    var cb = grid.CellBounds(idx % grid.Nx, idx / grid.Nx);
                    ctx.BeginFigure(ToLocal(cb.MinX, cb.MinY), true, true);
                    ctx.PolyLineTo(new[] { ToLocal(cb.MaxX, cb.MinY), ToLocal(cb.MaxX, cb.MaxY), ToLocal(cb.MinX, cb.MaxY) }, false, false);
                }
            }

            g.Freeze();
            _cutFillCells.Add((ColorRamp.Brush(ColorRamp.CutFill(depth, _cutFillMaxAbs)), g));
        }
    }

    // ================= рисуване =================

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        var rect = new Rect(RenderSize);
        drawingContext.DrawRectangle(Background, null, rect);
        drawingContext.PushClip(new RectangleGeometry(rect));

        double px = 1.0 / _scale; // 1 пиксел в локални единици
        drawingContext.PushTransform(new MatrixTransform(_scale, 0, 0, -_scale, _panX, _panY));

        foreach (var (brush, g) in _fills)
            drawingContext.DrawGeometry(brush, new Pen(brush, 0.7 * px), g);
        foreach (var (brush, g) in _cutFillCells)
            drawingContext.DrawGeometry(brush, null, g);

        // видимите повърхнини – теренните отдолу, проектните отгоре
        foreach (var layer in _layers)
        {
            bool d = layer.Design;
            if (layer.Edges != null) drawingContext.DrawGeometry(null, new Pen(d ? DesignEdgeBrush : EdgeBrush, px), layer.Edges);
            if (layer.Minor != null) drawingContext.DrawGeometry(null, new Pen(d ? DesignMinorBrush : MinorContourBrush, (d ? 1.3 : 1) * px), layer.Minor);
            if (layer.Major != null) drawingContext.DrawGeometry(null, new Pen(d ? DesignMajorBrush : MajorContourBrush, (d ? 2.3 : 2) * px), layer.Major);
        }

        if (ActiveSurface is { IsVisible: true } active)
        {
            var blPen = new Pen(BreaklineBrush, 2.2 * px) { LineJoin = PenLineJoin.Round };
            var selPen = new Pen(SelectedBreaklineBrush, 4 * px) { LineJoin = PenLineJoin.Round };
            foreach (var bl in active.Breaklines)
            {
                drawingContext.DrawGeometry(
                    null,
                    ReferenceEquals(bl, SelectedBreakline) ? selPen : blPen,
                    PolygonGeometry(bl.Vertices.Select(v => v.XY).ToList(), false));
            }

            foreach (var b in active.Boundaries)
            {
                var brush = b.Type switch
                {
                    BoundaryType.Hide => HideBoundaryBrush,
                    BoundaryType.Show => ShowBoundaryBrush,
                    _ => OuterBoundaryBrush,
                };
                bool sel = ReferenceEquals(b, SelectedBoundary);
                var pen = new Pen(brush, (sel ? 4 : 2.5) * px)
                {
                    DashStyle = new DashStyle(new[] { 6.0, 3.0 }, 0),
                    LineJoin = PenLineJoin.Round,
                };
                drawingContext.DrawGeometry(null, pen, PolygonGeometry(b.Vertices, true));
            }
        }

        if (Project != null)
        {
            foreach (var p in Project.Platforms)
            {
                if (p.Boundary.Count < 2) continue;
                bool sel = ReferenceEquals(p, SelectedPlatform);
                drawingContext.DrawGeometry(
                    PlatformFill,
                    new Pen(sel ? SelectedPlatformStroke : PlatformStroke, (sel ? 2.5 : 1.5) * px),
                    PolygonGeometry(p.Boundary, true));
            }
        }

        if (SelectionPolygon is { Count: >= 3 })
            drawingContext.DrawGeometry(SelectionFill, new Pen(SelectionStroke, 2 * px) { DashStyle = DashStyles.Dash }, PolygonGeometry(SelectionPolygon, true));

        drawingContext.Pop(); // трансформация

        DrawContourLabels(drawingContext, rect);
        DrawPoints(drawingContext, rect);
        DrawProfileOverlay(drawingContext);
        DrawPlatformLabels(drawingContext);
        DrawDraft(drawingContext);
        DrawSwapPreview(drawingContext);
        DrawDragFeedback(drawingContext);
        DrawLegend(drawingContext, rect);
        DrawScaleBar(drawingContext, rect);

        drawingContext.Pop(); // clip
    }

    private Geometry PolygonGeometry(IReadOnlyList<Point2> pts, bool closed)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(ToLocal(pts[0]), closed, closed);
            ctx.PolyLineTo(pts.Skip(1).Select(ToLocal).ToList(), true, false);
        }

        g.Freeze();
        return g;
    }

    private void DrawPoints(DrawingContext dc, Rect viewport)
    {
        if (!ShowPoints || ActiveSurface == null || !ActiveVisible) return;
        var pts = ActiveSurface.Points;
        var g = new StreamGeometry();
        int drawn = 0;
        using (var ctx = g.Open())
        {
            foreach (var p in pts)
            {
                var s = ToScreen(p.XY);
                if (!viewport.Contains(s)) continue;
                ctx.BeginFigure(new Point(s.X - 1.5, s.Y - 1.5), true, true);
                ctx.PolyLineTo(new[] { new Point(s.X + 1.5, s.Y - 1.5), new Point(s.X + 1.5, s.Y + 1.5), new Point(s.X - 1.5, s.Y + 1.5) }, false, false);
                if (++drawn > 200_000) break;
            }
        }

        g.Freeze();
        dc.DrawGeometry(PointBrush, null, g);

        foreach (var bl in ActiveSurface.Breaklines)
        {
            foreach (var v in bl.Vertices)
            {
                var sv = ToScreen(v.XY);
                if (viewport.Contains(sv)) dc.DrawRectangle(BreaklineBrush, null, new Rect(sv.X - 2.5, sv.Y - 2.5, 5, 5));
            }
        }

        // маркиране на точката под курсора при инструментите за точки
        if (_mouseInside && _tool is ToolMode.MovePoint or ToolMode.DeletePoint && _dragPoint < 0)
        {
            int i = ActiveSurface.FindNearestPoint(_mouseWorld.X, _mouseWorld.Y, PickRadiusPx / _scale);
            if (i >= 0)
            {
                var sp = ToScreen(pts[i].XY);
                dc.DrawEllipse(null, new Pen(HighlightBrush, 2), sp, 6, 6);
                DrawLabel(dc, $"Z = {pts[i].Z:F3}", new Point(sp.X + 9, sp.Y - 18));
            }
        }
    }

    private static readonly Brush ProfileBrush = ColorRamp.Brush(Color.FromRgb(0x00, 0x4E, 0xC2));

    private void DrawProfileOverlay(DrawingContext dc)
    {
        if (SectionLines != null)
        {
            for (int i = 0; i < SectionLines.Count; i++)
            {
                bool cur = i == CurrentSection;
                dc.DrawLine(
                    new Pen(cur ? SelectedBreaklineBrush : ProfileBrush, cur ? 3 : 1) { DashStyle = cur ? null : DashStyles.Dash },
                    ToScreen(SectionLines[i].Left),
                    ToScreen(SectionLines[i].Right));
            }
        }

        if (ProfileLine is { Count: >= 2 })
        {
            var pen = new Pen(ProfileBrush, 2.5) { LineJoin = PenLineJoin.Round };
            for (int i = 1; i < ProfileLine.Count; i++) dc.DrawLine(pen, ToScreen(ProfileLine[i - 1]), ToScreen(ProfileLine[i]));
            var s0 = ToScreen(ProfileLine[0]);
            dc.DrawEllipse(ProfileBrush, null, s0, 5, 5);
            DrawLabel(dc, "0+000", new Point(s0.X + 7, s0.Y - 8));
        }

        if (Marker is { } m)
        {
            var sm = ToScreen(m);
            dc.DrawEllipse(null, new Pen(SelectedBreaklineBrush, 2.5), sm, 7, 7);
            dc.DrawEllipse(SelectedBreaklineBrush, null, sm, 2.5, 2.5);
        }
    }

    private void DrawPlatformLabels(DrawingContext dc)
    {
        if (Project == null) return;
        foreach (var p in Project.Platforms)
        {
            if (p.Boundary.Count < 3) continue;
            Point2 c;
            try { c = p.GetReferencePoint(); } catch (ArgumentException) { continue; }
            var s = ToScreen(c);
            string slope = p.SlopeXPercent != 0 || p.SlopeYPercent != 0
                ? $"\ni = {p.SlopeXPercent:0.##}% / {p.SlopeYPercent:0.##}%"
                : "";
            DrawLabel(dc, $"{p.Name}\n▼ {p.BaseElevation:F2}{slope}", new Point(s.X + 4, s.Y - 10));
            dc.DrawEllipse(PlatformStroke, null, s, 2.5, 2.5);

            if (ReferenceEquals(p, SelectedPlatform) || _tool == ToolMode.EditPlatform)
            {
                foreach (var v in p.Boundary)
                {
                    var sv = ToScreen(v);
                    dc.DrawRectangle(Brushes.White, new Pen(SelectedPlatformStroke, 1.5), new Rect(sv.X - 4, sv.Y - 4, 8, 8));
                }
            }

            // в средата на всяка страна – маркер за добавяне на връх
            if (_tool == ToolMode.EditPlatform && _dragVertex == null)
            {
                for (int i = 0; i < p.Boundary.Count; i++)
                {
                    var sm = ToScreen((p.Boundary[i] + p.Boundary[(i + 1) % p.Boundary.Count]) * 0.5);
                    dc.DrawEllipse(Brushes.White, new Pen(SelectedPlatformStroke, 1.2), sm, 3.5, 3.5);
                }
            }
        }
    }

    private void DrawDraft(DrawingContext dc)
    {
        bool breakline = _tool is ToolMode.DrawBreakline or ToolMode.DrawProfile;
        if (breakline && _mouseInside && Snap(_mouseWorld) is { } snap)
            dc.DrawRectangle(null, new Pen(SnapBrush, 2), new Rect(ToScreen(snap).X - 6, ToScreen(snap).Y - 6, 12, 12));

        if (_draft.Count == 0) return;
        var pen = new Pen(breakline ? BreaklineBrush : DraftStroke, 1.5) { DashStyle = DashStyles.Dash };
        var pts = _draft.Select(ToScreen).ToList();
        if (_mouseInside) pts.Add(ToScreen(breakline ? Snap(_mouseWorld) ?? _mouseWorld : _mouseWorld));
        for (int i = 1; i < pts.Count; i++) dc.DrawLine(pen, pts[i - 1], pts[i]);
        if (!breakline && pts.Count >= 3) dc.DrawLine(new Pen(DraftStroke, 0.8) { DashStyle = DashStyles.Dot }, pts[^1], pts[0]);
        foreach (var p in _draft) dc.DrawEllipse(DraftStroke, null, ToScreen(p), 3, 3);
        DrawLabel(
            dc,
            _tool == ToolMode.DrawProfile ? "Трасе на профила: ляв бутон – връх (прилепва към точки), десен/Enter – край, Esc – отказ" : breakline ? "Ляв бутон – връх (прилепва към точки), щракване върху първия връх – затваряне, десен/Enter – край, Esc – отказ" : "Ляв бутон – връх, десен/Enter – край, Esc – отказ",
            new Point(10, 10));
    }

    /// <summary>Прилепване към точка или връх на структурна линия на активната повърхнина.</summary>
    private Point2? Snap(Point2 world)
    {
        if (ActiveSurface == null) return null;
        double r = PickRadiusPx / _scale;
        Point2? best = null;
        double bestD = r;
        if (_draft.Count >= 3 && Point2.Distance(_draft[0], world) <= bestD) { best = _draft[0]; bestD = Point2.Distance(_draft[0], world); }
        int i = ActiveSurface.FindNearestPoint(world.X, world.Y, r);
        if (i >= 0 && Point2.Distance(ActiveSurface.Points[i].XY, world) < bestD)
        {
            best = ActiveSurface.Points[i].XY;
            bestD = Point2.Distance(best.Value, world);
        }

        foreach (var bl in ActiveSurface.Breaklines)
        {
            foreach (var v in bl.Vertices)
            {
                double d = Point2.Distance(v.XY, world);
                if (d < bestD) { bestD = d; best = v.XY; }
            }
        }

        return best;
    }

    private static readonly Brush SwapOkBrush = ColorRamp.Brush(Color.FromRgb(0xFF, 0x8C, 0x00));
    private static readonly Brush SwapNewBrush = ColorRamp.Brush(Color.FromRgb(0x00, 0xA0, 0x40));

    /// <summary>Подсказка за инструмента „Завърти ребро“: реброто под курсора и новият диагонал.</summary>
    private void DrawSwapPreview(DrawingContext dc)
    {
        if (_tool != ToolMode.SwapEdge || !_mouseInside || ActiveSurface == null) return;
        if (!ActiveSurface.TryFindEdge(_mouseWorld.X, _mouseWorld.Y, double.PositiveInfinity, out int a, out int b)) return;
        var v = ActiveSurface.Vertices;
        if (ActiveSurface.CanSwapEdge(a, b, out int r, out int t, out var reason))
        {
            dc.DrawLine(new Pen(SwapOkBrush, 3), ToScreen(v[a].XY), ToScreen(v[b].XY));
            dc.DrawLine(new Pen(SwapNewBrush, 2.5) { DashStyle = DashStyles.Dash }, ToScreen(v[r].XY), ToScreen(v[t].XY));
            DrawLabel(dc, "Щракнете, за да завъртите реброто", new Point(10, 10));
        }
        else
        {
            dc.DrawLine(new Pen(HighlightBrush, 3), ToScreen(v[a].XY), ToScreen(v[b].XY));
            DrawLabel(dc, reason ?? "Реброто не може да се завърти.", new Point(10, 10));
        }
    }

    private void DrawDragFeedback(DrawingContext dc)
    {
        if (_dragPoint < 0) return; // контурът на платформа се рисува директно при влачене
        var s = ToScreen(_dragPos);
        dc.DrawEllipse(HighlightBrush, null, s, 4, 4);
        if (_dragPoint >= 0 && ActiveSurface != null)
            dc.DrawLine(new Pen(HighlightBrush, 1) { DashStyle = DashStyles.Dash }, ToScreen(ActiveSurface.Points[_dragPoint].XY), s);
    }

    private void DrawLegend(DrawingContext dc, Rect viewport)
    {
        double x = viewport.Right - 70, y = 16, h = 160;
        if (ActiveVisible && ActiveSurface!.TriangleCount > 0 && ColorMode != ColorMode.None)
        {
            int n = 40;
            for (int i = 0; i < n; i++)
            {
                double t = 1 - (i + 0.5) / n;
                var c = ColorMode == ColorMode.Slope ? ColorRamp.Slope(t * ColorRamp.MaxSlopePercent) : ColorRamp.Elevation(t);
                dc.DrawRectangle(ColorRamp.Brush(c), null, new Rect(x, y + i * h / n, 14, h / n + 0.5));
            }

            dc.DrawRectangle(null, new Pen(Brushes.Gray, 1), new Rect(x, y, 14, h));
            if (ColorMode == ColorMode.Slope)
            {
                DrawText(dc, $"≥{ColorRamp.MaxSlopePercent:0}%", new Point(x + 18, y - 6));
                DrawText(dc, "0%", new Point(x + 18, y + h - 8));
            }
            else
            {
                DrawText(dc, $"{_zMax:F1}", new Point(x + 18, y - 6));
                DrawText(dc, $"{_zMin:F1}", new Point(x + 18, y + h - 8));
            }

            y += h + 20;
        }

        // легенда на линиите, когато се виждат няколко повърхнини или проект
        if (_layers.Count > 1 || _layers.Any(l => l.Design))
        {
            var entries = _layers.AsEnumerable().Reverse() // най-горният слой – първи
                .Select(l => (string.Create(
                    CultureInfo.InvariantCulture,
                    $"{l.Name} ({l.Interval:0.###} m){(l.Active ? ", активна" : "")}"), l.Design)).ToList();
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double maxW = entries.Count == 0 ? 0 : entries.Max(e => new FormattedText(e.Item1, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, LabelBrush, dpi).Width);
            double lx = viewport.Right - 12 - maxW - 22;
            dc.DrawRoundedRectangle(LabelBack, null, new Rect(lx - 6, y - 4, maxW + 34, entries.Count * 18 + 6), 3, 3);
            foreach (var (name, design) in entries)
            {
                dc.DrawLine(new Pen(design ? DesignMajorBrush : MajorContourBrush, 2), new Point(lx, y + 7), new Point(lx + 16, y + 7));
                DrawText(dc, name, new Point(lx + 22, y));
                y += 18;
            }

            y += 10;
        }

        if (_cutFillCells.Count > 0)
        {
            dc.DrawRectangle(ColorRamp.Brush(ColorRamp.CutFill(-_cutFillMaxAbs, _cutFillMaxAbs)), null, new Rect(x - 40, y, 14, 14));
            DrawText(dc, "изкоп", new Point(x - 22, y));
            dc.DrawRectangle(ColorRamp.Brush(ColorRamp.CutFill(_cutFillMaxAbs, _cutFillMaxAbs)), null, new Rect(x - 40, y + 18, 14, 14));
            DrawText(dc, "насип", new Point(x - 22, y + 18));
            DrawText(dc, $"макс. |h| = {_cutFillMaxAbs:F2} m", new Point(x - 40, y + 36));
        }
    }

    private void DrawScaleBar(DrawingContext dc, Rect viewport)
    {
        double targetPx = 120;
        double meters = targetPx / _scale;
        double pow = Math.Pow(10, Math.Floor(Math.Log10(meters)));
        double nice = new[] { 1, 2, 5, 10 }.Select(k => k * pow).Last(v => v <= meters);
        double len = nice * _scale;
        var p0 = new Point(16, viewport.Bottom - 20);
        var pen = new Pen(Brushes.Black, 2);
        dc.DrawLine(pen, p0, new Point(p0.X + len, p0.Y));
        dc.DrawLine(pen, new Point(p0.X, p0.Y - 5), new Point(p0.X, p0.Y + 1));
        dc.DrawLine(pen, new Point(p0.X + len, p0.Y - 5), new Point(p0.X + len, p0.Y + 1));
        DrawText(dc, nice >= 1000 ? $"{nice / 1000:0.###} km" : $"{nice:0.###} m", new Point(p0.X + len + 6, p0.Y - 9));
    }

    // ================= надписи на хоризонталите =================
    private const double LabelSpacingPx = 300;
    private const double LabelFontSize = 11;
    private static readonly Brush LabelHalo = ColorRamp.Brush(Color.FromArgb(0xE0, 0xF7, 0xF7, 0xF4));
    private static readonly Brush ExistingLabelBrush = ColorRamp.Brush(Color.FromRgb(0x5A, 0x2E, 0x10));

    /// <param name="Phase">Позиция на първия надпис като дял от разстоянието между надписите.</param>
    private sealed record LabelLine(string Text, Point[] Local, Rect Bounds, Brush Brush, double Phase);

    private readonly List<LabelLine> _labelLines = new();

    /// <summary>Хоризонталите за надписване (свързани полилинии в локални координати).</summary>
    private void BuildLabelLines(Surface s, Brush brush, double phase)
    {
        double interval = s.EffectiveContourInterval();
        var lines = ContourGenerator.GeneratePolylines(s, interval, s.MajorContourEvery);
        bool anyMajor = lines.Any(l => l.IsMajor);
        string fmt = interval >= 1 ? "0.#" : interval >= 0.1 ? "0.0#" : "0.00#";
        foreach (var l in lines)
        {
            if (anyMajor && !l.IsMajor) continue;
            var pts = l.Points.Select(ToLocal).ToArray();
            double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            _labelLines.Add(new LabelLine(
                l.Level.ToString(fmt, CultureInfo.InvariantCulture),
                pts,
                new Rect(minX, minY, maxX - minX, maxY - minY),
                brush,
                phase));
        }
    }

    private void DrawContourLabels(DrawingContext dc, Rect viewport)
    {
        if (_labelLines.Count == 0) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        // видимата област в локални координати
        var a = new Point((viewport.Left - _panX) / _scale, (_panY - viewport.Top) / _scale);
        var b = new Point((viewport.Right - _panX) / _scale, (_panY - viewport.Bottom) / _scale);
        var visible = new Rect(a, b);

        int drawn = 0;
        var placed = new List<Rect>(); // заети места – надписите не се застъпват (активната повърхнина е първа)
        foreach (var line in _labelLines)
        {
            if (!visible.IntersectsWith(line.Bounds)) continue;
            var ft = new FormattedText(line.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, LabelFontSize, line.Brush, dpi);
            double w = ft.Width + 6;

            // полилинията в екранни координати + натрупана дължина
            var sp = new Point[line.Local.Length];
            var acc = new double[line.Local.Length];
            for (int i = 0; i < sp.Length; i++)
            {
                sp[i] = new Point(line.Local[i].X * _scale + _panX, -line.Local[i].Y * _scale + _panY);
                if (i > 0) acc[i] = acc[i - 1] + (sp[i] - sp[i - 1]).Length;
            }

            double total = acc[^1];
            if (total < w * 2) continue;

            // първият надпис – по средата на първия интервал, после през LabelSpacingPx
            double first = Math.Min(LabelSpacingPx * line.Phase, total / 2);
            for (double d = first; d <= total - w / 2; d += LabelSpacingPx)
            {
                // при силно извит участък – опитваме малко по-нататък
                for (double shift = 0; shift < LabelSpacingPx / 2 && d + shift <= total - w / 2; shift += 15)
                {
                    double c = d + shift;
                    var p0 = AtDistance(sp, acc, c - w / 2);
                    var p1 = AtDistance(sp, acc, c + w / 2);
                    if ((p1 - p0).Length < 0.9 * w) continue; // твърде извито
                    var center = AtDistance(sp, acc, c);
                    if (!viewport.Contains(center)) break;

                    double angle = Math.Atan2(p1.Y - p0.Y, p1.X - p0.X) * 180 / Math.PI;
                    if (angle > 90) angle -= 180;   // винаги четимо отляво надясно
                    if (angle < -90) angle += 180;

                    // обхващащ правоъгълник на завъртения надпис – проверка за застъпване
                    double rad = angle * Math.PI / 180, cs = Math.Abs(Math.Cos(rad)), sn = Math.Abs(Math.Sin(rad));
                    double hx = cs * w / 2 + sn * ft.Height / 2, hy = sn * w / 2 + cs * ft.Height / 2;
                    var aabb = new Rect(center.X - hx - 2, center.Y - hy - 2, 2 * hx + 4, 2 * hy + 4);
                    if (placed.Any(r => r.IntersectsWith(aabb))) continue;
                    placed.Add(aabb);

                    dc.PushTransform(new RotateTransform(angle, center.X, center.Y));
                    var box = new Rect(center.X - w / 2, center.Y - ft.Height / 2, w, ft.Height);
                    dc.DrawRoundedRectangle(LabelHalo, null, box, 2, 2);
                    dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
                    dc.Pop();
                    drawn++;
                    break;
                }

                if (drawn > 3000) return; // предпазител при много гъсти хоризонтали
            }
        }
    }

    private static Point AtDistance(Point[] pts, double[] acc, double d)
    {
        d = Math.Clamp(d, 0, acc[^1]);
        int i = Array.BinarySearch(acc, d);
        if (i >= 0) return pts[i];
        i = ~i;
        if (i <= 0) return pts[0];
        if (i >= pts.Length) return pts[^1];
        double seg = acc[i] - acc[i - 1];
        double t = seg > 0 ? (d - acc[i - 1]) / seg : 0;
        return pts[i - 1] + (pts[i] - pts[i - 1]) * t;
    }

    private void DrawText(DrawingContext dc, string text, Point at)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            LabelBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, at);
    }

    private void DrawLabel(DrawingContext dc, string text, Point at)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            LabelBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawRoundedRectangle(LabelBack, null, new Rect(at.X - 3, at.Y - 2, ft.Width + 6, ft.Height + 4), 3, 3);
        dc.DrawText(ft, at);
    }

    // ================= мишка и клавиатура =================

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var pos = e.GetPosition(this);
        var world = ToWorld(pos);

        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && _tool == ToolMode.Pan))
        {
            _panning = true;
            _panStart = pos;
            _panStartX = _panX;
            _panStartY = _panY;
            CaptureMouse();
            return;
        }

        if (e.ChangedButton == MouseButton.Right)
        {
            if (_tool == ToolMode.EditPlatform && _draft.Count == 0 &&
                FindPlatformVertex(world, PickRadiusPx / _scale) is { } del && del.Item1.Boundary.Count > 3)
            {
                var (dp, di) = del;
                PlatformEditStarting?.Invoke(dp);
                dp.Boundary.RemoveAt(di);
                PlatformGeometryChanged?.Invoke(dp, true);
                InvalidateVisual();
                return;
            }

            if (_draft.Count > 0) FinishDraft();
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;
        double pick = PickRadiusPx / _scale;

        switch (_tool)
        {
            case ToolMode.AddPoint:
                AddPointRequested?.Invoke(world);
                break;

            case ToolMode.SwapEdge:
                if (ActiveSurface != null &&
                    ActiveSurface.TryFindEdge(world.X, world.Y, double.PositiveInfinity, out int ea, out int eb))
                    SwapEdgeRequested?.Invoke(ea, eb);
                break;

            case ToolMode.DeletePoint:
                if (ActiveSurface != null)
                {
                    int i = ActiveSurface.FindNearestPoint(world.X, world.Y, pick);
                    if (i >= 0) DeletePointRequested?.Invoke(i);
                }

                break;

            case ToolMode.MovePoint:
                if (ActiveSurface != null)
                {
                    _dragPoint = ActiveSurface.FindNearestPoint(world.X, world.Y, pick);
                    if (_dragPoint >= 0) { _dragPos = world; _dragStartScreen = pos; CaptureMouse(); }
                }

                break;

            case ToolMode.EditPlatform:
                if (FindPlatformVertex(world, pick) is { } vh)
                    _dragVertex = (vh.Item1, vh.Item2, false);
                else if (FindPlatformMidpoint(world, pick) is { } mh)
                    _dragVertex = (mh.Item1, mh.Item2, true);
                if (_dragVertex != null)
                {
                    _platformDragStarted = false;
                    _dragStartScreen = pos;
                    _dragPos = world;
                    CaptureMouse();
                }
                else if (FindPlatformAt(world) is { } pl) PlatformClicked?.Invoke(pl);
                break;

            case ToolMode.DrawPlatform:
            case ToolMode.DrawSelection:
            case ToolMode.DrawBoundary:
                if (e.ClickCount == 2) FinishDraft();
                else _draft.Add(world);
                break;

            case ToolMode.DrawBreakline:
            case ToolMode.DrawProfile:
                if (e.ClickCount == 2) { FinishDraft(); break; }
                var snapped = Snap(world) ?? world;
                if (_tool == ToolMode.DrawBreakline && _draft.Count >= 3 && snapped == _draft[0])
                {
                    _draft.Add(snapped); // затворена линия
                    FinishDraft();
                }
                else if (_draft.Count == 0 || Point2.Distance(_draft[^1], snapped) > 1e-9)
                    _draft.Add(snapped);
                break;

            case ToolMode.Pan:
                break;
        }

        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);
        _mouseWorld = ToWorld(pos);
        _mouseInside = true;
        CursorMoved?.Invoke(_mouseWorld);

        if (_panning)
        {
            _panX = _panStartX + (pos.X - _panStart.X);
            _panY = _panStartY + (pos.Y - _panStart.Y);
        }
        else if (_dragVertex is { } dv && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_platformDragStarted && (pos - _dragStartScreen).Length >= 3)
            {
                _platformDragStarted = true;
                PlatformEditStarting?.Invoke(dv.Platform);
                if (dv.Midpoint)
                {
                    // влачене на маркер в средата на страната → нов връх след началото на страната
                    dv.Platform.Boundary.Insert(dv.Index + 1, _mouseWorld);
                    _dragVertex = dv = (dv.Platform, dv.Index + 1, false);
                }
            }

            if (_platformDragStarted)
            {
                dv.Platform.Boundary[dv.Index] = _mouseWorld;
                PlatformGeometryChanged?.Invoke(dv.Platform, false);
            }
        }
        else if (_dragPoint >= 0)
        {
            _dragPos = _mouseWorld;
        }

        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_panning)
        {
            _panning = false;
            ReleaseMouseCapture();
        }

        if (_dragPoint >= 0)
        {
            int i = _dragPoint;
            _dragPoint = -1;
            ReleaseMouseCapture();
            var up = e.GetPosition(this);
            if ((up - _dragStartScreen).Length < 3)
                PointEditRequested?.Invoke(i); // щракване без влачене – редакция на котата
            else
                MovePointRequested?.Invoke(i, _dragPos);
        }

        if (_dragVertex is { } dv)
        {
            _dragVertex = null;
            ReleaseMouseCapture();
            if (_platformDragStarted) PlatformGeometryChanged?.Invoke(dv.Platform, true);
            else PlatformClicked?.Invoke(dv.Platform); // щракване без влачене – избор на платформата
            _platformDragStarted = false;
        }

        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseInside = false;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var pos = e.GetPosition(this);
        double factor = e.Delta > 0 ? 1.2 : 1 / 1.2;
        double lx = (pos.X - _panX) / _scale;
        double ly = (_panY - pos.Y) / _scale;
        _scale = Math.Clamp(_scale * factor, 1e-4, 1e4);
        _panX = pos.X - lx * _scale;
        _panY = pos.Y + ly * _scale;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_draft.Count == 0) return;
        switch (e.Key)
        {
            case Key.Escape:
                _draft.Clear();
                e.Handled = true;
                break;
            case Key.Enter:
                FinishDraft();
                e.Handled = true;
                break;
            case Key.Back:
                _draft.RemoveAt(_draft.Count - 1);
                e.Handled = true;
                break;
        }

        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.PreviousSize.Width < 1) ZoomExtents();
    }

    private void FinishDraft()
    {
        if (_draft.Count >= (_tool is ToolMode.DrawBreakline or ToolMode.DrawProfile ? 2 : 3))
        {
            var poly = new List<Point2>(_draft);
            _draft.Clear();
            PolygonCompleted?.Invoke(_tool, poly);
        }
        else _draft.Clear();
        InvalidateVisual();
    }

    private (Platform, int)? FindPlatformVertex(Point2 world, double radius)
    {
        if (Project == null) return null;
        (Platform, int)? best = null;
        double bestD = radius;
        foreach (var p in Project.Platforms)
        {
            for (int i = 0; i < p.Boundary.Count; i++)
            {
                double d = Point2.Distance(p.Boundary[i], world);
                if (d <= bestD) { bestD = d; best = (p, i); }
            }
        }

        return best;
    }

    /// <summary>Най-близкият маркер в средата на страна: (платформа, индекс на началото на страната).</summary>
    private (Platform, int)? FindPlatformMidpoint(Point2 world, double radius)
    {
        if (Project == null) return null;
        (Platform, int)? best = null;
        double bestD = radius;
        foreach (var p in Project.Platforms)
        {
            for (int i = 0; i < p.Boundary.Count; i++)
            {
                var m = (p.Boundary[i] + p.Boundary[(i + 1) % p.Boundary.Count]) * 0.5;
                double d = Point2.Distance(m, world);
                if (d <= bestD) { bestD = d; best = (p, i); }
            }
        }

        return best;
    }

    private Platform? FindPlatformAt(Point2 world)
    {
        if (Project == null) return null;
        foreach (var p in Project.Platforms)
        {
            if (p.Boundary.Count < 3) continue;
            if (p.GetPolygon().Contains(world)) return p;
        }

        return null;
    }
}
