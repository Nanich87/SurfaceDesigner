using SurfaceDesigner.Core.Geometry;

namespace SurfaceDesigner.Core.Surfaces;

/// <summary>Точка от профил: километраж (хоризонтално разстояние от началото), положение и кота (null – извън повърхнината).</summary>
public readonly record struct ProfilePoint(double Station, double X, double Y, double? Z);

/// <summary>Напречен разрез: километраж по трасето и отсечката на разреза (отляво надясно по посоката на трасето).</summary>
public sealed record CrossSectionLine(double Station, Point2 Center, Point2 Left, Point2 Right, double LeftWidth, double RightWidth);

/// <summary>Площи на изкоп и насип в напречен разрез.</summary>
public readonly record struct SectionAreas(double Cut, double Fill);

/// <summary>Трасе – полилиния в план, по която се правят профили и разрези.</summary>
public static class Alignment
{
    public static double Length(IReadOnlyList<Point2> line)
    {
        double s = 0;
        for (int i = 1; i < line.Count; i++)
        {
            s += Point2.Distance(line[i - 1], line[i]);
        }

        return s;
    }

    /// <summary>Точка и единична посока на трасето при километраж <paramref name="station"/>.</summary>
    public static (Point2 Point, Point2 Direction) PointAt(IReadOnlyList<Point2> line, double station)
    {
        double s = 0;
        for (int i = 1; i < line.Count; i++)
        {
            double len = Point2.Distance(line[i - 1], line[i]);
            if (len <= 0)
            {
                continue;
            }

            if (station <= s + len || i == line.Count - 1)
            {
                var dir = (line[i] - line[i - 1]) * (1 / len);
                double t = Math.Clamp(station - s, 0, len);
                return (line[i - 1] + dir * t, dir);
            }

            s += len;
        }

        return (line[^1], new Point2(1, 0));
    }

    /// <summary>
    /// Напречни разрези през <paramref name="interval"/> m (плюс началото и края), перпендикулярни на трасето.
    /// </summary>
    public static List<CrossSectionLine> CrossSections(IReadOnlyList<Point2> line, double interval, double leftWidth, double rightWidth)
    {
        if (interval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        double total = Length(line);
        var stations = new List<double>();
        for (double s = 0; s < total - 1e-9; s += interval)
        {
            stations.Add(s);
        }

        stations.Add(total);

        return stations.Select(s =>
        {
            var (p, d) = PointAt(line, s);
            var left = new Point2(-d.Y, d.X); // нормала наляво
            return new CrossSectionLine(s, p, p + left * leftWidth, p - left * rightWidth, leftWidth, rightWidth);
        }).ToList();
    }

    /// <summary>Километражът във формат 0+000.00 (km+m).</summary>
    public static string FormatStation(double station)
    {
        int km = (int)Math.Floor(station / 1000);
        double m = station - km * 1000;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{km}+{m:000.00}");
    }
}

public static class ProfileSampler
{
    /// <summary>
    /// Профил на повърхнината по полилинията. Точки има във всички върхове на полилинията и във всички
    /// пресичания с ребра на TIN – между две съседни точки повърхнината е линейна, т.е. профилът е точен.
    /// Прекъсванията (дупки, извън повърхнината) са точки с Z = null.
    /// </summary>
    public static List<ProfilePoint> Sample(Surface surface, IReadOnlyList<Point2> line)
    {
        var result = new List<ProfilePoint>();
        if (line.Count < 2)
        {
            return result;
        }

        var v = surface.Vertices;
        var edges = surface.GetEdges();
        double station0 = 0;

        for (int i = 1; i < line.Count; i++)
        {
            var a = line[i - 1];
            var b = line[i];
            var d = b - a;
            double len = d.Length;
            if (len <= 0)
            {
                continue;
            }

            var ts = new List<double> { 0, 1 };
            double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X), minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            foreach (var (ia, ib) in edges)
            {
                var p = v[ia].XY;
                var q = v[ib].XY;
                if (Math.Max(p.X, q.X) < minX || Math.Min(p.X, q.X) > maxX || Math.Max(p.Y, q.Y) < minY || Math.Min(p.Y, q.Y) > maxY)
                {
                    continue;
                }

                var r = q - p;
                double den = Point2.Cross(d, r);
                if (Math.Abs(den) < 1e-12 * len * r.Length)
                {
                    continue;
                }

                var w = p - a;
                double t = Point2.Cross(w, r) / den;
                double u = Point2.Cross(w, d) / den;
                if (t > 0 && t < 1 && u >= -1e-12 && u <= 1 + 1e-12)
                {
                    ts.Add(t);
                }
            }

            ts.Sort();

            double? prevT = null;
            foreach (double t in ts)
            {
                if (prevT.HasValue && (t - prevT.Value) * len < 1e-6)
                {
                    continue;
                }

                if (prevT.HasValue)
                {
                    // дупка между две точки (напр. скрита зона) – отбелязваме прекъсване
                    double tm = (prevT.Value + t) / 2;
                    var pm = a + d * tm;
                    if (surface.ElevationAt(pm.X, pm.Y) is null && result.Count > 0 && result[^1].Z is not null)
                    {
                        result.Add(new ProfilePoint(station0 + tm * len, pm.X, pm.Y, null));
                    }
                }

                if (i > 1 && t == 0)
                {
                    prevT = t;
                    continue;
                } // общ връх с предишната отсечка

                var pt = a + d * t;
                result.Add(new ProfilePoint(station0 + t * len, pt.X, pt.Y, surface.ElevationAt(pt.X, pt.Y)));
                prevT = t;
            }

            station0 += len;
        }

