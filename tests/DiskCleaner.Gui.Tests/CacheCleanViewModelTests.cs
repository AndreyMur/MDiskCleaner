using System.Threading.Tasks;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

/// <summary>
/// Unit-тесты ViewModel экрана «Очистка кэшей» (задача фазы 5 модуля 02, #73):
/// состояние чекбоксов (NFR G3), отображение последствий/предупреждений (FR-2.1/2.5),
/// отдельная группа кэшей вне сканируемого диска (FR-2.3), согласие «Ask» (FR-2.11),
/// dry-run и подтверждение перед выполнением.
/// </summary>
public class CacheCleanViewModelTests
{
    [Fact]
    public async Task Analyze_GroupsCachesByManager_WithSizeCommandAndConsequences()
    {
        var harness = CacheCleanTestSupport.CreateHarness(CacheCleanTestSupport.TypicalProfileLeaves());

        await harness.ViewModel.AnalyzeCommand.ExecuteAsync(null);

        var npmGroup = harness.ViewModel.Groups.Single(g => g.Title == "npm");
        var npm = npmGroup.Actions.Single(a => a.Title == "npm cache");

        Assert.True(npmGroup.Actions.Count == 1);
        Assert.True(npm.IsSelectable);
        Assert.True(npm.HasNativeCommand);
        Assert.Equal("cmd.exe /c npm cache clean --force", npm.NativeCommandText);
        Assert.Contains("Штатная команда", npm.MethodText, System.StringComparison.Ordinal);
        Assert.Contains("Пакеты будут загружены заново", npm.ConsequencesText, System.StringComparison.Ordinal);
        Assert.Contains("Повторная загрузка", npm.RestoreText, System.StringComparison.Ordinal);
        Assert.Contains("ГБ", npm.SizeText, System.StringComparison.Ordinal);
        Assert.True(harness.ViewModel.HasPlan);
        Assert.Equal("Ничего не выбрано", harness.ViewModel.SelectedSummary);
    }

    [Fact]
    public async Task Analyze_AskConsentCache_IsIndividuallySelectableNotBulk()
    {
        var harness = CacheCleanTestSupport.CreateHarness(CacheCleanTestSupport.TypicalProfileLeaves());
        await harness.ViewModel.AnalyzeCommand.ExecuteAsync(null);

        var gradle = harness.ViewModel.Groups.Single(g => g.Title == "Gradle");
        var jdks = gradle.Actions.Single(a => a.Title == "Gradle JDK toolchains");

        Assert.True(jdks.RequiresExplicitConsent);
        Assert.Contains("согласия", jdks.ConsentText, System.StringComparison.Ordinal);
        Assert.True(jdks.IsSelectable);
        Assert.False(jdks.IsBulkSelectable);

        gradle.IsChecked = true;

        Assert.True(gradle.Actions.Single(a => a.Title == "Gradle caches").IsSelected);
        Assert.False(jdks.IsSelected);
    }

