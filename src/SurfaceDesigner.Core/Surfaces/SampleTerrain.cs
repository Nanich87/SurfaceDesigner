using SurfaceDesigner.Core.Geometry;

namespace SurfaceDesigner.Core.Surfaces;

/// <summary>Генератор на примерен (синтетичен) терен за тестове и демонстрация.</summary>
public static class SampleTerrain
{
    public static Surface Create(string name = "Терен (пример)", double size = 200, int pointCount = 1500, int seed = 42)
    {
        var rnd = new Random(seed);
        var pts = new List<Point3>(pointCount + 4);

        double H(double x, double y)
        {
            double u = x / size, v = y / size;
            return 100
                   + 6 * u + 3 * v // общ наклон
                   + 4 * Math.Exp(-((u - 0.3) * (u - 0.3) + (v - 0.7) * (v - 0.7)) / 0.02) // хълм
                   - 3 * Math.Exp(-((u - 0.75) * (u - 0.75) + (v - 0.3) * (v - 0.3)) / 0.03) // долина
                   + 0.6 * Math.Sin(u * 9) * Math.Cos(v * 7);
        }

        foreach (var (x, y) in new[] { (0.0, 0.0), (size, 0.0), (size, size), (0.0, size) })
        {
            pts.Add(new Point3(x, y, H(x, y)));
        }

        for (int i = 0; i < pointCount; i++)
        {
            double x = rnd.NextDouble() * size, y = rnd.NextDouble() * size;
            pts.Add(new Point3(x, y, Math.Round(H(x, y), 3)));
        }

        return new Surface(name, pts);
    }
}
