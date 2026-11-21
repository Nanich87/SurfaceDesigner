using SurfaceDesigner.Core;
using SurfaceDesigner.Core.Geometry;
using SurfaceDesigner.Core.Grading;
using SurfaceDesigner.Core.IO;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.Core.Tests;

public class TriangulationTests
{
    [Fact]
    public void RegularGrid_ProducesExpectedTriangleCount()
    {
        var pts = new List<Point2>();
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 10; x++)
                pts.Add(new Point2(x, y));
        var tris = Delaunay.Triangulate(pts);
        Assert.Equal(2 * 9 * 9, tris.Length / 3);
    }

    [Fact]
    public void RandomPoints_SatisfyEulerAndDelaunay()
    {
        var rnd = new Random(1);
        var pts = Enumerable.Range(0, 2000).Select(_ => new Point2(rnd.NextDouble() * 1000, rnd.NextDouble() * 1000)).ToList();
        var tris = Delaunay.Triangulate(pts);

        // сумата от площите на триъгълниците = площта на изпъкналата обвивка (без застъпване)
        double area = 0;
        for (int k = 0; k < tris.Length; k += 3)
            area += Math.Abs(Point2.Cross(pts[tris[k + 1]] - pts[tris[k]], pts[tris[k + 2]] - pts[tris[k]])) / 2;
        Assert.InRange(area, 0.97e6, 1.0e6);

        // нито една точка вътре в описаната окръжност на триъгълник (проверка на извадка)
        for (int k = 0; k < Math.Min(tris.Length, 600); k += 3)
        {
            var a = pts[tris[k]]; var b = pts[tris[k + 1]]; var c = pts[tris[k + 2]];
            double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
            double ux = ((a.X * a.X + a.Y * a.Y) * (b.Y - c.Y) + (b.X * b.X + b.Y * b.Y) * (c.Y - a.Y) + (c.X * c.X + c.Y * c.Y) * (a.Y - b.Y)) / d;
            double uy = ((a.X * a.X + a.Y * a.Y) * (c.X - b.X) + (b.X * b.X + b.Y * b.Y) * (a.X - c.X) + (c.X * c.X + c.Y * c.Y) * (b.X - a.X)) / d;
            double r = Point2.Distance(new Point2(ux, uy), a);
            Assert.All(pts, p => Assert.True(Point2.Distance(new Point2(ux, uy), p) >= r - 1e-6));
        }
    }

    [Fact]
    public void LargeCoordinates_AreHandled()
    {
        var pts = new List<Point3>();
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
                pts.Add(new Point3(8_500_000 + x * 10, 4_700_000 + y * 10, 500 + x));
        var s = new Surface("big", pts);
        Assert.Equal(32, s.TriangleCount);
        Assert.Equal(502.5, s.ElevationAt(8_500_025, 4_700_013)!.Value, 6);
    }
}

public class SurfaceTests
{
    private static Surface Plane(double size = 100, double step = 10, Func<double, double, double>? f = null)
    {
        f ??= (x, y) => 0.1 * x + 0.05 * y + 10;
        var pts = new List<Point3>();
        for (double y = 0; y <= size; y += step)
            for (double x = 0; x <= size; x += step)
                pts.Add(new Point3(x, y, f(x, y)));
        return new Surface("plane", pts);
    }

    [Fact]
    public void ElevationAt_InterpolatesPlaneExactly()
    {
        var s = Plane();
        Assert.Equal(0.1 * 33.3 + 0.05 * 71.7 + 10, s.ElevationAt(33.3, 71.7)!.Value, 9);
        Assert.Null(s.ElevationAt(-1, 5));
    }

    [Fact]
    public void Statistics_AreCorrect()
    {
        var s = Plane(f: (_, _) => 5);
        var st = s.GetStatistics();
        Assert.Equal(10000, st.Area2D, 6);
        Assert.Equal(10000, st.Area3D, 6);
        Assert.Equal(5, st.MinZ);
        Assert.Equal(121, st.PointCount);
    }

