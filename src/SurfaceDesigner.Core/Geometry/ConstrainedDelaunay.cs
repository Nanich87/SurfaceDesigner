namespace SurfaceDesigner.Core.Geometry;

/// <summary>
/// Ограничена триангулация на Делоне (CDT). Вмъква задължителни ребра в съществуваща
/// триангулация на Делоне чрез размяна на диагонали (алгоритъм на Sloan, 1993):
/// 1) намират се ребрата, пресичани от ограничението;
/// 2) те се „обръщат“, докато нито едно не пресича ограничението;
/// 3) условието на Делоне се възстановява локално за новите ребра (без да се пипат ограниченията).
/// Ако ограничение минава точно през връх, то се разделя на две.
/// </summary>
public static class ConstrainedDelaunay
{
    public sealed record Result(int[] Triangles, HashSet<(int, int)> ConstrainedEdges, int FailedConstraints);

    /// <param name="points">Върхове.</param>
    /// <param name="triangles">Триангулация на Делоне (по три индекса на триъгълник).</param>
    /// <param name="constraints">Задължителни ребра (двойки индекси).</param>
    public static Result Apply(IReadOnlyList<Point2> points, int[] triangles, IEnumerable<(int A, int B)> constraints)
    {
        var mesh = new TriMesh(points, null, triangles);
        int failed = 0;
        foreach (var (a, b) in constraints)
            if (a != b && !mesh.InsertConstraint(a, b)) failed++;
        return new Result(mesh.GetTriangles(), mesh.ConstrainedEdges, failed);
    }

    public static (int A, int B) Key(int a, int b) => a < b ? (a, b) : (b, a);
}

/// <summary>
/// Триъгълна мрежа с полуребра: полуребро e е от tri[e] към tri[Next(e)], opp[e] – противоположното (-1 по контура).
/// Поддържа вмъкване на ограничения с флипове (CDT) и точно вмъкване на отсечки чрез разцепване
/// на ребра/триъгълници (без промяна на интерполацията – за граници на повърхнини).
/// Координатите се пазят локално (спрямо първата точка) заради точността.
/// </summary>
internal sealed class TriMesh
{
    private const double SnapTol = 1e-7; // m – съвпадение на точка с връх/ребро

    private readonly List<double> _x = new();
    private readonly List<double> _y = new();
    private readonly List<double> _z = new();
    private readonly List<int> _tri;
    private readonly List<int> _opp;
    private readonly List<int> _vEdge;
    private readonly double _ox;
    private readonly double _oy;
    private readonly double _eps;
    private readonly List<int> _around = new();
    private int _lastTri;

    public HashSet<(int A, int B)> ConstrainedEdges { get; }

    /// <summary>Ако е зададен – всеки флип се записва като ребро (краищата преди завъртането), по ред.</summary>
    public List<(Point2 A, Point2 B)>? FlipLog { get; set; }

    /// <summary>Причина за последния неуспех при вмъкване на ограничение (диагностика).</summary>
    public string? LastFailure { get; private set; }

    /// <summary>Дали реброто a–b съществува в мрежата.</summary>
    public bool HasEdge(int a, int b) => a >= 0 && b >= 0 && a < _vEdge.Count && b < _vEdge.Count && FindEdge(a, b) >= 0;