        return result;
    }

    /// <summary>Кота в километраж <paramref name="station"/> чрез линейна интерполация по профила (null – прекъсване).</summary>
    public static double? ElevationAtStation(IReadOnlyList<ProfilePoint> profile, double station)
    {
        for (int i = 1; i < profile.Count; i++)
        {
            if (station > profile[i].Station)
            {
                continue;
            }

            var p0 = profile[i - 1];
            var p1 = profile[i];
            if (p0.Z is null || p1.Z is null)
            {
                return station == p1.Station ? p1.Z : station == p0.Station ? p0.Z : null;
            }

            double span = p1.Station - p0.Station;
            double t = span > 0 ? (station - p0.Station) / span : 0;
            return p0.Z + (p1.Z - p0.Z) * Math.Clamp(t, 0, 1);
        }

        return profile.Count > 0 && Math.Abs(profile[^1].Station - station) < 1e-9 ? profile[^1].Z : null;
    }

    /// <summary>
    /// Площи на изкоп и насип между съществуваща и проектна повърхнина в разреза (точно интегриране,
    /// включително точките, в които двете линии се пресичат). Участъци без данни за някоя от тях се пропускат.
    /// </summary>
    public static SectionAreas Areas(IReadOnlyList<ProfilePoint> existing, IReadOnlyList<ProfilePoint> design)
    {
        var stations = existing.Select(p => p.Station).Concat(design.Select(p => p.Station))
            .Distinct().OrderBy(s => s).ToList();
        double cut = 0, fill = 0;
        for (int i = 1; i < stations.Count; i++)
        {
            double s0 = stations[i - 1], s1 = stations[i];
            double sm = (s0 + s1) / 2;

            // и двете повърхнини трябва да са дефинирани в целия интервал
            if (ElevationAtStation(existing, sm) is null || ElevationAtStation(design, sm) is null)
            {
                continue;
            }

            var e0 = ElevationAtStation(existing, s0);
            var e1 = ElevationAtStation(existing, s1);
            var d0 = ElevationAtStation(design, s0);
            var d1 = ElevationAtStation(design, s1);
            if (e0 is null || e1 is null || d0 is null || d1 is null)
            {
                continue;
            }

            double h0 = d0.Value - e0.Value, h1 = d1.Value - e1.Value; // + насип, − изкоп
            double w = s1 - s0;
            if (h0 * h1 >= 0)
            {
                double area = (h0 + h1) / 2 * w;
                if (area > 0)
                {
                    fill += area;
                }
                else
                {
                    cut -= area;
                }
            }
            else
            {
                double sx = w * h0 / (h0 - h1); // пресичане на линиите
                double a1 = h0 / 2 * sx, a2 = h1 / 2 * (w - sx);
                if (a1 > 0)
                {
                    fill += a1;
                }
                else
                {
                    cut -= a1;
                }

                if (a2 > 0)
                {
                    fill += a2;
                }
                else
                {
                    cut -= a2;
                }
            }
        }

        return new SectionAreas(cut, fill);
    }
}
