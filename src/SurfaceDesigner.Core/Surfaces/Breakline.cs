using SurfaceDesigner.Core.Geometry;

namespace SurfaceDesigner.Core.Surfaces;

/// <summary>
/// Структурна линия (breakline) – 3D полилиния, чиито отсечки задължително стават ребра на TIN.
/// Използва се за ръбове, бордюри, дъна на канавки, горни/долни ръбове на откоси и др.
/// Затворена линия се задава с последен връх, съвпадащ с първия.
/// Обектът е неизменяем – промените създават нов обект.
/// </summary>
public sealed class Breakline
{
    public Breakline(string name, IEnumerable<Point3> vertices)
    {
        Name = name;
        var list = new List<Point3>();
        foreach (var v in vertices)
        {
            if (list.Count > 0 && Math.Abs(list[^1].X - v.X) < 1e-9 && Math.Abs(list[^1].Y - v.Y) < 1e-9)
            {
                continue;
            }

            list.Add(v);
        }

        if (list.Count < 2)
        {
            throw new ArgumentException("Структурната линия трябва да има поне 2 различни върха.");
        }

        Vertices = list;
    }

    public string Name { get; }

    public IReadOnlyList<Point3> Vertices { get; }

    public bool IsClosed =>
        Vertices.Count > 3 && Point2.Distance(Vertices[0].XY, Vertices[^1].XY) < 1e-9;

    public double Length2D
    {
        get
        {
            double s = 0;
            for (int i = 1; i < Vertices.Count; i++)
            {
                s += Point2.Distance(Vertices[i - 1].XY, Vertices[i].XY);
            }

            return s;
        }
    }

    public Bounds2 Bounds => Bounds2.FromPoints(Vertices.Select(v => v.XY));

    public Breakline WithName(string name) => new(name, Vertices);

    public Breakline Transform(Func<Point3, Point3> f) => new(Name, Vertices.Select(f));

    /// <summary>Премахва върховете, отговарящи на условието; останалите непрекъснати участъци стават отделни линии.</summary>
    public IEnumerable<Breakline> RemoveVertices(Predicate<Point3> remove)
    {
        var run = new List<Point3>();
        foreach (var v in Vertices)
        {
            if (remove(v))
            {
                if (run.Count >= 2)
                {
                    yield return new Breakline(Name, run);
                }

                run = new List<Point3>();
            }
            else
            {
                run.Add(v);
            }
        }

        if (run.Count >= 2)
        {
            yield return new Breakline(Name, run);
        }
    }

    /// <summary>Кота в точка от линията, зададена с хоризонтално разстояние от началото (по права между върховете).</summary>
    public double ElevationAtStation(double station)
    {
        double s = 0;
        for (int i = 1; i < Vertices.Count; i++)
        {
            var a = Vertices[i - 1];
            var b = Vertices[i];
            double len = Point2.Distance(a.XY, b.XY);
            if (station <= s + len || i == Vertices.Count - 1)
            {
                double t = len > 0 ? Math.Clamp((station - s) / len, 0, 1) : 0;
                return a.Z + (b.Z - a.Z) * t;
            }

            s += len;
        }

        return Vertices[^1].Z;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Name} ({Vertices.Count} т., {Length2D:F1} m{(IsClosed ? ", затворена" : "")})";
}

/// <summary>Разделя структурните линии в точките им на пресичане/допиране.</summary>
internal static class BreaklineSplitter
{
    private const double Eps = 1e-9;

    private readonly record struct Seg(int Line, int Index, Point3 A, Point3 B)
    {
        public double MinX => Math.Min(A.X, B.X);
        public double MaxX => Math.Max(A.X, B.X);
        public double MinY => Math.Min(A.Y, B.Y);
        public double MaxY => Math.Max(A.Y, B.Y);
    }

