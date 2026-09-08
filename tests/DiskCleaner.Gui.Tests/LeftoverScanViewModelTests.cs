using DiskCleaner.Core.Leftovers;
using DiskCleaner.Gui.Services;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

/// <summary>
/// Unit-тесты ViewModel экрана «Остатки» (задача фазы 5 модуля 03, #98):
/// состояние чекбоксов — всё выключено по умолчанию, опасные объекты требуют пообъектного
/// подтверждения (FR-3.8, §5); исключения по пути и по бренду с перестроением плана (FR-3.7);
/// dry-run-предпросмотр и выполнение с админ-шагами/UAC (FR-3.8, FR-3.4, §5).
/// </summary>
public class LeftoverScanViewModelTests
{
    [Fact]
    public async Task Scan_GroupsCandidatesByGroupName_AllDisabledByDefault_ShowsBasisAndDate()
    {
        var harness = LeftoverTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.HasPlan);
        Assert.True(harness.Source.Calls == 1);

        var titles = harness.ViewModel.Groups.Select(g => g.Title).ToList();
        Assert.Contains("Остатки апдейтеров", titles);
        Assert.Contains("Конфиги удалённых программ", titles);
        Assert.Contains("Осиротевшие папки в Program Files", titles);
        Assert.Contains("Осиротевшие папки в ProgramData", titles);
        Assert.Contains("Предыдущая версия Windows", titles);

        var all = harness.ViewModel.Groups.SelectMany(g => g.Items).ToList();
        Assert.All(all, i => Assert.False(i.IsSelected));
        Assert.All(all, i => Assert.False(i.Item.IsEnabled));
        Assert.Equal("Ничего не выбрано", harness.ViewModel.SelectedSummary);

