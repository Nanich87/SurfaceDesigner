using SurfaceDesigner.Core.Geometry;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.Core.Grading;

/// <summary>
/// Проектна (планирана) повърхнина като функция: съществуващ терен + платформи + откоси.
/// Обектът е неизменяем снимков модел – при промяна на платформите се създава нов.
/// </summary>
public sealed class GradingModel
{
    private readonly Surface _existing;
    private readonly Prepared[] _platforms;

    public GradingModel(Surface existing, IEnumerable<Platform> platforms)
    {
        _existing = existing;
        _existing.EnsureTriangulated();
        var stats = existing.GetStatistics();
        double egMin = double.IsNaN(stats.MinZ) ? 0 : stats.MinZ;
        double egMax = double.IsNaN(stats.MaxZ) ? 0 : stats.MaxZ;

        _platforms = platforms
            .Where(p => p.Boundary.Count >= 3)
            .Select(p => new Prepared(p, egMin, egMax))
            .ToArray();

        var b = Bounds2.Empty;
        foreach (var p in _platforms)
        {
            b = b.Union(p.InfluenceBounds);
        }

        InfluenceBounds = b;
    }

    public Surface Existing => _existing;

    /// <summary>Област, извън която проектът съвпада със съществуващия терен.</summary>
    public Bounds2 InfluenceBounds { get; }

    public bool IsInsidePlatform(double x, double y) =>
        _platforms.Any(p => p.Polygon.Bounds.Contains(x, y) && p.Polygon.Contains(x, y));

    /// <summary>Проектна кота или null, ако точката е извън терена и извън платформите.</summary>
    public double? DesignElevation(double x, double y)
    {
        foreach (var p in _platforms)
        {
            if (p.Polygon.Bounds.Contains(x, y) && p.Polygon.Contains(x, y))
            {
                return p.Plane(x, y);
            }
        }

        var eg = _existing.ElevationAt(x, y);
        if (eg is null)
        {
            return null;
        }

        double z = eg.Value;

        double best = z, bestDev = 0;
        foreach (var p in _platforms)
        {
            if (!p.InfluenceBounds.Contains(x, y))
            {
                continue;
            }

            double r = p.SlopeElevation(x, y, z);
            double dev = Math.Abs(r - z);
            if (dev > bestDev)
            {
                bestDev = dev;
                best = r;
            }
        }

        return best;
    }

    private sealed class Prepared
    {
        private readonly double _base;
        private readonly double _ix;
        private readonly double _iy;
        private readonly double _rx;
        private readonly double _ry;
        private readonly double _mCut;
        private readonly double _mFill;

        public Prepared(Platform p, double egMin, double egMax)
        {
            Polygon = p.GetPolygon();
            var r = p.ReferencePoint ?? Polygon.Centroid;
            _rx = r.X;
            _ry = r.Y;
            _base = p.BaseElevation;
            _ix = p.SlopeXPercent / 100.0;
            _iy = p.SlopeYPercent / 100.0;
            _mCut = Math.Max(p.CutSlopeRatio, 1e-3);
            _mFill = Math.Max(p.FillSlopeRatio, 1e-3);

            double pmin = double.PositiveInfinity, pmax = double.NegativeInfinity;
            foreach (var v in Polygon.Vertices)
            {
                double z = Plane(v.X, v.Y);
                pmin = Math.Min(pmin, z);
                pmax = Math.Max(pmax, z);
            }

            // Максимално разстояние, до което откосът може да достигне терена
            double reach = Math.Max(Math.Max(egMax - pmin, 0) * _mCut, Math.Max(pmax - egMin, 0) * _mFill);
            InfluenceBounds = Polygon.Bounds.Inflate(reach + 1e-6);
        }

        public Polygon2 Polygon { get; }
        public Bounds2 InfluenceBounds { get; }

        public double Plane(double x, double y) => _base + _ix * (x - _rx) + _iy * (y - _ry);

        /// <summary>Кота по откоса (или терена, ако откосът вече го е достигнал).</summary>
        public double SlopeElevation(double x, double y, double eg)
        {
            var (q, d) = Polygon.NearestOnBoundary(x, y);
            double zq = Plane(q.X, q.Y);
            double zCut = zq + d / _mCut;   // откос в изкоп се издига навън
            double zFill = zq - d / _mFill; // откос в насип се спуска навън
            if (eg > zCut)
            {
                return zCut;
            }

            if (eg < zFill)
            {
                return zFill;
            }

            return eg;
        }
    }
}
