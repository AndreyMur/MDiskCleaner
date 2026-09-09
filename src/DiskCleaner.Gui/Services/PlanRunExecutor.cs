using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Gui.Services;

/// <summary>
/// Реализация исполнителя полного плана поверх ядра <see cref="CleanPlanRunner"/> (Фаза 30,
/// FR-5.17–5.19): порядок фаз, подтверждение опасных шагов, отмена между шагами, снимок
/// свободного места до/после и итоговый отчёт с кандидатами второго эшелона.
/// </summary>
public sealed class PlanRunExecutor : IPlanRunExecutor
{
    private readonly CleanPlanRunner _runner;

    public PlanRunExecutor(CleanPlanRunner? runner = null)
    {
        _runner = runner ?? new CleanPlanRunner();
    }

    public Task<CleanPlanRunReport> RunAsync(
        IEnumerable<CleanupItem> planItems,
        CleanPlanRunOptions? options = null,
        IProgress<CleanPlanRunProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _runner.RunAsync(planItems, options, progress, cancellationToken);
}