    public TriMesh(
        IReadOnlyList<Point2> points,
        IReadOnlyList<double>? z,
        int[] triangles,
        IEnumerable<(int, int)>? constrained = null)
    {
        int n = points.Count;
        _ox = n > 0 ? points[0].X : 0;
        _oy = n > 0 ? points[0].Y : 0;
        double ext = 0;
        for (int i = 0; i < n; i++)
        {
            _x.Add(points[i].X - _ox);
            _y.Add(points[i].Y - _oy);
            _z.Add(z?[i] ?? 0);
            ext = Math.Max(ext, Math.Max(Math.Abs(_x[i]), Math.Abs(_y[i])));
        }

        _eps = 1e-12 * Math.Max(ext * ext, 1e-12);

        _tri = new List<int>(triangles);
        for (int k = 0; k < _tri.Count; k += 3)
        {
            if (Orient(_tri[k], _tri[k + 1], _tri[k + 2]) < 0)
                (_tri[k + 1], _tri[k + 2]) = (_tri[k + 2], _tri[k + 1]);
        }

        _opp = Enumerable.Repeat(-1, _tri.Count).ToList();
        var directed = new Dictionary<(int, int), int>(_tri.Count);
        for (int e = 0; e < _tri.Count; e++) directed[(_tri[e], _tri[Next(e)])] = e;
        for (int e = 0; e < _tri.Count; e++)
            if (directed.TryGetValue((_tri[Next(e)], _tri[e]), out int o)) _opp[e] = o;

        _vEdge = Enumerable.Repeat(-1, n).ToList();
        for (int e = 0; e < _tri.Count; e++) _vEdge[_tri[e]] = e;

        ConstrainedEdges = constrained != null ? new HashSet<(int, int)>(constrained) : new HashSet<(int, int)>();
    }

    public int VertexCount => _x.Count;

    public Point2 GetPoint(int i) => new(_x[i] + _ox, _y[i] + _oy);

    public double GetZ(int i) => _z[i];

    public int[] GetTriangles() => _tri.ToArray();

    private static int Next(int e) => e % 3 == 2 ? e - 2 : e + 1;

    private static int Prev(int e) => e % 3 == 0 ? e + 2 : e - 1;

    private static (int A, int B) Key(int a, int b) => ConstrainedDelaunay.Key(a, b);

    private double Orient(int a, int b, int c) =>
        (_x[b] - _x[a]) * (_y[c] - _y[a]) - (_y[b] - _y[a]) * (_x[c] - _x[a]);

    private double OrientP(int a, int b, double px, double py) =>
        (_x[b] - _x[a]) * (py - _y[a]) - (_y[b] - _y[a]) * (px - _x[a]);

    private int Sign(double v) => v > _eps ? 1 : v < -_eps ? -1 : 0;

    private bool Between(int a, int b, int p)
    {
        double dx = _x[b] - _x[a], dy = _y[b] - _y[a];
        double d1 = (_x[p] - _x[a]) * dx + (_y[p] - _y[a]) * dy;
        double d2 = (_x[p] - _x[b]) * -dx + (_y[p] - _y[b]) * -dy;
        return d1 > 0 && d2 > 0;
    }

    private bool SegmentsCross(int a, int b, int c, int d) =>
        Sign(Orient(a, b, c)) * Sign(Orient(a, b, d)) < 0 &&
        Sign(Orient(c, d, a)) * Sign(Orient(c, d, b)) < 0;

    private void CollectAround(int v)
    {
        _around.Clear();
        int start = _vEdge[v];
        if (start < 0) return;
        int h = start;
        bool closed = false;
        int guard = 0;
        while (guard++ < 100000)
        {
            _around.Add(h);
            int t = _opp[Prev(h)];
            if (t < 0) break;
            h = t;
            if (h == start) { closed = true; break; }
        }

        if (closed) return;
        h = start;
        guard = 0;
        while (guard++ < 100000)
        {
            int t = _opp[h];
            if (t < 0) break;
            h = Next(t);
            if (h == start) break;
            _around.Add(h);
        }
    }

    private int FindEdge(int u, int w)
    {
        CollectAround(u);
        foreach (int h in _around)
        {
            if (_tri[Next(h)] == w) return h;
            if (_tri[Prev(h)] == w) return Prev(h);
        }

        return -1;
    }

    private void Link(int a, int b)
    {
        _opp[a] = b;
        if (b >= 0) _opp[b] = a;
    }

    private int AddVertex(double x, double y, double z)
    {
        _x.Add(x); _y.Add(y); _z.Add(z);
        _vEdge.Add(-1);
        return _x.Count - 1;
    }

    private int AddTriangle(int a, int b, int c)
    {
        int t = _tri.Count;
        _tri.Add(a); _tri.Add(b); _tri.Add(c);
        _opp.Add(-1); _opp.Add(-1); _opp.Add(-1);
        return t;
    }

