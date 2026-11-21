using SurfaceDesigner.Core;
using SurfaceDesigner.Core.Geometry;
using SurfaceDesigner.Core.Grading;
using SurfaceDesigner.Core.IO;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.Core.Tests;

public class BreaklineTests
{
    private static List<Point3> Grid(double size, double step, Func<double, double, double> z, double jitter = 0, int seed = 3)
    {
        var rnd = new Random(seed);
        var pts = new List<Point3>();
        for (double y = 0; y <= size + 1e-9; y += step)
        {
            for (double x = 0; x <= size + 1e-9; x += step)
            {
                double jx = x, jy = y;
                if (jitter > 0 && x > 0 && y > 0 && x < size && y < size)
                {
                    jx += (rnd.NextDouble() - 0.5) * jitter;
                    jy += (rnd.NextDouble() - 0.5) * jitter;
                }

                pts.Add(new Point3(jx, jy, z(jx, jy)));
            }
        }

        return pts;
    }

    private static double Cross(Point2 o, Point2 a, Point2 b) => Point2.Cross(a - o, b - o);

    private static bool ProperlyCross(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        const double e = 1e-9;
        double d1 = Cross(a, b, c), d2 = Cross(a, b, d), d3 = Cross(c, d, a), d4 = Cross(c, d, b);
        return ((d1 > e && d2 < -e) || (d1 < -e && d2 > e)) && ((d3 > e && d4 < -e) || (d3 < -e && d4 > e));
    }

    /// <summary>Нито едно ребро на TIN не пресича отсечка от структурна линия.</summary>
    private static void AssertBreaklinesRespected(Surface s)
    {
        Assert.Equal(0, s.FailedBreaklineSegments);
        var v = s.Vertices;
        var edges = s.GetEdges();
        foreach (var bl in s.Breaklines)
        {
            for (int i = 1; i < bl.Vertices.Count; i++)
            {
                var a = bl.Vertices[i - 1].XY;
                var b = bl.Vertices[i].XY;
                foreach (var (p, q) in edges)
                    Assert.False(
                        ProperlyCross(a, b, v[p].XY, v[q].XY),
                        $"Ребро {v[p]}–{v[q]} пресича структурната линия {a}–{b}");
            }
        }
    }

    private static void AssertValidTriangulation(Surface s, double expectedArea)
    {
        var v = s.Vertices;
        var t = s.Triangles;
        double area = 0;
        for (int k = 0; k < t.Length; k += 3)
        {
            double a = Math.Abs(Cross(v[t[k]].XY, v[t[k + 1]].XY, v[t[k + 2]].XY)) / 2;
            Assert.True(a > 1e-12, "Изроден триъгълник");
            area += a;
        }

        Assert.Equal(expectedArea, area, 1e-6 * expectedArea);
    }

    [Fact]
    public void Cdt_InsertsEdgeCrossingManyTriangles()
    {
        var rnd = new Random(7);
        var pts = Enumerable.Range(0, 600).Select(_ => new Point2(rnd.NextDouble() * 100, rnd.NextDouble() * 100)).ToList();
        pts.Add(new Point2(-1, -1)); pts.Add(new Point2(101, -1)); pts.Add(new Point2(101, 101)); pts.Add(new Point2(-1, 101));
        int a = pts.Count; pts.Add(new Point2(2, 3));
        int b = pts.Count; pts.Add(new Point2(97, 91));

        var tri = Delaunay.Triangulate(pts);
        var r = ConstrainedDelaunay.Apply(pts, tri, new[] { (a, b) });

        Assert.Equal(0, r.FailedConstraints);
        Assert.Equal(tri.Length, r.Triangles.Length);
        bool found = false;
        for (int k = 0; k < r.Triangles.Length; k += 3)
        {
            for (int e = 0; e < 3; e++)
            {
                if (ConstrainedDelaunay.Key(r.Triangles[k + e], r.Triangles[k + (e + 1) % 3]) == ConstrainedDelaunay.Key(a, b))
                    found = true;
            }
        }

        Assert.True(found);

        double area = 0;
        for (int k = 0; k < r.Triangles.Length; k += 3)
            area += Math.Abs(Cross(pts[r.Triangles[k]], pts[r.Triangles[k + 1]], pts[r.Triangles[k + 2]])) / 2;
        Assert.Equal(102.0 * 102.0, area, 1e-6);
    }

