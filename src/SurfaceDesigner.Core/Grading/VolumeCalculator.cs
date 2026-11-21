using SurfaceDesigner.Core.Geometry;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.Core.Grading;

/// <summary>
/// Картограма на земните маси – квадратна мрежа с обеми изкоп/насип и средна работна кота за всяка клетка.
/// Работна кота = проектна − съществуваща (+ насип, − изкоп).
/// </summary>
public sealed class CutFillGrid
{
    public CutFillGrid(double originX, double originY, double cellSize, int nx, int ny)
    {
        OriginX = originX;
        OriginY = originY;
        CellSize = cellSize;
        Nx = nx;
        Ny = ny;
        Cut = new double[nx * ny];
        Fill = new double[nx * ny];
        MeanDepth = new double[nx * ny];
        Array.Fill(MeanDepth, double.NaN);
    }

    public double OriginX { get; }
    public double OriginY { get; }
    public double CellSize { get; }
    public int Nx { get; }
    public int Ny { get; }

    /// <summary>Обем изкоп в клетката, m³.</summary>
    public double[] Cut { get; }

    /// <summary>Обем насип в клетката, m³.</summary>
    public double[] Fill { get; }

    /// <summary>Средна работна кота в клетката, m (NaN – няма данни).</summary>
    public double[] MeanDepth { get; }

    public int Index(int ix, int iy) => iy * Nx + ix;

    public Bounds2 CellBounds(int ix, int iy) =>
        new(OriginX + ix * CellSize, OriginY + iy * CellSize, OriginX + (ix + 1) * CellSize, OriginY + (iy + 1) * CellSize);
}

public sealed record VolumeResult(double Cut, double Fill, double Area, double CutArea, double FillArea, CutFillGrid Grid)
{
    /// <summary>Баланс: насип − изкоп (положителен = недостиг на земни маси).</summary>
    public double Net => Fill - Cut;
}

public static class VolumeCalculator
{
    /// <summary>
    /// Обеми между базова (съществуваща) и сравнявана (проектна) повърхнина по метода на мрежата.
    /// Всяка клетка се семплира в <paramref name="subdivisions"/>×<paramref name="subdivisions"/> точки.
    /// </summary>
    public static VolumeResult Compute(
        Func<double, double, double?> baseSurface,
        Func<double, double, double?> compareSurface,
        Bounds2 region,
        double cellSize,
        int subdivisions = 3)
    {
        if (cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
        subdivisions = Math.Max(1, subdivisions);
        if (region.IsEmpty || region.Width <= 0 || region.Height <= 0)
            return new VolumeResult(0, 0, 0, 0, 0, new CutFillGrid(0, 0, cellSize, 0, 0));

        double ox = Math.Floor(region.MinX / cellSize) * cellSize;
        double oy = Math.Floor(region.MinY / cellSize) * cellSize;
        int nx = Math.Max(1, (int)Math.Ceiling((region.MaxX - ox) / cellSize));
        int ny = Math.Max(1, (int)Math.Ceiling((region.MaxY - oy) / cellSize));
        if ((long)nx * ny > 25_000_000)
            throw new InvalidOperationException("Твърде малка клетка за тази площ – увеличете размера на клетката.");

        var grid = new CutFillGrid(ox, oy, cellSize, nx, ny);
        double sub = cellSize / subdivisions;
        double sampleArea = sub * sub;
        var area = new double[ny];
        var cutArea = new double[ny];
        var fillArea = new double[ny];

        Parallel.For(0, ny, iy =>
        {
            for (int ix = 0; ix < nx; ix++)
            {
                double cut = 0, fill = 0, depthSum = 0;
                int samples = 0;
                for (int sy = 0; sy < subdivisions; sy++)
                {
                    for (int sx = 0; sx < subdivisions; sx++)
                    {
                        double x = ox + ix * cellSize + (sx + 0.5) * sub;
                        double y = oy + iy * cellSize + (sy + 0.5) * sub;
                        var z0 = baseSurface(x, y);
                        if (z0 is null) continue;
                        var z1 = compareSurface(x, y);
                        if (z1 is null) continue;
                        double d = z1.Value - z0.Value;
                        samples++;
                        depthSum += d;
                        if (d > 0) { fill += d * sampleArea; fillArea[iy] += sampleArea; }
                        else if (d < 0) { cut -= d * sampleArea; cutArea[iy] += sampleArea; }
                    }
                }

                int idx = grid.Index(ix, iy);
                grid.Cut[idx] = cut;
                grid.Fill[idx] = fill;
                if (samples > 0) grid.MeanDepth[idx] = depthSum / samples;
                area[iy] += samples * sampleArea;
            }
        });

        return new VolumeResult(grid.Cut.Sum(), grid.Fill.Sum(), area.Sum(), cutArea.Sum(), fillArea.Sum(), grid);
    }

    /// <summary>Обеми между две TIN повърхнини в общата им област.</summary>
    public static VolumeResult Compute(Surface existing, Surface design, double cellSize, int subdivisions = 3)
    {
        existing.EnsureTriangulated();
        design.EnsureTriangulated();
        return Compute(existing.ElevationAt, design.ElevationAt, existing.Bounds.Intersect(design.Bounds), cellSize, subdivisions);
    }

    /// <summary>Обеми на вертикалното планиране (терен спрямо платформи с откоси).</summary>
    public static VolumeResult Compute(GradingModel model, double cellSize, int subdivisions = 3)
    {
        var region = model.InfluenceBounds.Intersect(model.Existing.Bounds);
        return Compute(model.Existing.ElevationAt, model.DesignElevation, region, cellSize, subdivisions);
    }
}
