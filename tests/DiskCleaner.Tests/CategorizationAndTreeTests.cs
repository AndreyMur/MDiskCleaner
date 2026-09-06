using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Tests;

public class CategorizationAndTreeTests
{
    private readonly CategorizationService _categorizer = new();

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\npm-cache", CleanupCategory.Cache)]
    [InlineData(@"C:\Users\me\AppData\Local\ms-playwright", CleanupCategory.Cache)]
    [InlineData(@"C:\Users\me\.gradle\caches", CleanupCategory.Cache)]
    [InlineData(@"C:\Users\me\.cargo\registry", CleanupCategory.Cache)]
    [InlineData(@"C:\Users\me\AppData\Local\pip\Cache", CleanupCategory.Cache)]
    [InlineData(@"C:\Users\me\AppData\Local\uv\cache", CleanupCategory.Cache)]
    [InlineData(@"C:\Users\me\AppData\Local\pnpm-cache", CleanupCategory.Cache)]
    [InlineData(@"C:\Users\me\AppData\Local\dotslash", CleanupCategory.Cache)]
    [InlineData(@"C:\Users\me\AppData\Local\Temp", CleanupCategory.Temp)]
    [InlineData(@"C:\Windows\Temp", CleanupCategory.Temp)]
    [InlineData(@"C:\Users\me\AppData\Local\SomeApp-updater", CleanupCategory.Leftover)]
    [InlineData(@"C:\Users\me\.rustup", CleanupCategory.DevToolchain)]
    [InlineData(@"C:\Users\me\.gradle\jdks", CleanupCategory.DevToolchain)]
    [InlineData(@"C:\Users\me\AppData\Local\Android\Sdk", CleanupCategory.DevToolchain)]
    public void CategorizePath_ReturnsExpected(string path, CleanupCategory expected)
    {
        Assert.Equal(expected, _categorizer.CategorizePath(path));
    }

