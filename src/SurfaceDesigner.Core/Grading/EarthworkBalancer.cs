using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.Core.Grading;

public sealed record BalanceResult(double BaseElevation, VolumeResult Volumes, int Iterations, bool Converged);

/// <summary>
/// Търси проектна кота на платформа, при която изкопът (умножен по коефициента на разрохкване)
/// е равен на насипа – т.е. земните маси са балансирани.
/// </summary>
public static class EarthworkBalancer
{
    public static BalanceResult Balance(
        Surface existing,
        IReadOnlyList<Platform> platforms,
        Platform target,
        double cellSize,
        double swellFactor = 1.0,
        double tolerance = 0.001,
        int subdivisions = 2)
    {
        if (!platforms.Contains(target)) throw new ArgumentException("Платформата не е от списъка.", nameof(target));

        var stats = existing.GetStatistics();
        if (stats.PointCount < 3) throw new InvalidOperationException("Съществуващият терен няма достатъчно точки.");

        double original = target.BaseElevation;

        double Imbalance(double z)
        {
            target.BaseElevation = z;
            var v = VolumeCalculator.Compute(new GradingModel(existing, platforms), cellSize, subdivisions);
            return v.Cut * swellFactor - v.Fill; // намалява с повишаване на котата
        }

        double span = Math.Max(stats.MaxZ - stats.MinZ, 1.0);
        double lo = stats.MinZ - span, hi = stats.MaxZ + span;
        double fLo = Imbalance(lo), fHi = Imbalance(hi);
        int guard = 0;
        while (fLo < 0 && guard++ < 10) { lo -= span; fLo = Imbalance(lo); }
        guard = 0;
        while (fHi > 0 && guard++ < 10) { hi += span; fHi = Imbalance(hi); }

        if (fLo < 0 || fHi > 0)
        {
            target.BaseElevation = original;
            throw new InvalidOperationException("Не може да се намери балансираща кота.");
        }

        int iterations = 0;
        bool converged = false;
        while (iterations++ < 60)
        {
            double mid = (lo + hi) / 2;
            double f = Imbalance(mid);
            if (f > 0) lo = mid; else hi = mid;
            if (hi - lo < tolerance) { converged = true; break; }
        }

        double result = Math.Round((lo + hi) / 2, 3);
        target.BaseElevation = result;
        var volumes = VolumeCalculator.Compute(new GradingModel(existing, platforms), cellSize, subdivisions + 1);
        return new BalanceResult(result, volumes, iterations, converged);
    }
}
