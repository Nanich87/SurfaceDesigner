using SurfaceDesigner.Core.Geometry;
using SurfaceDesigner.Core.Grading;
using SurfaceDesigner.Core.IO;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.Core.Tests;

public class DataHullTests
{
    private static List<Point2> LShapedCloud(int seed = 1)
    {
        // L-образна площ: 100×100 без горния десен квадрат 50×50 (площ 7500 m²), гъста мрежа с шум
        var rnd = new Random(seed);
        var pts = new List<Point2>();
        for (double y = 0; y <= 100; y += 2.5)
            for (double x = 0; x <= 100; x += 2.5)
            {
                if (x > 50 && y > 50) continue;
                bool edge = x == 0 || y == 0 || x == 100 || y == 100 || x == 50 || y == 50;
                double jx = edge ? 0 : (rnd.NextDouble() - 0.5);
                double jy = edge ? 0 : (rnd.NextDouble() - 0.5);
                pts.Add(new Point2(x + jx, y + jy));
            }
        return pts;
    }

    [Fact]
    public void ConcaveHull_FollowsLShape()
    {
        var pts = LShapedCloud();
        var hull = DataHull.Compute(pts, DataHull.SuggestMaxEdge(pts));
        var poly = new Polygon2(hull);
        Assert.Equal(7500, poly.Area, 7500 * 0.01);
        Assert.False(poly.Contains(75, 75));
        Assert.True(poly.Contains(25, 75) && poly.Contains(75, 25));
        Assert.All(pts, p => Assert.True(poly.Contains(p) || poly.NearestOnBoundary(p.X, p.Y).Distance < 1e-6));
    }

    [Fact]
    public void InfiniteThreshold_GivesConvexHull()
    {
        var pts = LShapedCloud();
        var hull = DataHull.Compute(pts, 0);
        Assert.Equal(10000 - 50 * 50 / 2.0, new Polygon2(hull).Area, 1); // изпъкнала обвивка на L
    }

    [Fact]
    public void HullAsOuterBoundary_RemovesLongEdgeTriangles()
    {
        var pts = LShapedCloud();
        var s = new Surface("L", pts.Select(p => new Point3(p.X, p.Y, p.X * 0.1)));
        Assert.True(s.GetStatistics().Area2D > 8700); // изпъкнала обвивка – „мост“ през празния квадрат
        s.AddBoundary(new SurfaceBoundary("Контур", BoundaryType.Outer, DataHull.Compute(pts, DataHull.SuggestMaxEdge(pts))));
        Assert.Equal(7500, s.GetStatistics().Area2D, 75.0);
        Assert.Null(s.ElevationAt(75, 75));
    }
}

public class ContourPolylineTests
{
    [Fact]
    public void Plane_GivesOneOpenLinePerLevel()
    {
        var pts = new List<Point3>();
        var rnd = new Random(3);
        for (int i = 0; i < 400; i++) { double x = rnd.NextDouble() * 50, y = rnd.NextDouble() * 50; pts.Add(new Point3(x, y, x / 5)); }
        pts.AddRange(new[] { new Point3(0, 0, 0), new Point3(50, 0, 10), new Point3(50, 50, 10), new Point3(0, 50, 0) });
        var s = new Surface("p", pts);
        var lines = ContourGenerator.GeneratePolylines(s, 1);
        // нива 1..10: ъглите при x = 50 са точно на кота 10, така че хоризонталата 10 минава по ръба;
        // ниво 0 няма – върховете на нивото се смятат за „над“ него
        Assert.Equal(10, lines.Count);
        Assert.All(lines, l =>
        {
            Assert.False(l.IsClosed);
            Assert.All(l.Points, p => Assert.Equal(l.Level * 5, p.X, 6));
            Assert.Equal(50, Math.Abs(l.Points[0].Y - l.Points[^1].Y), 6);
        });
    }

