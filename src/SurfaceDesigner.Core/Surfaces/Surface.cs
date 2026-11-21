using SurfaceDesigner.Core.Geometry;

namespace SurfaceDesigner.Core.Surfaces;

/// <summary>
/// Триъгълна нерегулярна мрежа (TIN) – повърхнина, зададена с точки и структурни линии.
/// Триангулацията (ограничена триангулация на Делоне) се изгражда автоматично при първа нужда след промяна.
/// Класът е безопасен за паралелно четене (ElevationAt), но не и за едновременно четене и промяна.
/// </summary>
/// <remarks>
/// <see cref="Points"/> са редактируемите точки на повърхнината. <see cref="Vertices"/> са всички върхове
/// на TIN – точките (със същите индекси), следвани от върховете на структурните линии;
/// <see cref="Triangles"/> сочи към <see cref="Vertices"/>.
/// </remarks>
public sealed class Surface
{
    private readonly List<Point3> _points = new();
    private readonly List<Breakline> _breaklines = new();
    private readonly List<SurfaceBoundary> _boundaries = new();
    private readonly List<EdgeSwap> _edgeSwaps = new();
    private readonly object _sync = new();
    private double _maxEdgeLength;
    private volatile TinData? _tin;

    public Surface(string name) => Name = name;

    public Surface(string name, IEnumerable<Point3> points) : this(name) => _points.AddRange(points);

    public Surface(string name, IEnumerable<Point3> points, IEnumerable<Breakline> breaklines) : this(name, points) =>
        _breaklines.AddRange(breaklines);

    public string Name { get; set; }

    /// <summary>Проектна повърхнина – изобразява се с червени ребра и хоризонтали (не влияе на TIN).</summary>
    public bool IsDesign { get; set; }

    /// <summary>Дали повърхнината се показва в плана (не влияе на изчисленията).</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Връзка с терена и платформите – при наличие повърхнината се преизчислява автоматично.</summary>
    public Grading.GradingSource? GradingSource { get; set; }

    private double _contourInterval = 0.5;
    private int _majorContourEvery = 5;

    /// <summary>Стъпка на хоризонталите на тази повърхнина, m (изобразяване и експорт).</summary>
    public double ContourInterval
    {
        get => _contourInterval;
        set => _contourInterval = value > 0 && double.IsFinite(value) ? value : _contourInterval;
    }

    /// <summary>Всяка N-та хоризонтала е главна (удебелена и надписана).</summary>
    public int MajorContourEvery
    {
        get => _majorContourEvery;
        set => _majorContourEvery = Math.Max(1, value);
    }

    /// <summary>
    /// Действителна стъпка: зададената, но не по-гъста от <paramref name="maxLevels"/> хоризонтали
    /// за денивелацията на повърхнината (тогава – най-близката по-голяма „кръгла“ стъпка).
    /// </summary>
    public double EffectiveContourInterval(int maxLevels = 500)
    {
        if (TriangleCount == 0) return ContourInterval;
        var st = GetStatistics();
        double range = st.MaxZ - st.MinZ;
        if (!(range > 0) || range / ContourInterval <= maxLevels) return ContourInterval;
        return ContourGenerator.NiceStepAtLeast(range / maxLevels);
    }

    /// <summary>Брояч на промените в данните (точки, линии, граници, завъртания, параметри на триангулацията).</summary>
    public int Version { get; private set; }

    /// <summary>Триъгълници с ребро по-дълго от тази стойност се отстраняват (0 = без ограничение).
    /// Ребрата по структурни линии не се ограничават.</summary>
    public double MaxEdgeLength
    {
        get => _maxEdgeLength;
        set { _maxEdgeLength = Math.Max(0, value); Invalidate(); }
    }

    public IReadOnlyList<Point3> Points => _points;

    public IReadOnlyList<Breakline> Breaklines => _breaklines;

    /// <summary>Граници (външни/скриващи/показващи), прилагани по ред.</summary>
    public IReadOnlyList<SurfaceBoundary> Boundaries => _boundaries;

