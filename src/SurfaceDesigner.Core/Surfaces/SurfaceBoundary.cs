using SurfaceDesigner.Core.Geometry;

namespace SurfaceDesigner.Core.Surfaces;

public enum BoundaryType
{
    /// <summary>Външна граница – остава само повърхнината вътре.</summary>
    Outer,

    /// <summary>Скриваща граница – „дупка“ (сграда, водоем, зона без данни).</summary>
    Hide,

    /// <summary>Показваща граница – „остров“, който връща повърхнината вътре в скрита зона.</summary>
    Show,
}

/// <summary>
/// Граница на повърхнина – затворен многоъгълник в план. Изрязването е неразрушаващо:
/// контурът се вмъква в TIN чрез разцепване на ребрата, така че вътре интерполацията не се променя,
/// а ръбът минава точно по границата. Котите по контура се интерполират от повърхнината.
/// Границите се прилагат по ред – по-късната има предимство.
/// </summary>
public sealed class SurfaceBoundary
{
    public SurfaceBoundary(string name, BoundaryType type, IEnumerable<Point2> vertices)
    {
        Name = name;
        Type = type;
        Polygon = new Polygon2(vertices); // валидира и премахва дублирания/затварящия връх
    }

    public string Name { get; }
    public BoundaryType Type { get; }
    public Polygon2 Polygon { get; }
    public IReadOnlyList<Point2> Vertices => Polygon.Vertices;

    public SurfaceBoundary With(string? name = null, BoundaryType? type = null) =>
        new(name ?? Name, type ?? Type, Vertices);

    public static string TypeName(BoundaryType t) => t switch
    {
        BoundaryType.Outer => "външна",
        BoundaryType.Hide => "скриваща",
        BoundaryType.Show => "показваща",
        _ => t.ToString(),
    };

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Name} ({TypeName(Type)}, {Vertices.Count} т., {Polygon.Area:F1} m²)";

    /// <summary>Дали точка (център на триъгълник) е видима според набора граници.</summary>
    public static bool IsVisible(IReadOnlyList<SurfaceBoundary> boundaries, double x, double y)
    {
        bool hasOuter = false, inOuter = false;
        foreach (var b in boundaries)
        {
            if (b.Type != BoundaryType.Outer)
            {
                continue;
            }

            hasOuter = true;
            if (b.Polygon.Contains(x, y))
            {
                inOuter = true;
                break;
            }
        }

        bool outerOk = !hasOuter || inOuter;
        bool visible = outerOk;
        foreach (var b in boundaries)
        {
            if (b.Type == BoundaryType.Outer || !b.Polygon.Contains(x, y))
            {
                continue;
            }

            visible = b.Type == BoundaryType.Show && outerOk;
        }

        return visible;
    }
}
