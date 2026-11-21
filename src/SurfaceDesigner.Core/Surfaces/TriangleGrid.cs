using SurfaceDesigner.Core.Geometry;

namespace SurfaceDesigner.Core.Surfaces;

/// <summary>Равномерна мрежа от клетки за бързо намиране на триъгълника, съдържащ дадена точка.</summary>
internal sealed class TriangleGrid
{
    private readonly Point3[] _points;
    private readonly int[] _tris;
    private readonly double _minX, _minY, _cell;
    private readonly int _nx, _ny;
    private readonly int[] _cellStart; // CSR
    private readonly int[] _cellItems;

    public TriangleGrid(Point3[] points, int[] triangles)
    {
        _points = points;
        _tris = triangles;
        int triCount = triangles.Length / 3;

        var b = Bounds2.Empty;
        foreach (var t in triangles) b = b.Include(points[t].X, points[t].Y);
        if (triCount == 0 || b.IsEmpty)
        {
            _nx = _ny = 0;
            _cellStart = new int[1];
            _cellItems = Array.Empty<int>();
            return;
        }

        double w = Math.Max(b.Width, 1e-9), h = Math.Max(b.Height, 1e-9);
        _cell = Math.Max(Math.Sqrt(w * h / Math.Max(triCount, 1)) * 1.5, 1e-6);
        _minX = b.MinX;
        _minY = b.MinY;
        _nx = Math.Clamp((int)(w / _cell) + 1, 1, 4096);
        _ny = Math.Clamp((int)(h / _cell) + 1, 1, 4096);
        _cell = Math.Max(w / _nx, h / _ny) * (1 + 1e-9);
        if (_cell <= 0) _cell = 1;

        var counts = new int[_nx * _ny + 1];
        ForEachCell((ix, iy, _) => counts[iy * _nx + ix + 1]++);
        for (int i = 1; i < counts.Length; i++) counts[i] += counts[i - 1];
        _cellStart = counts;
        _cellItems = new int[counts[^1]];
        var fill = new int[_nx * _ny];
        ForEachCell((ix, iy, t) =>
        {
            int c = iy * _nx + ix;
            _cellItems[_cellStart[c] + fill[c]++] = t;
        });
    }

    private void ForEachCell(Action<int, int, int> action)
    {
        int triCount = _tris.Length / 3;
        for (int t = 0; t < triCount; t++)
        {
            var a = _points[_tris[3 * t]];
            var b = _points[_tris[3 * t + 1]];
            var c = _points[_tris[3 * t + 2]];
            int x0 = CellX(Math.Min(a.X, Math.Min(b.X, c.X)));
            int x1 = CellX(Math.Max(a.X, Math.Max(b.X, c.X)));
            int y0 = CellY(Math.Min(a.Y, Math.Min(b.Y, c.Y)));
            int y1 = CellY(Math.Max(a.Y, Math.Max(b.Y, c.Y)));
            for (int iy = y0; iy <= y1; iy++)
                for (int ix = x0; ix <= x1; ix++)
                    action(ix, iy, t);
        }
    }

    private int CellX(double x) => Math.Clamp((int)((x - _minX) / _cell), 0, _nx - 1);
    private int CellY(double y) => Math.Clamp((int)((y - _minY) / _cell), 0, _ny - 1);

    public int Find(double x, double y, out double l1, out double l2, out double l3)
    {
        l1 = l2 = l3 = 0;
        if (_nx == 0) return -1;
        double fx = (x - _minX) / _cell, fy = (y - _minY) / _cell;
        if (fx < -1e-9 || fy < -1e-9 || fx > _nx + 1e-9 || fy > _ny + 1e-9) return -1;
        int c = CellY(y) * _nx + CellX(x);
        for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
        {
            int t = _cellItems[k];
            if (Barycentric(t, x, y, out l1, out l2, out l3)) return t;
        }
        return -1;
    }

    private bool Barycentric(int t, double x, double y, out double l1, out double l2, out double l3)
    {
        var a = _points[_tris[3 * t]];
        var b = _points[_tris[3 * t + 1]];
        var c = _points[_tris[3 * t + 2]];
        double det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (Math.Abs(det) < 1e-18) { l1 = l2 = l3 = 0; return false; }
        l1 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / det;
        l2 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / det;
        l3 = 1 - l1 - l2;
        const double tol = -1e-9;
        return l1 >= tol && l2 >= tol && l3 >= tol;
    }
}
