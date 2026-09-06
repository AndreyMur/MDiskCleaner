using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;
using DiskCleaner.Core.Scanning;

namespace DiskCleaner.Tests;

public class ScannerAndAnalysisTests
{
    [Fact]
    public async Task DirectoryScanner_MeasuresNestedSizes()
    {
        using var root = new TempRoot();
        root.CreateFile("cache\\a.bin", 100);
        root.CreateFile("cache\\sub\\b.bin", 250);
        root.CreateFile("cache\\sub\\deep\\c.bin", 50);

        var scanner = new DirectoryScanner();
        var outcome = await scanner.MeasureAsync([root.Combine("cache")]);

        Assert.Single(outcome.Results);
        var measurement = outcome.Results[root.Combine("cache")];
        Assert.True(measurement.Exists);
        Assert.Equal(400, measurement.SizeBytes);
        Assert.Equal(3, measurement.FileCount);
        Assert.Empty(outcome.Errors);
    }

    [Fact]
    public async Task DirectoryScanner_ReportsMissingRootAsNonexistent()
    {
        var missing = Path.Combine(Path.GetTempPath(), "DiskCleaner.Tests", Guid.NewGuid().ToString("N"), "absent");
        var scanner = new DirectoryScanner();
        var outcome = await scanner.MeasureAsync([missing]);

        var measurement = outcome.Results[missing];
        Assert.False(measurement.Exists);
        Assert.Equal(0, measurement.SizeBytes);
    }

    [Fact]
    public async Task AnalysisService_MeasuresExistingAndSkipsNonexistent()
    {
        using var root = new TempRoot();
        var existingDir = root.Combine("existing");
        root.CreateFile("existing\\x.bin", 1234);

        var seeds = new[]
        {
            TestItems.Directory(existingDir),
            TestItems.Directory(Path.Combine(root.Path, "not-there"))
        };

        var inspector = new FakeProcessInspector();
        var analysis = new AnalysisService(processInspector: inspector);
        var result = await analysis.AnalyzeAsync(seeds);

        Assert.Single(result.Items);
        Assert.Equal(1, result.SkippedNonexistent);
        Assert.Equal(1234, result.Items[0].SizeBytes);
        Assert.Equal(1234, result.TotalBytes);
    }

    [Fact]
    public async Task AnalysisService_CommandOnlyLeafIsKeptWithoutPath()
    {
        using var root = new TempRoot();
        var commandOnly = new CleanupItem
        {
            Key = "cmd:docker",
            Path = null,
            DisplayName = "Docker prune",
            CommandOnly = true,
            Category = CleanupCategory.Cache
        };

        var analysis = new AnalysisService(processInspector: new FakeProcessInspector());
        var result = await analysis.AnalyzeAsync([commandOnly]);

        Assert.Single(result.Items);
        Assert.Null(result.Items[0].Path);
    }

    [Fact]
    public void InUseDetector_MarksByExecutablePathInsideTarget()
    {
        using var root = new TempRoot();
        var dir = root.Combine("sdk");
        root.CreateFile("sdk\\platform-tools\\adb.exe", 100);

        var item = TestItems.Directory(dir);
        var processes = new[]
        {
            new RunningProcessInfo(Path.Combine(dir, "platform-tools", "adb.exe"), "adb")
        };

        var marked = new InUseDetector().MarkInUse([item], processes);

        Assert.Equal(1, marked);
        Assert.True(item.InUse);
    }

    [Fact]
    public void InUseDetector_MarksByOwnerProcessName()
    {
        using var root = new TempRoot();
        var dir = root.Combine("codeCache");
        var item = new CleanupItem
        {
            Key = "code",
            Path = dir,
            DisplayName = "VS Code Cache",
            OwnerProcessNames = ["Code"],
            Category = CleanupCategory.Cache
        };

        var processes = new[] { new RunningProcessInfo(@"C:\Program Files\Microsoft VS Code\Code.exe", "Code") };

        var marked = new InUseDetector().MarkInUse([item], processes);

        Assert.True(item.InUse);
        Assert.Equal(1, marked);
    }
}
