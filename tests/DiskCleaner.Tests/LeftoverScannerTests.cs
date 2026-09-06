using DiskCleaner.Core.Leftovers;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

public class LeftoverScannerTests
{
    private static InstalledApp App(string name, string? location = null) => new()
    {
        ProductCode = "{" + Guid.NewGuid() + "}",
        ScopeKey = nameof(InstalledAppScope.LocalMachine64),
        DisplayName = name,
        InstallLocation = location
    };

    [Fact]
    public void Scan_FindsUpdaterFolders_InLocalAppData()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var lmStudio = root.Combine("LocalAppData\\lm-studio-updater");
        root.CreateFile("LocalAppData\\lm-studio-updater\\Update.exe", 100);
        root.CreateFile("LocalAppData\\qwen-updater\\Setup.exe", 200);

        var scanner = new LeftoverScannerService(
            environment: environment,
            processInspector: new FakeProcessInspector());

        var results = scanner.Scan([App("LM Studio")]);

        var updaters = results.Where(i => i.GroupName == "Остатки апдейтеров").ToList();
        Assert.Contains(updaters, i => i.DisplayName == "lm-studio-updater");
        Assert.Contains(updaters, i => i.DisplayName == "qwen-updater");
        Assert.All(updaters, i => Assert.Equal(CleanupCategory.Leftover, i.Category));
        Assert.All(updaters, i => Assert.Equal(CleanupRisk.Low, i.Risk));
        Assert.True(Directory.Exists(lmStudio));
    }

    [Fact]
    public void Scan_OrphanProgramFilesFolder_NotMatchedToWhitelist_IsCandidate()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        root.Combine("ProgramFiles\\4DDiG File Repair");
        root.CreateFile("ProgramFiles\\InstalledApp\\app.exe", 300);

        var scanner = new LeftoverScannerService(
            environment: environment,
            processInspector: new FakeProcessInspector());

        var results = scanner.Scan([App("InstalledApp", root.ProgramFiles + "\\InstalledApp")]);

        var orphans = results
            .Where(i => i.GroupName == "Осиротевшие папки в Program Files")
            .ToList();
        Assert.Contains(orphans, i => i.DisplayName == "4DDiG File Repair");
        Assert.DoesNotContain(orphans, i => i.DisplayName == "InstalledApp");
        Assert.All(orphans, i => Assert.True(i.RequiresAdmin));
    }

    [Fact]
    public void Scan_SkipsOrphanWhenRunningProcessInside()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var orphan = root.Combine("ProgramFiles\\OrphanTool");
        root.CreateFile("ProgramFiles\\OrphanTool\\orphan.exe", 100);

        var scanner = new LeftoverScannerService(
            environment: environment,
            processInspector: new FakeProcessInspector(
                new RunningProcessInfo(orphan + "\\orphan.exe", "orphan")));

        var results = scanner.Scan([App("Another App")]);
        Assert.DoesNotContain(results, i => i.DisplayName == "OrphanTool");
    }

    [Fact]
    public void Scan_Exclusions_SuppressUpdaterFolder()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        root.CreateFile("LocalAppData\\pachca-updater\\Update.exe", 100);

        var scanner = new LeftoverScannerService(
            environment: environment,
            processInspector: new FakeProcessInspector());

        var results = scanner.Scan([], ["pachca-updater"]);

        Assert.DoesNotContain(results, i => i.DisplayName == "pachca-updater");
    }
}