    [Fact]
    public void Contours_OnPlaneAreStraight()
    {
        var s = Plane(f: (x, _) => x / 10); // кота 0..10
        var c = ContourGenerator.Generate(s, 1.0);
        Assert.NotEmpty(c);
        Assert.All(c, seg =>
        {
            Assert.Equal(seg.Level * 10, seg.A.X, 6);
            Assert.Equal(seg.Level * 10, seg.B.X, 6);
        });
    }

    [Fact]
    public void Editor_RaiseLowerAndDelete()
    {
        var s = Plane(f: (_, _) => 0);
        var poly = new Polygon2(new[] { new Point2(25, 25), new Point2(75, 25), new Point2(75, 75), new Point2(25, 75) });
        int n = SurfaceEditor.RaiseLower(s, poly, 2);
        Assert.Equal(25, n);
        Assert.Equal(2, s.ElevationAt(50, 50)!.Value, 9);
        int d = SurfaceEditor.DeleteInside(s, poly);
        Assert.Equal(25, d);
        Assert.Equal(96, s.Points.Count);
    }

    [Fact]
    public void MaxEdgeLength_RemovesLongTriangles()
    {
        var s = new Surface("t", new[]
        {
            new Point3(0, 0, 0), new Point3(1, 0, 0), new Point3(0, 1, 0), new Point3(100, 100, 0),
        });
        Assert.True(s.TriangleCount >= 2);
        s.MaxEdgeLength = 5;
        Assert.Equal(1, s.TriangleCount);
    }
}

public class GradingTests
{
    private static Surface Flat(double z = 0, double size = 60)
    {
        var pts = new List<Point3>();
        for (double y = -size; y <= size; y += 5)
            for (double x = -size; x <= size; x += 5)
                pts.Add(new Point3(x, y, z));
        return new Surface("flat", pts);
    }

    private static Platform Square(double half, double z) => new()
    {
        Boundary = new() { new(-half, -half), new(half, -half), new(half, half), new(-half, half) },
        BaseElevation = z,
        CutSlopeRatio = 1.0,
        FillSlopeRatio = 1.5,
    };

    [Fact]
    public void FillPlatform_VolumeIncludesSlopes()
    {
        var eg = Flat();
        var p = Square(5, 1);
        var v = VolumeCalculator.Compute(new GradingModel(eg, new[] { p }), 0.25, 4);

        // призма 10×10×1 + 4 клина (0.5·1·1.5·10) + конус в ъглите (π·1.5²·1/3)
        double expected = 100 + 4 * 0.5 * 1.5 * 10 + Math.PI * 1.5 * 1.5 / 3;
        Assert.Equal(expected, v.Fill, expected * 0.01);
        Assert.Equal(0, v.Cut, 1e-6);
    }

    [Fact]
    public void CutPlatform_UsesCutSlope()
    {
        var eg = Flat();
        var p = Square(5, -2);
        var model = new GradingModel(eg, new[] { p });
        Assert.Equal(-2, model.DesignElevation(0, 0)!.Value, 9);
        Assert.Equal(-1, model.DesignElevation(6, 0)!.Value, 9);   // 1 m от ръба, откос 1:1
        Assert.Equal(0, model.DesignElevation(8, 0)!.Value, 9);    // извън откоса = терен
        var v = VolumeCalculator.Compute(model, 0.25, 4);
        double expected = 200 + 4 * 0.5 * 2 * 2 * 10 + Math.PI * 4 * 2 / 3;
        Assert.Equal(expected, v.Cut, expected * 0.01);
    }

    [Fact]
    public void Balance_OnInclinedTerrain_FindsMiddleElevation()
    {
        var pts = new List<Point3>();
        for (double y = -60; y <= 60; y += 5)
            for (double x = -60; x <= 60; x += 5)
                pts.Add(new Point3(x, y, 100 + 0.1 * x));
        var eg = new Surface("incl", pts);
        var p = Square(20, 90);
        var platforms = new List<Platform> { p };

        var r = EarthworkBalancer.Balance(eg, platforms, p, 1.0);
        Assert.True(r.Converged);
        Assert.Equal(100, r.BaseElevation, 0.15);
        Assert.Equal(r.Volumes.Cut, r.Volumes.Fill, r.Volumes.Cut * 0.03);
    }