    /// <summary>Върхове на TIN (точки + върхове на структурните линии).</summary>
    public IReadOnlyList<Point3> Vertices => Tin.Vertices;

    /// <summary>Индекси във <see cref="Vertices"/> – по три за триъгълник.</summary>
    public int[] Triangles => Tin.Triangles;

    public int TriangleCount => Tin.Triangles.Length / 3;

    public Bounds2 Bounds => Tin.Bounds;

    /// <summary>Брой отсечки от структурни линии, които не са могли да се вмъкнат в TIN.</summary>
    public int FailedBreaklineSegments => Tin.FailedConstraints;

    // ---------- промени на точки ----------

    public int AddPoint(Point3 p)
    {
        _points.Add(p);
        Invalidate();
        return _points.Count - 1;
    }

    public void AddPoints(IEnumerable<Point3> points)
    {
        _points.AddRange(points);
        Invalidate();
    }

    public void SetPoint(int index, Point3 p)
    {
        _points[index] = p;
        Invalidate();
    }

    public void RemovePointAt(int index)
    {
        _points.RemoveAt(index);
        Invalidate();
    }

    /// <summary>Премахва всички точки, отговарящи на условието. Връща броя премахнати.</summary>
    public int RemovePoints(Predicate<Point3> match)
    {
        int n = _points.RemoveAll(match);
        if (n > 0) Invalidate();
        return n;
    }

    /// <summary>Заменя всички точки с резултата от функцията.</summary>
    public void TransformPoints(Func<Point3, Point3> transform)
    {
        for (int i = 0; i < _points.Count; i++) _points[i] = transform(_points[i]);
        Invalidate();
    }

    public void Clear()
    {
        _points.Clear();
        _breaklines.Clear();
        _boundaries.Clear();
        _edgeSwaps.Clear();
        Invalidate();
    }

    // ---------- промени на структурни линии ----------

    public void AddBreakline(Breakline line)
    {
        _breaklines.Add(line);
        Invalidate();
    }

    public void RemoveBreaklineAt(int index)
    {
        _breaklines.RemoveAt(index);
        Invalidate();
    }

    public void SetBreakline(int index, Breakline line)
    {
        _breaklines[index] = line;
        Invalidate();
    }

    public void SetBreaklines(IEnumerable<Breakline> lines)
    {
        var copy = lines.ToList();
        _breaklines.Clear();
        _breaklines.AddRange(copy);
        Invalidate();
    }

    // ---------- промени на граници ----------

    public void AddBoundary(SurfaceBoundary boundary)
    {
        _boundaries.Add(boundary);
        Invalidate();
    }

    public void RemoveBoundaryAt(int index)
    {
        _boundaries.RemoveAt(index);
        Invalidate();
    }

    public void SetBoundary(int index, SurfaceBoundary boundary)
    {
        _boundaries[index] = boundary;
        Invalidate();
    }

    public void SetBoundaries(IEnumerable<SurfaceBoundary> boundaries)
    {
        var copy = boundaries.ToList();
        _boundaries.Clear();
        _boundaries.AddRange(copy);
        Invalidate();
    }

    // ---------- завъртане на ребра ----------

    /// <summary>Завъртания на ребра (размяна на диагонала), прилагани по ред след триангулацията.</summary>
    public IReadOnlyList<EdgeSwap> EdgeSwaps => _edgeSwaps;

    /// <summary>Брой завъртания, които не могат да се приложат към текущата триангулация (напр. след промяна на точките).</summary>
    public int FailedEdgeSwaps => Tin.FailedSwaps.Count;

    public void SetEdgeSwaps(IEnumerable<EdgeSwap> swaps)
    {
        var copy = swaps.ToList();
        _edgeSwaps.Clear();
        _edgeSwaps.AddRange(copy);
        Invalidate();
    }

    /// <summary>Премахва неприложимите завъртания. Връща броя премахнати.</summary>
    public int RemoveFailedEdgeSwaps()
    {
        var failed = Tin.FailedSwaps;
        if (failed.Count == 0) return 0;
        var set = new HashSet<int>(failed);
        var keep = _edgeSwaps.Where((_, i) => !set.Contains(i)).ToList();
        _edgeSwaps.Clear();
        _edgeSwaps.AddRange(keep);
        Invalidate();
        return set.Count;
    }