    private void UpdateVEdge(params int[] halfedges)
    {
        foreach (int h in halfedges) _vEdge[_tri[h]] = h;
    }

    // =====================================================================
    //  CDT – вмъкване на ограничение с флипове
    // =====================================================================

    /// <param name="restoreDelaunay">
    /// false – след вмъкването не се правят допълнителни флипове (нужно, когато се възпроизвежда дадена триангулация).
    /// </param>
    public bool InsertConstraint(int a, int b, bool restoreDelaunay = true)
    {
        var stack = new Stack<(int, int)>();
        stack.Push((a, b));
        int guard = 0;
        while (stack.Count > 0)
        {
            if (++guard > 100000) { LastFailure = "guard"; return false; }
            var (s, e) = stack.Pop();
            if (s == e) continue;
            if (_vEdge[s] < 0 || _vEdge[e] < 0) { LastFailure = "връх извън мрежата"; return false; }
            if (FindEdge(s, e) >= 0)
            {
                ConstrainedEdges.Add(Key(s, e));
                continue;
            }

            if (!Trace(s, e, out var crossing, out int via)) return false;
            if (via >= 0)
            {
                stack.Push((via, e));
                stack.Push((s, via));
                continue;
            }

            if (!RemoveCrossings(s, e, crossing, restoreDelaunay)) return false;
            ConstrainedEdges.Add(Key(s, e));
        }

        return true;
    }

    private bool Trace(int s, int e, out List<(int A, int B)> crossing, out int via)
    {
        crossing = new List<(int, int)>();
        via = -1;

        int x = -1;
        CollectAround(s);
        foreach (int h in _around)
        {
            int u = _tri[Next(h)], w = _tri[Prev(h)];
            int su = Sign(Orient(s, e, u)), sw = Sign(Orient(s, e, w));
            if (su == 0 && Between(s, e, u)) { via = u; return true; }
            if (sw == 0 && Between(s, e, w)) { via = w; return true; }
            if (su < 0 && sw > 0) { x = Next(h); break; }
        }

        if (x < 0) { LastFailure = "не е намерен начален триъгълник (Trace)"; return false; }

        int guard = 0;
        while (guard++ < 1_000_000)
        {
            int u = _tri[x], w = _tri[Next(x)];
            if (ConstrainedEdges.Contains(Key(u, w))) { LastFailure = "пресича друго ограничение"; return false; }
            crossing.Add((u, w));
            int o = _opp[x];
            if (o < 0) { LastFailure = "излиза извън мрежата (Trace)"; return false; }
            int v = _tri[Prev(o)];
            if (v == e) return true;
            int sv = Sign(Orient(s, e, v));
            if (sv == 0) { via = v; return true; }
            x = sv == Sign(Orient(s, e, u)) ? Prev(o) : Next(o);
        }

        { LastFailure = "Trace guard"; return false; }
    }

    private bool RemoveCrossings(int s, int e, List<(int A, int B)> crossing, bool restoreDelaunay = true)
    {
        var queue = new Queue<(int, int)>(crossing);
        var created = new List<(int, int)>();
        long limit = 1000 + 50L * crossing.Count * crossing.Count;
        long guard = 0;

        while (queue.Count > 0)
        {
            if (++guard > limit) { LastFailure = "RemoveCrossings: превишен лимит на флиповете"; return false; }
            var (u, w) = queue.Dequeue();
            int h = FindEdge(u, w);
            if (h < 0) continue;
            int o = _opp[h];
            if (o < 0) { LastFailure = "RemoveCrossings: контурно ребро"; return false; }

            int p = _tri[h], q = _tri[Next(h)], r = _tri[Prev(h)], t = _tri[Prev(o)];
            if (!IsConvex(p, q, r, t))
            {
                queue.Enqueue((u, w));
                continue;
            }

            Flip(h);
            if (r != s && r != e && t != s && t != e && SegmentsCross(s, e, r, t))
                queue.Enqueue((r, t));
            else
                created.Add((r, t));
        }

        bool changed = restoreDelaunay;
        int passes = 0;
        while (changed && passes++ < 100)
        {
            changed = false;
            for (int i = 0; i < created.Count; i++)
            {
                var (u, w) = created[i];
                if (Key(u, w) == Key(s, e) || ConstrainedEdges.Contains(Key(u, w))) continue;
                int h = FindEdge(u, w);
                if (h < 0) continue;
                int o = _opp[h];
                if (o < 0) continue;
                int p = _tri[h], q = _tri[Next(h)], r = _tri[Prev(h)], t = _tri[Prev(o)];
                if (InCircle(p, q, r, t) && IsConvex(p, q, r, t))
                {
                    Flip(h);
                    created[i] = (r, t);
                    changed = true;
                }
            }
        }

        return true;
    }