    [Fact]
    public void DesignSurface_MatchesModel()
    {
        var eg = Flat();
        var p = Square(10, 2);
        var v2 = VolumeCalculator.Compute(new GradingModel(eg, new[] { p }), 0.5);

        // по подразбиране – само зоната на планирането (до линията на догонване)
        var design = DesignSurfaceBuilder.Build(eg, new[] { p }, 1.0, "design");
        Assert.Equal(2, design.ElevationAt(3, 4)!.Value, 6);
        Assert.Null(design.ElevationAt(40, 40));
        // откос 1:1.5 от z = 2 при x = 10 → догонване при x = 13; отвъд него проектна повърхнина няма
        Assert.Equal(2 - 2.4 / 1.5, design.ElevationAt(12.4, 0)!.Value, 6);
        Assert.NotNull(design.ElevationAt(12.95, 0));
        Assert.Null(design.ElevationAt(13.6, 0));
        Assert.Contains(design.Boundaries, b => b.Type == BoundaryType.Outer);
        var v1 = VolumeCalculator.Compute(eg, design, 0.5);
        Assert.Equal(v2.Fill, v1.Fill, v2.Fill * 0.03);

        // пълно копие на терена с вградения проект
        var full = DesignSurfaceBuilder.Build(eg, new[] { p }, 1.0, "full", gradingZoneOnly: false);
        Assert.Equal(0, full.ElevationAt(40, 40)!.Value, 6);
        var v3 = VolumeCalculator.Compute(eg, full, 0.5);
        Assert.Equal(v2.Fill, v3.Fill, v2.Fill * 0.03);
    }

    [Fact]
    public void SlopedPlatform_FollowsGrades()
    {
        var p = Square(10, 50);
        p.SlopeXPercent = 2;
        p.SlopeYPercent = -1;
        Assert.Equal(50 + 0.2 - 0.05, p.PlaneElevation(10, 5), 9);
    }
}

public class IoTests
{
    [Fact]
    public void Parser_HandlesCommonFormats()
    {
        var pts = PointFileReader.Parse(new[]
        {
            "# коментар",
            "Номер X Y H",
            "1 100.5 200.25 10.1",
            "T2;101,5;201,25;11,2",
            "102.5\t202.25\t12.3",
            "103.5,203.25,13.4",
            "",
        });
        Assert.Equal(4, pts.Count);
        Assert.Equal(new Point3(101.5, 201.25, 11.2), pts[1]);

        var geo = PointFileReader.Parse(new[] { "1 4700000 8500000 500" }, geodetic: true);
        Assert.Equal(new Point3(8500000, 4700000, 500), geo[0]);
    }

    [Fact]
    public void Project_RoundTrips()
    {
        var project = new Project();
        project.Surfaces.Add(SampleTerrain.Create(pointCount: 50));
        project.Platforms.Add(new Platform
        {
            Name = "П1",
            Boundary = new() { new(0, 0), new(10, 0), new(10, 10) },
            BaseElevation = 101.5,
            SlopeXPercent = 1.5,
            ReferencePoint = new Point2(1, 2),
        });
        project.ExistingSurfaceName = project.Surfaces[0].Name;

        var copy = ProjectSerializer.FromJson(ProjectSerializer.ToJson(project));
        Assert.Equal(project.Surfaces[0].Points, copy.Surfaces[0].Points);
        Assert.Equal("П1", copy.Platforms[0].Name);
        Assert.Equal(new Point2(1, 2), copy.Platforms[0].ReferencePoint);
        Assert.Equal(1.5, copy.Platforms[0].SlopeXPercent);
        Assert.Equal(project.ExistingSurfaceName, copy.ExistingSurfaceName);
    }

    [Fact]
    public void LandXml_IsWritten()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sd_{Guid.NewGuid():N}.xml");
        try
        {
            var s = SampleTerrain.Create(pointCount: 30);
            LandXmlWriter.Write(path, new[] { s });
            var doc = System.Xml.Linq.XDocument.Load(path);
            var ns = System.Xml.Linq.XNamespace.Get("http://www.landxml.org/schema/LandXML-1.2");
            Assert.Equal(s.TriangleCount, doc.Descendants(ns + "F").Count());
        }
        finally { File.Delete(path); }
    }
}
