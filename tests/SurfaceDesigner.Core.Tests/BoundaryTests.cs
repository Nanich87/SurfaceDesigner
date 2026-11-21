using SurfaceDesigner.Core;
using SurfaceDesigner.Core.Geometry;
using SurfaceDesigner.Core.Grading;
using SurfaceDesigner.Core.IO;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.Core.Tests;

public class BoundaryTests
{
    private static readonly Point2[] Concave =
    {
        new(30, 20), new(170, 35), new(150, 110), new(95, 80), new(120, 175), new(25, 160),
    };

    private static Polygon2 Poly(params Point2[] v) => new(v);

    /// <summary>Вътре в границата котата е същата като на неизрязаната повърхнина, а извън – няма повърхнина.</summary>
    private static void AssertNonDestructive(Surface original, Surface clipped, Func<double, double, bool> expectedVisible, int seed = 11)
    {
        var rnd = new Random(seed);
        var b = original.Bounds.Inflate(20);
        int inside = 0;
        for (int i = 0; i < 4000; i++)
        {
            double x = b.MinX + rnd.NextDouble() * b.Width, y = b.MinY + rnd.NextDouble() * b.Height;
            var zo = original.ElevationAt(x, y);
            var zc = clipped.ElevationAt(x, y);
            if (expectedVisible(x, y) && zo.HasValue)
            {
                Assert.True(zc.HasValue, $"Очаква се повърхнина в ({x}; {y})");
                Assert.Equal(zo.Value, zc!.Value, 9);
                inside++;
            }
            else if (!expectedVisible(x, y))
                Assert.Null(zc);
        }
        Assert.True(inside > 20, $"Твърде малко проби вътре: {inside}");
    }

    private static bool NearBoundary(Polygon2 p, double x, double y) => p.NearestOnBoundary(x, y).Distance < 1e-6;

    [Fact]
    public void OuterBoundary_ClipsWithoutChangingInterpolation()
    {
        var original = SampleTerrain.Create();
        var clipped = original.Clone();
        clipped.AddBoundary(new SurfaceBoundary("Граница", BoundaryType.Outer, Concave));
        var poly = Poly(Concave);

        AssertNonDestructive(original, clipped, (x, y) => poly.Contains(x, y) && !NearBoundary(poly, x, y));
        Assert.Equal(poly.Area, clipped.GetStatistics().Area2D, 1e-6 * poly.Area);
    }

    [Fact]
    public void HideAndShow_AreAppliedInOrder()
    {
        var original = SampleTerrain.Create();
        var hide = Poly(new(40, 40), new(160, 40), new(160, 160), new(40, 160));
        var show = Poly(new(80, 80), new(120, 85), new(110, 120));
        var s = original.Clone();
        s.AddBoundary(new SurfaceBoundary("Сграда", BoundaryType.Hide, hide.Vertices));
        s.AddBoundary(new SurfaceBoundary("Двор", BoundaryType.Show, show.Vertices));

        AssertNonDestructive(original, s, (x, y) =>
            (!hide.Contains(x, y) || show.Contains(x, y)) && !NearBoundary(hide, x, y) && !NearBoundary(show, x, y)
            && original.ElevationAt(x, y).HasValue);
        Assert.Equal(200 * 200 - hide.Area + show.Area, s.GetStatistics().Area2D, 1e-3);
    }

    [Fact]
    public void Boundary_PartlyOutsideData_ClipsToIntersection()
    {
        var original = SampleTerrain.Create(); // 0..200 × 0..200
        var s = original.Clone();
        var poly = Poly(new(-50, -50), new(100, -50), new(100, 250), new(-50, 250));
        s.AddBoundary(new SurfaceBoundary("B", BoundaryType.Outer, poly.Vertices));
        Assert.Equal(100 * 200, s.GetStatistics().Area2D, 1e-6);
        AssertNonDestructive(original, s, (x, y) => poly.Contains(x, y) && !NearBoundary(poly, x, y));
    }

    [Fact]
    public void Boundary_AlongGridEdgesAndThroughVertices()
    {
        var pts = new List<Point3>();
        for (int y = 0; y <= 10; y++)
            for (int x = 0; x <= 10; x++)
                pts.Add(new Point3(x, y, Math.Sin(x) + Math.Cos(y)));
        var original = new Surface("g", pts);
        var s = original.Clone();
        // по линии на мрежата и по диагонали през възли
        var poly = Poly(new(2, 2), new(8, 2), new(8, 5), new(5, 8), new(2, 8));
        s.AddBoundary(new SurfaceBoundary("B", BoundaryType.Outer, poly.Vertices));
        Assert.Equal(poly.Area, s.GetStatistics().Area2D, 1e-9);
        AssertNonDestructive(original, s, (x, y) => poly.Contains(x, y) && !NearBoundary(poly, x, y));
    }

