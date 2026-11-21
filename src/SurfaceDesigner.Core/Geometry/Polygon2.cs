namespace SurfaceDesigner.Core.Geometry;

/// <summary>Затворен многоъгълник в равнината (неизменяем).</summary>
public sealed class Polygon2
{
    private readonly Point2[] _v;

    public Polygon2(IEnumerable<Point2> vertices)
    {
        var list = new List<Point2>();
        foreach (var p in vertices)
        {
            if (list.Count > 0 && Point2.Distance(list[^1], p) < 1e-9)
            {
                continue;
            }

            list.Add(p);
        }

        if (list.Count > 1 && Point2.Distance(list[0], list[^1]) < 1e-9)
        {
            list.RemoveAt(list.Count - 1);
        }

        if (list.Count < 3)
        {
            throw new ArgumentException("Многоъгълникът трябва да има поне 3 различни върха.");
        }

        _v = list.ToArray();
        Bounds = Bounds2.FromPoints(_v);
    }

    public IReadOnlyList<Point2> Vertices => _v;
    public Bounds2 Bounds { get; }

    public double SignedArea
    {
        get
        {
            double s = 0;
            for (int i = 0, j = _v.Length - 1; i < _v.Length; j = i++)
            {
                s += _v[j].X * _v[i].Y - _v[i].X * _v[j].Y;
            }

            return s / 2;
        }
    }

    public double Area => Math.Abs(SignedArea);

    public double Perimeter
    {
        get
        {
            double s = 0;
            for (int i = 0, j = _v.Length - 1; i < _v.Length; j = i++)
            {
                s += Point2.Distance(_v[i], _v[j]);
            }

            return s;
        }
    }

    public Point2 Centroid
    {
        get
        {
            double a = 0, cx = 0, cy = 0;
            for (int i = 0, j = _v.Length - 1; i < _v.Length; j = i++)
            {
                double f = _v[j].X * _v[i].Y - _v[i].X * _v[j].Y;
                a += f;
                cx += (_v[j].X + _v[i].X) * f;
                cy += (_v[j].Y + _v[i].Y) * f;
            }

            if (Math.Abs(a) < 1e-12)
            {
                return new Point2(_v.Average(p => p.X), _v.Average(p => p.Y));
            }

            a *= 0.5;
            return new Point2(cx / (6 * a), cy / (6 * a));
        }
    }

    /// <summary>Проверка дали точка е вътре (метод на лъча).</summary>
    public bool Contains(double x, double y)
    {
        if (!Bounds.Contains(x, y))
        {
            return false;
        }

        bool inside = false;
        for (int i = 0, j = _v.Length - 1; i < _v.Length; j = i++)
        {
            var a = _v[i];
            var b = _v[j];
            if ((a.Y > y) != (b.Y > y) &&
                x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    public bool Contains(Point2 p) => Contains(p.X, p.Y);

    /// <summary>Най-близка точка от контура на многоъгълника и разстоянието до нея.</summary>
    public (Point2 Point, double Distance) NearestOnBoundary(double x, double y)
    {
        var p = new Point2(x, y);
        Point2 best = _v[0];
        double bestD2 = double.PositiveInfinity;
        for (int i = 0, j = _v.Length - 1; i < _v.Length; j = i++)
        {
            var q = ClosestOnSegment(_v[j], _v[i], p);
            double dx = q.X - x, dy = q.Y - y;
            double d2 = dx * dx + dy * dy;
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = q;
            }
        }

        return (best, Math.Sqrt(bestD2));
    }

    /// <summary>Точки по контура през не повече от <paramref name="step"/> (включително върховете).</summary>
    public IEnumerable<Point2> Densify(double step)
    {
        for (int i = 0; i < _v.Length; i++)
        {
            var a = _v[i];
            var b = _v[(i + 1) % _v.Length];
            yield return a;
            if (step <= 0)
            {
                continue;
            }

            int n = (int)Math.Floor(Point2.Distance(a, b) / step);
            for (int k = 1; k < n; k++)
            {
                yield return a + (b - a) * ((double)k / n);
            }
        }
    }

    public static Point2 ClosestOnSegment(Point2 a, Point2 b, Point2 p)
    {
        var ab = b - a;
        double len2 = Point2.Dot(ab, ab);
        if (len2 < 1e-24)
        {
            return a;
        }

        double t = Math.Clamp(Point2.Dot(p - a, ab) / len2, 0, 1);
        return a + ab * t;
    }
}
