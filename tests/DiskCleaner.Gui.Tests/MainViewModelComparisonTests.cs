using System.Threading.Tasks;
using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Reports;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

/// <summary>
/// Раздел «Было/стало» в ViewModel (FR-1.13): после первого скана сравнения нет, после
/// повторного строится дельта с предыдущим сканом и показываются изменённые объекты.
/// </summary>
public class MainViewModelComparisonTests
{
    [Fact]
    public async Task FirstScan_HasNoComparisonYet()
    {
        var (vm, fake) = CreateScreen(SamplePlan.Build);

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.False(vm.HasComparison);
        Assert.Empty(vm.ComparisonRows);
        Assert.Equal(string.Empty, vm.ComparisonSummary);
    }

    [Fact]
    public async Task RepeatScanWithChanges_ShowsSummaryAndChangedRows()
    {
        var (vm, fake) = CreateScreen(SamplePlan.Build);

        await vm.AnalyzeCommand.ExecuteAsync(null);
        Assert.False(vm.HasComparison);

        fake.Handler = _ => ShrunkPlan();
        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.True(vm.HasComparison);

        var summary = vm.ComparisonSummary;
        Assert.StartsWith("Было: 6 объектов", summary);
        Assert.Contains("Стало: 5 объектов", summary);
        Assert.Contains("Освобождено ≈ " + CleanReportFormatter.FormatBytes(1_707_000), summary);
        Assert.Contains("объектов меньше на 1", summary);

        Assert.Equal(2, vm.ComparisonRows.Count);

        var npm = vm.ComparisonRows.Single(r => r.Name.Contains("npm cache", StringComparison.Ordinal));
        Assert.Equal(PlanObjectChangeKind.Changed, npm.ChangeKind);
        Assert.Equal("Изменён", npm.ChangeText);
        Assert.StartsWith("-", npm.Delta, StringComparison.Ordinal);

        var recycle = vm.ComparisonRows.Single(r => r.Name.Contains("Корзина (C:)", StringComparison.Ordinal));
        Assert.Equal(PlanObjectChangeKind.Removed, recycle.ChangeKind);
        Assert.Equal("Удалён", recycle.ChangeText);
        Assert.StartsWith("-", recycle.Delta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdenticalRepeatScan_HasComparisonWithoutChanges()
    {
        var (vm, fake) = CreateScreen(SamplePlan.Build);

        await vm.AnalyzeCommand.ExecuteAsync(null);
        fake.Handler = _ => SamplePlan.Build();

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.True(vm.HasComparison);
        Assert.Empty(vm.ComparisonRows);
        Assert.EndsWith("(без изменений)", vm.ComparisonSummary);
    }

    [Fact]
    public async Task DeltaText_FormatsGrownAndFreedSizes()
    {
        var (vm, fake) = CreateScreen(SamplePlan.Build);

        await vm.AnalyzeCommand.ExecuteAsync(null);
        fake.Handler = _ => GrowthPlan();

        await vm.AnalyzeCommand.ExecuteAsync(null);

        var summary = vm.ComparisonSummary;
        Assert.Contains("Размер вырос на " + CleanReportFormatter.FormatBytes(100_000), summary);
        Assert.Contains("объектов больше на 1", summary);
        Assert.Contains("Добавлен", vm.ComparisonRows.Single().ChangeText);
    }

    private static AnalysisResult ShrunkPlan()
    {
        var result = SamplePlan.Build();
        var items = result.Items.ToList();
        items.RemoveAll(i => i.Key == "recycle:c");
        items.Single(i => i.Key == "cache:npm").SizeBytes = 300_000;

        return new AnalysisResult
        {
            Items = items,
            InUseItems = 1,
            Elapsed = TimeSpan.FromMilliseconds(120)
        };
    }

    private static AnalysisResult GrowthPlan()
    {
        var items = SamplePlan.Build().Items.ToList();
        items.Add(new CleanupItem
        {
            Key = "cache:uv",
            DisplayName = "uv cache",
            GroupName = "Менеджеры пакетов",
            Category = CleanupCategory.Cache,
            Risk = CleanupRisk.Low,
            Path = @"C:\fake\uv-cache",
            SizeBytes = 100_000
        });

        return new AnalysisResult
        {
            Items = items,
            Elapsed = TimeSpan.FromMilliseconds(120)
        };
    }

    private static (MainViewModel Vm, FakeAnalysisCoordinator Fake) CreateScreen(
        Func<AnalysisResult> handler)
    {
        var fake = new FakeAnalysisCoordinator { Handler = _ => handler() };
        return (MainViewModelFactory.Create(fake), fake);
    }
}
