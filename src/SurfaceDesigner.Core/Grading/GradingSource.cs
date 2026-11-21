using System.Globalization;
using System.Text;

namespace SurfaceDesigner.Core.Grading;

/// <summary>
/// Връзка на проектна повърхнина с данните, от които е създадена (терен + платформите на проекта).
/// Свързаната повърхнина се преизчислява автоматично при промяна на платформите или на терена.
/// </summary>
public sealed class GradingSource
{
    public GradingSource(string existingSurfaceName, double gridStep, bool zoneOnly)
    {
        ExistingSurfaceName = existingSurfaceName;
        GridStep = gridStep;
        ZoneOnly = zoneOnly;
    }

    /// <summary>Име на съществуващия терен.</summary>
    public string ExistingSurfaceName { get; set; }

    /// <summary>Стъпка на мрежата по откосите, m.</summary>
    public double GridStep { get; }

    /// <summary>Само зоната на планирането (иначе – пълно копие на терена).</summary>
    public bool ZoneOnly { get; }

    /// <summary>Версия на повърхнината след последното генериране – по-голяма версия означава ръчна редакция.</summary>
    public int GeneratedVersion { get; set; } = -1;

    /// <summary>Отпечатък на входните данни при последното генериране – за пропускане на ненужни преизчисления.</summary>
    public string? LastInputKey { get; set; }

    public GradingSource Copy() => new(ExistingSurfaceName, GridStep, ZoneOnly);

    /// <summary>Отпечатък на входните данни: версия на терена + всички параметри на платформите.</summary>
    public string InputKey(Surfaces.Surface existing, IEnumerable<Platform> platforms)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(existing.Version).Append('|').Append(GridStep.ToString("R", ci)).Append('|').Append(ZoneOnly);
        foreach (var p in platforms)
        {
            sb.Append("|P:").Append(p.BaseElevation.ToString("R", ci))
              .Append(',').Append(p.SlopeXPercent.ToString("R", ci))
              .Append(',').Append(p.SlopeYPercent.ToString("R", ci))
              .Append(',').Append(p.CutSlopeRatio.ToString("R", ci))
              .Append(',').Append(p.FillSlopeRatio.ToString("R", ci));
            if (p.ReferencePoint is { } r)
            {
                sb.Append(",R").Append(r.X.ToString("R", ci)).Append(';').Append(r.Y.ToString("R", ci));
            }

            foreach (var v in p.Boundary)
            {
                sb.Append(';').Append(v.X.ToString("R", ci)).Append(' ').Append(v.Y.ToString("R", ci));
            }
        }

        return sb.ToString();
    }
}
