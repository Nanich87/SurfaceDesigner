using System.Globalization;
using System.Text;
using SurfaceDesigner.Core.Geometry;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.Core.IO;

/// <summary>
/// Експорт в DXF (ASCII, R12 – чете се от AutoCAD, BricsCAD, Civil 3D, QGIS и др.).
/// Координати: X – изток, Y – север (както в чертежите в КС2005).
/// </summary>
public static class DxfWriter
{
    public const string LayerMinor = "HORIZONTALI";
    public const string LayerMajor = "HORIZONTALI_GLAVNI";
    public const string LayerLabels = "HORIZONTALI_KOTI";
    public const string DesignPrefix = "PROEKTNI_";
    public const string LayerBoundary = "GRANICI";
    public const string LayerTin = "TIN";
    public const string LayerPoints = "TOCHKI";
    public const string LayerBreaklines = "STRUKTURNI_LINII";

    /// <summary>
    /// Хоризонтали като 2D полилинии с кота (POLYLINE, елевация = котата) на слоеве за главни/второстепенни,
    /// надписи на котите по главните хоризонтали и границите на повърхнината.
    /// </summary>
    /// <param name="textHeight">Височина на надписите в единиците на чертежа (m); 0 – без надписи.</param>
    /// <param name="labelSpacing">Разстояние между надписите по една хоризонтала, m.</param>
    /// <param name="includeTriangles">Да се добавят ли и ребрата на триъгълниците (3D линии с коти) на слой TIN.</param>
    public static int WriteContours(
        string path,
        Surface surface,
        double interval,
        int majorEvery = 5,
        double textHeight = 1.0,
        double labelSpacing = 100,
        bool includeTriangles = false)
    {
        var lines = ContourGenerator.GeneratePolylines(surface, interval, majorEvery);

        // Проектните хоризонтали – на отделни слоеве в червено (ACI 1), съществуващите – в кафяво/оранжево
        string prefix = surface.IsDesign ? DesignPrefix : "";
        string minor = prefix + LayerMinor, major = prefix + LayerMajor, labels = prefix + LayerLabels;
        string tin = prefix + LayerTin;
        var w = new Writer();
        w.Header(surface.IsDesign
            ? new[] { (minor, 1), (major, 1), (labels, 1), (tin, 1), (LayerBoundary, 3) }
            : new[] { (minor, 33), (major, 30), (labels, 7), (tin, 8), (LayerBoundary, 3) });

        if (includeTriangles)
        {
            var v = surface.Vertices;
            foreach (var (a, b) in surface.GetEdges())
            {
                w.Line(tin, v[a], v[b]);
            }
        }

        foreach (var c in lines)
        {
            w.Polyline2D(c.IsMajor ? major : minor, c.Points, c.Level, c.IsClosed);
        }

        if (textHeight > 0)
        {
            foreach (var c in lines.Where(l => l.IsMajor))
            {
                foreach (var (p, angle) in LabelPositions(c.Points, labelSpacing, textHeight))
                {
                    w.Text(labels, p, c.Level, textHeight, angle, c.Level.ToString("0.##", CultureInfo.InvariantCulture));
                }
            }
        }

        foreach (var b in surface.Boundaries)
        {
            w.Polyline2D(LayerBoundary, b.Vertices, 0, true);
        }

        w.Footer();
        File.WriteAllText(path, w.ToString(), Encoding.ASCII);
        return lines.Count;
    }

    /// <summary>TIN като 3DFACE, точките като POINT, структурните линии като 3D полилинии и границите.</summary>
    public static void WriteTin(string path, Surface surface)
    {
        // проектният TIN – на отделен слой в червено
        string tin = surface.IsDesign ? DesignPrefix + LayerTin : LayerTin;
        var w = new Writer();
        w.Header(new[] { (tin, surface.IsDesign ? 1 : 8), (LayerPoints, 7), (LayerBreaklines, 6), (LayerBoundary, 3) });

        var v = surface.Vertices;
        var t = surface.Triangles;
        for (int k = 0; k < t.Length; k += 3)
        {
            w.Face3D(tin, v[t[k]], v[t[k + 1]], v[t[k + 2]]);
        }

        foreach (var p in surface.Points)
        {
            w.Point(LayerPoints, p);
        }

        foreach (var b in surface.Breaklines)
        {
            w.Polyline3D(LayerBreaklines, b.Vertices);
        }

        foreach (var b in surface.Boundaries)
        {
            w.Polyline2D(LayerBoundary, b.Vertices, 0, true);
        }

        w.Footer();
        File.WriteAllText(path, w.ToString(), Encoding.ASCII);
    }