    /// <summary>Най-близкото ребро на триъгълника под точката (индекси във <see cref="Vertices"/>).</summary>
    public bool TryFindEdge(double x, double y, double maxDistance, out int a, out int b)
    {
        a = b = -1;
        int t = FindTriangle(x, y);
        if (t < 0) return false;
        var v = Tin.Vertices;
        var tri = Tin.Triangles;
        var p = new Point2(x, y);
        double best = maxDistance;
        for (int e = 0; e < 3; e++)
        {
            int i = tri[3 * t + e], j = tri[3 * t + (e + 1) % 3];
            double d = Point2.Distance(p, Polygon2.ClosestOnSegment(v[i].XY, v[j].XY, p));
            if (d <= best) { best = d; a = i; b = j; }
        }
        return a >= 0;
    }

    /// <summary>
    /// Проверява дали реброто a–b може да се завърти. При успех връща новия диагонал (r, t).
    /// </summary>
    public bool CanSwapEdge(int a, int b, out int r, out int t, out string? reason)
    {
        r = t = -1;
        var tin = Tin;
        var key = ConstrainedDelaunay.Key(a, b);
        if (tin.Constrained.Contains(key)) { reason = "Реброто лежи върху структурна линия или граница."; return false; }
        if (a >= tin.OriginalVertexCount || b >= tin.OriginalVertexCount) { reason = "Реброто е създадено от граница на повърхнината."; return false; }
        if (!tin.EdgeTriangles.TryGetValue(key, out var pair) || pair.T2 < 0)
        {
            reason = "Реброто е по външния контур – има само един съседен триъгълник.";
            return false;
        }
        r = Opposite(tin, pair.T1, a, b);
        t = Opposite(tin, pair.T2, a, b);
        if (r >= tin.OriginalVertexCount || t >= tin.OriginalVertexCount)
        {
            reason = "Съседен триъгълник е създаден от граница на повърхнината.";
            return false;
        }
        var v = tin.Vertices;
        if (!ProperlyCross(v[a].XY, v[b].XY, v[r].XY, v[t].XY))
        {
            reason = "Двата триъгълника образуват неизпъкнал четириъгълник – реброто не може да се завърти.";
            return false;
        }
        reason = null;
        return true;
    }

    /// <summary>Завърта реброто a–b (добавя операция в дефиницията на повърхнината).</summary>
    public bool SwapEdge(int a, int b, out string? reason)
    {
        if (!CanSwapEdge(a, b, out _, out _, out reason)) return false;
        var v = Tin.Vertices;
        _edgeSwaps.Add(new EdgeSwap(v[a].XY, v[b].XY));
        Invalidate();
        return true;
    }

    private static int Opposite(TinData tin, int tri, int a, int b)
    {
        for (int k = 0; k < 3; k++)
        {
            int v = tin.Triangles[3 * tri + k];
            if (v != a && v != b) return v;
        }
        return -1;
    }

    private static bool ProperlyCross(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        // локални координати спрямо a – за точност при големи координати
        var bb = b - a; var cc = c - a; var dd = d - a;
        double scale = Math.Max(Math.Max(bb.Length, cc.Length), dd.Length);
        double eps = 1e-12 * scale * scale;
        double d1 = Point2.Cross(bb, cc), d2 = Point2.Cross(bb, dd);
        // a е в началото: a − c = −c
        double d3 = Point2.Cross(dd - cc, cc * -1), d4 = Point2.Cross(dd - cc, bb - cc);
        return ((d1 > eps && d2 < -eps) || (d1 < -eps && d2 > eps)) &&
               ((d3 > eps && d4 < -eps) || (d3 < -eps && d4 > eps));
    }

    public void Invalidate()
    {
        _tin = null;
        Version++;
    }