    [Fact]
    public void Ridge_ElevationFollowsBreakline()
    {
        var s = new Surface("t", Grid(20, 2, (_, _) => 0, jitter: 0.8));
        var bl = new Breakline("ръб", new[] { new Point3(1, 3, 10), new Point3(11, 8, 6), new Point3(19, 15, 4) });
        s.AddBreakline(bl);

        AssertBreaklinesRespected(s);
        AssertValidTriangulation(s, 400);
        for (double st = 0; st <= bl.Length2D; st += 0.37)
        {
            // точка на разстояние st по линията
            double rem = st;
            Point2 p = default;
            for (int i = 1; i < bl.Vertices.Count; i++)
            {
                var a = bl.Vertices[i - 1].XY; var b = bl.Vertices[i].XY;
                double len = Point2.Distance(a, b);
                if (rem <= len || i == bl.Vertices.Count - 1) { p = a + (b - a) * Math.Min(rem / len, 1); break; }
                rem -= len;
            }

            Assert.Equal(bl.ElevationAtStation(st), s.ElevationAt(p.X, p.Y)!.Value, 6);
        }
    }

    [Fact]
    public void Breakline_ThroughCollinearGridPoints_IsSplit()
    {
        // Диагонал през всички възли (k, k) на мрежата и хоризонтала по ред от възли
        var s = new Surface("t", Grid(10, 1, (x, y) => x + y));
        s.AddBreakline(new Breakline("диагонал", new[] { new Point3(0, 0, 0), new Point3(10, 10, 20) }));
        s.AddBreakline(new Breakline("ред", new[] { new Point3(0, 3.0, 3), new Point3(10, 3.0, 13) }));
        s.AddBreakline(new Breakline("наклонена", new[] { new Point3(0, 0, 0), new Point3(10, 5, 15) })); // през (2,1), (4,2)…

        AssertBreaklinesRespected(s);
        AssertValidTriangulation(s, 100);
        Assert.Equal(7.0, s.ElevationAt(3.5, 3.5)!.Value, 9);
    }

    [Fact]
    public void CrossingBreaklines_GetIntersectionVertex()
    {
        var pts = new List<Point3> { new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0) };
        var rnd = new Random(5);
        for (int i = 0; i < 40; i++) pts.Add(new Point3(rnd.NextDouble() * 10, rnd.NextDouble() * 10, 0));
        var s = new Surface("t", pts);
        s.AddBreakline(new Breakline("A", new[] { new Point3(0.5, 5, 10), new Point3(9.5, 5, 10) }));
        s.AddBreakline(new Breakline("B", new[] { new Point3(5, 0.5, 0), new Point3(5, 9.5, 40) }));

        AssertBreaklinesRespected(s);
        Assert.Equal(10, s.ElevationAt(5, 5)!.Value, 9);   // приоритет на първата линия
        Assert.Equal(10, s.ElevationAt(2, 5)!.Value, 9);
        Assert.Equal(5, s.ElevationAt(5, 2.75)!.Value, 9);  // B: 0 → 10 между y = 0.5 и 5
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomBreaklines_StressTest(int seed)
    {
        var rnd = new Random(seed);
        var pts = Enumerable.Range(0, 1500).Select(_ => new Point3(rnd.NextDouble() * 100, rnd.NextDouble() * 100, rnd.NextDouble())).ToList();
        pts.AddRange(new[] { new Point3(0, 0, 0), new Point3(100, 0, 0), new Point3(100, 100, 0), new Point3(0, 100, 0) });
        var s = new Surface("t", pts);
        for (int l = 0; l < 25; l++)
        {
            var v = Enumerable.Range(0, 2 + rnd.Next(5))
                .Select(_ => new Point3(5 + rnd.NextDouble() * 90, 5 + rnd.NextDouble() * 90, rnd.NextDouble() * 10));
            s.AddBreakline(new Breakline($"L{l}", v));
        }

        AssertBreaklinesRespected(s);
        AssertValidTriangulation(s, 10000);
    }

