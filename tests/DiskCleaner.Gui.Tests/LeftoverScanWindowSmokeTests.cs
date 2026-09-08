using System.Windows;
using DiskCleaner.Gui;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

public class LeftoverScanWindowSmokeTests
{
    [Fact]
    public void LeftoverScanWindow_RendersPlanGroupsAndCards_Smoke()
    {
        StaRunner.Run(async () =>
        {
            var harness = LeftoverTestSupport.CreateHarness();
            await harness.ViewModel.ScanCommand.ExecuteAsync(null);

            AllItems(harness.ViewModel).Single(i => i.Title == "lm-studio-updater").IsSelected = true;
            var orphan = AllItems(harness.ViewModel).Single(i => i.Title == "OrphanTool");
            orphan.IsSelected = true;

            var window = new LeftoverScanWindow(harness.ViewModel)
            {
                Width = 1240,
                Height = 800
            };

            window.Show();
            await Task.Delay(50);
            window.UpdateLayout();

            Assert.Same(harness.ViewModel, window.DataContext);
            Assert.NotEmpty(window.ViewModel.Groups);
            Assert.Contains("Выбрано: 2", window.ViewModel.SelectedSummary, System.StringComparison.Ordinal);
            Assert.True(orphan.IsDangerousConfirmed);

            window.Close();
        });
    }

    private static IReadOnlyList<LeftoverItemViewModel> AllItems(LeftoverScanViewModel viewModel) =>
        viewModel.Groups.SelectMany(g => g.Items).ToList();
}
