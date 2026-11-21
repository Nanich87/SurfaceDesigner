using SurfaceDesigner.Core.Geometry;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.Core.Grading;

/// <summary>Създава проектна TIN повърхнина от съществуващия терен и платформите.</summary>
public static class DesignSurfaceBuilder
{
    private const double Tol = 1e-4;

    /// <param name="gridStep">Стъпка на мрежата за откосите и вътрешността на платформите, m.</param>
    /// <param name="gradingZoneOnly">
    /// true (по подразбиране) – повърхнината покрива само зоната на планирането (платформи и откоси до линията
    /// на догонване) и се ограничава с външна граница по нея; false – пълно копие на терена с вградения проект.
    /// </param>
    public static Surface Build(
        Surface existing,
        IReadOnlyList<Platform> platforms,
        double gridStep,
        string name,
        bool gradingZoneOnly = true)
    {
        if (gridStep <= 0) throw new ArgumentOutOfRangeException(nameof(gridStep));
        var model = new GradingModel(existing, platforms);
        var points = new List<Point3>();

        // 1. Върховете на TIN на терена, които не са засегнати от планирането – само при пълно копие на терена
        if (!gradingZoneOnly)
        {
            foreach (var p in existing.Vertices)
            {
                if (model.IsInsidePlatform(p.X, p.Y)) continue;
                var dz = model.DesignElevation(p.X, p.Y);
                if (dz is null || Math.Abs(dz.Value - p.Z) > Tol) continue;
                points.Add(p);
            }
        }

        // 2. Структурни линии: запазените участъци от линиите на терена + контурите на платформите (ръб/кант)
        bool Affected(Point3 p)
        {
            if (model.IsInsidePlatform(p.X, p.Y)) return true;
            var dz = model.DesignElevation(p.X, p.Y);
            return dz is null || Math.Abs(dz.Value - p.Z) > Tol;
        }

        var breaklines = gradingZoneOnly
            ? new List<Breakline>()
            : existing.Breaklines.SelectMany(b => b.RemoveVertices(Affected)).ToList();
        var platformRings = new List<List<Point3>>();

        foreach (var pl in platforms.Where(p => p.Boundary.Count >= 3))
        {
            var ring = new List<Point3>();
            foreach (var v in pl.GetPolygon().Densify(gridStep))
            {
                var z = model.DesignElevation(v.X, v.Y);
                if (z.HasValue) ring.Add(new Point3(v.X, v.Y, z.Value));
            }

            if (ring.Count < 3) continue;
            platformRings.Add(ring.ToList());
            ring.Add(ring[0]);
            breaklines.Add(new Breakline($"Ръб: {pl.Name}", ring));
        }

        var zoneBoundaries = new List<SurfaceBoundary>();

        // 3. Мрежа в зоната на въздействие – вътрешност на платформите и откосите
        var region = model.InfluenceBounds;
        if (!region.IsEmpty)
        {
            double x0 = Math.Floor(region.MinX / gridStep) * gridStep;
            double y0 = Math.Floor(region.MinY / gridStep) * gridStep;
            int nx = (int)Math.Ceiling((region.MaxX - x0) / gridStep) + 1;
            int ny = (int)Math.Ceiling((region.MaxY - y0) / gridStep) + 1;
            if ((long)nx * ny > 10_000_000)
                throw new InvalidOperationException("Твърде малка стъпка на мрежата за тази площ.");

            // Състояние на възлите: 0 – няма данни, 1 – непроменен терен, 2 – променен (откос/платформа)
            var state = new byte[nx * ny];
            var z = new double[nx * ny];
            Parallel.For(0, ny, iy =>
            {
                double y = y0 + iy * gridStep;
                for (int ix = 0; ix < nx; ix++)
                {
                    double x = x0 + ix * gridStep;
                    int k = iy * nx + ix;
                    var dz = model.DesignElevation(x, y);
                    if (dz is null) continue;
                    z[k] = dz.Value;
                    if (model.IsInsidePlatform(x, y)) { state[k] = 3; continue; }
                    var eg = existing.ElevationAt(x, y);
                    state[k] = (byte)(eg.HasValue && Math.Abs(dz.Value - eg.Value) > Tol ? 2 : 1);
                }
            });

            // Свързани зони на планирането (8-съседство на променените възли) – по една външна граница на зона
            var label = new int[nx * ny];
            Array.Fill(label, -1);
            int zones = 0;
            if (gradingZoneOnly)
            {
                var stack = new Stack<int>();
                for (int k0 = 0; k0 < state.Length; k0++)
                {
                    if (state[k0] < 2 || label[k0] >= 0) continue;
                    label[k0] = zones;
                    stack.Push(k0);
                    while (stack.Count > 0)
                    {
                        int k = stack.Pop(), cx = k % nx, cy = k / nx;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int jx = cx + dx, jy = cy + dy;
                                if (jx < 0 || jy < 0 || jx >= nx || jy >= ny) continue;
                                int j = jy * nx + jx;
                                if (state[j] >= 2 && label[j] < 0) { label[j] = zones; stack.Push(j); }
                            }
                        }
                    }

                    zones++;
                }
            }