        var studio = AllItems(harness.ViewModel).Single(i => i.Title == "AndroidStudio2025.3.2");
        Assert.Contains("отсутствует в реестре Uninstall", studio.ReasonText, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(studio.LastWriteText);
        Assert.Contains("2026", studio.LastWriteText, StringComparison.Ordinal);

        var orphan = AllItems(harness.ViewModel).Single(i => i.Title == "OrphanTool");
        Assert.True(orphan.RequiresAdmin);
        Assert.True(orphan.RequiresConfirmation);
        Assert.Contains("UAC", orphan.LabelsText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scan_WindowsOldCard_ShowsSizeAdminAndRecommendedRemovalMethod()
    {
        var harness = LeftoverTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var windowsOld = AllItems(harness.ViewModel).Single(i => i.Title == "Windows.old");
        Assert.True(windowsOld.RequiresAdmin);
        Assert.True(windowsOld.RequiresConfirmation);
        Assert.True(windowsOld.HasRecommendedRemovalMethod);
        Assert.Contains("Storage Sense", windowsOld.RecommendedRemovalMethod, StringComparison.Ordinal);
        Assert.Contains("ГБ", windowsOld.SizeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToggleOrdinaryUpdater_EnablesWithoutAnyConfirmation()
    {
        var harness = LeftoverTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var updater = AllItems(harness.ViewModel).Single(i => i.Title == "lm-studio-updater");
        Assert.False(updater.RequiresConfirmation);

        updater.IsSelected = true;

        Assert.True(updater.IsSelected);
        Assert.True(updater.Item.IsEnabled);
        Assert.Null(harness.Dialogs.LastConfirmMessage);
        Assert.Contains("Выбрано: 1", harness.ViewModel.SelectedSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToggleDangerousObject_RequiresPerObjectConfirmation_ConfirmedEnables()
    {
        var harness = LeftoverTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var orphan = AllItems(harness.ViewModel).Single(i => i.Title == "OrphanTool");
        Assert.True(orphan.RequiresConfirmation);

        orphan.IsSelected = true;

        Assert.True(orphan.IsSelected);
        Assert.True(orphan.IsDangerousConfirmed);
        Assert.NotNull(harness.Dialogs.LastConfirmMessage);
        Assert.Contains("пообъектного подтверждения", harness.Dialogs.LastConfirmMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UAC", harness.Dialogs.LastConfirmMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Почему это остаток", harness.Dialogs.LastConfirmMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToggleDangerousObject_DeclinedConfirmation_StaysDisabled()
    {
        var harness = LeftoverTestSupport.CreateHarness();
        harness.Dialogs.ConfirmResult = false;
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var orphan = AllItems(harness.ViewModel).Single(i => i.Title == "OrphanTool");
        orphan.IsSelected = true;

        Assert.False(orphan.IsSelected);
        Assert.False(orphan.IsDangerousConfirmed);
        Assert.NotNull(harness.Dialogs.LastConfirmMessage);
    }

    [Fact]
    public async Task GroupCheckBox_TogglesOnlyObjectsWithoutMandatoryConfirmation()
    {
        var harness = LeftoverTestSupport.CreateHarness();
        harness.Source.Handler = LeftoverTestSupport.BuildMixedGroupPlan;
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var group = harness.ViewModel.Groups.Single(g => g.Title == "Остатки апдейтеров");
        Assert.True(group.CanBulkCheck);

        group.IsChecked = true;

        var updater = group.Items.Single(i => i.Title == "qwen-updater");
        var dangerous = group.Items.Single(i => i.Title == "dangerous-updater");
        Assert.True(updater.IsSelected);
        Assert.False(dangerous.IsSelected);

        dangerous.IsSelected = true;
        Assert.True(dangerous.IsSelected);
        Assert.True(group.IsChecked); // qwen + dangerous: bulk-часть вся отмечена, состояние группы — true
        Assert.Contains("выбрано 2 из 2", group.SelectedText, StringComparison.Ordinal);

        updater.IsSelected = false;
        Assert.False(group.IsChecked); // единственный bulk-объект снят — bulk-часть пуста
    }

    [Fact]
    public async Task GroupWithOnlyDangerousObjects_HasNoBulkCheck()
    {
        var harness = LeftoverTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var orphanGroup = harness.ViewModel.Groups.Single(g => g.Title == "Осиротевшие папки в Program Files");
        Assert.False(orphanGroup.CanBulkCheck);
        Assert.True(orphanGroup.HasSubtitle);
        Assert.Contains("пообъектного подтверждения", orphanGroup.Subtitle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddExclusionByPath_StoresEntry_AndRescanDropsCandidateFromPlan()
    {
        var harness = LeftoverTestSupport.CreateHarness();
        harness.Dialogs.ExclusionResult = LeftoverExclusionChoice.Path;
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var updater = AllItems(harness.ViewModel).Single(i => i.Title == "qwen-updater");
        await harness.ViewModel.AddExclusionCommand.ExecuteAsync(updater);

        Assert.Contains(updater.PathText, harness.Exclusions.Load(), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(AllItems(harness.ViewModel), i => i.Title == "qwen-updater");
        Assert.Contains("План обновлён", harness.ViewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddExclusionByBrand_StoresBrand_AndRescanDropsCandidateFromPlan()
    {
        var harness = LeftoverTestSupport.CreateHarness();
        harness.Dialogs.ExclusionResult = LeftoverExclusionChoice.Brand;
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var updater = AllItems(harness.ViewModel).Single(i => i.Title == "qwen-updater");
        await harness.ViewModel.AddExclusionCommand.ExecuteAsync(updater);

        Assert.Contains("qwen-updater", harness.Exclusions.Load(), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(AllItems(harness.ViewModel), i => i.Title == "qwen-updater");
    }

    [Fact]
    public async Task AddExclusion_CancelledByUser_StoresNothingAndKeepsCandidate()
    {
        var harness = LeftoverTestSupport.CreateHarness();
        harness.Dialogs.ExclusionResult = LeftoverExclusionChoice.None;
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var updater = AllItems(harness.ViewModel).Single(i => i.Title == "qwen-updater");
        await harness.ViewModel.AddExclusionCommand.ExecuteAsync(updater);

        Assert.Empty(harness.Exclusions.Load());
        Assert.Contains(AllItems(harness.ViewModel), i => i.Title == "qwen-updater");
    }

    [Fact]
    public async Task PreviewDryRun_PassesDryRunFlag_ShowsReport_DeletesNothing()
    {
        var harness = LeftoverTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        AllItems(harness.ViewModel).Single(i => i.Title == "lm-studio-updater").IsSelected = true;
        await harness.ViewModel.PreviewDryRunCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Executor.Calls);
        Assert.True(harness.Executor.LastOptions!.DryRun);
        Assert.Single(harness.Executor.LastItems!);
        Assert.NotNull(harness.Dialogs.ShownReport);
        Assert.True(harness.Dialogs.ShownReport!.DryRun);
        Assert.Contains("Предпросмотр", harness.ViewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_ConfirmationShowsAdminStepsWithUac_RunsWithConfirmDangerousAndReport()
    {
        var harness = LeftoverTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var orphan = AllItems(harness.ViewModel).Single(i => i.Title == "OrphanTool");
        orphan.IsSelected = true;

        await harness.ViewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.NotNull(harness.Dialogs.LastConfirmMessage);
        Assert.Contains("Админ-шаги", harness.Dialogs.LastConfirmMessage, StringComparison.Ordinal);
        Assert.Contains("UAC", harness.Dialogs.LastConfirmMessage, StringComparison.Ordinal);
        Assert.Contains("OrphanTool", harness.Dialogs.LastConfirmMessage, StringComparison.Ordinal);

        Assert.Equal(1, harness.Executor.Calls);
        Assert.False(harness.Executor.LastOptions!.DryRun);
        Assert.True(harness.Executor.LastOptions.ConfirmDangerous);
        var item = Assert.Single(harness.Executor.LastItems!);
        Assert.Equal("OrphanTool", item.DisplayName);
        Assert.NotNull(harness.Dialogs.ShownReport);
        Assert.Contains("Удаление завершено", harness.ViewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("План обновлён", harness.ViewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal(2, harness.Source.Calls); // первичный скан + обновление плана после удаления
    }

    [Fact]
    public async Task Execute_DeclinedConfirmation_RunsNothing()
    {
        var harness = LeftoverTestSupport.CreateHarness();
        harness.Dialogs.ConfirmResult = false;
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        AllItems(harness.ViewModel).Single(i => i.Title == "lm-studio-updater").IsSelected = true;
        await harness.ViewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.Executor.Calls);
        Assert.Contains("отменено", harness.ViewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_NothingSelected_DoesNotRun()
    {
        var harness = LeftoverTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        await harness.ViewModel.ExecuteCommand.ExecuteAsync(null);
        await harness.ViewModel.PreviewDryRunCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.Executor.Calls);
        Assert.Null(harness.Dialogs.LastConfirmMessage);
    }

    [Fact]
    public async Task SelectionSummary_ReflectsExplicitChecksAndSizes()
    {
        var harness = LeftoverTestSupport.CreateHarness();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        AllItems(harness.ViewModel).Single(i => i.Title == "lm-studio-updater").IsSelected = true;
        AllItems(harness.ViewModel).Single(i => i.Title == "qwen-updater").IsSelected = true;

        Assert.Contains("Выбрано: 2", harness.ViewModel.SelectedSummary, StringComparison.Ordinal);
        Assert.Contains("ГБ", harness.ViewModel.SelectedSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_NoCandidates_ReportsEmptyPlan()
    {
        var harness = LeftoverTestSupport.CreateHarness();
        harness.Source.Handler = () => new LeftoverPlan();
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.HasPlan);
        Assert.Empty(harness.ViewModel.Groups);
        Assert.Contains("Остатки не найдены", harness.ViewModel.StatusText, StringComparison.Ordinal);
    }

    private static IReadOnlyList<LeftoverItemViewModel> AllItems(LeftoverScanViewModel viewModel) =>
        viewModel.Groups.SelectMany(g => g.Items).ToList();
}