    /// <summary>Места за надписи по линията: през <paramref name="spacing"/> m, по посоката на линията (четими – не надолу).</summary>
    private static IEnumerable<(Point2 Point, double Angle)> LabelPositions(IReadOnlyList<Point2> pts, double spacing, double textHeight)
    {
        double total = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            total += Point2.Distance(pts[i - 1], pts[i]);
        }

        if (total < textHeight * 4)
        {
            yield break; // твърде къса
        }

        double next = Math.Min(spacing / 2, total / 2);
        double s = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double len = Point2.Distance(pts[i - 1], pts[i]);
            while (len > 0 && next <= s + len)
            {
                var d = (pts[i] - pts[i - 1]) * (1 / len);
                var p = pts[i - 1] + d * (next - s);
                double angle = Math.Atan2(d.Y, d.X) * 180 / Math.PI;
                if (angle > 90)
                {
                    angle -= 180;
                }

                if (angle < -90)
                {
                    angle += 180;
                }

                yield return (p, angle);
                next += spacing;
            }

            s += len;
        }
    }

    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        private void G(int code, string value) => _sb.Append(code).Append('\n').Append(value).Append('\n');

        private void G(int code, double value) => G(code, value.ToString("0.0#####", Ci));

        private void G(int code, int value) => G(code, value.ToString(Ci));

        public void Header(IEnumerable<(string Name, int Color)> layers)
        {
            G(0, "SECTION");
            G(2, "HEADER");
            G(9, "$ACADVER");
            G(1, "AC1009");
            G(0, "ENDSEC");

            var list = layers.ToList();
            G(0, "SECTION");
            G(2, "TABLES");
            G(0, "TABLE");
            G(2, "LAYER");
            G(70, list.Count);
            foreach (var (name, color) in list)
            {
                G(0, "LAYER");
                G(2, name);
                G(70, 0);
                G(62, color);
                G(6, "CONTINUOUS");
            }

            G(0, "ENDTAB");
            G(0, "ENDSEC");

            G(0, "SECTION");
            G(2, "ENTITIES");
        }

        public void Footer()
        {
            G(0, "ENDSEC");
            G(0, "EOF");
        }

        public void Polyline2D(string layer, IReadOnlyList<Point2> pts, double elevation, bool closed)
        {
            int n = closed && pts.Count > 1 && pts[0] == pts[^1] ? pts.Count - 1 : pts.Count;
            G(0, "POLYLINE");
            G(8, layer);
            G(66, 1);
            G(10, 0.0);
            G(20, 0.0);
            G(30, elevation);
            G(70, closed ? 1 : 0);
            for (int i = 0; i < n; i++)
            {
                G(0, "VERTEX");
                G(8, layer);
                G(10, pts[i].X);
                G(20, pts[i].Y);
                G(30, elevation);
            }

            G(0, "SEQEND");
            G(8, layer);
        }

        public void Polyline3D(string layer, IReadOnlyList<Point3> pts)
        {
            G(0, "POLYLINE");
            G(8, layer);
            G(66, 1);
            G(10, 0.0);
            G(20, 0.0);
            G(30, 0.0);
            G(70, 8); // 3D полилиния
            foreach (var p in pts)
            {
                G(0, "VERTEX");
                G(8, layer);
                G(10, p.X);
                G(20, p.Y);
                G(30, p.Z);
                G(70, 32); // 3D връх
            }

            G(0, "SEQEND");
            G(8, layer);
        }

        public void Face3D(string layer, Point3 a, Point3 b, Point3 c)
        {
            G(0, "3DFACE");
            G(8, layer);
            G(10, a.X);
            G(20, a.Y);
            G(30, a.Z);
            G(11, b.X);
            G(21, b.Y);
            G(31, b.Z);
            G(12, c.X);
            G(22, c.Y);
            G(32, c.Z);
            G(13, c.X);
            G(23, c.Y);
            G(33, c.Z);
        }

        public void Line(string layer, Point3 a, Point3 b)
        {
            G(0, "LINE");
            G(8, layer);
            G(10, a.X);
            G(20, a.Y);
            G(30, a.Z);
            G(11, b.X);
            G(21, b.Y);
            G(31, b.Z);
        }

        public void Point(string layer, Point3 p)
        {
            G(0, "POINT");
            G(8, layer);
            G(10, p.X);
            G(20, p.Y);
            G(30, p.Z);
        }

        public void Text(string layer, Point2 p, double z, double height, double rotation, string text)
        {
            G(0, "TEXT");
            G(8, layer);
            G(10, p.X);
            G(20, p.Y);
            G(30, z);
            G(40, height);
            G(1, text);
            G(50, rotation);
            G(72, 1); // центрирано
            G(11, p.X);
            G(21, p.Y);
            G(31, z);
        }

        public override string ToString() => _sb.ToString();
    }
}
