using SurfaceDesigner.Core;
using SurfaceDesigner.Core.Geometry;
using SurfaceDesigner.Core.IO;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.Core.Tests;

public class EdgeSwapTests
{
    /// <summary>Правоъгълник 10×6 с „гърбица“ в един ъгъл – котата в центъра зависи от диагонала (0 или 5).</summary>
    private static Surface Quad() => new("q", new[]
    {
        new Point3(0, 0, 0), new Point3(10, 0, 0), new Point3(10, 6, 10), new Point3(0, 6, 0),
    });

    private static HashSet<(int A, int B)> Edges(Surface s) => s.GetEdges().Select(e => (e.A, e.B)).ToHashSet();

    [Fact]
    public void Swap_ChangesDiagonalAndInterpolation()
    {
        var s = Quad();
        double before = s.ElevationAt(5, 3)!.Value;
        Assert.True(s.TryFindEdge(5, 3, 1, out int a, out int b));
        Assert.True(s.CanSwapEdge(a, b, out int r, out int t, out _));

        Assert.True(s.SwapEdge(a, b, out _));
        Assert.Single(s.EdgeSwaps);
        var edges = Edges(s);
        Assert.DoesNotContain(ConstrainedDelaunay.Key(a, b), edges);
        Assert.Contains(ConstrainedDelaunay.Key(r, t), edges);
        double after = s.ElevationAt(5, 3)!.Value;
        Assert.Equal(5.0, before + after, 9); // единият диагонал дава 5, другият 0
        Assert.Equal(2, s.TriangleCount);
    }

    [Fact]
    public void SwapTwice_RestoresOriginal()
    {
        var s = Quad();
        double z0 = s.ElevationAt(5, 3)!.Value;
        s.TryFindEdge(5, 3, 1, out int a, out int b);
        s.SwapEdge(a, b, out _);
        s.TryFindEdge(5, 3, 1, out a, out b);
        Assert.True(s.SwapEdge(a, b, out _));
        Assert.Equal(z0, s.ElevationAt(5, 3)!.Value, 9);
        Assert.Equal(0, s.FailedEdgeSwaps);
    }

    [Fact]
    public void Swap_SurvivesRetriangulationCloneAndSave()
    {
        var s = Quad();
        s.TryFindEdge(5, 3, 1, out int a, out int b);
        s.SwapEdge(a, b, out _);
        double z = s.ElevationAt(5, 3)!.Value;

        s.AddPoint(new Point3(30, 30, 1)); // нова триангулация
        Assert.Equal(z, s.ElevationAt(5, 3)!.Value, 9);
        Assert.Equal(z, s.Clone().ElevationAt(5, 3)!.Value, 9);

        var project = new Project();
        project.Surfaces.Add(s);
        var copy = ProjectSerializer.FromJson(ProjectSerializer.ToJson(project)).Surfaces[0];
        Assert.Single(copy.EdgeSwaps);
        Assert.Equal(z, copy.ElevationAt(5, 3)!.Value, 9);
        Assert.Equal(s.Triangles, copy.Triangles);
    }

    [Fact]
    public void HullEdge_CannotBeSwapped()
    {
        var s = Quad();
        Assert.True(s.TryFindEdge(5, 0.01, 1, out int a, out int b));
        Assert.False(s.SwapEdge(a, b, out var reason));
        Assert.Contains("контур", reason);
        Assert.Empty(s.EdgeSwaps);
    }

    [Fact]
    public void NonConvexQuad_CannotBeSwapped()
    {
        // „стрелка“: вдлъбнатият връх (5,2) прави четириъгълника неизпъкнал за реброто (0,0)–(5,2)
        var s = new Surface("arrow", new[] { new Point3(0, 0, 0), new Point3(10, 0, 0), new Point3(5, 2, 0), new Point3(5, 10, 0) });
        s.MaxEdgeLength = 0;
        foreach (var (a, b) in s.GetEdges())
        {
            var va = s.Vertices[a].XY; var vb = s.Vertices[b].XY;
            bool isReflexEdge = (va == new Point2(5, 2) && vb == new Point2(5, 10)) || (vb == new Point2(5, 2) && va == new Point2(5, 10));
            if (isReflexEdge)
            {
                Assert.False(s.CanSwapEdge(a, b, out _, out _, out var reason));
                Assert.Contains("неизпъкнал", reason);
                return;
            }
        }

        Assert.Fail("Очакваното ребро липсва");
    }

