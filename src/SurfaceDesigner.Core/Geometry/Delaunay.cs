namespace SurfaceDesigner.Core.Geometry;

/// <summary>
/// Бърза 2D триангулация на Делоне (sweep-hull алгоритъм по идеята на Delaunator, O(n log n)).
/// Резултатът е масив от индекси – по три за всеки триъгълник.
/// </summary>
public static class Delaunay
{
    private const double Epsilon = 1.0 / (1L << 52);

    public static int[] Triangulate(IReadOnlyList<Point2> points)
    {
        int n = points.Count;
        if (n < 3) return Array.Empty<int>();

        // Работим в локални координати, за да не губим точност при големи геодезически координати.
        double ox = points[0].X, oy = points[0].Y;
        var coords = new double[n * 2];
        for (int i = 0; i < n; i++)
        {
            coords[2 * i] = points[i].X - ox;
            coords[2 * i + 1] = points[i].Y - oy;
        }

        var builder = new Builder(coords);
        return builder.Run();
    }

    private sealed class Builder
    {
        private readonly double[] _coords;
        private readonly int _n;
        private readonly int[] _triangles;
        private readonly int[] _halfedges;
        private readonly int _hashSize;
        private readonly int[] _hullPrev;
        private readonly int[] _hullNext;
        private readonly int[] _hullTri;
        private readonly int[] _hullHash;
        private readonly int[] _edgeStack = new int[1024];
        private int _trianglesLen;
        private int _hullStart;
        private double _cx, _cy;

        public Builder(double[] coords)
        {
            _coords = coords;
            _n = coords.Length / 2;
            int maxTriangles = Math.Max(2 * _n - 5, 0);
            _triangles = new int[maxTriangles * 3];
            _halfedges = new int[maxTriangles * 3];
            _hashSize = (int)Math.Ceiling(Math.Sqrt(_n));
            _hullPrev = new int[_n];
            _hullNext = new int[_n];
            _hullTri = new int[_n];
            _hullHash = new int[_hashSize];
        }

        public int[] Run()
        {
            var c = _coords;
            int n = _n;

            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            var ids = new int[n];
            for (int i = 0; i < n; i++)
            {
                double x = c[2 * i], y = c[2 * i + 1];
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
                ids[i] = i;
            }
            double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;

            int i0 = 0, i1 = -1, i2 = -1;
            double minDist = double.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                double d = Dist(cx, cy, c[2 * i], c[2 * i + 1]);
                if (d < minDist) { i0 = i; minDist = d; }
            }
            double i0x = c[2 * i0], i0y = c[2 * i0 + 1];

            minDist = double.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                if (i == i0) continue;
                double d = Dist(i0x, i0y, c[2 * i], c[2 * i + 1]);
                if (d < minDist && d > 0) { i1 = i; minDist = d; }
            }
            if (i1 < 0) return Array.Empty<int>();
            double i1x = c[2 * i1], i1y = c[2 * i1 + 1];