    private bool IsConvex(int p, int q, int r, int t) =>
        Sign(Orient(r, t, p)) * Sign(Orient(r, t, q)) < 0 &&
        Sign(Orient(p, q, r)) * Sign(Orient(p, q, t)) < 0;

    private bool InCircle(int a, int b, int c, int d)
    {
        double adx = _x[a] - _x[d], ady = _y[a] - _y[d];
        double bdx = _x[b] - _x[d], bdy = _y[b] - _y[d];
        double cdx = _x[c] - _x[d], cdy = _y[c] - _y[d];
        double ad = adx * adx + ady * ady, bd = bdx * bdx + bdy * bdy, cd = cdx * cdx + cdy * cdy;
        double det = adx * (bdy * cd - bd * cdy) - ady * (bdx * cd - bd * cdx) + ad * (bdx * cdy - bdy * cdx);
        return det > _eps * Math.Max(Math.Max(ad, bd), cd);
    }

    /// <summary>
    /// Завърта (разменя диагонала на) реброто a–b, ако е вътрешно, не е ограничение
    /// и двата съседни триъгълника образуват строго изпъкнал четириъгълник.
    /// </summary>
    public bool TrySwapEdge(int a, int b)
    {
        if (a < 0 || b < 0 || a >= _vEdge.Count || b >= _vEdge.Count) return false;
        if (ConstrainedEdges.Contains(Key(a, b))) return false;
        int h = FindEdge(a, b);
        if (h < 0 || _opp[h] < 0) return false;
        int p = _tri[h], q = _tri[Next(h)], r = _tri[Prev(h)], t = _tri[Prev(_opp[h])];
        if (!IsConvex(p, q, r, t)) return false;
        Flip(h);
        return true;
    }

    /// <summary>(p,q,r) + (q,p,t) → (t,r,p) + (r,t,q).</summary>
    private void Flip(int h)
    {
        int o = _opp[h];
        int hn = Next(h), hp = Prev(h), on = Next(o), op = Prev(o);
        int p = _tri[h], q = _tri[hn], r = _tri[hp], t = _tri[op];

        int a = _opp[hn], b = _opp[hp], c = _opp[on], d = _opp[op];
        FlipLog?.Add((GetPoint(p), GetPoint(q)));

        _tri[h] = t; _tri[hn] = r; _tri[hp] = p;
        _tri[o] = r; _tri[on] = t; _tri[op] = q;

        Link(h, o);
        Link(hn, b);
        Link(hp, c);
        Link(on, d);
        Link(op, a);
        UpdateVEdge(h, hn, hp, op);
    }

    // =====================================================================
    //  Точно вмъкване на отсечка (разцепване) – запазва интерполацията
    // =====================================================================
    private enum Loc
    {
        Outside,
        Vertex,
        Edge,
        Face
    }

    private Loc Locate(double px, double py, out int index)
    {
        index = -1;
        int nt = _tri.Count / 3;
        if (nt == 0) return Loc.Outside;
        int t = _lastTri < nt ? _lastTri : 0;
        for (int step = 0; step < 100000; step++)
        {
            int moveTo = -2;
            for (int i = 0; i < 3; i++)
            {
                int e = 3 * t + i;
                if (Sign(OrientP(_tri[e], _tri[Next(e)], px, py)) < 0)
                {
                    moveTo = _opp[e] < 0 ? -1 : _opp[e] / 3;
                    break;
                }
            }

            if (moveTo == -2) return Classify(t, px, py, out index);
            if (moveTo == -1) break; // извън контура (или вдлъбнатост) – пълно търсене
            t = moveTo;
        }

        for (t = 0; t < nt; t++)
        {
            bool inside = true;
            for (int i = 0; i < 3 && inside; i++)
                if (Sign(OrientP(_tri[3 * t + i], _tri[Next(3 * t + i)], px, py)) < 0) inside = false;
            if (inside) return Classify(t, px, py, out index);
        }

        return Loc.Outside;
    }

