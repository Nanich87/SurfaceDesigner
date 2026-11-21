using System.Xml.Linq;
using SurfaceDesigner.Core.Geometry;
using SurfaceDesigner.Core.IO;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.Core.Tests;

public class LandXmlReaderTests
{
    private static HashSet<string> TriangleSet(Surface s)
    {
        static (long, long) K(Point2 p) => ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4));
        var v = s.Vertices; var t = s.Triangles;
        var set = new HashSet<string>();
        for (int k = 0; k < t.Length; k += 3)
        {
            var arr = new[] { K(v[t[k]].XY), K(v[t[k + 1]].XY), K(v[t[k + 2]].XY) };
            Array.Sort(arr);
            set.Add(string.Join(";", arr));
        }
        return set;
    }

    private static Surface RoundTrip(Surface s, out LandXmlImportResult result)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sd_{Guid.NewGuid():N}.xml");
        try
        {
            LandXmlWriter.Write(path, new[] { s });
            result = LandXmlReader.Read(path);
            return Assert.Single(result.Surfaces);
        }
        finally { File.Delete(path); }
    }

    private static void AssertSameTin(Surface expected, Surface actual)
    {
        Assert.Equal(expected.TriangleCount, actual.TriangleCount);
        Assert.True(TriangleSet(expected).SetEquals(TriangleSet(actual)), "Триангулациите се различават");
        var rnd = new Random(4);
        var b = expected.Bounds;
        for (int i = 0; i < 2000; i++)
        {
            double x = b.MinX + rnd.NextDouble() * b.Width, y = b.MinY + rnd.NextDouble() * b.Height;
            var z1 = expected.ElevationAt(x, y);
            var z2 = actual.ElevationAt(x, y);
            Assert.Equal(z1.HasValue, z2.HasValue);
            // LandXML записва координатите до 0.1 мм – затова допуск 1 мм
            if (z1.HasValue) Assert.Equal(z1.Value, z2!.Value, 1e-3);
        }
    }

    [Fact]
    public void DelaunayTin_RoundTripsExactly()
    {
        var s = SampleTerrain.Create("Терен", pointCount: 800);
        var r = RoundTrip(s, out var res);
        AssertSameTin(s, r);
        Assert.Contains(res.Messages, m => m.Contains("възпроизведена точно"));
        Assert.Empty(r.Boundaries);
        Assert.Equal("Терен", r.Name);
    }

    [Fact]
    public void NonDelaunayTin_WithBreaklinesSwapsAndHoles_RoundTripsExactly()
    {
        var s = SampleTerrain.Create("Сложна", pointCount: 1500);
        s.AddBreakline(new Breakline("ръб", new[] { new Point3(15, 30, 110), new Point3(120, 70, 104), new Point3(185, 160, 112) }));
        // ръчни завъртания на ребра → не-Делоне триангулация
        var rnd = new Random(9);
        for (int i = 0; i < 150; i++)
            if (s.TryFindEdge(rnd.NextDouble() * 200, rnd.NextDouble() * 200, 100, out int a, out int b)) s.SwapEdge(a, b, out _);
        // вдлъбнат външен контур, дупка и остров в дупката
        s.AddBoundary(new SurfaceBoundary("O", BoundaryType.Outer, new Point2[] { new(5, 8), new(150, 3), new(196, 90), new(110, 110), new(180, 190), new(10, 185) }));
        s.AddBoundary(new SurfaceBoundary("H", BoundaryType.Hide, new Point2[] { new(40, 40), new(100, 45), new(95, 100), new(45, 95) }));
        s.AddBoundary(new SurfaceBoundary("S", BoundaryType.Show, new Point2[] { new(60, 60), new(80, 62), new(75, 80) }));

        var r = RoundTrip(s, out var res);
        AssertSameTin(s, r);
        Assert.Contains(res.Messages, m => m.Contains("възпроизведена точно"));
        Assert.NotEmpty(r.EdgeSwaps);
        Assert.Contains(r.Boundaries, b => b.Type == BoundaryType.Outer);
        Assert.Contains(r.Boundaries, b => b.Type == BoundaryType.Hide);
        Assert.Contains(r.Boundaries, b => b.Type == BoundaryType.Show);
        Assert.Equal(s.GetStatistics().Area2D, r.GetStatistics().Area2D, 1e-6 * s.GetStatistics().Area2D);

        // импортираната повърхнина остава същата и след запис/зареждане на проекта
        var project = new SurfaceDesigner.Core.Project();
        project.Surfaces.Add(r);
        var again = ProjectSerializer.FromJson(ProjectSerializer.ToJson(project)).Surfaces[0];
        AssertSameTin(r, again);
    }

    [Fact]
    public void Breaklines_AreImportedAsEditableObjects_AndTinStaysExact()
    {
        var s = SampleTerrain.Create("С линии", pointCount: 1200);
        // две пресичащи се линии (пресечната точка става връх на TIN) и затворена линия
        s.AddBreakline(new Breakline("Ос", new[] { new Point3(10, 100, 103), new Point3(190, 110, 106) }));
        s.AddBreakline(new Breakline("Напречна", new[] { new Point3(100, 10, 101), new Point3(95, 190, 108) }));
        s.AddBreakline(new Breakline("Площадка", new[] { new Point3(140, 140, 107), new Point3(170, 140, 107), new Point3(170, 170, 107), new Point3(140, 170, 107), new Point3(140, 140, 107) }));
        var rnd = new Random(3);
        for (int i = 0; i < 60; i++)
            if (s.TryFindEdge(rnd.NextDouble() * 200, rnd.NextDouble() * 200, 100, out int a, out int b)) s.SwapEdge(a, b, out _);
        s.AddBoundary(new SurfaceBoundary("H", BoundaryType.Hide, new Point2[] { new(30, 30), new(60, 30), new(55, 60) }));

        var r = RoundTrip(s, out var res);
        AssertSameTin(s, r);
        Assert.Contains(res.Messages, m => m.Contains("възпроизведена точно") && m.Contains("3 структурни линии"));

        Assert.Equal(new[] { "Ос", "Напречна", "Площадка" }, r.Breaklines.Select(b => b.Name).ToArray());
        Assert.True(r.Breaklines[2].IsClosed);
        // всеки оригинален връх присъства в импортираната линия (плюс евентуално точки по нея – напр. пресичането)
        for (int i = 0; i < 3; i++)
            foreach (var v in s.Breaklines[i].Vertices)
                Assert.Contains(r.Breaklines[i].Vertices, w => Point2.Distance(v.XY, w.XY) < 1e-3);
        Assert.Contains(r.Breaklines[0].Vertices, w => r.Breaklines[1].Vertices.Any(u => Point2.Distance(u.XY, w.XY) < 1e-3)); // общ връх
        Assert.Equal(0, r.FailedBreaklineSegments);
        Assert.All(r.Breaklines, b => Assert.All(b.Vertices.Zip(b.Vertices.Skip(1)), seg =>
            Assert.Equal((seg.First.Z + seg.Second.Z) / 2, r.ElevationAt((seg.First.X + seg.Second.X) / 2, (seg.First.Y + seg.Second.Y) / 2)!.Value, 6)));
    }

    [Fact]
    public void Breaklines_NotHonoredByFaces_AreSplit()
    {
        // квадрат с диагонал 1-3; линия 1→3 (ребро, 2D) и линия 2→4 (НЕ е ребро – пресича диагонала); линия към точка извън TIN
        var xml = """
            <LandXML xmlns="http://www.landxml.org/schema/LandXML-1.2">
              <Surfaces><Surface name="Квадрат">
                <SourceData><Breaklines>
                  <Breakline name="Диагонал"><PntList2D>0 0 10 10</PntList2D></Breakline>
                  <Breakline name="Насрещен"><PntList3D>0 10 1 10 0 1</PntList3D></Breakline>
                  <Breakline name="Навън"><PntList3D>0 0 0 0 10 1 50 50 9</PntList3D></Breakline>
                </Breaklines></SourceData>
                <Definition surfType="TIN">
                  <Pnts><P id="1">0 0 0</P><P id="2">0 10 1</P><P id="3">10 10 2</P><P id="4">10 0 1</P></Pnts>
                  <Faces><F>1 2 3</F><F>1 3 4</F></Faces>
                </Definition>
              </Surface></Surfaces>
            </LandXML>
            """;
        var res = LandXmlReader.Parse(XDocument.Parse(xml));
        var s = Assert.Single(res.Surfaces);
        Assert.Equal(new[] { "Диагонал", "Навън" }, s.Breaklines.Select(b => b.Name).ToArray());
        Assert.Equal(2, s.Breaklines[0].Vertices[1].Z); // котата е от точката на TIN, не от линията
        Assert.Equal(2, s.Breaklines[1].Vertices.Count); // връхът извън повърхнината е изпуснат
        Assert.Contains(res.Messages, m => m.Contains("не са ребра"));
        Assert.Contains(res.Messages, m => m.Contains("не съвпадат с точки"));
        Assert.Contains(res.Messages, m => m.Contains("възпроизведена точно"));
        Assert.Equal(2, s.TriangleCount);
    }

    [Fact]
    public void Parse_HandlesNamespacesInvisibleFacesAndCoordinateOrder()
    {
        // LandXML 1.0 namespace; P: „север изток кота“; невидим триъгълник i="1"
        var xml = """
            <LandXML xmlns="http://www.landxml.org/schema/LandXML-1.0" version="1.0">
              <Surfaces>
                <Surface name="Тест">
                  <Definition surfType="TIN">
                    <Pnts>
                      <P id="10">4700000 8500000 100</P>
                      <P id="11">4700000 8500010 101</P>
                      <P id="12">4700010 8500010 102</P>
                      <P id="13">4700010 8500000 103</P>
                    </Pnts>
                    <Faces>
                      <F>10 11 12</F>
                      <F i="1">10 12 13</F>
                    </Faces>
                  </Definition>
                </Surface>
              </Surfaces>
            </LandXML>
            """;
        var res = LandXmlReader.Parse(XDocument.Parse(xml));
        var s = Assert.Single(res.Surfaces);
        Assert.Equal(1, s.TriangleCount);
        // изток = X, север = Y
        Assert.Equal(new Point3(8500010, 4700000, 101), s.Points.Single(p => p.Z == 101));
        Assert.NotNull(s.ElevationAt(8500008, 4700002)); // видимият триъгълник 10-11-12 (под диагонала)
        Assert.Null(s.ElevationAt(8500002, 4700008));    // невидимият 10-12-13 (над диагонала) липсва
        Assert.Contains(res.Messages, m => m.Contains("невидими"));
    }

    [Fact]
    public void Parse_OverlappingTriangles_AreReported()
    {
        // неизпъкнал четириъгълник с „грешния“ диагонал → двата триъгълника се застъпват (повреден файл)
        var xml = """
            <LandXML xmlns="http://www.landxml.org/schema/LandXML-1.2">
              <Surfaces><Surface name="Повреден"><Definition surfType="TIN">
                <Pnts>
                  <P id="1">0 0 0</P><P id="2">0 10 0</P><P id="3">3 4 0</P><P id="4">10 0 0</P><P id="5">10 10 0</P>
                </Pnts>
                <Faces><F>1 2 3</F><F>1 3 4</F><F>2 5 3</F><F>3 5 4</F><F>2 4 5</F></Faces>
              </Definition></Surface></Surfaces>
            </LandXML>
            """;
        var res = LandXmlReader.Parse(XDocument.Parse(xml));
        Assert.Single(res.Surfaces);
        Assert.Contains(res.Messages, m => m.Contains("различават от източника"));
    }

    [Fact]
    public void Parse_CgPointsOnly_BecomesPointSurface()
    {
        var xml = """
            <LandXML xmlns="http://www.landxml.org/schema/LandXML-1.2">
              <CgPoints>
                <CgPoint name="1">100 200 10</CgPoint>
                <CgPoint name="2">100 210 11</CgPoint>
                <CgPoint name="3">110 205 12</CgPoint>
              </CgPoints>
            </LandXML>
            """;
        var res = LandXmlReader.Parse(XDocument.Parse(xml));
        var s = Assert.Single(res.Surfaces);
        Assert.Equal(3, s.Points.Count);
        Assert.Equal(1, s.TriangleCount);
        Assert.Contains(new Point3(200, 100, 10), s.Points);
    }
}