    /// <summary>Заменя данните на повърхнината (точки, линии, граници, завъртания) с тези на друга – напр. при преизчисляване.</summary>
    public void ReplaceContentFrom(Surface other)
    {
        _points.Clear();
        _points.AddRange(other._points);
        _breaklines.Clear();
        _breaklines.AddRange(other._breaklines);
        _boundaries.Clear();
        _boundaries.AddRange(other._boundaries);
        _edgeSwaps.Clear();
        _edgeSwaps.AddRange(other._edgeSwaps);
        _maxEdgeLength = other._maxEdgeLength;
        Invalidate();
    }

    public Surface Clone(string? name = null)
    {
        var s = new Surface(name ?? Name, _points, _breaklines) { MaxEdgeLength = MaxEdgeLength, IsDesign = IsDesign, IsVisible = IsVisible };
        s.SetBoundaries(_boundaries);
        s.SetEdgeSwaps(_edgeSwaps);
        s.GradingSource = GradingSource?.Copy();
        s.ContourInterval = ContourInterval;
        s.MajorContourEvery = MajorContourEvery;
        return s;
    }

    // ---------- заявки ----------

    /// <summary>Интерполирана кота в точка (x, y) или null, ако точката е извън повърхнината.</summary>
    public double? ElevationAt(double x, double y) => Tin.ElevationAt(x, y);

    /// <summary>Индекс на триъгълника, съдържащ точката, или -1.</summary>
    public int FindTriangle(double x, double y) => Tin.Grid.Find(x, y, out _, out _, out _);

    /// <summary>Индекс на най-близката точка (от <see cref="Points"/>) в радиус <paramref name="maxDistance"/> или -1.</summary>
    public int FindNearestPoint(double x, double y, double maxDistance)
    {
        int best = -1;
        double bestD2 = maxDistance * maxDistance;
        for (int i = 0; i < _points.Count; i++)
        {
            double dx = _points[i].X - x, dy = _points[i].Y - y;
            double d2 = dx * dx + dy * dy;
            if (d2 <= bestD2) { bestD2 = d2; best = i; }
        }
        return best;
    }

    /// <summary>Уникални ребра на триангулацията (индекси във <see cref="Vertices"/>, A &lt; B).</summary>
    public IReadOnlyList<(int A, int B)> GetEdges() => Tin.Edges;

    /// <summary>Дали реброто (индекси във <see cref="Vertices"/>) лежи върху структурна линия или граница.</summary>
    public bool IsBreaklineEdge(int a, int b) => Tin.Constrained.Contains(ConstrainedDelaunay.Key(a, b));

    /// <summary>Наклон на триъгълник в проценти.</summary>
    public double TriangleSlopePercent(int triangle)
    {
        var v = Tin.Vertices;
        var t = Tin.Triangles;
        var a = v[t[3 * triangle]];
        var b = v[t[3 * triangle + 1]];
        var c = v[t[3 * triangle + 2]];
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        double nx = uy * vz - uz * vy;
        double ny = uz * vx - ux * vz;
        double nz = ux * vy - uy * vx;
        if (Math.Abs(nz) < 1e-12) return double.PositiveInfinity;
        return Math.Sqrt(nx * nx + ny * ny) / Math.Abs(nz) * 100.0;
    }

    public SurfaceStatistics GetStatistics()
    {
        var tin = Tin;
        var verts = tin.Vertices;
        double minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity, sumZ = 0;
        int used = 0;
        foreach (int i in tin.UsedVertices())
        {
            var p = verts[i];
            minZ = Math.Min(minZ, p.Z);
            maxZ = Math.Max(maxZ, p.Z);
            sumZ += p.Z;
            used++;
        }

        double area2D = 0, area3D = 0;
        var t = tin.Triangles;
        for (int k = 0; k < t.Length; k += 3)
        {
            var a = verts[t[k]];
            var b = verts[t[k + 1]];
            var c = verts[t[k + 2]];
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
            double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            area2D += Math.Abs(nz) / 2;
            area3D += Math.Sqrt(nx * nx + ny * ny + nz * nz) / 2;
        }

        bool any = used > 0;
        return new SurfaceStatistics(
            _points.Count, _breaklines.Count, _boundaries.Count, t.Length / 3,
            any ? minZ : double.NaN,
            any ? maxZ : double.NaN,
            any ? sumZ / used : double.NaN,
            area2D, area3D, tin.Bounds);
    }

