using DiskCleaner.Core.Models;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

public class TreeItemViewModelTests
{
    [Fact]
    public void CategoryRoot_AggregatesSizeAndObjectCount()
    {
        var result = SamplePlan.Build();
        var root = result.CategoryTree.Single(c => c.Key == "category:Cache");
        var vm = new TreeItemViewModel(root, null, () => { });

        Assert.True(vm.IsCategoryRoot);
        Assert.Equal(3, vm.GetLeaves().Count());
        Assert.Equal("3 объекта", vm.CategoryObjectsText);
        Assert.Equal(3_500_000, vm.Item.EffectiveSizeBytes);
    }

    [Fact]
    public void NonRootNode_HasNoCategorySummary()
    {
        var leaf = new CleanupItem
        {
            Key = "leaf",
            DisplayName = "npm cache",
            Category = CleanupCategory.Cache,
            Risk = CleanupRisk.Low,
            SizeBytes = 10
        };

        var vm = new TreeItemViewModel(leaf, parent: null, () => { });

        Assert.False(vm.IsCategoryRoot);
        Assert.Equal(string.Empty, vm.CategoryObjectsText);
        Assert.True(vm.IsSelectable);
    }

    [Fact]
    public void CheckRoot_PropagatesToAllLeaves_AndNotifiesSelectionChanged()
    {
        var selectionChanges = 0;
        var rootVm = new TreeItemViewModel(
            BuildMiniCategory(leafCount: 3),
            null,
            () => selectionChanges++);

        Assert.Equal(false, rootVm.IsChecked);

        rootVm.IsChecked = true;

        Assert.True(rootVm.GetLeaves().All(l => l.IsChecked == true));
        Assert.Equal(true, rootVm.IsChecked);
        Assert.Equal(1, selectionChanges);
    }

    [Fact]
    public void ClickOnFullyCheckedNode_NullFromThreeStateBox_UnchecksAllLeaves()
    {
        var selectionChanges = 0;
        var rootVm = new TreeItemViewModel(
            BuildMiniCategory(leafCount: 3),
            null,
            () => selectionChanges++);

        rootVm.IsChecked = true;
        Assert.True(rootVm.GetLeaves().All(l => l.IsChecked == true));

        rootVm.IsChecked = null;

        Assert.All(rootVm.GetLeaves(), leaf => Assert.False(leaf.IsChecked));
        Assert.Equal(false, rootVm.IsChecked);
        Assert.Equal(2, selectionChanges);
    }

    [Fact]
    public void ClickOnCheckedLeaf_NullFromThreeStateBox_UnchecksLeaf()
    {
        var rootVm = new TreeItemViewModel(
            BuildMiniCategory(leafCount: 2),
            null,
            () => { });
        var leaves = rootVm.GetLeaves().ToList();

        leaves[0].IsChecked = true;
        Assert.True(leaves[0].IsChecked);

        leaves[0].IsChecked = null;

        Assert.False(leaves[0].IsChecked);
        Assert.Equal(false, rootVm.IsChecked);
    }

    [Fact]
    public void InUseLeaf_CannotBeCheckedByCategorySelection()
    {
        var rootVm = new TreeItemViewModel(
            BuildMiniCategory(leafCount: 2, markInUseLast: true),
            null,
            () => { });

        rootVm.IsChecked = true;

        var leaves = rootVm.GetLeaves().ToList();
        Assert.True(leaves[0].CanCheck);
        Assert.False(leaves[1].CanCheck);
        Assert.Equal(true, leaves[0].IsChecked);
        Assert.Null(leaves[1].IsChecked);
    }

    private static CleanupItem BuildMiniCategory(int leafCount, bool markInUseLast = false)
    {
        var children = Enumerable.Range(1, leafCount)
            .Select(i => new CleanupItem
            {
                Key = "leaf:" + i,
                Path = @"C:\fake\leaf" + i,
                DisplayName = "Объект " + i,
                Category = CleanupCategory.Cache,
                Risk = CleanupRisk.Low,
                SizeBytes = 100 * i,
                InUse = markInUseLast && i == leafCount
            })
            .ToList();

        return new CleanupItem
        {
            Key = "category:Cache",
            DisplayName = "Кэши",
            Category = CleanupCategory.Cache,
            Children = children
        };
    }
}
