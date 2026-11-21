using SurfaceDesigner.Core.Geometry;

namespace SurfaceDesigner.Core.Grading;

/// <summary>
/// Проектна площадка (платформа) за вертикално планиране.
/// Повърхността ѝ е равнина: Z = Кота + iX·(x − x0) + iY·(y − y0),
/// където iX и iY са надлъжните наклони (в %), а (x0, y0) е референтната точка.
/// Извън контура терена се „догонва“ с откоси: изкоп 1:m и насип 1:m.
/// </summary>
public sealed class Platform
{
    public string Name { get; set; } = "Платформа";

    /// <summary>Контур на платформата.</summary>
    public List<Point2> Boundary { get; set; } = new();

    /// <summary>Проектна кота в референтната точка, m.</summary>
    public double BaseElevation { get; set; }

    /// <summary>Референтна точка на котата; при null се използва центърът на тежестта на контура.</summary>
    public Point2? ReferencePoint { get; set; }

    /// <summary>Наклон по оста X (изток), % (положителен = расте на изток).</summary>
    public double SlopeXPercent { get; set; }

    /// <summary>Наклон по оста Y (север), % (положителен = расте на север).</summary>
    public double SlopeYPercent { get; set; }

    /// <summary>Откос в изкоп – 1:m (m хоризонтално на 1 вертикално).</summary>
    public double CutSlopeRatio { get; set; } = 1.0;

    /// <summary>Откос в насип – 1:m (m хоризонтално на 1 вертикално).</summary>
    public double FillSlopeRatio { get; set; } = 1.5;

    public Polygon2 GetPolygon() => new(Boundary);

    public Point2 GetReferencePoint() => ReferencePoint ?? GetPolygon().Centroid;

    public double PlaneElevation(double x, double y)
    {
        var r = GetReferencePoint();
        return BaseElevation + SlopeXPercent / 100.0 * (x - r.X) + SlopeYPercent / 100.0 * (y - r.Y);
    }

    public Platform Clone() => new()
    {
        Name = Name,
        Boundary = new List<Point2>(Boundary),
        BaseElevation = BaseElevation,
        ReferencePoint = ReferencePoint,
        SlopeXPercent = SlopeXPercent,
        SlopeYPercent = SlopeYPercent,
        CutSlopeRatio = CutSlopeRatio,
        FillSlopeRatio = FillSlopeRatio,
    };
}
