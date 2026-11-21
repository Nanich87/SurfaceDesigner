namespace SurfaceDesigner.Core.Geometry;

/// <summary>Точка в равнината (X – изток, Y – север).</summary>
public readonly record struct Point2(double X, double Y)
{
    public static Point2 operator +(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);

    public static Point2 operator -(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);

    public static Point2 operator *(Point2 a, double k) => new(a.X * k, a.Y * k);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public static double Distance(Point2 a, Point2 b) => (a - b).Length;

    public static double Dot(Point2 a, Point2 b) => a.X * b.X + a.Y * b.Y;

    public static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;

    /// <inheritdoc/>
    public override string ToString() => $"({X:F3}; {Y:F3})";
}

/// <summary>Пространствена точка (X, Y – план, Z – кота).</summary>
public readonly record struct Point3(double X, double Y, double Z)
{
    public Point2 XY => new(X, Y);

    /// <inheritdoc/>
    public override string ToString() => $"({X:F3}; {Y:F3}; {Z:F3})";
}

/// <summary>Правоъгълна обхващаща област.</summary>
public readonly record struct Bounds2(double MinX, double MinY, double MaxX, double MaxY)
{
    public static Bounds2 Empty => new(
        double.PositiveInfinity,
        double.PositiveInfinity,
        double.NegativeInfinity,
        double.NegativeInfinity);

    public bool IsEmpty => MinX > MaxX || MinY > MaxY;
    public double Width => IsEmpty ? 0 : MaxX - MinX;
    public double Height => IsEmpty ? 0 : MaxY - MinY;
    public Point2 Center => new((MinX + MaxX) / 2, (MinY + MaxY) / 2);

    public Bounds2 Include(double x, double y) =>
        new(Math.Min(MinX, x), Math.Min(MinY, y), Math.Max(MaxX, x), Math.Max(MaxY, y));

    public Bounds2 Include(Point2 p) => Include(p.X, p.Y);

    public Bounds2 Union(Bounds2 o) =>
        o.IsEmpty ? this : IsEmpty ? o : new(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));

    public Bounds2 Intersect(Bounds2 o) =>
        new(Math.Max(MinX, o.MinX), Math.Max(MinY, o.MinY), Math.Min(MaxX, o.MaxX), Math.Min(MaxY, o.MaxY));

    public Bounds2 Inflate(double d) => IsEmpty ? this : new(MinX - d, MinY - d, MaxX + d, MaxY + d);

    public bool Contains(double x, double y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

    public static Bounds2 FromPoints(IEnumerable<Point2> points)
    {
        var b = Empty;
        foreach (var p in points)
        {
            b = b.Include(p);
        }

        return b;
    }
}
