using SurfaceDesigner.Core.Grading;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.Core;

/// <summary>Проект: набор от повърхнини и платформи за вертикално планиране.</summary>
public sealed class Project
{
    public List<Surface> Surfaces { get; } = new();
    public List<Platform> Platforms { get; } = new();

    /// <summary>Настройки на изгледа (отметките в „Изглед“, оцветяване, картограма).</summary>
    public ViewSettings View { get; set; } = new();

    /// <summary>Име на повърхнината, използвана като съществуващ терен при вертикалното планиране.</summary>
    public string? ExistingSurfaceName { get; set; }

    public Surface? ExistingSurface =>
        Surfaces.FirstOrDefault(s => s.Name == ExistingSurfaceName) ?? Surfaces.FirstOrDefault();

    public Surface? FindSurface(string name) => Surfaces.FirstOrDefault(s => s.Name == name);

    /// <summary>Уникално име на повърхнина на база на подаденото.</summary>
    public string UniqueSurfaceName(string baseName)
    {
        if (FindSurface(baseName) == null) return baseName;
        for (int i = 2; ; i++)
        {
            var n = $"{baseName} ({i})";
            if (FindSurface(n) == null) return n;
        }
    }
}
