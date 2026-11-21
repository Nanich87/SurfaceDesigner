namespace SurfaceDesigner.Core;

/// <summary>
/// Настройки на изгледа в плана – записват се с проекта и се отменят с „Отмени“.
/// Запис (record) – сравнява се по стойност.
/// </summary>
public sealed record ViewSettings
{
    public bool ShowPoints { get; init; } = true;
    public bool ShowTriangles { get; init; } = true;
    public bool ShowContours { get; init; } = true;
    public bool ShowContourLabels { get; init; } = true;

    /// <summary>Надписи и по хоризонталите на другите видими повърхнини.</summary>
    public bool ShowBackgroundLabels { get; init; } = true;

    /// <summary>Картограма на земните маси.</summary>
    public bool ShowCutFill { get; init; } = true;

    /// <summary>Оцветяване: 0 – без, 1 – по кота, 2 – по наклон.</summary>
    public int ColorMode { get; init; } = 1;
}