    [Fact]
    public void Cone_GivesClosedLines()
    {
        var pts = new List<Point3>();
        for (int r = 0; r <= 20; r++)
            for (int k = 0; k < Math.Max(1, r * 6); k++)
            {
                double a = 2 * Math.PI * k / Math.Max(1, r * 6);
                pts.Add(new Point3(r * Math.Cos(a), r * Math.Sin(a), 20 - r));
            }
        var s = new Surface("cone", pts);
        var lines = ContourGenerator.GeneratePolylines(s, 2.5, 2);
        Assert.NotEmpty(lines);
        Assert.All(lines.Where(l => l.Level < 19.5 && l.Level > 0.5), l => Assert.True(l.IsClosed, $"Ниво {l.Level} не е затворено"));
        Assert.Contains(lines, l => l.IsMajor);
    }
}

public class ProfileTests
{
    private static Surface Plane(Func<double, double, double> z)
    {
        var pts = new List<Point3>();
        for (double y = 0; y <= 100; y += 10)
            for (double x = 0; x <= 100; x += 10)
                pts.Add(new Point3(x, y, z(x, y)));
        return new Surface("p", pts);
    }

    [Fact]
    public void Profile_IsExactAndHasEdgeCrossings()
    {
        var s = Plane((x, y) => 0.1 * x + 0.02 * y + 50);
        var line = new List<Point2> { new(5, 5), new(95, 47), new(60, 90) };
        var prof = ProfileSampler.Sample(s, line);
        Assert.True(prof.Count > 20);
        Assert.Equal(0, prof[0].Station);
        Assert.Equal(Alignment.Length(line), prof[^1].Station, 9);
        Assert.All(prof, p => Assert.Equal(0.1 * p.X + 0.02 * p.Y + 50, p.Z!.Value, 9));
        Assert.True(prof.Zip(prof.Skip(1)).All(z => z.Second.Station > z.First.Station));
    }

    [Fact]
    public void Profile_MarksGapsInHiddenAreas()
    {
        var s = Plane((x, _) => x);
        s.AddBoundary(new SurfaceBoundary("дупка", BoundaryType.Hide, new Point2[] { new(40, 0), new(60, 0), new(60, 100), new(40, 100) }));
        var prof = ProfileSampler.Sample(s, new[] { new Point2(0, 50), new Point2(100, 50) });
        Assert.Contains(prof, p => p.Z is null && p.Station > 40 && p.Station < 60);
        Assert.Null(ProfileSampler.ElevationAtStation(prof, 50));
        Assert.Equal(30, ProfileSampler.ElevationAtStation(prof, 30)!.Value, 9);
    }

    [Fact]
    public void CrossSections_AreaAndAverageEndVolume()
    {
        var eg = Plane((_, _) => 0);
        var platform = new Platform
        {
            Boundary = new() { new(30, 20), new(70, 20), new(70, 80), new(30, 80) },
            BaseElevation = 1,
            FillSlopeRatio = 2,
        };
        var design = DesignSurfaceBuilder.Build(eg, new[] { platform }, 1.0, "d");

        var axis = new List<Point2> { new(50, 30), new(50, 70) };
        var sections = Alignment.CrossSections(axis, 10, 30, 30);
        Assert.Equal(5, sections.Count);

        foreach (var cs in sections)
        {
            var line = new[] { cs.Left, cs.Right };
            var a = ProfileSampler.Areas(ProfileSampler.Sample(eg, line), ProfileSampler.Sample(design, line));
            // платформа 40 m × 1 m + два откоса 1:2 (½·2·1 всеки) = 42 m²
            Assert.Equal(42, a.Fill, 1e-6);
            Assert.Equal(0, a.Cut, 1e-9);
        }
    }

    [Fact]
    public void Areas_HandleCrossingLines()
    {
        // съществуваща: z = 0; проектна: z = x/10 − 1 по x ∈ [0, 20] → изкоп 5 (0..10), насип 5 (10..20)
        var eg = new List<ProfilePoint> { new(0, 0, 0, 0), new(20, 20, 0, 0) };
        var d = new List<ProfilePoint> { new(0, 0, 0, -1), new(20, 20, 0, 1) };
        var a = ProfileSampler.Areas(eg, d);
        Assert.Equal(5, a.Cut, 9);
        Assert.Equal(5, a.Fill, 9);
    }

    [Fact]
    public void StationFormat()
    {
        Assert.Equal("0+000.00", Alignment.FormatStation(0));
        Assert.Equal("1+234.50", Alignment.FormatStation(1234.5));
    }
}

