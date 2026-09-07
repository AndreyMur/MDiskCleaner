using System.Windows;
using DiskCleaner.Gui;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

public class CacheCleanWindowSmokeTests
{
    [Fact]
    public void CacheCleanWindow_RendersPlanGroupsAndCards_Smoke()
    {
        StaRunner.Run(async () =>
        {
            var harness = CacheCleanTestSupport.CreateHarness(CacheCleanTestSupport.TypicalProfileLeaves());
            await harness.ViewModel.AnalyzeCommand.ExecuteAsync(null);
            HarnessHelper.Select(harness.ViewModel, "npm cache", "Gradle caches");

            var window = new CacheCleanWindow(harness.ViewModel)
            {
                Width = 1180,
                Height = 760
            };

            window.Show();
            await Task.Delay(50);
            window.UpdateLayout();

            Assert.Same(harness.ViewModel, window.DataContext);
            Assert.NotEmpty(window.ViewModel.Groups);
            Assert.Contains("Выбрано: 2", window.ViewModel.SelectedSummary, System.StringComparison.Ordinal);

            window.Close();
        });
    }
}
