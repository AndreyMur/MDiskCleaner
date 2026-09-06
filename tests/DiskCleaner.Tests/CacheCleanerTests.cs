using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Tests;

public class CacheCleanerTests
{
    [Fact]
    public async Task DryRun_DeletesNothing_AndReportsExpectedFreed()
    {
        using var root = new TempRoot();
        var dir = root.Combine("npm-cache");
        root.CreateFile("npm-cache\\pkg\\file.bin", 1000);
        root.CreateFile("npm-cache\\file2.bin", 2000);

        var item = TestItems.Directory(dir);
        item.SizeBytes = 3000;
        var cleaner = new CacheCleanerService();
        var report = await cleaner.CleanAsync(
            [item],
            new CleanOptions { DryRun = true });

        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir, "file2.bin")));
        Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.DryRun, report.Entries[0].Outcome);
        Assert.Equal(3000, report.Entries[0].FreedBytes);
        Assert.Equal(3000, report.TotalFreedBytes);
        Assert.Equal(0, report.FailedItems);
    }

    [Fact]
    public async Task Clean_DirectlyDeletesDirectory()
    {
        using var root = new TempRoot();
        var dir = root.Combine("pip-cache");
        root.CreateFile("pip-cache\\a.bin", 500);

        var item = TestItems.Directory(dir);
        var cleaner = new CacheCleanerService();
        var report = await cleaner.CleanAsync([item], new CleanOptions { AllowNativeCommands = false });

        Assert.False(Directory.Exists(dir));
        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.DirectDeleted, entry.Outcome);
        Assert.Equal(500, entry.FreedBytes);
        Assert.Empty(entry.Note ?? string.Empty);
    }

    [Fact]
    public async Task Clean_NativeCommandExitZeroButNoEffect_FallsBackToDirectDelete()
    {
        using var root = new TempRoot();
        var dir = root.Combine("npm-real");
        root.CreateFile("npm-real\\pkg\\file.bin", 4000);

        var item = new CleanupItem
        {
            Key = "npm",
            Path = dir,
            DisplayName = "npm cache",
            Category = CleanupCategory.Cache,
            Target = CleanupTarget.Directory,
            CleanCommandFile = "cmd.exe",
            CleanCommandArgs = "/c npm cache clean --force"
        };

        var runner = new FakeCommandRunner().Result(new CommandResult(0, string.Empty, false));
        var cleaner = new CacheCleanerService(runner: runner);
        var report = await cleaner.CleanAsync([item]);

        Assert.False(Directory.Exists(dir));
        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.DirectDeleted, entry.Outcome);
        Assert.Equal(4000, entry.FreedBytes);
        Assert.NotNull(entry.Note);
    }

    [Fact]
    public async Task Clean_LockedFilesAreSkipped_AndPlanContinuesToNextItem()
    {
        using var root = new TempRoot();
        var lockedDir = root.Combine("locked-cache");
        var lockedFile = root.CreateFile("locked-cache\\locked.bin", 100);
        root.CreateFile("locked-cache\\free.bin", 200);

        var otherDir = root.Combine("other-cache");
        root.CreateFile("other-cache\\data.bin", 700);

        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var cleaner = new CacheCleanerService();
            var report = await cleaner.CleanAsync(
                [TestItems.Directory(lockedDir), TestItems.Directory(otherDir)],
                new CleanOptions { AllowNativeCommands = false });

            Assert.Equal(2, report.Entries.Count);

            var partial = report.Entries.Single(e => e.Outcome == CleanOutcome.Partial);
            Assert.Equal(200, partial.FreedBytes);

            var other = report.Entries.Single(e => e.Outcome == CleanOutcome.DirectDeleted);
            Assert.Equal(700, other.FreedBytes);

            Assert.True(File.Exists(lockedFile));
            Assert.False(File.Exists(Path.Combine(otherDir, "data.bin")));
        }
    }

    [Fact]
    public async Task Clean_InUseItemIsSkipped()
    {
        using var root = new TempRoot();
        var dir = root.Combine("in-use");
        root.CreateFile("in-use\\a.bin", 100);

        var item = TestItems.Directory(dir);
        item.InUse = true;

        var cleaner = new CacheCleanerService();
        var report = await cleaner.CleanAsync([item]);

        Assert.True(Directory.Exists(dir));
        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.InUseSkipped, entry.Outcome);
        Assert.Equal(0, entry.FreedBytes);
    }

    [Fact]
    public async Task Clean_CommandOnlyItem_RunsCommandWithoutDeletingPath()
    {
        using var root = new TempRoot();
        var item = new CleanupItem
        {
            Key = "docker",
            Path = null,
            DisplayName = "Docker prune",
            CommandOnly = true,
            AllowDirectDelete = false,
            CleanCommandFile = "docker",
            CleanCommandArgs = "system prune -af",
            Category = CleanupCategory.Cache
        };

        var runner = new FakeCommandRunner().Result(new CommandResult(0, "Total reclaimed space: 1.2GB", false));
        var cleaner = new CacheCleanerService(runner: runner);
        var report = await cleaner.CleanAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.CommandOnlyCleaned, entry.Outcome);
    }

    [Fact]
    public async Task Clean_CommandOnlyFailedCommand_IsError()
    {
        using var root = new TempRoot();
        var item = new CleanupItem
        {
            Key = "docker",
            Path = null,
            DisplayName = "Docker prune",
            CommandOnly = true,
            CleanCommandFile = "docker",
            CleanCommandArgs = "system prune -af",
            Category = CleanupCategory.Cache
        };

        var runner = new FakeCommandRunner().Result(new CommandResult(1, "Cannot connect to the Docker daemon", false));
        var cleaner = new CacheCleanerService(runner: runner);
        var report = await cleaner.CleanAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.Error, entry.Outcome);
        Assert.Contains("Docker", entry.Note);
    }
}