    private Loc Classify(int t, double px, double py, out int index)
    {
        _lastTri = t;
        for (int i = 0; i < 3; i++)
        {
            int v = _tri[3 * t + i];
            double dx = _x[v] - px, dy = _y[v] - py;
            if (dx * dx + dy * dy <= SnapTol * SnapTol) { index = v; return Loc.Vertex; }
        }

        for (int i = 0; i < 3; i++)
        {
            int e = 3 * t + i;
            int a = _tri[e], b = _tri[Next(e)];
            double len = Math.Sqrt((_x[b] - _x[a]) * (_x[b] - _x[a]) + (_y[b] - _y[a]) * (_y[b] - _y[a]));
            if (Math.Abs(OrientP(a, b, px, py)) <= SnapTol * len) { index = e; return Loc.Edge; }
        }

        index = t;
        return Loc.Face;
    }

    private int InsertPoint(double px, double py)
    {
        switch (Locate(px, py, out int idx))
        {
            case Loc.Vertex: return idx;
            case Loc.Edge: return SplitEdge(idx, px, py);
            case Loc.Face: return SplitFace(idx, px, py);
            default: return -1;
        }
    }

    /// <summary>Разделя триъгълник t на три с нов връх (котата – по равнината на триъгълника).</summary>
    private int SplitFace(int t, double px, double py)
    {
        int e0 = 3 * t, e1 = e0 + 1, e2 = e0 + 2;
        int a = _tri[e0], b = _tri[e1], c = _tri[e2];
        double det = (_y[b] - _y[c]) * (_x[a] - _x[c]) + (_x[c] - _x[b]) * (_y[a] - _y[c]);
        double l1 = ((_y[b] - _y[c]) * (px - _x[c]) + (_x[c] - _x[b]) * (py - _y[c])) / det;
        double l2 = ((_y[c] - _y[a]) * (px - _x[c]) + (_x[a] - _x[c]) * (py - _y[c])) / det;
        int v = AddVertex(px, py, l1 * _z[a] + l2 * _z[b] + (1 - l1 - l2) * _z[c]);

        int oBC = _opp[e1], oCA = _opp[e2];

        // t = (a, b, v): e0: a→b, e1: b→v, e2: v→a
        _tri[e0] = a; _tri[e1] = b; _tri[e2] = v;
        int f = AddTriangle(b, c, v);  // f: b→c, f+1: c→v, f+2: v→b
        int g = AddTriangle(c, a, v);  // g: c→a, g+1: a→v, g+2: v→c
        Link(f, oBC);
        Link(g, oCA);
        Link(e1, f + 2);
        Link(f + 1, g + 2);
        Link(g + 1, e2);
        UpdateVEdge(e0, e2, f, f + 1, g, g + 1);
        _lastTri = t;
        return v;
    }

