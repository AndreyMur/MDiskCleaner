namespace DiskCleaner.Core.Analysis;

/// <summary>
/// Параметры одного анализа для <see cref="AnalysisCoordinator"/>:
/// либо полный скан диска (корень <see cref="DiskRootPath"/>, FR-1.1/FR-1.2),
/// либо анализ «известных объектов» профиля/реестра/системы (корень не задан).
/// </summary>
public sealed class AnalysisRunOptions
{
    /// <summary>
    /// Корень сканирования (обычно корень диска, например <c>C:\</c>).
    /// Пустое значение — анализ известных объектов профиля, реестра и системных объектов
    /// (кэши, остатки, приложения, Temp/Корзина и т.п.) без полного обхода диска.
    /// </summary>
    public string? DiskRootPath { get; init; }

    /// <summary>
    /// Включать системные каталоги (<c>Windows</c>, <c>System Volume Information</c>,
    /// <c>$Recycle.Bin</c>, <c>WindowsApps</c>) в обход диска (FR-1.2). Применимо только
    /// для полного скана диска.
    /// </summary>
    public bool IncludeSystemDirectories { get; init; }

    /// <summary>Включать в план диска записи установленного ПО (FR-1.8).</summary>
    public bool IncludeInstalledApps { get; init; } = true;

    public bool IsProfileMode => string.IsNullOrWhiteSpace(DiskRootPath);
}
