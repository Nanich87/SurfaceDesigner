using System.Text.Json;
using SurfaceDesigner.Core.Geometry;
using SurfaceDesigner.Core.Grading;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.Core.IO;

/// <summary>Запис и четене на проект във формат JSON (*.sdproj).</summary>
public static class ProjectSerializer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static void Save(Project project, string path) => File.WriteAllText(path, ToJson(project));

    public static Project Load(string path) => FromJson(File.ReadAllText(path));

    public static string ToJson(Project project)
    {
        var dto = new ProjectDto
        {
            Version = 1,
            ExistingSurfaceName = project.ExistingSurfaceName,
            View = project.View,
            Surfaces = project.Surfaces.Select(s => new SurfaceDto
            {
                Name = s.Name,
                MaxEdgeLength = s.MaxEdgeLength,
                IsDesign = s.IsDesign,
                IsVisible = s.IsVisible ? null : false,
                ContourInterval = s.ContourInterval,
                MajorContourEvery = s.MajorContourEvery,
                Link = s.GradingSource is { } g ? new LinkDto { Existing = g.ExistingSurfaceName, GridStep = g.GridStep, ZoneOnly = g.ZoneOnly } : null,
                Points = s.Points.Select(p => new[] { p.X, p.Y, p.Z }).ToList(),
                Breaklines = s.Breaklines.Count == 0 ? null : s.Breaklines.Select(b => new BreaklineDto
                {
                    Name = b.Name,
                    Points = b.Vertices.Select(p => new[] { p.X, p.Y, p.Z }).ToList(),
                }).ToList(),
                Boundaries = s.Boundaries.Count == 0 ? null : s.Boundaries.Select(b => new BoundaryDto
                {
                    Name = b.Name,
                    Type = b.Type.ToString(),
                    Points = b.Vertices.Select(p => new[] { p.X, p.Y }).ToList(),
                }).ToList(),
                EdgeSwaps = s.EdgeSwaps.Count == 0 ? null : s.EdgeSwaps.Select(e => new[] { e.A.X, e.A.Y, e.B.X, e.B.Y }).ToList(),
            }).ToList(),
            Platforms = project.Platforms.Select(p => new PlatformDto
            {
                Name = p.Name,
                Boundary = p.Boundary.Select(v => new[] { v.X, v.Y }).ToList(),
                BaseElevation = p.BaseElevation,
                ReferencePoint = p.ReferencePoint is { } r ? new[] { r.X, r.Y } : null,
                SlopeXPercent = p.SlopeXPercent,
                SlopeYPercent = p.SlopeYPercent,
                CutSlopeRatio = p.CutSlopeRatio,
                FillSlopeRatio = p.FillSlopeRatio,
            }).ToList(),
        };
        return JsonSerializer.Serialize(dto, Options);
    }

    public static Project FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<ProjectDto>(json) ?? throw new InvalidDataException("Невалиден файл на проект.");
        var project = new Project { ExistingSurfaceName = dto.ExistingSurfaceName, View = dto.View ?? new ViewSettings() };
        foreach (var s in dto.Surfaces ?? new())
        {
            var surface = new Surface(s.Name ?? "Повърхнина",
                (s.Points ?? new()).Where(a => a.Length >= 3).Select(a => new Point3(a[0], a[1], a[2])))
            {
                MaxEdgeLength = s.MaxEdgeLength,
                IsDesign = s.IsDesign,
                IsVisible = s.IsVisible ?? true,
                // собствени настройки; при по-стари проекти – общите за проекта (или стойностите по подразбиране)
                ContourInterval = s.ContourInterval ?? dto.ContourInterval ?? 0.5,
                MajorContourEvery = s.MajorContourEvery ?? dto.MajorContourEvery ?? 5,
            };
            var lines = (s.Breaklines ?? new())
                .Select(b => (b.Name, Pts: (b.Points ?? new()).Where(a => a.Length >= 3).Select(a => new Point3(a[0], a[1], a[2])).ToList()))
                .Where(b => b.Pts.Count >= 2)
                .Select(b => new Breakline(b.Name ?? "Структурна линия", b.Pts));
            surface.SetBreaklines(lines);
            var boundaries = (s.Boundaries ?? new())
                .Select(b => (b.Name, b.Type, Pts: (b.Points ?? new()).Where(a => a.Length >= 2).Select(a => new Point2(a[0], a[1])).ToList()))
                .Where(b => b.Pts.Count >= 3)
                .Select(b => new SurfaceBoundary(b.Name ?? "Граница",
                    Enum.TryParse<BoundaryType>(b.Type, out var t) ? t : BoundaryType.Outer, b.Pts));
            surface.SetBoundaries(boundaries);
            if (s.Link is { Existing: { } linkEg } link && link.GridStep > 0)
                surface.GradingSource = new GradingSource(linkEg, link.GridStep, link.ZoneOnly);
            surface.SetEdgeSwaps((s.EdgeSwaps ?? new()).Where(a => a.Length >= 4)
                .Select(a => new EdgeSwap(new Point2(a[0], a[1]), new Point2(a[2], a[3]))));
            project.Surfaces.Add(surface);
        }
        foreach (var p in dto.Platforms ?? new())
        {
            project.Platforms.Add(new Platform
            {
                Name = p.Name ?? "Платформа",
                Boundary = (p.Boundary ?? new()).Where(a => a.Length >= 2).Select(a => new Point2(a[0], a[1])).ToList(),
                BaseElevation = p.BaseElevation,
                ReferencePoint = p.ReferencePoint is { Length: >= 2 } r ? new Point2(r[0], r[1]) : null,
                SlopeXPercent = p.SlopeXPercent,
                SlopeYPercent = p.SlopeYPercent,
                CutSlopeRatio = p.CutSlopeRatio,
                FillSlopeRatio = p.FillSlopeRatio,
            });
        }
        return project;
    }

    private sealed class ProjectDto
    {
        public int Version { get; set; }
        public string? ExistingSurfaceName { get; set; }
        public ViewSettings? View { get; set; }        // липсва в по-стари проекти → по подразбиране
        public double? ContourInterval { get; set; }   // само за четене на проекти с обща стъпка
        public int? MajorContourEvery { get; set; }
        public List<SurfaceDto>? Surfaces { get; set; }
        public List<PlatformDto>? Platforms { get; set; }
    }

    private sealed class SurfaceDto
    {
        public string? Name { get; set; }
        public double MaxEdgeLength { get; set; }
        public bool IsDesign { get; set; }
        public bool? IsVisible { get; set; } // липсва в по-стари проекти → видима
        public double? ContourInterval { get; set; }
        public int? MajorContourEvery { get; set; }
        public LinkDto? Link { get; set; }
        public List<double[]>? Points { get; set; }
        public List<BreaklineDto>? Breaklines { get; set; }
        public List<BoundaryDto>? Boundaries { get; set; }
        public List<double[]>? EdgeSwaps { get; set; }
    }

    private sealed class LinkDto
    {
        public string? Existing { get; set; }
        public double GridStep { get; set; }
        public bool ZoneOnly { get; set; } = true;
    }

    private sealed class BoundaryDto
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public List<double[]>? Points { get; set; }
    }

    private sealed class BreaklineDto
    {
        public string? Name { get; set; }
        public List<double[]>? Points { get; set; }
    }

    private sealed class PlatformDto
    {
        public string? Name { get; set; }
        public List<double[]>? Boundary { get; set; }
        public double BaseElevation { get; set; }
        public double[]? ReferencePoint { get; set; }
        public double SlopeXPercent { get; set; }
        public double SlopeYPercent { get; set; }
        public double CutSlopeRatio { get; set; } = 1.0;
        public double FillSlopeRatio { get; set; } = 1.5;
    }
}
