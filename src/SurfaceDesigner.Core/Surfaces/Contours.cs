using SurfaceDesigner.Core.Geometry;

namespace SurfaceDesigner.Core.Surfaces;

/// <summary>Отсечка от хоризонтала.</summary>
public readonly record struct ContourSegment(double Level, Point2 A, Point2 B, bool IsMajor);

/// <summary>Хоризонтала като свързана полилиния.</summary>
public sealed record ContourPolyline(double Level, bool IsMajor, IReadOnlyList<Point2> Points, bool IsClosed);

public static class ContourGenerator
{
    /// <summary>
    /// Генерира хоризонтали през <paramref name="interval"/> метра като отделни отсечки (по една на триъгълник).
    /// Всяка <paramref name="majorEvery"/>-та хоризонтала е удебелена (главна).
    /// </summary>
    public static List<ContourSegment> Generate(Surface surface, double interval, int majorEvery = 5)
    {
        var result = new List<ContourSegment>();
        if (interval <= 0 || surface.TriangleCount == 0) return result;

        var pts = surface.Vertices;
        var t = surface.Triangles;
        for (int k = 0; k < t.Length; k += 3)
        {
            var a = pts[t[k]];
            var b = pts[t[k + 1]];
            var c = pts[t[k + 2]];
            double zmin = Math.Min(a.Z, Math.Min(b.Z, c.Z));
            double zmax = Math.Max(a.Z, Math.Max(b.Z, c.Z));
            long first = (long)Math.Ceiling(zmin / interval);
            long last = (long)Math.Floor(zmax / interval);
            for (long li = first; li <= last; li++)
            {
                double level = li * interval;
                if (TrySlice(a, b, c, level, out var p, out var q))
                    result.Add(new ContourSegment(level, p, q, majorEvery > 0 && li % majorEvery == 0));
            }
        }

        return result;
    }

    /// <summary>Най-малката „кръгла“ стъпка (1, 2, 2.5, 5 × 10ⁿ), не по-малка от зададената.</summary>
    public static double NiceStepAtLeast(double min)
    {
        if (!(min > 0)) return 1;
        double pow = Math.Pow(10, Math.Floor(Math.Log10(min)));
        foreach (double k in new[] { 1, 2, 2.5, 5, 10 })
            if (k * pow >= min * (1 - 1e-12)) return k * pow;
        return 10 * pow;
    }

    /// <summary>Хоризонтали, свързани в полилинии (отворени – до края на повърхнината, или затворени).</summary>
    public static List<ContourPolyline> GeneratePolylines(Surface surface, double interval, int majorEvery = 5)
    {
        var result = new List<ContourPolyline>();
        foreach (var group in Generate(surface, interval, majorEvery).GroupBy(s => s.Level))
        {
            var segs = group.ToList();
            bool major = segs[0].IsMajor;

            // връзки: крайна точка → отсечки (точките са побитово еднакви благодарение на каноничния ред)
            var byPoint = new Dictionary<Point2, List<int>>();
            for (int i = 0; i < segs.Count; i++)
            {
                Add(byPoint, segs[i].A, i);
                Add(byPoint, segs[i].B, i);
            }

            var used = new bool[segs.Count];

            // Първо отворените линии (започват от точка само с една отсечка), после затворените
            var starts = Enumerable.Range(0, segs.Count)
                .OrderBy(i => byPoint[segs[i].A].Count == 1 || byPoint[segs[i].B].Count == 1 ? 0 : 1);
            foreach (int s0 in starts)
            {
                if (used[s0]) continue;
                used[s0] = true;
                var seg = segs[s0];

                // ориентираме така, че да започнем от свободния край (ако има)
                var start = byPoint[seg.A].Count == 1 ? seg.A : byPoint[seg.B].Count == 1 ? seg.B : seg.A;
                var line = new List<Point2> { start, start == seg.A ? seg.B : seg.A };
                Extend(line, segs, byPoint, used);
                if (Point2.Distance(line[0], line[^1]) > 0 && byPoint[line[0]].Count > 1)
                {
                    // не започнахме от край – продължаваме и назад
                    line.Reverse();
                    Extend(line, segs, byPoint, used);
                }

                bool closed = line.Count > 3 && line[0] == line[^1];
                result.Add(new ContourPolyline(group.Key, major, line, closed));
            }
        }

        return result;
    }

    private static void Extend(List<Point2> line, List<ContourSegment> segs, Dictionary<Point2, List<int>> byPoint, bool[] used)
    {
        while (true)
        {
            var end = line[^1];
            int next = -1;
            foreach (int i in byPoint[end])
                if (!used[i]) { next = i; break; }
            if (next < 0) return;
            used[next] = true;
            var s = segs[next];
            line.Add(s.A == end ? s.B : s.A);
        }
    }

    private static void Add(Dictionary<Point2, List<int>> d, Point2 p, int i)
    {
        if (!d.TryGetValue(p, out var list)) d[p] = list = new List<int>(2);
        list.Add(i);
    }

    private static bool TrySlice(Point3 a, Point3 b, Point3 c, double level, out Point2 p, out Point2 q)
    {
        // Върховете точно на нивото се третират като "над" него – така няма изродени случаи.
        bool ua = a.Z >= level, ub = b.Z >= level, uc = c.Z >= level;
        p = q = default;
        if (ua == ub && ub == uc) return false;

        Span<Point2> hits = stackalloc Point2[2];
        int n = 0;
        if (ua != ub) hits[n++] = Lerp(a, b, level);
        if (ub != uc) hits[n++] = Lerp(b, c, level);
        if (uc != ua && n < 2) hits[n++] = Lerp(c, a, level);
        if (n < 2) return false;
        p = hits[0];
        q = hits[1];
        return Point2.Distance(p, q) > 1e-12;
    }

    /// <summary>Пресечна точка с реброто – в каноничен ред на краищата, за еднакъв резултат от двата съседни триъгълника.</summary>
    private static Point2 Lerp(Point3 a, Point3 b, double level)
    {
        if (b.X < a.X || (b.X == a.X && b.Y < a.Y)) (a, b) = (b, a);
        double t = (level - a.Z) / (b.Z - a.Z);

        // хоризонтала точно през връх – връщаме самия връх (побитово), за да се свързват отсечките
        if (t <= 0) return a.XY;
        if (t >= 1) return b.XY;
        return new Point2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }
}