            var hullPoints = Enumerable.Range(0, zones).Select(_ => new List<Point2>()).ToArray();

            var rows = new List<Point3>[ny];
            var rowHull = new List<(int Zone, Point2 P)>[ny];
            Parallel.For(0, ny, iy =>
            {
                var row = new List<Point3>();
                var hull = new List<(int, Point2)>();
                double y = y0 + iy * gridStep;
                for (int ix = 0; ix < nx; ix++)
                {
                    int k = iy * nx + ix;
                    if (state[k] == 0) continue;
                    double x = x0 + ix * gridStep;

                    // Откосите се включват изцяло; вътрешността на платформата (равнина) – разредено
                    // и по края; непроменен терен – само ако граничи с променената зона.
                    bool include = state[k] == 2 || (state[k] == 3 && ix % 5 == 0 && iy % 5 == 0);
                    for (int dy = -1; dy <= 1 && !include; dy++)
                    {
                        for (int dx = -1; dx <= 1 && !include; dx++)
                        {
                            int jx = ix + dx, jy = iy + dy;
                            if (jx < 0 || jy < 0 || jx >= nx || jy >= ny) continue;
                            byte s = state[jy * nx + jx];
                            include = state[k] == 3 ? s != 3 : s >= 2;
                        }
                    }

                    if (include) row.Add(new Point3(x, y, z[k]));
                    if (label[k] >= 0) hull.Add((label[k], new Point2(x, y)));

                    // Линия на догонване: бисекция между променен и непроменен съседен възел
                    if (state[k] == 2)
                    {
                        void Daylight(double xo, double yo)
                        {
                            // позицията на догонването влиза в контура на зоната винаги – и когато съвпада с възел
                            if (AddDaylight(model, existing, row, x, y, xo, yo) is { } d && label[k] >= 0) hull.Add((label[k], d));
                        }

                        if (ix + 1 < nx && state[k + 1] == 1) Daylight(x + gridStep, y);
                        if (ix > 0 && state[k - 1] == 1) Daylight(x - gridStep, y);
                        if (iy + 1 < ny && state[k + nx] == 1) Daylight(x, y + gridStep);
                        if (iy > 0 && state[k - nx] == 1) Daylight(x, y - gridStep);
                    }
                }

                rows[iy] = row;
                rowHull[iy] = hull;
            });
            foreach (var r in rows) points.AddRange(r);

