using System.Globalization;
using System.Text;
using SurfaceDesigner.Core.Geometry;

namespace SurfaceDesigner.Core.IO;

/// <summary>
/// Четене на точки от текстов файл. Поддържани редове:
/// <c>X Y Z</c> или <c>Номер X Y Z [код...]</c>; разделители – интервал, табулация, „;“ или „,“.
/// Редове, които не започват с числа (заглавия, коментари), се пропускат.
/// </summary>
public static class PointFileReader
{
    private static readonly char[] Separators = { ' ', '\t', ';' };

    /// <param name="geodetic">
    /// true – файлът е в геодезическа система (X – север, Y – изток); координатите се разменят,
    /// за да се получи вътрешната система (X – изток, Y – север).
    /// </param>
    public static List<Point3> Read(string path, bool geodetic = false) =>
        Parse(File.ReadLines(path, Encoding.UTF8), geodetic);

    public static List<Point3> Parse(IEnumerable<string> lines, bool geodetic = false)
    {
        var result = new List<Point3>();
        foreach (var raw in lines)
            if (TryParseLine(raw, geodetic, out var p)) result.Add(p);
        return result;
    }

    /// <summary>
    /// Чете полилинии (структурни линии): всяка е поредица от редове с координати; линиите се разделят
    /// с празен ред или ред без координати (напр. „END“, „Линия 2“). Затворена линия – последният връх = първия.
    /// </summary>
    public static List<List<Point3>> ReadPolylines(string path, bool geodetic = false) =>
        ParsePolylines(File.ReadLines(path, Encoding.UTF8), geodetic);

    public static List<List<Point3>> ParsePolylines(IEnumerable<string> lines, bool geodetic = false)
    {
        var result = new List<List<Point3>>();
        var current = new List<Point3>();
        foreach (var raw in lines)
        {
            if (TryParseLine(raw, geodetic, out var p))
            {
                current.Add(p);
                continue;
            }

            if (current.Count >= 2) result.Add(current);
            current = new List<Point3>();
        }

        if (current.Count >= 2) result.Add(current);
        return result;
    }

    private static bool TryParseLine(string raw, bool geodetic, out Point3 point)
    {
        point = default;
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) return false;

        string[] tokens = line.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1)
            tokens = line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        else
            tokens = tokens.Select(t => t.Replace(',', '.')).ToArray();

        if (!TryGetXyz(tokens, out double a, out double b, out double z)) return false;
        point = geodetic ? new Point3(b, a, z) : new Point3(a, b, z);
        return true;
    }

    private static bool TryGetXyz(string[] t, out double a, out double b, out double z)
    {
        a = b = z = 0;
        if (t.Length == 3)
            return P(t[0], out a) && P(t[1], out b) && P(t[2], out z);
        if (t.Length >= 4)
        {
            // Номер X Y Z [код] – номерът може да е и текст
            if (P(t[1], out a) && P(t[2], out b) && P(t[3], out z)) return true;

            // X Y Z код
            return P(t[0], out a) && P(t[1], out b) && P(t[2], out z);
        }

        return false;
    }

    private static bool P(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);
}

public static class PointFileWriter
{
    /// <summary>Записва точките като „Номер X Y Z“ (разделител – интервал).</summary>
    public static void Write(string path, IEnumerable<Point3> points, bool geodetic = false)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        int n = 1;
        foreach (var p in points)
        {
            double a = geodetic ? p.Y : p.X, b = geodetic ? p.X : p.Y;
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{n++} {a:F3} {b:F3} {p.Z:F3}"));
        }
    }
}
