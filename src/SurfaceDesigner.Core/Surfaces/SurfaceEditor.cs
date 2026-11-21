using SurfaceDesigner.Core.Geometry;

namespace SurfaceDesigner.Core.Surfaces;

/// <summary>Операции за редакция на повърхнини.</summary>
public static class SurfaceEditor
{
    /// <summary>
    /// Повдига (dz &gt; 0) или понижава (dz &lt; 0) точките и върховете на структурни линии в многоъгълника.
    /// Връща броя засегнати точки.
    /// </summary>
    public static int RaiseLower(Surface surface, Polygon2 region, double dz)
    {
        int n = 0;
        surface.TransformPoints(p =>
        {
            if (!region.Contains(p.X, p.Y))
            {
                return p;
            }

            n++;
            return p with { Z = p.Z + dz };
        });
        TransformBreaklines(surface, p => region.Contains(p.X, p.Y) ? p with { Z = p.Z + dz } : p);
        return n;
    }

    /// <summary>
    /// Изравнява областта на зададена кота: точките и върховете на структурни линии вътре получават
    /// кота <paramref name="z"/>, а контурът се добавя като затворена структурна линия – ръбът е ясно очертан.
    /// </summary>
    public static int Flatten(Surface surface, Polygon2 region, double z, string breaklineName = "Ръб на изравняване")
    {
        int n = 0;
        surface.TransformPoints(p =>
        {
            if (!region.Contains(p.X, p.Y))
            {
                return p;
            }

            n++;
            return p with { Z = z };
        });
        TransformBreaklines(surface, p => region.Contains(p.X, p.Y) ? p with { Z = z } : p);
        var ring = region.Vertices.Select(v => new Point3(v.X, v.Y, z)).ToList();
        ring.Add(ring[0]);
        surface.AddBreakline(new Breakline(breaklineName, ring));
        return n;
    }

    /// <summary>Изтрива точките (и участъците от структурни линии) вътре в многоъгълника.</summary>
    public static int DeleteInside(Surface surface, Polygon2 region)
    {
        RemoveBreaklineVertices(surface, p => region.Contains(p.X, p.Y));
        return surface.RemovePoints(p => region.Contains(p.X, p.Y));
    }

    /// <summary>Изтрива точките (и участъците от структурни линии) извън многоъгълника.</summary>
    public static int DeleteOutside(Surface surface, Polygon2 region)
    {
        RemoveBreaklineVertices(surface, p => !region.Contains(p.X, p.Y));
        return surface.RemovePoints(p => !region.Contains(p.X, p.Y));
    }

    private static void TransformBreaklines(Surface surface, Func<Point3, Point3> f)
    {
        if (surface.Breaklines.Count > 0)
        {
            surface.SetBreaklines(surface.Breaklines.Select(b => b.Transform(f)));
        }
    }

    private static void RemoveBreaklineVertices(Surface surface, Predicate<Point3> remove)
    {
        if (surface.Breaklines.Count > 0)
        {
            surface.SetBreaklines(surface.Breaklines.SelectMany(b => b.RemoveVertices(remove)));
        }
    }

    /// <summary>
    /// Лапласово изглаждане по Z: всяка точка се премества към средната кота на съседите си.
    /// Ако е зададена област – изглаждат се само точките в нея.
    /// Структурните линии остават непроменени (те са „твърди“ ръбове).
    /// </summary>
    public static int Smooth(Surface surface, int iterations = 1, double factor = 0.5, Polygon2? region = null)
    {
        factor = Math.Clamp(factor, 0, 1);
        int affected = 0;
        for (int it = 0; it < iterations; it++)
        {
            var pts = surface.Points;
            var verts = surface.Vertices;
            int n = pts.Count;
            var sum = new double[n];
            var cnt = new int[n];
            foreach (var (a, b) in surface.GetEdges())
            {
                if (a < n)
                {
                    sum[a] += verts[b].Z;
                    cnt[a]++;
                }

                if (b < n)
                {
                    sum[b] += verts[a].Z;
                    cnt[b]++;
                }
            }

            var boundary = BoundaryVertices(surface);
            var newZ = new double[n];
            affected = 0;
            for (int i = 0; i < n; i++)
            {
                var p = pts[i];
                bool apply = cnt[i] > 0 && !boundary.Contains(i) && (region == null || region.Contains(p.X, p.Y));
                newZ[i] = apply ? p.Z + factor * (sum[i] / cnt[i] - p.Z) : p.Z;
                if (apply)
                {
                    affected++;
                }
            }

            int idx = 0;
            surface.TransformPoints(p => p with { Z = newZ[idx++] });
        }

        return affected;
    }

    /// <summary>Нова повърхнина, получена чрез семплиране в правоъгълна мрежа със стъпка <paramref name="step"/>.</summary>
    public static Surface ResampleToGrid(Surface surface, double step, string name)
    {
        if (step <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(step));
        }

        var b = surface.Bounds;
        var result = new Surface(name) { MaxEdgeLength = surface.MaxEdgeLength > 0 ? Math.Max(surface.MaxEdgeLength, step * 1.5) : 0 };
        if (b.IsEmpty)
        {
            return result;
        }

        double x0 = Math.Floor(b.MinX / step) * step;
        double y0 = Math.Floor(b.MinY / step) * step;
        var list = new List<Point3>();
        for (double y = y0; y <= b.MaxY + 1e-9; y += step)
        {
            for (double x = x0; x <= b.MaxX + 1e-9; x += step)
            {
                var z = surface.ElevationAt(x, y);
                if (z.HasValue)
                {
                    list.Add(new Point3(x, y, z.Value));
                }
            }
        }

        result.AddPoints(list);
        result.SetBreaklines(surface.Breaklines); // ръбовете и границите се запазват
        result.SetBoundaries(surface.Boundaries);
        result.ContourInterval = surface.ContourInterval;
        result.MajorContourEvery = surface.MajorContourEvery;
        return result;
    }

    /// <summary>Върхове по външния контур на триангулацията.</summary>
    public static HashSet<int> BoundaryVertices(Surface surface)
    {
        var count = new Dictionary<(int, int), int>();
        var t = surface.Triangles;
        for (int k = 0; k < t.Length; k += 3)
        {
            for (int e = 0; e < 3; e++)
            {
                int a = t[k + e], b = t[k + (e + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        }

        var set = new HashSet<int>();
        foreach (var (key, c) in count)
        {
            if (c == 1)
            {
                set.Add(key.Item1);
                set.Add(key.Item2);
            }
        }

        return set;
    }
}