            double minRadius = double.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                if (i == i0 || i == i1) continue;
                double r = Circumradius(i0x, i0y, i1x, i1y, c[2 * i], c[2 * i + 1]);
                if (r < minRadius) { i2 = i; minRadius = r; }
            }
            if (i2 < 0 || double.IsInfinity(minRadius) || double.IsNaN(minRadius))
                return Array.Empty<int>(); // всички точки са колинеарни
            double i2x = c[2 * i2], i2y = c[2 * i2 + 1];

            if (Orient(i0x, i0y, i1x, i1y, i2x, i2y))
            {
                (i1, i2) = (i2, i1);
                (i1x, i2x) = (i2x, i1x);
                (i1y, i2y) = (i2y, i1y);
            }

            (_cx, _cy) = Circumcenter(i0x, i0y, i1x, i1y, i2x, i2y);

            var dists = new double[n];
            for (int i = 0; i < n; i++) dists[i] = Dist(c[2 * i], c[2 * i + 1], _cx, _cy);
            Array.Sort(dists, ids);

            _hullStart = i0;
            _hullNext[i0] = _hullPrev[i2] = i1;
            _hullNext[i1] = _hullPrev[i0] = i2;
            _hullNext[i2] = _hullPrev[i1] = i0;
            _hullTri[i0] = 0;
            _hullTri[i1] = 1;
            _hullTri[i2] = 2;

            Array.Fill(_hullHash, -1);
            _hullHash[HashKey(i0x, i0y)] = i0;
            _hullHash[HashKey(i1x, i1y)] = i1;
            _hullHash[HashKey(i2x, i2y)] = i2;

            _trianglesLen = 0;
            AddTriangle(i0, i1, i2, -1, -1, -1);

            double xp = 0, yp = 0;
            for (int k = 0; k < ids.Length; k++)
            {
                int i = ids[k];
                double x = c[2 * i], y = c[2 * i + 1];

                if (k > 0 && Math.Abs(x - xp) <= Epsilon && Math.Abs(y - yp) <= Epsilon) continue;
                xp = x; yp = y;

                if (i == i0 || i == i1 || i == i2) continue;

                // видимо ребро от обвивката чрез хеша
                int start = 0;
                for (int j = 0, key = HashKey(x, y); j < _hashSize; j++)
                {
                    start = _hullHash[(key + j) % _hashSize];
                    if (start != -1 && start != _hullNext[start]) break;
                }

                start = _hullPrev[start];
                int e = start, q;
                while (true)
                {
                    q = _hullNext[e];
                    if (Orient(x, y, c[2 * e], c[2 * e + 1], c[2 * q], c[2 * q + 1])) break;
                    e = q;
                    if (e == start) { e = -1; break; }
                }
                if (e == -1) continue; // почти дублирана точка

                int t = AddTriangle(e, i, _hullNext[e], -1, -1, _hullTri[e]);
                _hullTri[i] = Legalize(t + 2);
                _hullTri[e] = t;

                int nn = _hullNext[e];
                while (true)
                {
                    q = _hullNext[nn];
                    if (!Orient(x, y, c[2 * nn], c[2 * nn + 1], c[2 * q], c[2 * q + 1])) break;
                    t = AddTriangle(nn, i, q, _hullTri[i], -1, _hullTri[nn]);
                    _hullTri[i] = Legalize(t + 2);
                    _hullNext[nn] = nn; // премахната
                    nn = q;
                }

                if (e == start)
                {
                    while (true)
                    {
                        q = _hullPrev[e];
                        if (!Orient(x, y, c[2 * q], c[2 * q + 1], c[2 * e], c[2 * e + 1])) break;
                        t = AddTriangle(q, i, e, -1, _hullTri[e], _hullTri[q]);
                        Legalize(t + 2);
                        _hullTri[q] = t;
                        _hullNext[e] = e;
                        e = q;
                    }
                }

                _hullStart = _hullPrev[i] = e;
                _hullNext[e] = _hullPrev[nn] = i;
                _hullNext[i] = nn;

                _hullHash[HashKey(x, y)] = i;
                _hullHash[HashKey(c[2 * e], c[2 * e + 1])] = e;
            }

            var result = new int[_trianglesLen];
            Array.Copy(_triangles, result, _trianglesLen);
            return result;
        }

        private int HashKey(double x, double y)
        {
            int k = (int)Math.Floor(PseudoAngle(x - _cx, y - _cy) * _hashSize) % _hashSize;
            return k < 0 ? k + _hashSize : k;
        }

        private int Legalize(int a)
        {
            var c = _coords;
            int i = 0;
            int ar;
            while (true)
            {
                int b = _halfedges[a];
                int a0 = a - a % 3;
                ar = a0 + (a + 2) % 3;

                if (b == -1)
                {
                    if (i == 0) break;
                    a = _edgeStack[--i];
                    continue;
                }

                int b0 = b - b % 3;
                int al = a0 + (a + 1) % 3;
                int bl = b0 + (b + 2) % 3;

                int p0 = _triangles[ar];
                int pr = _triangles[a];
                int pl = _triangles[al];
                int p1 = _triangles[bl];

                bool illegal = InCircle(
                    c[2 * p0], c[2 * p0 + 1],
                    c[2 * pr], c[2 * pr + 1],
                    c[2 * pl], c[2 * pl + 1],
                    c[2 * p1], c[2 * p1 + 1]);

                if (illegal)
                {
                    _triangles[a] = p1;
                    _triangles[b] = p0;

                    int hbl = _halfedges[bl];
                    if (hbl == -1)
                    {
                        int e = _hullStart;
                        do
                        {
                            if (_hullTri[e] == bl) { _hullTri[e] = a; break; }
                            e = _hullPrev[e];
                        } while (e != _hullStart);
                    }
                    Link(a, hbl);
                    Link(b, _halfedges[ar]);
                    Link(ar, bl);

                    int br = b0 + (b + 1) % 3;
                    if (i < _edgeStack.Length) _edgeStack[i++] = br;
                }
                else
                {
                    if (i == 0) break;
                    a = _edgeStack[--i];
                }
            }
            return ar;
        }

        private void Link(int a, int b)
        {
            _halfedges[a] = b;
            if (b != -1) _halfedges[b] = a;
        }

        private int AddTriangle(int i0, int i1, int i2, int a, int b, int c)
        {
            int t = _trianglesLen;
            _triangles[t] = i0;
            _triangles[t + 1] = i1;
            _triangles[t + 2] = i2;
            Link(t, a);
            Link(t + 1, b);
            Link(t + 2, c);
            _trianglesLen += 3;
            return t;
        }
    }

    private static double Dist(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx, dy = ay - by;
        return dx * dx + dy * dy;
    }

    private static bool Orient(double px, double py, double qx, double qy, double rx, double ry) =>
        (qy - py) * (rx - qx) - (qx - px) * (ry - qy) < 0;

    private static double PseudoAngle(double dx, double dy)
    {
        double s = Math.Abs(dx) + Math.Abs(dy);
        if (s == 0) return 0;
        double p = dx / s;
        return (dy > 0 ? 3 - p : 1 + p) / 4;
    }

    private static bool InCircle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py)
    {
        double dx = ax - px, dy = ay - py;
        double ex = bx - px, ey = by - py;
        double fx = cx - px, fy = cy - py;
        double ap = dx * dx + dy * dy;
        double bp = ex * ex + ey * ey;
        double cp = fx * fx + fy * fy;
        return dx * (ey * cp - bp * fy) - dy * (ex * cp - bp * fx) + ap * (ex * fy - ey * fx) < 0;
    }

    private static double Circumradius(double ax, double ay, double bx, double by, double cx, double cy)
    {
        double dx = bx - ax, dy = by - ay;
        double ex = cx - ax, ey = cy - ay;
        double bl = dx * dx + dy * dy;
        double cl = ex * ex + ey * ey;
        double den = dx * ey - dy * ex;
        if (den == 0) return double.PositiveInfinity;
        double d = 0.5 / den;
        double x = (ey * bl - dy * cl) * d;
        double y = (dx * cl - ex * bl) * d;
        return x * x + y * y;
    }

    private static (double X, double Y) Circumcenter(double ax, double ay, double bx, double by, double cx, double cy)
    {
        double dx = bx - ax, dy = by - ay;
        double ex = cx - ax, ey = cy - ay;
        double bl = dx * dx + dy * dy;
        double cl = ex * ex + ey * ey;
        double d = 0.5 / (dx * ey - dy * ex);
        return (ax + (ey * bl - dy * cl) * d, ay + (dx * cl - ex * bl) * d);
    }
}
