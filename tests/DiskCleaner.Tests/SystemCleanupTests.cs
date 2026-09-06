using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Tests;

public class SystemCleanupTests
{
    [Fact]
    public async Task DirectoryDeleter_DeleteContentsOnly_ClearsContent_KeepsRoot()
    {
        using var root = new TempRoot();
        var dir = root.Combine("clean-temp");
        root.CreateFile("clean-temp\\a.bin", 1000);
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        root.CreateFile("clean-temp\\sub\\b.bin", 500);

        var outcome = await new DirectoryDeleter().DeletePathAsync(
            dir,
            CleanupTarget.Directory,
            deleteContentsOnly: true);

        Assert.True(Directory.Exists(dir), "Каталог должен сохраниться при очистке содержимого.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        Assert.Equal(1500, outcome.FreedBytes);
        Assert.True(outcome.FullyDeleted);
    }

    [Fact]
    public async Task CacheCleaner_LockedFile_SkipsIt_CompletesPlan_WithoutThrowing()
    {
        using var root = new TempRoot();
        var lockedDir = root.Combine("locked-temp");
        var healthyDir = root.Combine("healthy-temp");
        var lockedFile = root.CreateFile("locked-temp\\locked.bin", 700);
        root.CreateFile("locked-temp\\ok.bin", 300);
        root.CreateFile("healthy-temp\\c.bin", 900);

        var cleaner = new CacheCleanerService();
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var items = new[]
            {
                new CleanupItem
                {
                    Key = "temp:locked",
                    Path = lockedDir,
                    DisplayName = "Занятый Temp",
                    Category = CleanupCategory.Temp,
                    Risk = CleanupRisk.Low,
                    Target = CleanupTarget.Directory,
                    DeleteContentsOnly = true
                },
                new CleanupItem
                {
                    Key = "temp:healthy",
                    Path = healthyDir,
                    DisplayName = "Свободный Temp",
                    Category = CleanupCategory.Temp,
                    Risk = CleanupRisk.Low,
                    Target = CleanupTarget.Directory,
                    DeleteContentsOnly = true
                }
            };

            var report = await cleaner.CleanAsync(items);

            Assert.Equal(2, report.Entries.Count);
            var lockedEntry = report.Entries.Single(e => e.Item.Key == "temp:locked");
            var healthyEntry = report.Entries.Single(e => e.Item.Key == "temp:healthy");

            Assert.Equal(CleanOutcome.Partial, lockedEntry.Outcome);
            Assert.Contains("locked.bin", string.Join("; ", lockedEntry.Note ?? string.Empty));
            Assert.Equal(CleanOutcome.DirectDeleted, healthyEntry.Outcome);
            Assert.True(Directory.Exists(lockedDir));
            Assert.True(File.Exists(lockedFile));
        }
    }

    [Fact]
    public void SystemScanSeedsProvider_BuildsSystemSeeds()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var windows = root.Combine("Win");

        Directory.CreateDirectory(Path.Combine(windows, "Temp"));
        Directory.CreateDirectory(Path.Combine(windows, "SoftwareDistribution", "Download"));
        Directory.CreateDirectory(Path.Combine(root.Path, "Temp"));
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(windows)!, "Windows.old"));
        root.CreateFile("Win\\Temp\\t.tmp", 10);
        root.CreateFile("Win\\SoftwareDistribution\\Download\\u.cab", 20);
        root.CreateFile("Temp\\u.tmp", 5);
        root.CreateFile("Windows.old\\w.txt", 30);

        var recycleBin = root.Combine("RB");
        root.CreateFile("RB\\$RABC.bin", 4096);

        var provider = new SystemScanSeedsProvider(
            environment: environment,
            windowsDirectory: windows,
            recycleBinDirectories: [recycleBin]);

        var seeds = provider.BuildSeeds();

        var userTemp = Assert.Single(seeds, s => s.Key == "system:temp:user");
        Assert.True(userTemp.DeleteContentsOnly);
        Assert.False(userTemp.RequiresAdmin);

        var windowsTemp = Assert.Single(seeds, s => s.Key == "system:temp:windows");
        Assert.True(windowsTemp.RequiresAdmin);
        Assert.True(windowsTemp.DeleteContentsOnly);

        var update = Assert.Single(seeds, s => s.Key == "system:windows-update:download");
        Assert.Equal("wuauserv", update.ServiceName);
        Assert.True(update.RequiresAdmin);
        Assert.Equal(CleanupCategory.SystemFile, update.Category);

        var bin = Assert.Single(seeds, s => s.Key.StartsWith("system:recycle-bin:", StringComparison.Ordinal));
        Assert.Equal(CleanupCategory.RecycleBin, bin.Category);
        Assert.Equal(recycleBin, bin.Path);
        Assert.True(bin.DeleteContentsOnly);

        Assert.Contains(seeds, s => s.Key == "system:windows-old" && s.RequiresAdmin && s.Risk == CleanupRisk.High);
    }

    [Fact]
    public void ElevatedScenarioBuilder_ServiceCleanItem_ProducesServiceCleanDirectoryStep()
    {
        var item = new CleanupItem
        {
            Key = "system:wu",
            Path = @"C:\Windows\SoftwareDistribution\Download",
            DisplayName = "Windows Update cache",
            Category = CleanupCategory.SystemFile,
            Target = CleanupTarget.Directory,
            RequiresAdmin = true,
            DeleteContentsOnly = true,
            ServiceName = "wuauserv"
        };

        var scenario = new ElevatedScenarioBuilder().Build([item]);

        var step = Assert.Single(scenario.Steps);
        Assert.Equal(ElevatedStepKind.ServiceCleanDirectory, step.Kind);
        Assert.Equal("wuauserv", step.ServiceName);
        Assert.True(step.DeleteContentsOnly);
    }

    [Fact]
    public async Task ElevatedScenarioRunner_ClearsContents_ForUnknownService()
    {
        using var root = new TempRoot();
        var dir = root.Combine("sd-download");
        root.CreateFile("sd-download\\update.cab", 5000);

        var runner = new ElevatedScenarioRunner();
        var journal = await runner.RunAsync(new ElevatedScenario
        {
            Steps =
            [
                new ElevatedStep
                {
                    Id = "svc",
                    Kind = ElevatedStepKind.ServiceCleanDirectory,
                    Path = dir,
                    ServiceName = "DiskCleaner.NoSuchService." + Guid.NewGuid().ToString("N"),
                    DeleteContentsOnly = true
                }
            ]
        });

        var result = Assert.Single(journal.Results);
        Assert.True(result.Success);
        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        Assert.Equal(5000, result.FreedBytes);
    }
}
