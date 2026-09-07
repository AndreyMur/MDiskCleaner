using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Gui.Services;

/// <summary>Адаптер поверх <see cref="PlanExecutor"/> для экрана очистки кэшей.</summary>
public sealed class PlanExecutorCacheCleanExecutor : ICacheCleanExecutor
{
    private readonly PlanExecutor _executor;

    public PlanExecutorCacheCleanExecutor(PlanExecutor? executor = null)
    {
        _executor = executor ?? new PlanExecutor();
    }

    public Task<CleanReport> CleanAsync(
        IEnumerable<CleanupItem> selectedItems,
        CleanOptions? options = null,
        IProgress<CleanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _executor.CleanAsync(selectedItems, options, progress, cancellationToken);
}