    /// <summary>Разцепва реброто на полуребро h с нов връх (котата – линейно по реброто).</summary>
    private int SplitEdge(int h, double px, double py)
    {
        int o = _opp[h];
        int hn = Next(h), hp = Prev(h);
        int p = _tri[h], q = _tri[hn], r = _tri[hp];

        double dx = _x[q] - _x[p], dy = _y[q] - _y[p];
        double tpar = Math.Clamp(((px - _x[p]) * dx + (py - _y[p]) * dy) / (dx * dx + dy * dy), 0, 1);

        // точката се поставя точно върху реброто
        int v = AddVertex(_x[p] + dx * tpar, _y[p] + dy * tpar, _z[p] + (_z[q] - _z[p]) * tpar);

        bool constrained = ConstrainedEdges.Remove(Key(p, q));
        if (constrained) { ConstrainedEdges.Add(Key(p, v)); ConstrainedEdges.Add(Key(v, q)); }

        // T1 (p,q,r) → (p,v,r) + T3 (v,q,r)
        int oQR = _opp[hn];
        _tri[hn] = v; // h: p→v, hn: v→r, hp: r→p
        int a = AddTriangle(v, q, r); // a: v→q, a+1: q→r, a+2: r→v
        Link(a + 1, oQR);
        Link(hn, a + 2);

        if (o >= 0)
        {
            // T2 (q,p,s) → (v,p,s) + T4 (q,v,s)
            int on = Next(o), op = Prev(o);
            int s = _tri[op];
            int oSQ = _opp[op];
            _tri[o] = v; // o: v→p, on: p→s, op: s→v
            int d = AddTriangle(q, v, s); // d: q→v, d+1: v→s, d+2: s→q
            Link(d + 2, oSQ);
            Link(op, d + 1);
            Link(h, o);
            Link(a, d);
            UpdateVEdge(o, on, op, d, d + 1, d + 2);
        }
        else
        {
            _opp[h] = -1;
            _opp[a] = -1;
        }

        UpdateVEdge(h, hn, hp, a, a + 1, a + 2);
        _lastTri = h / 3;
        return v;
    }

    /// <summary>
    /// Вмъква отсечката A→B точно: проследява я през мрежата и разцепва всяко пресичано ребро,
    /// така че отсечката става поредица от ребра (маркирани като ограничения). Части извън мрежата се пропускат.
    /// </summary>
    public void InsertSegment(Point2 a, Point2 b)
    {
        double ax = a.X - _ox, ay = a.Y - _oy, bx = b.X - _ox, by = b.Y - _oy;
        double segLen = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        if (segLen < SnapTol) return;

        int c = InsertPoint(ax, ay);
        double cs = 0; // изминат дял от отсечката
        double minAdvance = SnapTol / segLen;
        int startVertices = _x.Count;
        int guard = 0;
        while (guard++ < 1_000_000)
        {
            // последна защита: една отсечка не може да създаде повече върхове от броя на ребрата в мрежата
            if (_x.Count - startVertices > _tri.Count + 16) return;
            if (c < 0)
            {
                if (!FindEntry(ax, ay, bx, by, cs, out double es, out int entryEdge, out double ex, out double ey)) return;
                c = Classify(entryEdge / 3, ex, ey, out int idx) switch
                {
                    Loc.Vertex => idx,
                    Loc.Edge => SplitEdge(idx, ex, ey),
                    _ => SplitEdge(entryEdge, ex, ey),
                };
                cs = Math.Max(es, Param(ax, ay, bx, by, _x[c], _y[c]));
                continue;
            }

            double dbx = bx - _x[c], dby = by - _y[c];
            if (dbx * dbx + dby * dby <= SnapTol * SnapTol) return; // достигнат край

            int next = Step(c, bx, by);
            if (next < 0)
            {
                // излизаме от мрежата във върха c
                cs = Math.Max(cs, Param(ax, ay, bx, by, _x[c], _y[c]));
                c = -1;
                continue;
            }

            // защита от зацикляне: всяка стъпка трябва да напредва по отсечката
            double ns = Param(ax, ay, bx, by, _x[next], _y[next]);
            if (next == c || ns <= cs + minAdvance * 0.5) return;

            ConstrainedEdges.Add(Key(c, next));
            c = next;
            cs = ns;
        }
    }

