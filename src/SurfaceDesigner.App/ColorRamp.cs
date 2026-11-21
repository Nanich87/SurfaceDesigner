using System.Windows.Media;

namespace SurfaceDesigner.App;

/// <summary>Цветови скали за оцветяване на повърхнини.</summary>
internal static class ColorRamp
{
    private static readonly (double T, Color C)[] ElevationStops =
    {
        (0.00, Color.FromRgb(0x2B, 0x83, 0xBA)),
        (0.25, Color.FromRgb(0xAB, 0xDD, 0xA4)),
        (0.50, Color.FromRgb(0xFF, 0xFF, 0xBF)),
        (0.75, Color.FromRgb(0xFD, 0xAE, 0x61)),
        (1.00, Color.FromRgb(0xA6, 0x4B, 0x2A)),
    };

    private static readonly (double T, Color C)[] SlopeStops =
    {
        (0.00, Color.FromRgb(0x1A, 0x96, 0x41)),
        (0.33, Color.FromRgb(0xA6, 0xD9, 0x6A)),
        (0.55, Color.FromRgb(0xFF, 0xFF, 0xBF)),
        (0.75, Color.FromRgb(0xFD, 0xAE, 0x61)),
        (1.00, Color.FromRgb(0xD7, 0x19, 0x1C)),
    };

    /// <summary>Наклон (%), при който цветът е най-червен.</summary>
    public const double MaxSlopePercent = 40;

    public static Color Elevation(double t) => Interpolate(ElevationStops, t);

    public static Color Slope(double percent) => Interpolate(SlopeStops, percent / MaxSlopePercent);

    /// <summary>Цвят за работна кота: червено – изкоп, синьо – насип.</summary>
    public static Color CutFill(double depth, double maxAbs)
    {
        double t = maxAbs > 0 ? Math.Clamp(Math.Abs(depth) / maxAbs, 0, 1) : 0;
        byte a = (byte)(60 + 170 * t);
        return depth < 0 ? Color.FromArgb(a, 0xD7, 0x30, 0x27) : Color.FromArgb(a, 0x31, 0x6B, 0xD6);
    }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Interpolate((double T, Color C)[] stops, double t)
    {
        t = double.IsNaN(t) ? 0 : Math.Clamp(t, 0, 1);
        for (int i = 1; i < stops.Length; i++)
        {
            if (t <= stops[i].T)
            {
                var (t0, c0) = stops[i - 1];
                var (t1, c1) = stops[i];
                double u = (t - t0) / (t1 - t0);
                return Color.FromRgb(
                    (byte)(c0.R + (c1.R - c0.R) * u),
                    (byte)(c0.G + (c1.G - c0.G) * u),
                    (byte)(c0.B + (c1.B - c0.B) * u));
            }
        }
        return stops[^1].C;
    }
}
