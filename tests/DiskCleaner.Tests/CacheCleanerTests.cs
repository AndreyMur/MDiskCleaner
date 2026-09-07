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

    // ---- Фаза 3 модуля 02 (FR-2.6–2.8): исполнение штатной команды с контролем exit-кода,
    // повторное измерение размера и «честный» fallback на прямое удаление. ----

    private static CleanupItem CacheWithCommand(
        string path,
        string commandFile,
        string commandArgs,
        bool allowDirectDelete = true,
        bool commandOnly = false,
        int? timeoutSec = null,
        CleanupRisk risk = CleanupRisk.Low)
    {
        var full = System.IO.Path.GetFullPath(path);
        return new CleanupItem
        {
            Key = commandOnly ? "command:" + commandFile : "cache:" + full,
            Path = commandOnly ? null : full,
            DisplayName = commandOnly ? commandFile : System.IO.Path.GetFileName(full),
            Category = CleanupCategory.Cache,
            Risk = risk,
            Target = CleanupTarget.Directory,
            CleanCommandFile = commandFile,
            CleanCommandArgs = commandArgs,
            CleanCommandTimeoutSec = timeoutSec,
            AllowDirectDelete = allowDirectDelete,
            CommandOnly = commandOnly
        };
    }

    [Fact]
    public async Task Clean_NativeCommandExitZeroReducesSizeEnough_IsNativeCleaned()
    {
        using var root = new TempRoot();
        var dir = root.Combine("uv-cache");
        root.CreateFile("uv-cache\\a.bin", 2000);
        root.CreateFile("uv-cache\\b.bin", 2000);

        var item = CacheWithCommand(dir, "uv", "cache clean");
        var runner = new FakeCommandRunner((_, _) =>
        {
            File.Delete(Path.Combine(dir, "a.bin"));
            return Task.FromResult(new CommandResult(0, string.Empty, false));
        });

        var cleaner = new CacheCleanerService(runner: runner);
        var report = await cleaner.CleanAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.NativeCleaned, entry.Outcome);
        Assert.Equal(2000, entry.FreedBytes);
        Assert.True(Directory.Exists(dir));
        Assert.False(File.Exists(Path.Combine(dir, "a.bin")));
        Assert.True(File.Exists(Path.Combine(dir, "b.bin")));
        Assert.Empty(entry.Note ?? string.Empty);
    }

    [Fact]
    public async Task Clean_NativeCommandExitZeroRemovesDirectory_IsNativeCleaned_WithoutDeleter()
    {
        using var root = new TempRoot();
        var dir = root.Combine("rustup-toolchains");
        root.CreateFile("rustup-toolchains\\toolchains\\a\\bin\\rustc.exe", 3000);

        var item = CacheWithCommand(dir, "cmd.exe", "/c rustup self uninstall -y", timeoutSec: 600);
        var runner = new FakeCommandRunner((_, _) =>
        {
            Directory.Delete(dir, recursive: true);
            return Task.FromResult(new CommandResult(0, string.Empty, false));
        });

        var cleaner = new CacheCleanerService(runner: runner);
        var report = await cleaner.CleanAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.NativeCleaned, entry.Outcome);
        Assert.Equal(3000, entry.FreedBytes);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Clean_NativeCommandExitZeroPartialEffectBelowThreshold_FallsBackDirectWithMark()
    {
        using var root = new TempRoot();
        var dir = root.Combine("npm-real");
        root.CreateFile("npm-real\\small.bin", 900);
        root.CreateFile("npm-real\\big.bin", 9000);

        var item = CacheWithCommand(dir, "cmd.exe", "/c npm cache clean --force", timeoutSec: 300);
        var runner = new FakeCommandRunner((_, _) =>
        {
            File.Delete(Path.Combine(dir, "small.bin"));
            return Task.FromResult(new CommandResult(0, string.Empty, false));
        });

        var cleaner = new CacheCleanerService(runner: runner);
        var report = await cleaner.CleanAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.DirectDeleted, entry.Outcome);
        Assert.Equal(9900, entry.FreedBytes);
        Assert.False(Directory.Exists(dir));
        Assert.Contains("«требует прямого удаления»", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("кодом 0", entry.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clean_NativeCommandExitZeroNoEffect_FallsBackDirect_AndJournalMarksIt()
    {
        using var root = new TempRoot();
        var dir = root.Combine("npm-real");
        root.CreateFile("npm-real\\pkg\\file.bin", 4000);

        var item = CacheWithCommand(dir, "cmd.exe", "/c npm cache clean --force", timeoutSec: 300);
        var runner = new FakeCommandRunner().Result(new CommandResult(0, string.Empty, false));
        var cleaner = new CacheCleanerService(runner: runner);
        var report = await cleaner.CleanAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.DirectDeleted, entry.Outcome);
        Assert.Equal(4000, entry.FreedBytes);
        Assert.False(Directory.Exists(dir));
        Assert.Contains("«требует прямого удаления»", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("применено прямое удаление", entry.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clean_NativeCommandExitNonZero_FallsBackDirect_AndNotesExitCode()
    {
        using var root = new TempRoot();
        var dir = root.Combine("gradle-cache");
        root.CreateFile("gradle-cache\\a.bin", 5000);

        var item = CacheWithCommand(dir, "cmd.exe", "/c uv cache clean", timeoutSec: 180);
        var runner = new FakeCommandRunner().Result(new CommandResult(1, "failed", false));
        var cleaner = new CacheCleanerService(runner: runner);
        var report = await cleaner.CleanAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.DirectDeleted, entry.Outcome);
        Assert.Equal(5000, entry.FreedBytes);
        Assert.False(Directory.Exists(dir));
        Assert.Contains("кодом 1", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("«требует прямого удаления»", entry.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clean_NativeCommandTimedOut_StopsCommandAndFallsBackDirect()
    {
        using var root = new TempRoot();
        var dir = root.Combine("locked-cache");
        root.CreateFile("locked-cache\\a.bin", 2000);

        var item = CacheWithCommand(dir, "cmd.exe", "/c npm cache clean --force");
        var runner = new FakeCommandRunner().Result(new CommandResult(-1, "timed out", true));
        var cleaner = new CacheCleanerService(runner: runner);
        var report = await cleaner.CleanAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.DirectDeleted, entry.Outcome);
        Assert.False(Directory.Exists(dir));
        Assert.Contains("таймаут", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("«требует прямого удаления»", entry.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clean_NativeCommandExitZeroNoEffect_AndDirectForbidden_IsMarked_NotDeleted()
    {
        using var root = new TempRoot();
        var dir = root.Combine("system-cache");
        root.CreateFile("system-cache\\a.bin", 1500);

        var item = CacheWithCommand(dir, "cmd.exe", "/c some manager clean", allowDirectDelete: false);
        var runner = new FakeCommandRunner().Result(new CommandResult(0, string.Empty, false));
        var cleaner = new CacheCleanerService(runner: runner);
        var report = await cleaner.CleanAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.Error, entry.Outcome);
        Assert.Equal(0, entry.FreedBytes);
        Assert.True(Directory.Exists(dir));
        Assert.Contains("«требует прямого удаления»", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("недоступно", entry.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clean_CommandOnly_ParsesReclaimedBytesFromManagerOutput()
    {
        using var root = new TempRoot();
        var item = CacheWithCommand(
            root.Combine("docker"),
            "docker",
            "system prune -af",
            allowDirectDelete: false,
            commandOnly: true,
            timeoutSec: 600,
            risk: CleanupRisk.Medium);

        var runner = new FakeCommandRunner().Result(new CommandResult(0, "Total reclaimed space: 2GB", false));
        var cleaner = new CacheCleanerService(runner: runner);
        var report = await cleaner.CleanAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.CommandOnlyCleaned, entry.Outcome);
        Assert.Equal(2L * 1024 * 1024 * 1024, entry.FreedBytes);
        Assert.Contains("освобождено", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 ГБ", entry.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clean_CommandOnly_ParsesDecimalReclaimedBytes()
    {
        using var root = new TempRoot();
        var item = CacheWithCommand(root.Combine("docker"), "docker", "system prune -af", allowDirectDelete: false, commandOnly: true);
        var runner = new FakeCommandRunner().Result(new CommandResult(0, "Total reclaimed space: 1.5GB", false));
        var cleaner = new CacheCleanerService(runner: runner);
        var report = await cleaner.CleanAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal((long)(1.5 * 1024 * 1024 * 1024), entry.FreedBytes);
    }

    [Fact]
    public async Task Clean_Command_UsesTimeoutFromCatalogDefinition()
    {
        using var root = new TempRoot();
        var dir = root.Combine("npm-cache");
        root.CreateFile("npm-cache\\a.bin", 100);

        var item = CacheWithCommand(dir, "cmd.exe", "/c npm cache clean --force", timeoutSec: 600);
        var runner = new FakeCommandRunner();
        var cleaner = new CacheCleanerService(runner: runner);
        await cleaner.CleanAsync([item]);

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(600, invocation.TimeoutSec);
    }

    [Fact]
    public async Task Clean_Command_DefaultsTimeout_WhenCatalogDoesNotSetIt()
    {
        using var root = new TempRoot();
        var item = CacheWithCommand(root.Combine("docker"), "docker", "system prune -af", allowDirectDelete: false, commandOnly: true);
        var runner = new FakeCommandRunner();
        var cleaner = new CacheCleanerService(runner: runner);
        await cleaner.CleanAsync([item]);

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(300, invocation.TimeoutSec);
    }

    // ---- Фаза 4 модуля 02: прямое удаление каталога с прогрессом (NFR) и
    // удаление «осиротевшего» кэша по известному пути без штатной команды (FR-2.4). ----

    [Fact]
    public async Task Clean_DirectDelete_ReportsProgress_AndFreesAllBytes()
    {
        using var root = new TempRoot();
        var dir = root.Combine("npm-cache");
        root.CreateFile("npm-cache\\a.bin", 1000);
        root.CreateFile("npm-cache\\b.bin", 2000);

        var item = TestItems.Directory(dir);
        item.SizeBytes = 3000;
        var events = new List<CleanProgress>();
        var cleaner = new CacheCleanerService();
        var report = await cleaner.CleanAsync(
            [item],
            new CleanOptions { AllowNativeCommands = false },
            new Progress<CleanProgress>(events.Add));

        Assert.False(Directory.Exists(dir));
        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.DirectDeleted, entry.Outcome);
        Assert.Equal(3000, entry.FreedBytes);

        Assert.Contains(
            events,
            e => e.Detail is not null &&
                 e.Detail.Contains("Прямое удаление", StringComparison.OrdinalIgnoreCase) &&
                 e.BytesCleaned >= 3000);
    }

    [Fact]
    public async Task Clean_OrphanCache_IsDirectDeleted_WithoutNativeCommand()
    {
        using var root = new TempRoot();
        var dir = root.Combine("npm-orphan");
        root.CreateFile("npm-orphan\\a.bin", 4000);

        var item = new CleanupItem
        {
            Key = "npm-cache:" + dir,
            Path = dir,
            DisplayName = "npm cache (осиротевший)",
            Category = CleanupCategory.Cache,
            Risk = CleanupRisk.Low,
            Target = CleanupTarget.Directory,
            IsOrphan = true,
            AllowDirectDelete = true,
            CleanCommandFile = "cmd.exe",
            CleanCommandArgs = "/c npm cache clean --force"
        };

        var runner = new FakeCommandRunner();
        var cleaner = new CacheCleanerService(runner: runner);
        var report = await cleaner.CleanAsync([item]);

        Assert.Empty(runner.Invocations);
        Assert.False(Directory.Exists(dir));
        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.DirectDeleted, entry.Outcome);
        Assert.Equal(4000, entry.FreedBytes);
        Assert.Contains("осиротевший", entry.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DryRun_OrphanCache_PreviewDeletesNothing_AndExplainsDirectDeletion()
    {
        using var root = new TempRoot();
        var dir = root.Combine("npm-orphan");
        root.CreateFile("npm-orphan\\a.bin", 2500);

        var item = new CleanupItem
        {
            Key = "npm-cache:" + dir,
            Path = dir,
            DisplayName = "npm cache (осиротевший)",
            Category = CleanupCategory.Cache,
            Risk = CleanupRisk.Low,
            Target = CleanupTarget.Directory,
            IsOrphan = true,
            SizeBytes = 2500
        };

        var cleaner = new CacheCleanerService();
        var report = await cleaner.CleanAsync([item], new CleanOptions { DryRun = true });

        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir, "a.bin")));
        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.DryRun, entry.Outcome);
        Assert.Equal(2500, entry.FreedBytes);
        Assert.Contains("осиротевший", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("удалено напрямую", entry.Note, StringComparison.OrdinalIgnoreCase);
    }
}