    [Fact]
    public void RiskForCategory_Maps()
    {
        Assert.Equal(CleanupRisk.Low, _categorizer.RiskForCategory(CleanupCategory.Cache));
        Assert.Equal(CleanupRisk.Low, _categorizer.RiskForCategory(CleanupCategory.Temp));
        Assert.Equal(CleanupRisk.Low, _categorizer.RiskForCategory(CleanupCategory.RecycleBin));
        Assert.Equal(CleanupRisk.Medium, _categorizer.RiskForCategory(CleanupCategory.Leftover));
        Assert.Equal(CleanupRisk.Medium, _categorizer.RiskForCategory(CleanupCategory.DevToolchain));
        Assert.Equal(CleanupRisk.High, _categorizer.RiskForCategory(CleanupCategory.InstalledApp));
        Assert.Equal(CleanupRisk.High, _categorizer.RiskForCategory(CleanupCategory.UserData));
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\npm-cache", CleanupRisk.Low)]
    [InlineData(@"C:\Users\me\AppData\Local\Temp", CleanupRisk.Low)]
    [InlineData(@"C:\Windows\Temp", CleanupRisk.Low)]
    [InlineData(@"C:\Users\me\AppData\Local\SomeApp-updater", CleanupRisk.Low)]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\SomeApp-updater", CleanupRisk.Low)]
    [InlineData(@"C:\Users\me\AppData\Local\Android\Sdk", CleanupRisk.Medium)]
    [InlineData(@"C:\Users\me\.rustup", CleanupRisk.Medium)]
    [InlineData(@"C:\Program Files\SomeApp", CleanupRisk.High)]
    [InlineData(@"C:\Program Files (x86)\SomeApp", CleanupRisk.High)]
    [InlineData(@"C:\Users\me\Documents", CleanupRisk.High)]
    [InlineData(@"D:\Data\Projects", CleanupRisk.High)]
    public void RiskForPath_ReturnsExpected(string path, CleanupRisk expected)
    {
        Assert.Equal(expected, _categorizer.RiskForPath(path));
    }

    [Fact]
    public void DefaultAction_MapsFromRisk()
    {
        Assert.Equal(CleanupDefaultAction.Clean, _categorizer.DefaultAction(CleanupRisk.Low));
        Assert.Equal(CleanupDefaultAction.Ask, _categorizer.DefaultAction(CleanupRisk.Medium));
        Assert.Equal(CleanupDefaultAction.Keep, _categorizer.DefaultAction(CleanupRisk.High));
    }

    [Fact]
    public void DefaultActionFor_InUseItem_ReturnsKeepRegardlessOfRisk()
    {
        var inUseCache = new CleanupItem
        {
            Key = "cache",
            Path = @"C:\Users\me\AppData\Local\npm-cache",
            DisplayName = "npm cache",
            Category = CleanupCategory.Cache,
            Risk = CleanupRisk.Low,
            InUse = true
        };

        Assert.Equal(CleanupDefaultAction.Keep, _categorizer.DefaultActionFor(inUseCache));

        var freeCache = new CleanupItem
        {
            Key = "cache2",
            Path = @"C:\Users\me\AppData\Local\pnpm-cache",
            DisplayName = "pnpm cache",
            Category = CleanupCategory.Cache,
            Risk = CleanupRisk.Low,
            InUse = false
        };

        Assert.Equal(CleanupDefaultAction.Clean, _categorizer.DefaultActionFor(freeCache));
    }

    [Fact]
    public void TreeBuilder_GroupsByCategoryThenGroup_SortedBySize()
    {
        var big = MakeItem("big", "grpA", 300);
        var small = MakeItem("small", "grpA", 100);
        var lone = MakeItem("lone", null, 500);
        var other = MakeItem("other", null, 400, CleanupCategory.DevToolchain);

        var tree = new TreeBuilder().Build([big, small, lone, other]);

        Assert.Equal(2, tree.Count);
        Assert.Equal(CleanupCategory.Cache, tree[0].Category);
        Assert.Equal(CleanupCategory.DevToolchain, tree[1].Category);

        var cacheRoot = tree[0];
        Assert.Equal(2, cacheRoot.Children.Count);

        var loneNode = cacheRoot.Children.Single(c => !c.IsGroup);
        Assert.Equal(500, loneNode.EffectiveSizeBytes);

        var groupNode = cacheRoot.Children.Single(c => c.IsGroup);
        Assert.Equal("grpA", groupNode.DisplayName);
        Assert.Equal(2, groupNode.Children.Count);
        Assert.Equal("big", groupNode.Children[0].DisplayName);
        Assert.Equal(400, groupNode.EffectiveSizeBytes);
    }

    [Fact]
    public void CleanupItem_CarriesPathSizeCategoryRiskFlagsAndAggregatesChildren()
    {
        var leafA = new CleanupItem
        {
            Key = "a",
            Path = @"C:\Users\me\AppData\Local\npm-cache",
            DisplayName = "npm cache",
            Category = CleanupCategory.Cache,
            Risk = CleanupRisk.Low,
            RequiresAdmin = false,
            SizeBytes = 100,
            FileCount = 2
        };
        var leafB = new CleanupItem
        {
            Key = "b",
            Path = @"C:\Users\me\AppData\Local\pip\Cache",
            DisplayName = "pip cache",
            Category = CleanupCategory.Cache,
            Risk = CleanupRisk.Low,
            RequiresAdmin = false,
            InUse = true,
            SizeBytes = 300,
            FileCount = 5
        };

        var group = new CleanupItem
        {
            Key = "group",
            DisplayName = "Менеджеры",
            Category = CleanupCategory.Cache,
            Children = new[] { leafA, leafB }
        };

        Assert.False(leafA.IsGroup);
        Assert.False(leafB.IsGroup);
        Assert.True(group.IsGroup);
        Assert.True(leafB.InUse);
        Assert.False(group.RequiresAdmin);
        Assert.Equal(400, group.EffectiveSizeBytes);
        Assert.Equal(7, group.EffectiveFileCount);
        Assert.Equal(400, leafA.EffectiveSizeBytes + leafB.EffectiveSizeBytes);
    }

    private static CleanupItem MakeItem(string name, string? group, long size, CleanupCategory category = CleanupCategory.Cache)
    {
        var full = System.IO.Path.Combine(@"C:\fake", name);
        return new CleanupItem
        {
            Key = name,
            Path = full,
            DisplayName = name,
            GroupName = group,
            Category = category,
            SizeBytes = size
        };
    }
}
