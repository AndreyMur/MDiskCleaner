using System.Windows;
using DiskCleaner.Gui;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

public class UninstallerWindowSmokeTests
{
    [Fact]
    public void UninstallerWindow_RendersRowsAndOrphansAndSdk_Smoke()
    {
        StaRunner.Run(async () =>
        {
            var harness = UninstallerTestSupport.CreateHarness();
            await harness.ViewModel.ScanCommand.ExecuteAsync(null);

            Rows(harness.ViewModel).Single(i => i.Title == "Java SE Development Kit 11.0.19").IsSelected = true;

            var window = new UninstallerWindow(harness.ViewModel)
            {
                Width = 1280,
                Height = 820
            };

            window.Show();
            await Task.Delay(50);
            window.UpdateLayout();

            Assert.Same(harness.ViewModel, window.DataContext);
            Assert.True(window.ViewModel.HasPlan);
            Assert.True(window.ViewModel.HasOrphans);
            Assert.True(window.ViewModel.HasSdkVersions);
            Assert.NotEmpty(window.ViewModel.Rows);
            Assert.Contains("Выбрано: 1", window.ViewModel.SelectedSummary, StringComparison.Ordinal);

            window.Close();
        });
    }

    private static IReadOnlyList<UninstallItemViewModel> Rows(UninstallerViewModel viewModel) =>
        viewModel.Rows.ToList();
}
