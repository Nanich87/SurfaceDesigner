using SurfaceDesigner.Core.Geometry;

namespace SurfaceDesigner.Core.Surfaces;

/// <summary>
/// Завъртане на ребро (размяна на диагонала на четириъгълника от двата съседни триъгълника).
/// Реброто се идентифицира с координатите на краищата си, затова операцията оцелява при
/// повторна триангулация и се прилага, докато тези два върха и реброто между тях съществуват.
/// </summary>
public readonly record struct EdgeSwap(Point2 A, Point2 B);
