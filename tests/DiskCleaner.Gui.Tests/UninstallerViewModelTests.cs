using DiskCleaner.Core.Uninstall;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

/// <summary>
/// Unit-тесты ViewModel экрана «Деинсталляция и зачистка» (задача #123):
/// состояние выбора (всё выключено по умолчанию, FR-4.2), пометки «дубль/старая версия/
/// проверить вручную» (FR-4.2/4.3), пошаговое подтверждение с объёмом и названием (FR-4.11),
/// запрос перезагрузки (FR-4.12), осиротевшие записи с подтверждением (FR-4.13) и инструкция
/// для GUI-деинсталляторов при массовом удалении SDK (FR-4.7, FR-4.11, §7).
/// </summary>
public class UninstallerViewModelTests
{
    [Fact]
    public async Task Scan_LoadsRowsDisabled_ShowsMarksSizeColumnsAndCounts()
    {
        var harness = UninstallerTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.HasPlan);
        Assert.Equal(4, harness.ViewModel.Rows.Count);

        var jdkOld = Rows(harness.ViewModel).Single(i => i.Title == "Java SE Development Kit 11.0.19");
        Assert.True(jdkOld.IsRecommended);
        Assert.True(jdkOld.IsDuplicate);
        Assert.True(jdkOld.IsOldVersion);
        Assert.Contains("дубль", jdkOld.MarksText, StringComparison.Ordinal);
        Assert.Contains("старая версия", jdkOld.MarksText, StringComparison.Ordinal);
        Assert.False(jdkOld.IsSelected);
        Assert.False(jdkOld.Item.IsEnabled);

        var review = Rows(harness.ViewModel).Single(i => i.Title == "SomeTool");
        Assert.True(review.NeedsReview);
        Assert.Contains("проверить вручную", review.MarksText, StringComparison.Ordinal);
        Assert.Equal("Проверить вручную", review.GroupName, StringComparer.Ordinal);

        Assert.All(Rows(harness.ViewModel), i => Assert.False(i.IsSelected));
        Assert.Equal("Ничего не выбрано", harness.ViewModel.SelectedSummary);