    private static double Param(double ax, double ay, double bx, double by, double px, double py)
    {
        double dx = bx - ax, dy = by - ay;
        return ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy);
    }

    /// <summary>Една стъпка от връх c към B: връща следващия връх по отсечката (реброто c–следващ съществува) или -1.</summary>
    private int Step(int c, double bx, double by)
    {
        double dx = bx - _x[c], dy = by - _y[c];
        double distB2 = dx * dx + dy * dy;
        double distB = Math.Sqrt(distB2);

        CollectAround(c);
        var around = _around.ToArray();

        // Съсед точно по посоката на отсечката
        foreach (int h in around)
        {
            foreach (var (u, hh) in new[] { (_tri[Next(h)], h), (_tri[Prev(h)], Prev(h)) })
            {
                double ux = _x[u] - _x[c], uy = _y[u] - _y[c];
                double cross = dx * uy - dy * ux;
                double dot = dx * ux + dy * uy;
                if (dot <= 0 || Math.Abs(cross) > SnapTol * distB) continue;
                double du2 = ux * ux + uy * uy;
                if (du2 <= distB2 + SnapTol) return u;     // u е между c и B (или е B)
                return SplitEdge(hh, bx, by);              // B лежи върху реброто c–u
            }
        }

        // Триъгълник, в чийто ъгъл при c попада посоката към B
        foreach (int h in around)
        {
            int u = _tri[Next(h)], w = _tri[Prev(h)];
            double su = OrientP(c, u, bx, by); // B отляво на c→u
            double sw = OrientP(c, w, bx, by); // B отдясно на c→w
            if (su <= 0 || sw >= 0) continue;

            double sb = OrientP(u, w, bx, by);
            int t = h / 3;
            double lenUW = Math.Sqrt((_x[w] - _x[u]) * (_x[w] - _x[u]) + (_y[w] - _y[u]) * (_y[w] - _y[u]));
            if (sb > SnapTol * lenUW) return SplitFace(t, bx, by);          // B е вътре в триъгълника
            if (Math.Abs(sb) <= SnapTol * lenUW) return SplitEdge(Next(h), bx, by); // B е върху u–w

            // Пресечна точка на c→B с u–w
            double rx = _x[w] - _x[u], ry = _y[w] - _y[u];
            double den = dx * ry - dy * rx;
            if (Math.Abs(den) < 1e-300) return -1;
            double s = ((_x[u] - _x[c]) * ry - (_y[u] - _y[c]) * rx) / den;
            double ix = _x[c] + dx * s, iy = _y[c] + dy * s;

            // пресечна точка практически във връх – използва се самият връх
            if ((ix - _x[u]) * (ix - _x[u]) + (iy - _y[u]) * (iy - _y[u]) <= SnapTol * SnapTol) return u;
            if ((ix - _x[w]) * (ix - _x[w]) + (iy - _y[w]) * (iy - _y[w]) <= SnapTol * SnapTol) return w;
            return SplitEdge(Next(h), ix, iy);
        }

        return -1;
    }

    /// <summary>Първото влизане на отсечката A→B в мрежата след дял <paramref name="fromS"/>.</summary>
    private bool FindEntry(
        double ax,
        double ay,
        double bx,
        double by,
        double fromS,
        out double entryS,
        out int edge,
        out double ex,
        out double ey)
    {
        entryS = double.PositiveInfinity;
        edge = -1;
        ex = ey = 0;
        double dx = bx - ax, dy = by - ay;
        double len = Math.Sqrt(dx * dx + dy * dy);

        // повторно влизане – поне 10·допуска по-напред (защита при почти колинеарни ребра по контура)
        double minS = fromS + 10 * SnapTol / len;

        for (int e = 0; e < _tri.Count; e++)
        {
            if (_opp[e] >= 0) continue;
            int p = _tri[e], q = _tri[Next(e)];
            double rx = _x[q] - _x[p], ry = _y[q] - _y[p];
            double den = dx * ry - dy * rx;

            // Мрежата е отляво на външното полуребро p→q. Истинско влизане = пресичане отдясно наляво
            // под ненулев ъгъл; плъзгане по почти колинеарно ребро не е влизане.
            double rlen = Math.Sqrt(rx * rx + ry * ry);
            if (-den <= 1e-9 * rlen * len) continue;
            double qx = _x[p] - ax, qy = _y[p] - ay;
            double s = (qx * ry - qy * rx) / den;
            double r = (qx * dy - qy * dx) / den;
            if (s < minS || s > 1 + 1e-12 || r < -1e-12 || r > 1 + 1e-12) continue;
            if (s < entryS) { entryS = s; edge = e; }
        }

        if (edge < 0) return false;
        ex = ax + dx * entryS;
        ey = ay + dy * entryS;
        return true;
    }
}
