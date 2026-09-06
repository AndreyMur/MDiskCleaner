using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;
using DiskCleaner.Core.Scanning;
using DiskCleaner.Core.Uninstall;

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
    public async Task DirectoryScanner_TimesOutSlowBranchAndContinuesOthers()
    {
        var slow = @"C:\fake\slow-branch";
        var fast = @"C:\fake\fast-branch";

        var scanner = new DirectoryScanner(new ScanOptions
        {
            BranchTimeout = TimeSpan.FromMilliseconds(50)
        });
        scanner.MeasureOverride = async (path, ct) =>
        {
            if (string.Equals(path, slow, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return new DirectoryMeasurement(path, 1, 1, true);
        };

        var outcome = await scanner.MeasureAsync([slow, fast]);

        Assert.Contains(slow, outcome.Results.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(fast, outcome.Results.Keys, StringComparer.OrdinalIgnoreCase);

        Assert.Contains(outcome.Errors, e => e.Contains("таймаут", StringComparison.OrdinalIgnoreCase));

        var slowMeasurement = outcome.Results[slow];
        Assert.True(slowMeasurement.TimedOut);

        var fastMeasurement = outcome.Results[fast];
        Assert.True(fastMeasurement.Exists);
        Assert.False(fastMeasurement.TimedOut);
        Assert.Equal(1, fastMeasurement.SizeBytes);
    }

    [Fact]
    public async Task DirectoryScanner_SkipsInaccessibleBranchLogsAndContinues()
    {
        using var root = new TempRoot();
        var scanRoot = root.Combine("scan");
        root.CreateFile("scan\\keep.bin", 50);

        var locked = root.Combine("scan", "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllBytes(Path.Combine(locked, "hidden.bin"), new byte[1000]);

        var rule = DenyListAccess(locked);
        try
        {
            var scanner = new DirectoryScanner();
            var outcome = await scanner.MeasureAsync([scanRoot]);

            var measurement = outcome.Results[scanRoot];
            Assert.True(measurement.Exists);
            Assert.Equal(50, measurement.SizeBytes);
            Assert.Equal(1, measurement.FileCount);
            Assert.Contains(outcome.Errors, e => e.Contains(locked, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            RestoreAccess(locked, rule);
        }
    }

    [Fact]
    public async Task DirectoryScanner_SkipsDanglingJunctionWithoutError()
    {
        using var root = new TempRoot();
        root.CreateFile("scan\\real.bin", 30);

        var junction = root.Combine("scan", "broken-link");
        var missingTarget = root.Combine("scan", "does-not-exist-target");
        var created = CreateJunction(junction, missingTarget);
        if (!created)
        {
            return;
        }

        var scanner = new DirectoryScanner();
        var outcome = await scanner.MeasureAsync([root.Combine("scan")]);

        var measurement = outcome.Results[root.Combine("scan")];
        Assert.True(measurement.Exists);
        Assert.Equal(30, measurement.SizeBytes);
        Assert.Equal(1, measurement.FileCount);
        Assert.Empty(outcome.Errors);
    }

    private static System.Security.AccessControl.FileSystemAccessRule DenyListAccess(string directory)
    {
        var directoryInfo = new DirectoryInfo(directory);
        var security = directoryInfo.GetAccessControl();
        var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Не удалось получить SID текущего пользователя.");
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            currentUser,
            System.Security.AccessControl.FileSystemRights.Read | System.Security.AccessControl.FileSystemRights.ReadAndExecute,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Deny);
        security.AddAccessRule(rule);
        directoryInfo.SetAccessControl(security);
        return rule;
    }

    private static void RestoreAccess(string directory, System.Security.AccessControl.FileSystemAccessRule rule)
    {
        var directoryInfo = new DirectoryInfo(directory);
        var security = directoryInfo.GetAccessControl();
        security.RemoveAccessRule(rule);
        directoryInfo.SetAccessControl(security);
    }

    private static bool CreateJunction(string junctionPath, string targetPath)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(junctionPath);
            startInfo.ArgumentList.Add(targetPath);

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
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
    public async Task AnalysisService_InstalledAppSeed_MeasuresRealInstallFolderSize()
    {
        using var root = new TempRoot();
        var installDir = root.Combine("Android Studio");
        root.CreateFile("Android Studio\\bin\\studio64.exe", 3000);
        root.CreateFile("Android Studio\\lib\\app.jar", 7000);

        var app = InstalledSoftware("aaa", "Android Studio", installDir, estimatedBytes: 4096);
        var seeds = BuildSeeds([app]);

        var analysis = new AnalysisService(processInspector: new FakeProcessInspector());
        var result = await analysis.AnalyzeAsync(seeds);

        var leaf = Assert.Single(result.Items);
        Assert.Equal(CleanupCategory.InstalledApp, leaf.Category);
        Assert.Equal(installDir, leaf.Path);
        Assert.Equal(10000, leaf.SizeBytes);
        Assert.Equal(0, result.SkippedNonexistent);
    }

    [Fact]
    public async Task AnalysisService_InstalledAppWithoutInstallFolder_KeepsRegistryEstimate()
    {
        var app = InstalledSoftware("bbb", "Java 17", location: null, estimatedBytes: 4096);
        var seeds = BuildSeeds([app]);

        var analysis = new AnalysisService(processInspector: new FakeProcessInspector());
        var result = await analysis.AnalyzeAsync(seeds);

        var leaf = Assert.Single(result.Items);
        Assert.Null(leaf.Path);
        Assert.Equal(4096, leaf.SizeBytes);
        Assert.Equal(0, result.SkippedNonexistent);
    }

    [Fact]
    public async Task AnalysisService_MarksInstalledAppInUse_WhenProcessRunsFromInstallFolder()
    {
        using var root = new TempRoot();
        var installDir = root.Combine("Android SDK");
        root.CreateFile("Android SDK\\platform-tools\\adb.exe", 42);

        var app = InstalledSoftware("ccc", "Android SDK Tools", installDir, estimatedBytes: 2048);
        var seeds = BuildSeeds([app]);

        var processes = new[]
        {
            new RunningProcessInfo(Path.Combine(installDir, "platform-tools", "adb.exe"), "adb")
        };
        var analysis = new AnalysisService(processInspector: new FakeProcessInspector(processes));
        var result = await analysis.AnalyzeAsync(seeds);

        var leaf = Assert.Single(result.Items);
        Assert.True(leaf.InUse);
        Assert.Equal(1, result.InUseItems);
        Assert.Equal(CleanupDefaultAction.Keep, new CategorizationService().DefaultActionFor(leaf));
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

    [Fact]
    public async Task AnalysisService_SkipsTimedOutBranchFromPlan()
    {
        var timedOut = @"C:\fake\timed-out-cache";
        var fine = @"C:\fake\fine-cache";

        var scanner = new DirectoryScanner(new ScanOptions
        {
            BranchTimeout = TimeSpan.FromMilliseconds(50)
        });
        scanner.MeasureOverride = (path, ct) => Task.FromResult(
            string.Equals(path, timedOut, StringComparison.OrdinalIgnoreCase)
                ? new DirectoryMeasurement(path, 0, 0, true, TimedOut: true)
                : new DirectoryMeasurement(path, 123, 4, true));

        var seeds = new[]
        {
            TestItems.Directory(timedOut, category: CleanupCategory.Cache),
            TestItems.Directory(fine, category: CleanupCategory.Cache)
        };

        var analysis = new AnalysisService(scanner: scanner, processInspector: new FakeProcessInspector());
        var result = await analysis.AnalyzeAsync(seeds);

        var item = Assert.Single(result.Items);
        Assert.Equal(fine, item.Path);
        Assert.Equal(123, item.SizeBytes);
        Assert.Equal(1, result.TimedOutBranches);
        Assert.Equal(0, result.SkippedNonexistent);
    }

    private static IReadOnlyList<CleanupItem> BuildSeeds(IReadOnlyList<InstalledApp> apps)
    {
        var planner = new UninstallPlannerService(registry: new UninstallRegistryService(branches: []));
        return planner.BuildSeedsFrom(
            apps,
            new UninstallPlannerOptions
            {
                IncludeAllApps = true,
                IncludeOrphanedRegistryEntries = false
            });
    }

    private static InstalledApp InstalledSoftware(
        string id,
        string displayName,
        string? location,
        long estimatedBytes) => new()
    {
        ProductCode = "{" + id.PadLeft(8, '0') + "}",
        ScopeKey = nameof(InstalledAppScope.LocalMachine32),
        DisplayName = displayName,
        Publisher = "ACME",
        DisplayVersion = "1.0.0",
        InstallDate = "20240101",
        InstallLocation = location,
        EstimatedSizeBytes = estimatedBytes,
        UninstallString = "\"C:\\fake\\unins000.exe\""
    };
}