        Assert.True(harness.ViewModel.HasSdkVersions);
        Assert.Single(harness.ViewModel.SdkVersions);
        Assert.Equal(UninstallerTestSupport.SdkVersion, harness.ViewModel.SdkVersions[0].DisplayVersion);
    }

    [Fact]
    public async Task Scan_LoadsOrphanedUninstallRecordsGroup()
    {
        var harness = UninstallerTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.HasOrphans);
        var orphan = Assert.Single(harness.ViewModel.Orphans);
        Assert.Equal("Ghost", orphan.Title);
        Assert.Contains("C:\\Program Files\\GhostApp", orphan.MissingPathsText, StringComparison.Ordinal);
        Assert.False(orphan.IsSelected);
    }

    [Fact]
    public async Task Filter_Recommended_ShowsOnlyDuplicatesAndOldVersions()
    {
        var harness = UninstallerTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        harness.ViewModel.SelectedFilter = harness.ViewModel.FilterOptions.Single(o =>
            o.Kind == UninstallFilterKind.Recommended);

        var row = Assert.Single(harness.ViewModel.Rows);
        Assert.Equal("Java SE Development Kit 11.0.19", row.Title);
        Assert.True(row.IsRecommended);
        Assert.True(row.IsDuplicate && row.IsOldVersion);
    }

    [Fact]
    public async Task Filter_ReviewManually_ShowsAnomalyGroupOnly()
    {
        var harness = UninstallerTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        harness.ViewModel.SelectedFilter = harness.ViewModel.FilterOptions.Single(o =>
            o.Kind == UninstallFilterKind.ReviewManually);

        var row = Assert.Single(harness.ViewModel.Rows);
        Assert.Equal("SomeTool", row.Title);
        Assert.True(row.NeedsReview);
        Assert.Equal("Проверить вручную", row.GroupName, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Toggle_EnablesPlanItemAndUpdatesSelectionSummary()
    {
        var harness = UninstallerTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var jdkOld = Rows(harness.ViewModel).Single(i => i.Title == "Java SE Development Kit 11.0.19");
        jdkOld.IsSelected = true;

        Assert.True(jdkOld.IsSelected);
        Assert.True(jdkOld.Item.IsEnabled);
        Assert.Contains("Выбрано: 1", harness.ViewModel.SelectedSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_ConfirmsEachStepIndividually_RunsAndRefreshesList()
    {
        var harness = UninstallerTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        Rows(harness.ViewModel).Single(i => i.Title == "Java SE Development Kit 11.0.19").IsSelected = true;

        await harness.ViewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Dialogs.ConfirmStepCalls);
        Assert.Equal("Java SE Development Kit 11.0.19", harness.Dialogs.LastConfirmedStep!.DisplayName);
        Assert.NotNull(harness.Dialogs.LastConfirmMessage);
        Assert.Contains("объём записи", harness.Dialogs.LastConfirmMessage, StringComparison.Ordinal);
        Assert.Contains("UAC", harness.Dialogs.LastConfirmMessage, StringComparison.Ordinal);

        Assert.Equal(1, harness.Executor.AppsCalls);
        var app = Assert.Single(harness.Executor.LastApps!);
        Assert.Equal("Java SE Development Kit 11.0.19", app.DisplayName);
        Assert.NotNull(harness.Dialogs.ShownUninstallReport);
        Assert.Contains("Деинсталляция завершена", harness.ViewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal(2, harness.Source.Calls); // первичный скан + обновление списка после удаления
    }

    [Fact]
    public async Task Execute_DeclinedStepConfirmation_RunsNothing()
    {
        var harness = UninstallerTestSupport.CreateHarness();
        harness.Dialogs.ConfirmStepResult = false;
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        Rows(harness.ViewModel).Single(i => i.Title == "Java SE Development Kit 11.0.19").IsSelected = true;
        await harness.ViewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.Executor.AppsCalls);
        Assert.Contains("не подтверждены", harness.ViewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_ReportNeedsReboot_AsksReboot()
    {
        var harness = UninstallerTestSupport.CreateHarness();
        harness.Executor.AppsHandler = apps => UninstallerTestSupport.BuildExecutionReport(apps, rebootRequired: true);
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        Rows(harness.ViewModel).Single(i => i.Title == "Java SE Development Kit 11.0.19").IsSelected = true;
        await harness.ViewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.NotNull(harness.Dialogs.LastRebootRequest);
        Assert.True(harness.Dialogs.LastRebootRequest.Recommended);
        Assert.Contains("отложена", harness.ViewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveSdkVersion_ShowsGuiInstructionAndRunsBulkWithProgressAndRefresh()
    {
        var harness = UninstallerTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var sdk = Assert.Single(harness.ViewModel.SdkVersions);
        Assert.Same(sdk, harness.ViewModel.SelectedSdkVersion);

        await harness.ViewModel.RemoveSdkVersionCommand.ExecuteAsync(null);

        Assert.NotNull(harness.Dialogs.LastConfirmMessage);
        Assert.Contains("завершите его вручную", harness.Dialogs.LastConfirmMessage, StringComparison.Ordinal);
        Assert.Contains(UninstallerTestSupport.SdkVersion, harness.Dialogs.LastConfirmMessage, StringComparison.Ordinal);

        Assert.Equal(1, harness.Executor.SdkCalls);
        Assert.Equal(UninstallerTestSupport.SdkVersion, harness.Executor.LastSdkVersion);
        Assert.NotNull(harness.Dialogs.ShownSdkReport);
        Assert.Contains("полностью удалена", harness.ViewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal(2, harness.Source.Calls); // первичный скан + обновление после удаления
    }

    [Fact]
    public async Task RemoveSdkVersion_ReportNeedsReboot_AsksReboot()
    {
        var harness = UninstallerTestSupport.CreateHarness();
        harness.Executor.SdkHandler = version => UninstallerTestSupport.BuildSdkReport(version, rebootRequired: true);
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        await harness.ViewModel.RemoveSdkVersionCommand.ExecuteAsync(null);

        Assert.NotNull(harness.Dialogs.LastRebootRequest);
        Assert.True(harness.Dialogs.LastRebootRequest.Recommended);
    }

    [Fact]
    public async Task OrphanToggle_RequiresPerObjectConfirmation_DeclinedStaysDisabled()
    {
        var harness = UninstallerTestSupport.CreateHarness();
        harness.Dialogs.ConfirmResult = false;
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var orphan = Assert.Single(harness.ViewModel.Orphans);
        orphan.IsSelected = true;

        Assert.False(orphan.IsSelected);
        Assert.False(orphan.IsConfirmed);
        Assert.NotNull(harness.Dialogs.LastConfirmMessage);
        Assert.Contains("только запись Uninstall", harness.Dialogs.LastConfirmMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteOrphans_ConfirmsAndRemovesRegistryRecords_ShowsCleanReportAndRefreshes()
    {
        var harness = UninstallerTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var orphan = Assert.Single(harness.ViewModel.Orphans);
        orphan.IsSelected = true;
        Assert.True(orphan.IsSelected);
        Assert.True(orphan.IsConfirmed);

        await harness.ViewModel.ExecuteOrphansCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Executor.OrphansCalls);
        var match = Assert.Single(harness.Executor.LastOrphans!);
        Assert.Equal("Ghost", match.DisplayName);
        Assert.NotNull(harness.Dialogs.ShownCleanReport);
        Assert.Contains("удалено/уже отсутствует — 1", harness.ViewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal(2, harness.Source.Calls);
    }

    private static IReadOnlyList<UninstallItemViewModel> Rows(UninstallerViewModel viewModel) =>
        viewModel.Rows.ToList();
}