    [Fact]
    public async Task Analyze_InUseAndBlockedCaches_AreNotSelectable()
    {
        var leaves = new[]
        {
            CacheCleanTestSupport.NpmCache(),
            CacheCleanTestSupport.VscodeGpuCache(),
            CacheCleanTestSupport.VscodeLogsInUse(),
            CacheCleanTestSupport.VscodeUserPathBlocked()
        };
        var harness = CacheCleanTestSupport.CreateHarness(leaves);
        await harness.ViewModel.AnalyzeCommand.ExecuteAsync(null);

        var vsCode = harness.ViewModel.Groups.Single(g => g.Title == "VS Code");

        var gpu = vsCode.Actions.Single(a => a.Title == "VS Code GPUCache");
        var logs = vsCode.Actions.Single(a => a.Title == "VS Code logs");
        var blocked = vsCode.Actions.Single(a => a.Action.Item.DisplayName == "VS Code User");

        Assert.True(gpu.IsSelectable);
        Assert.False(logs.IsSelectable);
        Assert.False(blocked.IsSelectable);
        Assert.Contains("используется", logs.ReasonText, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FR-2.10", blocked.ReasonText, System.StringComparison.OrdinalIgnoreCase);

        logs.IsSelected = true;
        blocked.IsSelected = true;

        Assert.False(logs.IsSelected);
        Assert.False(blocked.IsSelected);
    }

    [Fact]
    public async Task Analyze_WarningAboutRunningVSCode_IsShown()
    {
        var harness = CacheCleanTestSupport.CreateHarness(CacheCleanTestSupport.TypicalProfileLeaves());
        await harness.ViewModel.AnalyzeCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.HasWarnings);
        Assert.Contains("VS Code", harness.ViewModel.WarningsText, System.StringComparison.Ordinal);
        Assert.Contains("отложены", harness.ViewModel.WarningsText, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Analyze_OffDiskCache_ShownInSeparateGroupWithCleaningOffer()
    {
        var leaves = new[]
        {
            CacheCleanTestSupport.NpmCache(),
            CacheCleanTestSupport.PnpmStoreOnD()
        };
        var harness = CacheCleanTestSupport.CreateHarness(leaves, scannedDiskRoot: @"C:\");
        await harness.ViewModel.AnalyzeCommand.ExecuteAsync(null);

        var offDisk = harness.ViewModel.Groups.Single(g => g.Title == "Вне сканируемого диска");
        Assert.True(offDisk.IsOffDiskGroup);
        Assert.True(offDisk.HasSubtitle);

        var pnpm = offDisk.Actions.Single(a => a.Title == "pnpm: pnpm store");
        Assert.True(pnpm.IsOffDisk);
        Assert.True(pnpm.IsSelectable);
        Assert.Contains(@"D:\", pnpm.PathText, System.StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(harness.ViewModel.Groups, g => g.Title == "pnpm");
        Assert.NotNull(harness.ViewModel.ContextText);
    }

    [Fact]
    public async Task FullyCheckedGroup_NullFromThreeStateBoxClick_UnchecksBulkObjects()
    {
        var harness = CacheCleanTestSupport.CreateHarness(CacheCleanTestSupport.TypicalProfileLeaves());
        await harness.ViewModel.AnalyzeCommand.ExecuteAsync(null);

        var gradle = harness.ViewModel.Groups.Single(g => g.Title == "Gradle");
        gradle.IsChecked = true;
        var bulk = gradle.Actions.Single(a => a.Title == "Gradle caches");
        Assert.True(bulk.IsSelected);
        Assert.Equal(true, gradle.IsChecked);

        gradle.IsChecked = null;

        Assert.False(bulk.IsSelected);
        Assert.Equal(false, gradle.IsChecked);
        Assert.Contains("Ничего не выбрано", harness.ViewModel.SelectedSummary, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_NoScannedDisk_AllCachesInScope()
    {
        var leaves = new[]
        {
            CacheCleanTestSupport.NpmCache(),
            CacheCleanTestSupport.PnpmStoreOnD()
        };
        var harness = CacheCleanTestSupport.CreateHarness(leaves, scannedDiskRoot: null);
        await harness.ViewModel.AnalyzeCommand.ExecuteAsync(null);

        Assert.Contains(harness.ViewModel.Groups, g => g.Title == "npm");
        Assert.Contains(harness.ViewModel.Groups, g => g.Title == "pnpm");
        Assert.DoesNotContain(harness.ViewModel.Groups, g => g.Title == "Вне сканируемого диска");
    }

    [Fact]
    public async Task SelectionSummary_ReflectsExplicitChecks()
    {
        var harness = CacheCleanTestSupport.CreateHarness(CacheCleanTestSupport.TypicalProfileLeaves());
        await harness.ViewModel.AnalyzeCommand.ExecuteAsync(null);

        Action("npm", "npm cache").IsSelected = true;
        Action("Gradle", "Gradle caches").IsSelected = true;

        Assert.Contains("Выбрано: 2", harness.ViewModel.SelectedSummary, System.StringComparison.Ordinal);

        Action("npm", "npm cache").IsSelected = false;

        Assert.Contains("Выбрано: 1", harness.ViewModel.SelectedSummary, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Выбрано: 2", harness.ViewModel.SelectedSummary, System.StringComparison.Ordinal);

        CacheActionViewModel Action(string group, string title) =>
            harness.ViewModel.Groups
                .Single(g => g.Title == group)
                .Actions
                .Single(a => a.Title == title);
    }

    [Fact]
    public async Task PreviewDryRun_PassesDryRunFlag_AndShowsReportWithoutDeleting()
    {
        var harness = CacheCleanTestSupport.CreateHarness(CacheCleanTestSupport.TypicalProfileLeaves());
        await harness.ViewModel.AnalyzeCommand.ExecuteAsync(null);

        HarnessHelper.Select(harness.ViewModel, "npm cache", "Gradle caches");
        await harness.ViewModel.PreviewDryRunCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Executor.Calls);
        Assert.True(harness.Executor.LastOptions!.DryRun);
        Assert.Equal(2, harness.Executor.LastItems!.Count);
        Assert.NotNull(harness.Dialogs.ShownReport);
        Assert.True(harness.Dialogs.ShownReport!.DryRun);
        Assert.Contains("Предпросмотр", harness.ViewModel.StatusText, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_ConfirmsAndRuns_ShowsReportAndRefreshesPlan()
    {
        var harness = CacheCleanTestSupport.CreateHarness(CacheCleanTestSupport.TypicalProfileLeaves());
        await harness.ViewModel.AnalyzeCommand.ExecuteAsync(null);

        HarnessHelper.Select(harness.ViewModel, "npm cache");
        await harness.ViewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Executor.Calls);
        Assert.False(harness.Executor.LastOptions!.DryRun);
        Assert.Single(harness.Executor.LastItems!);
        Assert.NotNull(harness.Dialogs.LastConfirmMessage);
        Assert.Contains("Выполнить очистку 1 объектов", harness.Dialogs.LastConfirmMessage, System.StringComparison.Ordinal);
        Assert.NotNull(harness.Dialogs.ShownReport);
        Assert.Contains("Очистка завершена", harness.ViewModel.StatusText, System.StringComparison.Ordinal);
        Assert.Equal(2, harness.Coordinator.Calls);
    }

    [Fact]
    public async Task Execute_ConfirmationListsAskConsentObjects()
    {
        var harness = CacheCleanTestSupport.CreateHarness(CacheCleanTestSupport.TypicalProfileLeaves());
        await harness.ViewModel.AnalyzeCommand.ExecuteAsync(null);

        HarnessHelper.Select(harness.ViewModel, "npm cache", "Gradle JDK toolchains");
        await harness.ViewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.NotNull(harness.Dialogs.LastConfirmMessage);
        Assert.Contains("Объекты, требующие явного согласия", harness.Dialogs.LastConfirmMessage, System.StringComparison.Ordinal);
        Assert.Contains("Gradle JDK toolchains", harness.Dialogs.LastConfirmMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_DeclinedConfirmation_DoesNotRunAnything()
    {
        var harness = CacheCleanTestSupport.CreateHarness(CacheCleanTestSupport.TypicalProfileLeaves());
        harness.Dialogs.ConfirmResult = false;
        await harness.ViewModel.AnalyzeCommand.ExecuteAsync(null);

        HarnessHelper.Select(harness.ViewModel, "npm cache");
        await harness.ViewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.Executor.Calls);
        Assert.Contains("отменена", harness.ViewModel.StatusText, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_NothingSelected_DoesNotRun()
    {
        var harness = CacheCleanTestSupport.CreateHarness(CacheCleanTestSupport.TypicalProfileLeaves());
        await harness.ViewModel.AnalyzeCommand.ExecuteAsync(null);

        await harness.ViewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.Executor.Calls);
        Assert.Equal(1, harness.Coordinator.Calls); // только выполненный «Сформировать план»
    }
}