    // ---------- триангулация ----------

    private TinData Tin
    {
        get
        {
            var tin = _tin;
            if (tin != null) return tin;
            lock (_sync)
            {
                tin = _tin;
                if (tin == null)
                {
                    tin = TinData.Build(_points.ToArray(), _breaklines.ToArray(), _boundaries.ToArray(), _edgeSwaps.ToArray(), _maxEdgeLength);
                    _tin = tin;
                }
                return tin;
            }
        }
    }

    /// <summary>Принудително изграждане на триангулацията (напр. преди паралелна обработка).</summary>
    public void EnsureTriangulated() => _ = Tin;

    private sealed class TinData
    {
        public required Point3[] Vertices { get; init; }
        public required int[] Triangles { get; init; }
        public required Bounds2 Bounds { get; init; }
        public required TriangleGrid Grid { get; init; }
        public required HashSet<(int, int)> Constrained { get; init; }
        public required int FailedConstraints { get; init; }
        public required List<int> FailedSwaps { get; init; }

        /// <summary>Брой върхове преди вмъкването на границите (върховете след това са създадени от граници).</summary>
        public required int OriginalVertexCount { get; init; }

        private IReadOnlyList<(int, int)>? _edges;

        public IReadOnlyList<(int A, int B)> Edges => _edges ??= ComputeEdges();