    [Fact]
    public void Boundary_CrossingBreakline_KeepsBreakline()
    {
        var original = SampleTerrain.Create();
        original.AddBreakline(new Breakline("ръб", new[] { new Point3(10, 100, 120), new Point3(190, 110, 125) }));
        var s = original.Clone();
        s.AddBoundary(new SurfaceBoundary("B", BoundaryType.Outer, Concave));
        var poly = Poly(Concave);

        Assert.Equal(0, s.FailedBreaklineSegments);
        AssertNonDestructive(original, s, (x, y) => poly.Contains(x, y) && !NearBoundary(poly, x, y));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RandomStarPolygons_StressTest(int seed)
    {
        var rnd = new Random(seed);
        var original = SampleTerrain.Create(pointCount: 2500, seed: seed);
        original.MaxEdgeLength = 12; // неизпъкнал контур на данните
        var s = original.Clone();
        var c = new Point2(60 + rnd.NextDouble() * 80, 60 + rnd.NextDouble() * 80);
        int n = 5 + rnd.Next(20);
        var star = Enumerable.Range(0, n).Select(i =>
        {
            double a = 2 * Math.PI * i / n, r = 20 + rnd.NextDouble() * 100;
            return new Point2(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }).ToArray();
        var type = seed % 2 == 0 ? BoundaryType.Outer : BoundaryType.Hide;
        s.AddBoundary(new SurfaceBoundary("star", type, star));
        var poly = Poly(star);

        AssertNonDestructive(original, s, (x, y) =>
            (type == BoundaryType.Outer ? poly.Contains(x, y) : !poly.Contains(x, y)) && !NearBoundary(poly, x, y), seed);

        // площта = площта на сечението (по Монте Карло върху оригинала – тук проверяваме сумата)
        double inside = s.GetStatistics().Area2D;
        var sOther = original.Clone();
        sOther.AddBoundary(new SurfaceBoundary("star", type == BoundaryType.Outer ? BoundaryType.Hide : BoundaryType.Outer, star));
        Assert.Equal(original.GetStatistics().Area2D, inside + sOther.GetStatistics().Area2D, 1e-6 * original.GetStatistics().Area2D);
    }

    [Fact]
    public void Boundary_AlongHullOfPointsOnBoundary_DoesNotRunAway()
    {
        // Регресия: точките лежат точно по границата, така че контурът минава по външните ребра на TIN
        var eg = SampleTerrain.Create();
        eg.AddBoundary(new SurfaceBoundary("B", BoundaryType.Outer, Concave));
        var copy = new Surface("copy", eg.Vertices.Where(v => eg.ElevationAt(v.X, v.Y).HasValue));
        copy.AddBoundary(new SurfaceBoundary("B", BoundaryType.Outer, Concave));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        copy.EnsureTriangulated();
        Assert.True(sw.ElapsedMilliseconds < 5000);
        Assert.True(copy.Vertices.Count < copy.Points.Count * 1.2, $"Твърде много върхове: {copy.Vertices.Count}");
        Assert.Equal(new Polygon2(Concave).Area, copy.GetStatistics().Area2D, 1e-6 * new Polygon2(Concave).Area);
    }

    [Fact]
    public void Volumes_RespectHideBoundary()
    {
        var pts = new List<Point3>();
        for (double y = -60; y <= 60; y += 5)
            for (double x = -60; x <= 60; x += 5)
                pts.Add(new Point3(x, y, 0));
        var eg = new Surface("eg", pts);
        eg.AddBoundary(new SurfaceBoundary("дупка", BoundaryType.Hide, new Point2[] { new(-2, -10), new(2, -10), new(2, 10), new(-2, 10) }));
        var p = new Platform { Boundary = new() { new(-5, -5), new(5, -5), new(5, 5), new(-5, 5) }, BaseElevation = 1, FillSlopeRatio = 1.5 };

        var design = DesignSurfaceBuilder.Build(eg, new[] { p }, 1.0, "d");
        Assert.Single(design.Boundaries, b => b.Type == BoundaryType.Hide); // скриващата граница на терена се пренася
        Assert.Null(eg.ElevationAt(0, 8));
        Assert.Null(design.ElevationAt(0, 8));
        var v = VolumeCalculator.Compute(eg, design, 0.25);
        double hole = 4 * 10;
        Assert.InRange(v.Fill, 100 - hole * 0.98 + 25, 100 - hole + 40); // без дупката в насипа
    }

    [Fact]
    public void Boundaries_RoundTripAndLandXml()
    {
        var project = new Project();
        var s = SampleTerrain.Create(pointCount: 200);
        s.AddBoundary(new SurfaceBoundary("Обхват", BoundaryType.Outer, Concave));
        s.AddBoundary(new SurfaceBoundary("Сграда", BoundaryType.Hide, new Point2[] { new(50, 50), new(70, 50), new(70, 70) }));
        project.Surfaces.Add(s);

        var copy = ProjectSerializer.FromJson(ProjectSerializer.ToJson(project)).Surfaces[0];
        Assert.Equal(2, copy.Boundaries.Count);
        Assert.Equal(BoundaryType.Hide, copy.Boundaries[1].Type);
        Assert.Equal(s.Boundaries[0].Vertices, copy.Boundaries[0].Vertices);
        Assert.Equal(s.TriangleCount, copy.TriangleCount);

        var path = Path.Combine(Path.GetTempPath(), $"sd_{Guid.NewGuid():N}.xml");
        try
        {
            LandXmlWriter.Write(path, new[] { s });
            var doc = System.Xml.Linq.XDocument.Load(path);
            var ns = System.Xml.Linq.XNamespace.Get("http://www.landxml.org/schema/LandXML-1.2");
            Assert.Equal(2, doc.Descendants(ns + "Boundary").Count());
            Assert.Equal(s.TriangleCount, doc.Descendants(ns + "F").Count());
        }
        finally { File.Delete(path); }
    }
}
