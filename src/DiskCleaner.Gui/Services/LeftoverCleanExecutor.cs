using DiskCleaner.Core.Leftovers;

namespace DiskCleaner.Gui.Services;

/// <summary>Адаптер поверх <see cref="LeftoverCleanService"/> для экрана «Остатки».</summary>
public sealed class LeftoverCleanExecutor : ILeftoverCleanExecutor
{
    private readonly LeftoverCleanService _service;

    public LeftoverCleanExecutor(LeftoverCleanService? service = null)
    {
        _service = service ?? new LeftoverCleanService();
    }

    public Task<LeftoverCleanReport> CleanAsync(
        IReadOnlyList<LeftoverPlanItem> selectedItems,
        LeftoverCleanOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _service.CleanAsync(selectedItems, options, cancellationToken);
}
