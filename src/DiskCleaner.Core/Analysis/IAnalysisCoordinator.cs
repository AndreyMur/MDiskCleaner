using DiskCleaner.Core.Scanning;

namespace DiskCleaner.Core.Analysis;

/// <summary>
/// Единая точка запуска анализа для GUI (FR-1.1, FR-1.11): выбирает источник
/// (полный скан диска или известные объекты профиля/реестра/системы), выполняет
/// скан в фоне с прогрессом и отменой и возвращает готовый план для отображения.
/// GUI зависит только от этого интерфейса (тонкий слой без бизнес-логики).
/// </summary>
public interface IAnalysisCoordinator
{
    Task<AnalysisResult> RunAsync(
        AnalysisRunOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