    /// <summary>
    /// Връща полилиниите, допълнени с върхове във всички пресечни точки.
    /// Котата в пресечна точка се взема от по-рано добавената линия (приоритет по ред),
    /// освен ако точката е връх на някоя от линиите – тогава се ползва котата на върха.
    /// </summary>
    public static List<List<Point3>> Split(IReadOnlyList<Breakline> lines)
    {
        var segs = new List<Seg>();
        for (int l = 0; l < lines.Count; l++)
        {
            var v = lines[l].Vertices;
            for (int i = 1; i < v.Count; i++)
            {
                segs.Add(new Seg(l, i - 1, v[i - 1], v[i]));
            }
        }

        var inserts = new Dictionary<(int, int), List<(double T, Point3 P)>>();
        var order = Enumerable.Range(0, segs.Count).OrderBy(i => segs[i].MinX).ToArray();

        for (int oi = 0; oi < order.Length; oi++)
        {
            var s1 = segs[order[oi]];
            for (int oj = oi + 1; oj < order.Length; oj++)
            {
                var s2 = segs[order[oj]];
                if (s2.MinX > s1.MaxX + Eps)
                {
                    break;
                }

                if (s2.MinY > s1.MaxY + Eps || s2.MaxY < s1.MinY - Eps)
                {
                    continue;
                }

                if (!Intersect(s1, s2, out double t, out double u))
                {
                    continue;
                }

                bool interior1 = t > Eps && t < 1 - Eps;
                bool interior2 = u > Eps && u < 1 - Eps;
                if (!interior1 && !interior2)
                {
                    continue; // общ връх
                }

                // Кота: връх на една от линиите има приоритет, иначе – по-рано добавената линия
                double z;
                if (!interior1)
                {
                    z = t < 0.5 ? s1.A.Z : s1.B.Z;
                }
                else if (!interior2)
                {
                    z = u < 0.5 ? s2.A.Z : s2.B.Z;
                }
                else
                {
                    var first = s1.Line < s2.Line || (s1.Line == s2.Line && s1.Index < s2.Index) ? (s1, t) : (s2, u);
                    z = first.Item1.A.Z + (first.Item1.B.Z - first.Item1.A.Z) * first.Item2;
                }

                double x = s1.A.X + (s1.B.X - s1.A.X) * t;
                double y = s1.A.Y + (s1.B.Y - s1.A.Y) * t;
                var p = new Point3(x, y, z);
                if (interior1)
                {
                    Add(inserts, (s1.Line, s1.Index), t, p);
                }

                if (interior2)
                {
                    Add(inserts, (s2.Line, s2.Index), u, p);
                }
            }
        }

        var result = new List<List<Point3>>(lines.Count);
        for (int l = 0; l < lines.Count; l++)
        {
            var v = lines[l].Vertices;
            var poly = new List<Point3> { v[0] };
            for (int i = 1; i < v.Count; i++)
            {
                if (inserts.TryGetValue((l, i - 1), out var extra))
                {
                    foreach (var (_, p) in extra.OrderBy(e => e.T))
                    {
                        poly.Add(p);
                    }
                }

                poly.Add(v[i]);
            }

            result.Add(poly);
        }

        return result;
    }

    private static void Add(Dictionary<(int A, int B), List<(double T, Point3 P)>> d, (int A, int B) key, double t, Point3 p)
    {
        if (!d.TryGetValue(key, out var list))
        {
            d[key] = list = new List<(double, Point3)>();
        }

        list.Add((t, p));
    }

    private static bool Intersect(Seg s1, Seg s2, out double t, out double u)
    {
        t = u = 0;
        double rx = s1.B.X - s1.A.X, ry = s1.B.Y - s1.A.Y;
        double sx = s2.B.X - s2.A.X, sy = s2.B.Y - s2.A.Y;
        double den = rx * sy - ry * sx;
        double scale = (Math.Abs(rx) + Math.Abs(ry)) * (Math.Abs(sx) + Math.Abs(sy));
        if (Math.Abs(den) <= 1e-12 * scale)
        {
            return false; // успоредни/колинеарни
        }

        double qx = s2.A.X - s1.A.X, qy = s2.A.Y - s1.A.Y;
        t = (qx * sy - qy * sx) / den;
        u = (qx * ry - qy * rx) / den;
        return t >= -Eps && t <= 1 + Eps && u >= -Eps && u <= 1 + Eps;
    }
}