    [Fact]
    public void BreaklineVertex_OverridesCoincidentPoint()
    {
        var s = new Surface("t", Grid(10, 1, (_, _) => 0));
        s.AddBreakline(new Breakline("b", new[] { new Point3(2, 2, 5), new Point3(8, 8, 5) }));
        Assert.Equal(5, s.ElevationAt(2, 2)!.Value, 9);
        Assert.Equal(5, s.ElevationAt(5, 5)!.Value, 9);
        Assert.Equal(121, s.Points.Count); // точките не се променят
    }

    [Fact]
    public void Flatten_CreatesSharpEdge()
    {
        var s = new Surface("t", Grid(20, 2, (x, _) => x));
        var poly = new Polygon2(new[] { new Point2(5, 5), new Point2(15, 5), new Point2(15, 15), new Point2(5, 15) });
        SurfaceEditor.Flatten(s, poly, 3);
        AssertBreaklinesRespected(s);
        Assert.Equal(3, s.ElevationAt(5.001, 10)!.Value, 9);
        Assert.Equal(3, s.ElevationAt(14.999, 7)!.Value, 9);
        Assert.Equal(4, s.ElevationAt(4, 10)!.Value, 9); // извън ръба – терен
    }

    [Fact]
    public void DeleteInside_SplitsBreaklines()
    {
        var s = new Surface("t", Grid(20, 2, (_, _) => 0));
        s.AddBreakline(new Breakline("b", Enumerable.Range(0, 11).Select(i => new Point3(i * 2, 10, 1))));
        SurfaceEditor.DeleteInside(s, new Polygon2(new[] { new Point2(7, 0), new Point2(13, 0), new Point2(13, 20), new Point2(7, 20) }));
        Assert.Equal(2, s.Breaklines.Count);
        AssertBreaklinesRespected(s);
    }

    [Fact]
    public void DesignSurface_PlatformEdgeIsBreakline()
    {
        var eg = new Surface("eg", Grid(60, 5, (_, _) => 0));
        var p = new Platform
        {
            Boundary = new() { new(17.3, 21.1), new(41.7, 18.2), new(44.4, 39.9), new(28.8, 47.5), new(15.1, 37.7) },
            BaseElevation = 2,
        };
        var design = DesignSurfaceBuilder.Build(eg, new[] { p }, 1.0, "d");
        Assert.Single(design.Breaklines);
        AssertBreaklinesRespected(design);

        // точно на 1 cm вътре от ръба – котата на платформата
        Assert.Equal(2, design.ElevationAt(17.3 + 0.01, 30)!.Value, 6);
    }

    [Fact]
    public void Breaklines_RoundTripAndLandXml()
    {
        var project = new Project();
        var s = new Surface("t", Grid(10, 2, (_, _) => 1));
        s.AddBreakline(new Breakline("Бордюр", new[] { new Point3(1, 1, 2), new Point3(9, 2, 3), new Point3(5, 9, 4) }));
        project.Surfaces.Add(s);

        var copy = ProjectSerializer.FromJson(ProjectSerializer.ToJson(project)).Surfaces[0];
        Assert.Single(copy.Breaklines);
        Assert.Equal("Бордюр", copy.Breaklines[0].Name);
        Assert.Equal(s.Breaklines[0].Vertices, copy.Breaklines[0].Vertices);

        var path = Path.Combine(Path.GetTempPath(), $"sd_{Guid.NewGuid():N}.xml");
        try
        {
            LandXmlWriter.Write(path, new[] { s });
            var doc = System.Xml.Linq.XDocument.Load(path);
            var ns = System.Xml.Linq.XNamespace.Get("http://www.landxml.org/schema/LandXML-1.2");
            Assert.Single(doc.Descendants(ns + "Breakline"));
            Assert.Equal(s.TriangleCount, doc.Descendants(ns + "F").Count());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PolylineParser_SplitsOnBlankAndTextLines()
    {
        var lines = PointFileReader.ParsePolylines(new[]
        {
            "Линия 1",
            "1 0 0 10", "2 5 0 11", "3 9 1 12",
            "END",
            "0 5 1", "5 5 2",
            "",
            "7 7 7",               // единичен връх – пропуска се
        });
        Assert.Equal(2, lines.Count);
        Assert.Equal(3, lines[0].Count);
        Assert.Equal(new Point3(5, 5, 2), lines[1][1]);
    }
}
