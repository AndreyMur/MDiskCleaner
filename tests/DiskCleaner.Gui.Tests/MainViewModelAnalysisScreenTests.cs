using System.Threading.Tasks;
using DiskCleaner.Core.Models;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

public class MainViewModelAnalysisScreenTests
{
    [Fact]
    public void Sources_ContainDisksAndProfileMode_DefaultIsSystemDisk()
    {
        var (vm, _) = CreateScreen();

        Assert.NotEmpty(vm.Sources);
        Assert.Contains(vm.Sources, s => s.IsDiskSource);
        Assert.Contains(vm.Sources, s => s.RootPath is null);
        Assert.NotNull(vm.SelectedSource);
        Assert.True(vm.SelectedSource!.IsDiskSource);
        Assert.False(vm.IncludeSystemDirectories);
        Assert.True(vm.IsSystemDirectoriesToggleEnabled);
    }

    [Fact]
    public async Task Analyze_RunsCoordinatorWithSelectedDiskAndIncludeSystemFlag()
    {
        var (vm, fake) = CreateScreen();
        vm.IncludeSystemDirectories = true;

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.Calls);
        Assert.NotNull(fake.LastOptions);
        Assert.Equal(vm.SelectedSource!.RootPath, fake.LastOptions!.DiskRootPath);
        Assert.True(fake.LastOptions.IncludeSystemDirectories);
        Assert.True(fake.LastOptions.IncludeInstalledApps);
    }

    [Fact]
    public async Task Analyze_ProfileSource_RunsProfileMode()
    {
        var (vm, fake) = CreateScreen();
        vm.SelectedSource = vm.Sources.Single(s => s.RootPath is null);
        vm.IncludeSystemDirectories = true;

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.NotNull(fake.LastOptions);
        Assert.Null(fake.LastOptions!.DiskRootPath);
        Assert.False(fake.LastOptions.IncludeSystemDirectories);
    }

    [Fact]
    public async Task Analyze_LoadsCategoryTreeWithObjectCounts()
    {
        var (vm, _) = CreateScreen();

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.RootNodes.Count);
        var cacheRoot = vm.RootNodes.Single(n => n.Item.Key == "category:Cache");
        Assert.Equal("3 объекта", cacheRoot.CategoryObjectsText);
        Assert.Contains("Найдено объектов: 6", vm.StatusText);
        Assert.Contains("используется: 1", vm.StatusText);
        Assert.Equal("Ничего не выбрано", vm.SelectedSummary);
    }

    [Fact]
    public async Task SelectedLeaves_FillPlanGrid_WithDefaultActionsAndLabels()
    {
        var (vm, _) = CreateScreen();
        await vm.AnalyzeCommand.ExecuteAsync(null);

        var leaves = vm.RootNodes.SelectMany(n => n.GetLeaves()).ToList();
        Check(leaves, "npm cache").IsChecked = true;
        Check(leaves, "pip cache").IsChecked = true;
        Check(leaves, "Oracle JDK 8").IsChecked = true;

        Assert.Equal(3, vm.PlanRows.Count);

        var npmRow = vm.PlanRows.Single(r => r.Name.EndsWith("npm cache", StringComparison.Ordinal));
        Assert.Equal("Очистить", npmRow.DefaultActionText);
        Assert.Equal("Кэши", npmRow.Category);

        var appRow = vm.PlanRows.Single(r => r.Name.Contains("Oracle JDK 8", StringComparison.Ordinal));
        Assert.Equal("Спросить", appRow.DefaultActionText);
        Assert.Equal("да", appRow.AdminText);
        Assert.True(appRow.NeedsReview);
        Assert.Contains("Review manually", appRow.FlagsText);

        Assert.Contains("Выбрано: 3 объектов", vm.SelectedSummary);
    }

    [Fact]
    public async Task InUseLeaf_IsNotIncludedIntoPlan_AfterCategoryCheck()
    {
        var (vm, _) = CreateScreen();
        await vm.AnalyzeCommand.ExecuteAsync(null);

        var cacheRoot = vm.RootNodes.Single(n => n.Item.Key == "category:Cache");
        cacheRoot.IsChecked = true;

        Assert.DoesNotContain(vm.PlanRows, r => r.Name.Contains("VS Code Cache", StringComparison.Ordinal));
        Assert.Equal(2, vm.PlanRows.Count);
        Assert.All(vm.PlanRows, r => Assert.Equal("Очистить", r.DefaultActionText));
    }

    [Fact]
    public async Task UncheckingLeaves_ClearsPlan()
    {
        var (vm, _) = CreateScreen();
        await vm.AnalyzeCommand.ExecuteAsync(null);

        var leaves = vm.RootNodes.SelectMany(n => n.GetLeaves()).ToList();
        var npm = Check(leaves, "npm cache");
        npm.IsChecked = true;
        Assert.Single(vm.PlanRows);

        npm.IsChecked = false;

        Assert.Empty(vm.PlanRows);
        Assert.Equal("Ничего не выбрано", vm.SelectedSummary);
    }

    [Fact]
    public async Task ScheduledAnalysis_UsesProfileModeOptions()
    {
        var (vm, fake) = CreateScreen();

        await vm.RunScheduledAnalysisAsync();

        Assert.Equal(1, fake.Calls);
        Assert.NotNull(fake.LastOptions);
        Assert.Null(fake.LastOptions!.DiskRootPath);
    }

    [Fact]
    public void ToggleForSystemDirectories_DisabledForProfileSource()
    {
        var (vm, _) = CreateScreen();
        vm.SelectedSource = vm.Sources.Single(s => s.RootPath is null);

        Assert.False(vm.IsSystemDirectoriesToggleEnabled);
    }

    private static (MainViewModel Vm, FakeAnalysisCoordinator Fake) CreateScreen()
    {
        var fake = new FakeAnalysisCoordinator
        {
            Handler = _ => SamplePlan.Build()
        };
        return (MainViewModelFactory.Create(fake), fake);
    }

    private static TreeItemViewModel Check(IEnumerable<TreeItemViewModel> leaves, string displayName) =>
        leaves.Single(l => l.Item.DisplayName == displayName);
}