public class DxfTests
{
    [Fact]
    public void Contours_DxfIsWellFormed()
    {
        var s = SampleTerrain.Create(pointCount: 600);
        s.AddBoundary(new SurfaceBoundary("B", BoundaryType.Outer, new Point2[] { new(10, 10), new(190, 15), new(180, 190), new(15, 180) }));
        var path = Path.Combine(Path.GetTempPath(), $"sd_{Guid.NewGuid():N}.dxf");
        try
        {
            int n = DxfWriter.WriteContours(path, s, 0.5, 5, 1.0, 50);
            var lines = File.ReadAllLines(path);
            Assert.True(lines.Length % 2 == 0, "Двойки код/стойност");
            Assert.Equal("EOF", lines[^1]);
            var pairs = Enumerable.Range(0, lines.Length / 2).Select(i => (lines[2 * i].Trim(), lines[2 * i + 1])).ToList();
            Assert.Equal(n + 1, pairs.Count(p => p.Item1 == "0" && p.Item2 == "POLYLINE")); // + границата
            Assert.Equal(pairs.Count(p => p.Item1 == "0" && p.Item2 == "POLYLINE"), pairs.Count(p => p.Item1 == "0" && p.Item2 == "SEQEND"));
            Assert.Contains(pairs, p => p.Item1 == "0" && p.Item2 == "TEXT");
            Assert.Contains(pairs, p => p.Item1 == "8" && p.Item2 == DxfWriter.LayerMajor);
            // всички числови кодове на координати се четат с точка
            Assert.All(pairs.Where(p => p.Item1 is "10" or "20" or "30"), p =>
                Assert.True(double.TryParse(p.Item2, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Tin_DxfHasOneFacePerTriangle()
    {
        var s = SampleTerrain.Create(pointCount: 200);
        s.AddBreakline(new Breakline("b", new[] { new Point3(10, 10, 101), new Point3(150, 120, 104) }));
        var path = Path.Combine(Path.GetTempPath(), $"sd_{Guid.NewGuid():N}.dxf");
        try
        {
            DxfWriter.WriteTin(path, s);
            var text = File.ReadAllText(path);
            Assert.Equal(s.TriangleCount, CountEntity(text, "3DFACE"));
            Assert.Equal(s.Points.Count, CountEntity(text, "POINT"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DesignSurface_IsRed_WithTriangles()
    {
        var eg = SampleTerrain.Create(pointCount: 300);
        var p = new Platform { Boundary = new() { new(60, 60), new(140, 60), new(140, 140), new(60, 140) }, BaseElevation = 105 };
        var design = DesignSurfaceBuilder.Build(eg, new[] { p }, 2.0, "Проект");
        Assert.True(design.IsDesign);
        Assert.False(eg.IsDesign);
        Assert.True(design.Clone().IsDesign);

        var project = new SurfaceDesigner.Core.Project();
        project.Surfaces.Add(eg);
        project.Surfaces.Add(design);
        var copy = ProjectSerializer.FromJson(ProjectSerializer.ToJson(project));
        Assert.False(copy.Surfaces[0].IsDesign);
        Assert.True(copy.Surfaces[1].IsDesign);

        var path = Path.Combine(Path.GetTempPath(), $"sd_{Guid.NewGuid():N}.dxf");
        try
        {
            DxfWriter.WriteContours(path, design, 0.5, 5, 1.0, 50, includeTriangles: true);
            var l = File.ReadAllLines(path);
            var pairs = Enumerable.Range(0, l.Length / 2).Select(i => (l[2 * i].Trim(), l[2 * i + 1])).ToList();
            Assert.Equal(design.GetEdges().Count, pairs.Count(x => x.Item1 == "0" && x.Item2 == "LINE"));
            // всички слоеве на хоризонталите и TIN са проектни и червени (ACI 1)
            Assert.Contains(pairs, x => x.Item1 == "8" && x.Item2 == DxfWriter.DesignPrefix + DxfWriter.LayerMajor);
            Assert.Contains(pairs, x => x.Item1 == "8" && x.Item2 == DxfWriter.DesignPrefix + DxfWriter.LayerTin);
            Assert.DoesNotContain(pairs, x => x.Item1 == "8" && x.Item2 == DxfWriter.LayerMinor);
            for (int i = 0; i < pairs.Count - 3; i++)
                if (pairs[i] == ("0", "LAYER") && pairs[i + 1].Item2.StartsWith(DxfWriter.DesignPrefix))
                    Assert.Equal(("62", "1"), pairs[i + 3]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Visibility_RoundTripsAndDefaultsToVisible()
    {
        var project = new SurfaceDesigner.Core.Project();
        var a = SampleTerrain.Create("A", pointCount: 50);
        var b = SampleTerrain.Create("B", pointCount: 50);
        b.IsVisible = false;
        project.Surfaces.Add(a);
        project.Surfaces.Add(b);

        var json = ProjectSerializer.ToJson(project);
        var copy = ProjectSerializer.FromJson(json);
        Assert.True(copy.Surfaces[0].IsVisible);
        Assert.False(copy.Surfaces[1].IsVisible);
        Assert.False(b.Clone().IsVisible);

        // проект от по-стара версия – без поле за видимост
        var old = ProjectSerializer.FromJson(json.Replace("\"IsVisible\":false,", "").Replace("\"IsVisible\":null,", ""));
        Assert.All(old.Surfaces, s => Assert.True(s.IsVisible));
    }

    [Fact]
    public void LinkedDesign_RebuildsWithNewSlopes()
    {
        var eg = SampleTerrain.Create("Терен", pointCount: 400);
        var p = new Platform { Boundary = new() { new(60, 60), new(140, 60), new(140, 140), new(60, 140) }, BaseElevation = 105 };
        var platforms = new List<Platform> { p };
        var design = DesignSurfaceBuilder.Build(eg, platforms, 2.0, "Проект");

        Assert.NotNull(design.GradingSource);
        Assert.Equal("Терен", design.GradingSource!.ExistingSurfaceName);
        Assert.Equal(105, design.ElevationAt(120, 100)!.Value, 6);

        // промяна на наклона → различен отпечатък на входа → преизчисляването дава новата равнина
        string key0 = design.GradingSource.InputKey(eg, platforms);
        p.SlopeXPercent = 2;
        Assert.NotEqual(key0, design.GradingSource.InputKey(eg, platforms));
        int v0 = design.Version;
        design.ReplaceContentFrom(DesignSurfaceBuilder.Rebuild(design, eg, platforms)!);
        Assert.True(design.Version > v0);
        var c = p.GetReferencePoint();
        Assert.Equal(105 + 0.02 * (120 - c.X), design.ElevationAt(120, 100)!.Value, 6);

        // промяна на терена също променя отпечатъка
        string key1 = design.GradingSource.InputKey(eg, platforms);
        eg.AddPoint(new Point3(1, 1, 100));
        Assert.NotEqual(key1, design.GradingSource.InputKey(eg, platforms));

        // връзката се записва в проекта
        var project = new SurfaceDesigner.Core.Project();
        project.Surfaces.Add(eg);
        project.Surfaces.Add(design);
        var copy = ProjectSerializer.FromJson(ProjectSerializer.ToJson(project));
        Assert.Null(copy.Surfaces[0].GradingSource);
        Assert.Equal("Терен", copy.Surfaces[1].GradingSource!.ExistingSurfaceName);
        Assert.Equal(2.0, copy.Surfaces[1].GradingSource!.GridStep);
        Assert.True(copy.Surfaces[1].GradingSource!.ZoneOnly);
    }

    [Fact]
    public void ContourSettings_ArePerSurface()
    {
        var eg = SampleTerrain.Create("Терен", pointCount: 300);
        eg.ContourInterval = 1;
        eg.MajorContourEvery = 4;
        var other = SampleTerrain.Create("Друга", pointCount: 100);
        Assert.Equal(0.5, other.ContourInterval); // по подразбиране
        Assert.Equal(5, other.MajorContourEvery);

        // наследяване: копие, пресемплиране, проектна повърхнина
        Assert.Equal(1, eg.Clone().ContourInterval);
        Assert.Equal(4, SurfaceEditor.ResampleToGrid(eg, 10, "мрежа").MajorContourEvery);
        var p = new Platform { Boundary = new() { new(60, 60), new(140, 60), new(140, 140), new(60, 140) }, BaseElevation = 105 };
        var design = DesignSurfaceBuilder.Build(eg, new[] { p }, 2.0, "Проект");
        Assert.Equal(1, design.ContourInterval);
        Assert.Equal(4, design.MajorContourEvery);
        // преизчисляването не променя настройките на проекта
        design.ContourInterval = 0.25;
        design.ReplaceContentFrom(DesignSurfaceBuilder.Rebuild(design, eg, new[] { p })!);
        Assert.Equal(0.25, design.ContourInterval);

        // запис по повърхнина
        var project = new SurfaceDesigner.Core.Project();
        project.Surfaces.Add(eg);
        project.Surfaces.Add(design);
        var copy = ProjectSerializer.FromJson(ProjectSerializer.ToJson(project));
        Assert.Equal((1.0, 4), (copy.Surfaces[0].ContourInterval, copy.Surfaces[0].MajorContourEvery));
        Assert.Equal((0.25, 4), (copy.Surfaces[1].ContourInterval, copy.Surfaces[1].MajorContourEvery));

        // по-стар проект с обща стъпка → всяка повърхнина я получава
        var old = ProjectSerializer.FromJson(
            "{\"Version\":1,\"ContourInterval\":2,\"MajorContourEvery\":10,\"Surfaces\":[{\"Name\":\"A\",\"Points\":[[0,0,0],[1,0,0],[0,1,1]]}],\"Platforms\":[]}");
        Assert.Equal((2.0, 10), (old.Surfaces[0].ContourInterval, old.Surfaces[0].MajorContourEvery));
        // още по-стар – без никакви настройки
        var older = ProjectSerializer.FromJson("{\"Version\":1,\"Surfaces\":[{\"Name\":\"A\",\"Points\":[[0,0,0],[1,0,0],[0,1,1]]}],\"Platforms\":[]}");
        Assert.Equal((0.5, 5), (older.Surfaces[0].ContourInterval, older.Surfaces[0].MajorContourEvery));
    }

    [Fact]
    public void ViewSettings_RoundTrip()
    {
        var project = new SurfaceDesigner.Core.Project
        {
            View = new SurfaceDesigner.Core.ViewSettings { ShowTriangles = false, ShowContourLabels = false, ColorMode = 2 },
        };
        var copy = ProjectSerializer.FromJson(ProjectSerializer.ToJson(project));
        Assert.Equal(project.View, copy.View); // сравнение по стойност
        Assert.False(copy.View.ShowTriangles);
        Assert.Equal(2, copy.View.ColorMode);

        // по-стар проект без настройки на изгледа → по подразбиране
        var old = ProjectSerializer.FromJson("{\"Version\":1,\"Surfaces\":[],\"Platforms\":[]}");
        Assert.Equal(new SurfaceDesigner.Core.ViewSettings(), old.View);
    }

    [Fact]
    public void EffectiveInterval_LimitsDensity()
    {
        var s = SampleTerrain.Create(pointCount: 300); // денивелация ≈ 9.5 m
        s.ContourInterval = 0.5;
        Assert.Equal(0.5, s.EffectiveContourInterval());
        s.ContourInterval = 0.001;
        Assert.Equal(0.02, s.EffectiveContourInterval(), 9); // 9.5 / 500 = 0.019 → 0.02
        Assert.Equal(0.25, ContourGenerator.NiceStepAtLeast(0.21), 9);
        Assert.Equal(1, ContourGenerator.NiceStepAtLeast(1), 9);
    }

    [Fact]
    public void MajorEvery_ControlsMajorContours()
    {
        var s = SampleTerrain.Create(pointCount: 300);
        var segs = ContourGenerator.Generate(s, 0.5, 4);
        Assert.All(segs, c => Assert.Equal(Math.Abs(Math.IEEERemainder(c.Level, 2.0)) < 1e-9, c.IsMajor));
    }

    private static int CountEntity(string dxf, string name)
    {
        var l = dxf.Split('\n');
        int n = 0;
        for (int i = 0; i + 1 < l.Length; i += 2)
            if (l[i].Trim() == "0" && l[i + 1].Trim() == name) n++;
        return n;
    }
}
