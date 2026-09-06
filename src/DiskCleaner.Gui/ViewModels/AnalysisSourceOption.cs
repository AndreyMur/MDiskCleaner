namespace DiskCleaner.Gui.ViewModels;

/// <summary>
/// Вариант источника анализа на экране «Анализ» (FR-1.1): либо диск/корень для полного
/// скана, либо режим «известные объекты профиля и системы» (кэши, реестр, остатки, системные
/// объекты) без полного обхода диска.
/// </summary>
public sealed class AnalysisSourceOption
{
    public required string DisplayName { get; init; }

    /// <summary>Корень сканирования. Пустое значение — режим профиля/известных объектов.</summary>
    public string? RootPath { get; init; }

    public bool IsDiskSource => !string.IsNullOrEmpty(RootPath);

    public override string ToString() => DisplayName;
}