    [Fact]
    public void BreaklineEdge_CannotBeSwapped()
    {
        var s = Quad();
        s.AddBreakline(new Breakline("b", new[] { new Point3(0, 0, 0), new Point3(10, 6, 10) }));
        Assert.True(s.TryFindEdge(5, 3, 1, out int a, out int b));
        Assert.False(s.SwapEdge(a, b, out var reason));
        Assert.Contains("структурна", reason);
    }

    [Fact]
    public void Swap_FailsAfterPointDeleted_AndCanBeCleaned()
    {
        var s = Quad();
        s.TryFindEdge(5, 3, 1, out int a, out int b);
        s.SwapEdge(a, b, out _);
        s.RemovePointAt(s.FindNearestPoint(s.Vertices[a].X, s.Vertices[a].Y, 0.01));
        Assert.Equal(1, s.FailedEdgeSwaps);
        Assert.Equal(1, s.RemoveFailedEdgeSwaps());
        Assert.Empty(s.EdgeSwaps);
        Assert.Equal(0, s.FailedEdgeSwaps);
    }

    [Fact]
    public void Swap_WorksInsideBoundary_ButNotOnBoundaryEdges()
    {
        var s = SampleTerrain.Create(pointCount: 400);
        s.AddBoundary(new SurfaceBoundary("B", BoundaryType.Outer, new Point2[] { new(20.3, 20.7), new(180.1, 25.2), new(150.4, 170.9), new(30.2, 160.6) }));
        double area = s.GetStatistics().Area2D;

        // ребро по границата
        Assert.True(s.TryFindEdge(100, 22.95, 0.5, out int a, out int b));
        Assert.False(s.CanSwapEdge(a, b, out _, out _, out _));

        // вътрешни ребра – намираме допустимо и го завъртаме
        int swapped = 0;
        var rnd = new Random(2);
        for (int i = 0; i < 200 && swapped < 10; i++)
        {
            double x = 50 + rnd.NextDouble() * 80, y = 50 + rnd.NextDouble() * 80;
            if (s.TryFindEdge(x, y, 100, out a, out b) && s.SwapEdge(a, b, out _)) swapped++;
        }

        Assert.Equal(10, swapped);
        Assert.Equal(0, s.FailedEdgeSwaps);
        Assert.Equal(area, s.GetStatistics().Area2D, 1e-6 * area);
    }

    [Fact]
    public void ManySwaps_KeepValidTriangulation()
    {
        var s = SampleTerrain.Create(pointCount: 1500);
        var st0 = s.GetStatistics();
        var rnd = new Random(5);
        int swapped = 0;
        for (int i = 0; i < 400; i++)
        {
            double x = rnd.NextDouble() * 200, y = rnd.NextDouble() * 200;
            if (s.TryFindEdge(x, y, 100, out int a, out int b) && s.SwapEdge(a, b, out _)) swapped++;
        }

        Assert.True(swapped > 300);
        Assert.Equal(0, s.FailedEdgeSwaps);

        var st = s.GetStatistics();
        Assert.Equal(st0.TriangleCount, st.TriangleCount);
        Assert.Equal(st0.Area2D, st.Area2D, 1e-6 * st0.Area2D);
        var v = s.Vertices; var t = s.Triangles;
        for (int k = 0; k < t.Length; k += 3)
            Assert.True(Math.Abs(Point2.Cross(v[t[k + 1]].XY - v[t[k]].XY, v[t[k + 2]].XY - v[t[k]].XY)) > 1e-12);
    }
}