        private static (long, long) Key(Point3 p) => ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4));

        /// <summary>Индекси на върховете, участващи в триъгълници (или всички, ако няма триъгълници).</summary>
        public IEnumerable<int> UsedVertices()
        {
            if (Triangles.Length == 0) return Enumerable.Range(0, Vertices.Length);
            var used = new bool[Vertices.Length];
            foreach (int i in Triangles) used[i] = true;
            return Enumerable.Range(0, Vertices.Length).Where(i => used[i]);
        }

        public static TinData Build(Point3[] points, Breakline[] breaklines, SurfaceBoundary[] boundaries, EdgeSwap[] swaps, double maxEdge)
        {
            var verts = new List<Point3>(points);
            var byKey = new Dictionary<(long, long), int>();
            var unique = new List<int>(points.Length);
            var constraints = new List<(int, int)>();

            // 1. Върховете на структурните линии (с приоритет пред съвпадащи точки)
            if (breaklines.Length > 0)
            {
                foreach (var poly in BreaklineSplitter.Split(breaklines))
                {
                    int prev = -1;
                    foreach (var v in poly)
                    {
                        if (!byKey.TryGetValue(Key(v), out int idx))
                        {
                            idx = verts.Count;
                            verts.Add(v);
                            byKey[Key(v)] = idx;
                            unique.Add(idx);
                        }
                        if (prev >= 0 && prev != idx) constraints.Add((prev, idx));
                        prev = idx;
                    }
                }
            }

            // 2. Точките (дубликатите в рамките на 0.1 мм и точките върху структурни линии се пропускат –
            //    по линията котата се определя от самата линия)
            var onLine = constraints.Count > 0 ? new SegmentIndex(verts, constraints) : null;
            for (int i = 0; i < points.Length; i++)
            {
                if (onLine != null && onLine.IsOnSegment(points[i].X, points[i].Y, 1e-4)) continue;
                if (byKey.TryAdd(Key(points[i]), i)) unique.Add(i);
            }

            // 3. Делоне + вмъкване на ограниченията (в локални индекси)
            var local = new int[verts.Count];
            Array.Fill(local, -1);
            for (int k = 0; k < unique.Count; k++) local[unique[k]] = k;
            var xy = unique.Select(i => verts[i].XY).ToArray();
            var tri = Delaunay.Triangulate(xy);

            var constrainedLocal = new HashSet<(int, int)>();
            int failed = 0;
            if (constraints.Count > 0 && tri.Length > 0)
            {
                var cdt = ConstrainedDelaunay.Apply(xy, tri, constraints.Select(c => (local[c.Item1], local[c.Item2])));
                tri = cdt.Triangles;
                constrainedLocal = cdt.ConstrainedEdges;
                failed = cdt.FailedConstraints;
            }

            var constrained = new HashSet<(int, int)>(
                constrainedLocal.Select(c => ConstrainedDelaunay.Key(unique[c.Item1], unique[c.Item2])));

            // 4. Филтър по максимална дължина на ребро (ребрата по структурни линии не се броят)
            var tris = new List<int>(tri.Length);
            double max2 = maxEdge > 0 ? maxEdge * maxEdge : double.PositiveInfinity;
            for (int k = 0; k < tri.Length; k += 3)
            {
                int a = unique[tri[k]], b = unique[tri[k + 1]], c = unique[tri[k + 2]];
                if (maxEdge > 0 && (TooLong(a, b) || TooLong(b, c) || TooLong(c, a))) continue;
                tris.Add(a); tris.Add(b); tris.Add(c);
            }

            bool TooLong(int i, int j)
            {
                double dx = verts[i].X - verts[j].X, dy = verts[i].Y - verts[j].Y;
                return dx * dx + dy * dy > max2 && !constrained.Contains(ConstrainedDelaunay.Key(i, j));
            }

            // 5. Завъртания на ребра – прилагат се по ред; неприложимите се отчитат
            var failedSwaps = new List<int>();
            if (swaps.Length > 0)
            {
                if (tris.Count == 0) failedSwaps.AddRange(Enumerable.Range(0, swaps.Length));
                else
                {
                    var mesh = new TriMesh(verts.Select(v => v.XY).ToList(), verts.Select(v => v.Z).ToList(), tris.ToArray(), constrained);
                    for (int i = 0; i < swaps.Length; i++)
                    {
                        bool ok = byKey.TryGetValue(Key2(swaps[i].A), out int sa) &&
                                  byKey.TryGetValue(Key2(swaps[i].B), out int sb) &&
                                  mesh.TrySwapEdge(sa, sb);
                        if (!ok) failedSwaps.Add(i);
                    }
                    tris = new List<int>(mesh.GetTriangles());
                }
            }
            int originalVertexCount = verts.Count;

            // 6. Граници: точно вмъкване на контурите (разцепване) и филтър по център на тежестта
            if (boundaries.Length > 0 && tris.Count > 0)
            {
                var mesh = new TriMesh(verts.Select(v => v.XY).ToList(), verts.Select(v => v.Z).ToList(), tris.ToArray(), constrained);
                foreach (var b in boundaries)
                {
                    var ring = b.Vertices;
                    for (int i = 0; i < ring.Count; i++) mesh.InsertSegment(ring[i], ring[(i + 1) % ring.Count]);
                }
                for (int i = verts.Count; i < mesh.VertexCount; i++)
                {
                    var p = mesh.GetPoint(i);
                    verts.Add(new Point3(p.X, p.Y, mesh.GetZ(i)));
                }
                constrained = mesh.ConstrainedEdges;

                var all = mesh.GetTriangles();
                tris = new List<int>(all.Length);
                for (int k = 0; k < all.Length; k += 3)
                {
                    var a = verts[all[k]]; var b = verts[all[k + 1]]; var c = verts[all[k + 2]];
                    if (SurfaceBoundary.IsVisible(boundaries, (a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3))
                    {
                        tris.Add(all[k]); tris.Add(all[k + 1]); tris.Add(all[k + 2]);
                    }
                }
            }

            var vertArray = verts.ToArray();
            var bounds = Bounds2.Empty;
            if (tris.Count > 0)
                foreach (int i in tris) bounds = bounds.Include(vertArray[i].X, vertArray[i].Y);
            else
                foreach (var p in vertArray) bounds = bounds.Include(p.X, p.Y);

            var triangles = tris.ToArray();
            return new TinData
            {
                Vertices = vertArray,
                Triangles = triangles,
                Bounds = bounds,
                Grid = new TriangleGrid(vertArray, triangles),
                Constrained = constrained,
                FailedConstraints = failed,
                FailedSwaps = failedSwaps,
                OriginalVertexCount = originalVertexCount,
            };
        }

        private static (long, long) Key2(Point2 p) => ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4));

        private Dictionary<(int, int), (int T1, int T2)>? _edgeTriangles;

        /// <summary>Ребро → двата съседни триъгълника (T2 = -1 за контурно ребро). Изгражда се при нужда.</summary>
        public Dictionary<(int, int), (int T1, int T2)> EdgeTriangles
        {
            get
            {
                if (_edgeTriangles != null) return _edgeTriangles;
                var d = new Dictionary<(int, int), (int, int)>(Triangles.Length);
                for (int t = 0; t < Triangles.Length / 3; t++)
                    for (int e = 0; e < 3; e++)
                    {
                        var key = ConstrainedDelaunay.Key(Triangles[3 * t + e], Triangles[3 * t + (e + 1) % 3]);
                        d[key] = d.TryGetValue(key, out var pr) ? (pr.Item1, t) : (t, -1);
                    }
                return _edgeTriangles = d;
            }
        }

        /// <summary>Пространствен индекс на отсечки – за бърза проверка дали точка лежи върху структурна линия.</summary>
        private sealed class SegmentIndex
        {
            private readonly List<Point3> _v;
            private readonly List<(int, int)> _segs;
            private readonly Dictionary<(int, int), List<int>> _cells = new();
            private readonly double _cell;

            public SegmentIndex(List<Point3> verts, List<(int, int)> segs)
            {
                _v = verts;
                _segs = segs;
                double total = 0;
                foreach (var (a, b) in segs) total += Point2.Distance(verts[a].XY, verts[b].XY);
                _cell = Math.Max(total / segs.Count, 1e-3);
                for (int s = 0; s < segs.Count; s++)
                {
                    var a = verts[segs[s].Item1].XY;
                    var b = verts[segs[s].Item2].XY;
                    int n = (int)Math.Ceiling(Point2.Distance(a, b) / (_cell * 0.5)) + 1;
                    var added = new HashSet<(int, int)>();
                    for (int k = 0; k <= n; k++)
                    {
                        var p = a + (b - a) * ((double)k / n);
                        var c = CellOf(p.X, p.Y);
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                var key = (c.Item1 + dx, c.Item2 + dy);
                                if (!added.Add(key)) continue;
                                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>();
                                list.Add(s);
                            }
                    }
                }
            }

            private (int, int) CellOf(double x, double y) => ((int)Math.Floor(x / _cell), (int)Math.Floor(y / _cell));

            public bool IsOnSegment(double x, double y, double tol)
            {
                if (!_cells.TryGetValue(CellOf(x, y), out var list)) return false;
                var p = new Point2(x, y);
                foreach (int s in list)
                {
                    var q = Polygon2.ClosestOnSegment(_v[_segs[s].Item1].XY, _v[_segs[s].Item2].XY, p);
                    if (Point2.Distance(p, q) <= tol) return true;
                }
                return false;
            }
        }

        public double? ElevationAt(double x, double y)
        {
            int t = Grid.Find(x, y, out double l1, out double l2, out double l3);
            if (t < 0) return null;
            return l1 * Vertices[Triangles[3 * t]].Z + l2 * Vertices[Triangles[3 * t + 1]].Z + l3 * Vertices[Triangles[3 * t + 2]].Z;
        }

        private IReadOnlyList<(int, int)> ComputeEdges()
        {
            var set = new HashSet<(int, int)>();
            var list = new List<(int, int)>();
            for (int k = 0; k < Triangles.Length; k += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = Triangles[k + e], b = Triangles[k + (e + 1) % 3];
                    var key = a < b ? (a, b) : (b, a);
                    if (set.Add(key)) list.Add(key);
                }
            }
            return list;
        }
    }
}

public sealed record SurfaceStatistics(
    int PointCount,
    int BreaklineCount,
    int BoundaryCount,
    int TriangleCount,
    double MinZ,
    double MaxZ,
    double MeanZ,
    double Area2D,
    double Area3D,
    Bounds2 Bounds);
