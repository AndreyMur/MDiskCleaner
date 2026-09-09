using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Models;
using DiskCleaner.Gui.Services;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

/// <summary>Контекст теста выполнения полного плана главного экрана (фаза 5 модуля 05).</summary>
internal sealed record MainPlanHarness(
    FakeAnalysisCoordinator Coordinator,
    FakePlanRunExecutor Executor,
    FakePlanRunDialogs Dialogs,
    MainViewModel ViewModel);

/// <summary>
/// Подмены и синтетические данные для тестов выполнения полного плана (FR-5.14–5.19):
/// фейковые исполнитель плана и диалоги, чтобы unit-тесты не выполняли реальных операций.
/// </summary>
internal static class PlanRunTestSupport
{
    public static MainPlanHarness Create(AnalysisResult analysis)
    {
        var coordinator = new FakeAnalysisCoordinator
        {
            Handler = _ => analysis
        };
        var executor = new FakePlanRunExecutor();
        var dialogs = new FakePlanRunDialogs();
        var vm = MainViewModelFactory.Create(coordinator, planExecutor: executor, planDialogs: dialogs);
        return new MainPlanHarness(coordinator, executor, dialogs, vm);
    }

    public static AnalysisResult TreeOf(IReadOnlyList<CleanupItem> leaves) =>
        new()
        {
            CategoryTree = new TreeBuilder().Build(leaves),
            Items = leaves,
            Elapsed = TimeSpan.FromMilliseconds(80),
            InUseItems = leaves.Count(l => l.InUse)
        };

    public static CleanupItem Leaf(
        string key,
        string displayName,
        CleanupCategory category,
        CleanupRisk risk,
        long? sizeBytes,
        string? group = null,
        bool inUse = false,
        bool requiresAdmin = false,
        string? warning = null)
    {
        return new CleanupItem
        {
            Key = key,
            Path = @"C:\fake\" + key,
            DisplayName = displayName,
            GroupName = group,
            Category = category,
            Risk = risk,
            Target = CleanupTarget.Directory,
            SizeBytes = sizeBytes,
            InUse = inUse,
            RequiresAdmin = requiresAdmin,
            Warning = warning
        };
    }
}

/// <summary>Фейковый исполнитель полного плана: запоминает вход и возвращает синтетический отчёт.</summary>
internal sealed class FakePlanRunExecutor : IPlanRunExecutor
{
    public Func<IReadOnlyList<CleanupItem>, CleanPlanRunOptions, CleanPlanRunReport>? Handler { get; set; }

    public int Calls { get; private set; }

    public IReadOnlyList<CleanupItem>? LastItems { get; private set; }

    public CleanPlanRunOptions? LastOptions { get; private set; }

    public Task<CleanPlanRunReport> RunAsync(
        IEnumerable<CleanupItem> planItems,
        CleanPlanRunOptions? options = null,
        IProgress<CleanPlanRunProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastItems = planItems.ToList();
        LastOptions = options ?? new CleanPlanRunOptions();
        var report = Handler?.Invoke(LastItems, LastOptions) ?? DefaultReport(LastItems, LastOptions);
        return Task.FromResult(report);
    }

    public static CleanPlanRunReport DefaultReport(
        IReadOnlyList<CleanupItem> items,
        CleanPlanRunOptions options)
    {
        var entries = items.Select(item => new CleanEntry(
            item,
            options.DryRun ? CleanOutcome.DryRun : CleanOutcome.DirectDeleted,
            options.DryRun || item.InUse ? 0 : Math.Max(0, item.EffectiveSizeBytes),
            options.DryRun ? "Будет очищено (dry-run)" : "Удалено")).ToList();

        var planned = items.Sum(i => Math.Max(0, i.EffectiveSizeBytes));
        var freed = entries.Sum(e => Math.Max(0, e.FreedBytes));

        return CleanPlanRunReportBuilder.Build(
            CleanPlanPhases.GroupByPhase(items),
            entries,
            Array.Empty<DriveFreeSpaceSnapshot>(),
            Array.Empty<CleanupItem>(),
            planned,
            freed,
            options.DryRun,
            canceled: false,
            TimeSpan.FromMilliseconds(20));
    }
}

/// <summary>Фейковые диалоги плана: запоминают подтверждаемый шаг и показанный отчёт.</summary>
internal sealed class FakePlanRunDialogs : IPlanRunDialogService
{
    public bool ConfirmResult { get; set; } = true;

    public CleanupItem? LastConfirmedItem { get; private set; }

    public int ConfirmCalls { get; private set; }

    public CleanPlanRunReport? ShownReport { get; private set; }

    public int ShowReportCalls { get; private set; }

    public Task<bool> ConfirmDangerousStepAsync(CleanupItem item)
    {
        ConfirmCalls++;
        LastConfirmedItem = item;
        return Task.FromResult(ConfirmResult);
    }

    public void ShowRunReport(CleanPlanRunReport report)
    {
        ShowReportCalls++;
        ShownReport = report;
    }
}
