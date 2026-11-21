namespace SurfaceDesigner.Core.Geometry;

/// <summary>
/// Контур на данните – вдлъбната обвивка на точките (χ-shape, Duckham и др., 2008).
/// Тръгва от изпъкналата обвивка (триангулация на Делоне) и последователно премахва външни триъгълници
/// с най-дълго външно ребро, по-дълго от прага. Триъгълник не се премахва, ако срещуположният му връх
/// вече е по контура – така резултатът винаги е прост многоъгълник без „прищипвания“ и дупки.
/// </summary>
public static class DataHull
{
    /// <summary>Контур (обратно на часовниковата стрелка) или празен списък при по-малко от 3 точки.</summary>
    /// <param name="maxEdgeLength">Външни ребра, по-дълги от това, се „изяждат“. 0 или ∞ – изпъкнала обвивка.</param>
    public static List<Point2> Compute(IReadOnlyList<Point2> points, double maxEdgeLength)
    {
        var pts = Unique(points);
        var tri = Delaunay.Triangulate(pts);
        if (tri.Length == 0) return new List<Point2>();
        if (maxEdgeLength <= 0) maxEdgeLength = double.PositiveInfinity;

        // CCW ориентация и полуребра
        double Cross(int a, int b, int c) =>
            (pts[b].X - pts[a].X) * (pts[c].Y - pts[a].Y) - (pts[b].Y - pts[a].Y) * (pts[c].X - pts[a].X);
        for (int k = 0; k < tri.Length; k += 3)
            if (Cross(tri[k], tri[k + 1], tri[k + 2]) < 0) (tri[k + 1], tri[k + 2]) = (tri[k + 2], tri[k + 1]);

        int Next(int e) => e % 3 == 2 ? e - 2 : e + 1;
        int Prev(int e) => e % 3 == 0 ? e + 2 : e - 1;

        var opp = new int[tri.Length];
        Array.Fill(opp, -1);
        var directed = new Dictionary<(int, int), int>(tri.Length);
        for (int e = 0; e < tri.Length; e++) directed[(tri[e], tri[Next(e)])] = e;
        for (int e = 0; e < tri.Length; e++)
            if (directed.TryGetValue((tri[Next(e)], tri[e]), out int o)) opp[e] = o;

        int nt = tri.Length / 3;
        var removed = new bool[nt];
        var onBoundary = new bool[pts.Count];
        var queue = new PriorityQueue<int, double>();
        double Len(int e)
        {
            var a = pts[tri[e]]; var b = pts[tri[Next(e)]];
            return Point2.Distance(a, b);
        }

        for (int e = 0; e < tri.Length; e++)
            if (opp[e] < 0)
            {
                onBoundary[tri[e]] = true;
                queue.Enqueue(e, -Len(e));
            }

        int remaining = nt;
        while (queue.TryDequeue(out int e, out double negLen))
        {
            if (-negLen <= maxEdgeLength) break;
            int t = e / 3;
            if (removed[t] || remaining <= 1) continue;
            int v = tri[Prev(e)];
            if (onBoundary[v]) continue; // би прищипало контура

            removed[t] = true;
            remaining--;
            onBoundary[v] = true;
            foreach (int side in new[] { Next(e), Prev(e) })
            {
                int o = opp[side];
                if (o >= 0 && !removed[o / 3]) queue.Enqueue(o, -Len(o));
            }
        }

        // Извличане на контура: външните полуребра на останалите триъгълници образуват един цикъл
        var outgoing = new Dictionary<int, int>();
        int startEdge = -1;
        for (int e = 0; e < tri.Length; e++)
        {
            if (removed[e / 3]) continue;
            if (opp[e] >= 0 && !removed[opp[e] / 3]) continue;
            outgoing[tri[e]] = e;
            startEdge = e;
        }
        var ring = new List<Point2>();
        if (startEdge < 0) return ring;
        int h = startEdge;
        int guard = 0;
        do
        {
            ring.Add(pts[tri[h]]);
            if (!outgoing.TryGetValue(tri[Next(h)], out h)) break;
        } while (h != startEdge && guard++ < tri.Length);

        return RemoveCollinear(ring);
    }

    /// <summary>Препоръчителен праг: 3 × медианата на дължините на ребрата в триангулацията.</summary>
    public static double SuggestMaxEdge(IReadOnlyList<Point2> points)
    {
        var pts = Unique(points);
        var tri = Delaunay.Triangulate(pts);
        if (tri.Length == 0) return 0;
        var lengths = new List<double>(tri.Length);
        for (int k = 0; k < tri.Length; k += 3)
            for (int e = 0; e < 3; e++)
                lengths.Add(Point2.Distance(pts[tri[k + e]], pts[tri[k + (e + 1) % 3]]));
        lengths.Sort();
        return 3 * lengths[lengths.Count / 2];
    }

    private static List<Point2> Unique(IReadOnlyList<Point2> points)
    {
        var seen = new HashSet<(long, long)>();
        var list = new List<Point2>(points.Count);
        foreach (var p in points)
            if (seen.Add(((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4)))) list.Add(p);
        return list;
    }

    private static List<Point2> RemoveCollinear(List<Point2> ring)
    {
        if (ring.Count < 4) return ring;
        var result = new List<Point2>();
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[(i - 1 + ring.Count) % ring.Count];
            var b = ring[i];
            var c = ring[(i + 1) % ring.Count];
            double cross = Point2.Cross(b - a, c - b);
            double scale = Point2.Distance(a, b) * Point2.Distance(b, c);
            if (Math.Abs(cross) > 1e-10 * scale) result.Add(b);
        }
        return result.Count >= 3 ? result : ring;
    }
}