            if (gradingZoneOnly)
            {
                foreach (var h in rowHull)
                    foreach (var (zone, p) in h) hullPoints[zone].Add(p);

                // ръбовете на платформите – към зоната на най-близкия променен възел
                foreach (var ring in platformRings)
                {
                    foreach (var v in ring)
                    {
                        int cx = (int)Math.Round((v.X - x0) / gridStep), cy = (int)Math.Round((v.Y - y0) / gridStep);
                        int zone = -1;
                        for (int dy = -1; dy <= 1 && zone < 0; dy++)
                        {
                            for (int dx = -1; dx <= 1 && zone < 0; dx++)
                            {
                                int jx = cx + dx, jy = cy + dy;
                                if (jx >= 0 && jy >= 0 && jx < nx && jy < ny) zone = label[jy * nx + jx];
                            }
                        }

                        if (zone >= 0) hullPoints[zone].Add(v.XY);
                    }
                }

                // контур на всяка зона – вдлъбната обвивка, която следва линията на догонване
                for (int zi = 0; zi < zones; zi++)
                {
                    if (hullPoints[zi].Count < 3) continue;
                    var ring = DataHull.Compute(hullPoints[zi], 3 * gridStep);
                    if (ring.Count >= 3)
                    {
                        zoneBoundaries.Add(new SurfaceBoundary(
                            zones == 1 ? "Зона на планирането" : $"Зона на планирането {zi + 1}", BoundaryType.Outer, ring));
                    }
                }
            }
        }

        var design = new Surface(name, points, breaklines) { MaxEdgeLength = existing.MaxEdgeLength, IsDesign = true };
        design.GradingSource = new GradingSource(existing.Name, gridStep, gradingZoneOnly); // връзка за автоматично преизчисляване
        design.ContourInterval = existing.ContourInterval;
        design.MajorContourEvery = existing.MajorContourEvery;
        if (gradingZoneOnly)
        {
            // Само зоната на планирането: външни граници по линията на догонване + скриващите/показващите на терена
            design.SetBoundaries(zoneBoundaries.Concat(existing.Boundaries.Where(b => b.Type != BoundaryType.Outer)));
            return design;
        }

        // Границите на терена се пренасят (иначе TIN би „затворил“ дупките и вдлъбнатините)
        design.SetBoundaries(existing.Boundaries);

        // Ръчните завъртания на ребра – само там, където теренът не е засегнат
        design.SetEdgeSwaps(existing.EdgeSwaps.Where(e =>
        {
            var za = existing.ElevationAt(e.A.X, e.A.Y);
            var zb = existing.ElevationAt(e.B.X, e.B.Y);
            return za.HasValue && zb.HasValue &&
                   !Affected(new Point3(e.A.X, e.A.Y, za.Value)) && !Affected(new Point3(e.B.X, e.B.Y, zb.Value));
        }));
        return design;
    }

    /// <summary>
    /// Преизчислява свързана проектна повърхнина по нейните параметри (<see cref="Surface.GradingSource"/>)
    /// и текущите терен и платформи. Връща новото съдържание или null, ако теренът липсва.
    /// </summary>
    public static Surface? Rebuild(Surface design, Surface existing, IReadOnlyList<Platform> platforms)
    {
        var src = design.GradingSource ?? throw new InvalidOperationException("Повърхнината не е свързана с платформите.");
        if (existing.TriangleCount == 0) return null;
        var result = Build(existing, platforms, src.GridStep, design.Name, src.ZoneOnly);
        result.EnsureTriangulated();
        return result;
    }

    /// <summary>
    /// Намира с бисекция точката на догонване между променен (In) и непроменен (Out) възел.
    /// Добавя я като връх, ако е между възлите; връща позицията ѝ (или null при липса на данни).
    /// </summary>
    private static Point2? AddDaylight(
        GradingModel model,
        Surface existing,
        List<Point3> output,
        double xIn,
        double yIn,
        double xOut,
        double yOut)
    {
        double t0 = 0, t1 = 1; // t0 – променено, t1 – непроменено
        for (int i = 0; i < 12; i++)
        {
            double t = (t0 + t1) / 2;
            double x = xIn + (xOut - xIn) * t, y = yIn + (yOut - yIn) * t;
            var dz = model.DesignElevation(x, y);
            var eg = existing.ElevationAt(x, y);
            if (dz is null || eg is null) return null;
            if (Math.Abs(dz.Value - eg.Value) > Tol) t0 = t; else t1 = t;
        }

        if (t1 >= 0.999) return new Point2(xOut, yOut); // догонването е във външния възел (той вече е в TIN)
        double xf = xIn + (xOut - xIn) * t1, yf = yIn + (yOut - yIn) * t1;
        var zf = existing.ElevationAt(xf, yf);
        if (zf.HasValue && t1 > 0.001) output.Add(new Point3(xf, yf, zf.Value));
        return new Point2(xf, yf);
    }
}
