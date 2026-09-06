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
    [InlineData(@"C:\Users\me\AppData\Local\SomeApp-updater", CleanupCategory.Leftover)]
    [InlineData(@"C:\Users\me\.rustup", CleanupCategory.DevToolchain)]
    [InlineData(@"C:\Users\me\.gradle\jdks", CleanupCategory.DevToolchain)]
    public void CategorizePath_ReturnsExpected(string path, CleanupCategory expected)
    {
        Assert.Equal(expected, _categorizer.CategorizePath(path));
    }

    [Fact]
    public void RiskForCategory_Maps()
    {
        Assert.Equal(CleanupRisk.Low, _categorizer.RiskForCategory(CleanupCategory.Cache));
        Assert.Equal(CleanupRisk.Low, _categorizer.RiskForCategory(CleanupCategory.Temp));
        Assert.Equal(CleanupRisk.Medium, _categorizer.RiskForCategory(CleanupCategory.DevToolchain));
        Assert.Equal(CleanupRisk.High, _categorizer.RiskForCategory(CleanupCategory.UserData));
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
