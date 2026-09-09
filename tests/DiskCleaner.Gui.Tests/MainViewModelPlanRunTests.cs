using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Gui.Tests;

/// <summary>
/// Тесты «Выполнить план» главного экрана (задачи #151/#152/#153, FR-5.14–5.19): план
/// исполняется исполнителем фаз (кэши → остатки → ПО → системные шаги), опасные шаги
/// подтверждаются по одному, dry-run не спрашивает подтверждений, отмена между шагами
/// сохраняет выполненные фазы и не пересобирает план; итоговый отчёт показывается в GUI.
/// </summary>
public class MainViewModelPlanRunTests
{
    [Fact]
    public async Task DryRun_DoesNotAskConfirmations_RunsExecutorAndShowsReport()
    {
        var (coordinator, executor, dialogs, vm) = PlanRunTestSupport.Create(SamplePlan.Build());
        await vm.AnalyzeCommand.ExecuteAsync(null);
        vm.DryRun = true;

        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Equal(1, executor.Calls);
        Assert.NotNull(executor.LastOptions);
        Assert.True(executor.LastOptions!.DryRun);
        Assert.Null(executor.LastOptions.ConfirmDangerousStep);
        Assert.Equal(2, executor.LastItems!.Count);

        Assert.Equal(1, dialogs.ShowReportCalls);
        Assert.NotNull(dialogs.ShownReport);
        Assert.Equal(1, coordinator.Calls);
        Assert.Contains("Предпросмотр (dry-run)", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalRun_ConfirmationCallbackWired_ShowsReportAndRefreshesPlan()
    {
        var (coordinator, executor, dialogs, vm) = PlanRunTestSupport.Create(SamplePlan.Build());
        await vm.AnalyzeCommand.ExecuteAsync(null);

        var leaves = vm.RootNodes.SelectMany(n => n.GetLeaves()).ToList();
        var recycle = leaves.Single(l => l.Item.Key == "recycle:c");
        recycle.IsChecked = true;

        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Equal(1, executor.Calls);
        Assert.False(executor.LastOptions!.DryRun);
        Assert.NotNull(executor.LastOptions.ConfirmDangerousStep);
        Assert.Contains(executor.LastItems!, i => i.Key == "recycle:c");

        var callback = executor.LastOptions.ConfirmDangerousStep!;
        var cacheItem = executor.LastItems!.Single(i => i.Key == "cache:npm");

        dialogs.ConfirmResult = false;
        Assert.False(await callback(cacheItem));
        Assert.Same(cacheItem, dialogs.LastConfirmedItem);

        dialogs.ConfirmResult = true;
        Assert.True(await callback(cacheItem));
        Assert.Equal(2, dialogs.ConfirmCalls);

        Assert.Equal(1, dialogs.ShowReportCalls);
        Assert.NotNull(dialogs.ShownReport);
        Assert.Equal(2, coordinator.Calls);
    }

    [Fact]
    public async Task CanceledRun_ShowsReport_DoesNotRefreshAndSummarizes()
    {
        var (coordinator, executor, dialogs, vm) = PlanRunTestSupport.Create(SamplePlan.Build());
        executor.Handler = (items, options) =>
        {
            var report = FakePlanRunExecutor.DefaultReport(items, options);
            return new CleanPlanRunReport
            {
                Entries = report.Entries,
                Phases = report.Phases,
                PlannedBytes = report.PlannedBytes,
                FreedBytes = report.FreedBytes,
                DiskFreedBytes = report.DiskFreedBytes,
                DriveSnapshots = report.DriveSnapshots,
                SkippedOrBlocked = report.SkippedOrBlocked,
                SecondEchelonCandidates = report.SecondEchelonCandidates,
                DryRun = false,
                Canceled = true,
                Elapsed = report.Elapsed
            };
        };

        await vm.AnalyzeCommand.ExecuteAsync(null);
        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.ShowReportCalls);
        Assert.NotNull(dialogs.ShownReport);
        Assert.True(dialogs.ShownReport!.Canceled);
        Assert.Equal(1, coordinator.Calls);
        Assert.Contains("прервано между шагами", vm.StatusText, StringComparison.Ordinal);
    }
}
