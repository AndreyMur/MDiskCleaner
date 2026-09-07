using System.Diagnostics;
using DiskCleaner.Core.Leftovers;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

/// <summary>
/// Интеграционные тесты сканера остатков на временных каталогах: только верхние уровни
/// шести корней (без рекурсии, NFR ≤ 60 с), группа «Остатки апдейтеров» с размером и
/// датой последнего изменения, замер времени сканирования (FR-3.1, FR-3.2).
/// </summary>
public class LeftoverCandidateScannerTests
{
    private static InstalledApp App(string name, string? location = null) => new()
    {
        ProductCode = "{" + Guid.NewGuid() + "}",
        ScopeKey = nameof(InstalledAppScope.LocalMachine64),
        DisplayName = name,
        InstallLocation = location
    };

    private static LeftoverCandidateScanner Scanner(FakeEnvironment environment) =>
        new(environment);

    [Fact]
    public void Scan_OnlyTopLevelsOfAllSixRoots_UnmatchedAreCandidates()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        root.CreateFile("ProgramFiles\\InstalledApp\\app.exe", 100);
        root.CreateFile("ProgramFiles\\InstalledApp\\deep\\weird.dat", 50);
        root.CreateFile("ProgramFiles\\Google\\Chrome\\Application\\chrome.exe", 200);
        root.CreateFile("ProgramFiles\\Orphan Tool\\tool.bin", 10);
        root.CreateFile("ProgramFilesX86\\OldTool\\x.exe", 20);
        root.CreateFile("ProgramData\\Wondershare\\data.bin", 30);
        root.CreateFile("LocalAppData\\SomeApp\\data.bin", 40);
        root.CreateFile("AppData\\SomeApp\\config.txt", 5);
        root.CreateFile("UserProfile\\.leftover-dir\\state", 7);

        var scanner = Scanner(environment);
        var apps = new[]
        {
            App("InstalledApp", root.ProgramFiles + "\\InstalledApp"),
            App("Google Chrome", root.ProgramFiles + "\\Google\\Chrome\\Application")
        };

        var candidates = scanner.Scan(apps);

        Assert.DoesNotContain(candidates, c => c.DisplayName == "InstalledApp");
        Assert.DoesNotContain(candidates, c => c.DisplayName == "Google");

        Assert.DoesNotContain(candidates, c =>
            c.Path.StartsWith(root.ProgramFiles + "\\InstalledApp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(candidates, c =>
            c.Path.Contains("\\Google\\", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(candidates, c =>
            c.Path == root.Combine("ProgramFiles", "Orphan Tool") &&
            c.GroupName == "Осиротевшие папки в Program Files" &&
            c.RequiresAdmin &&
            c.Reason == LeftoverReason.NotInUninstallRegistry);

        Assert.Contains(candidates, c =>
            c.Path == root.Combine("ProgramFilesX86", "OldTool") &&
            c.GroupName == "Осиротевшие папки в Program Files (x86)" &&
            c.RequiresAdmin);

        Assert.Contains(candidates, c =>
            c.Path == root.Combine("ProgramData", "Wondershare") &&
            c.RequiresAdmin);

        Assert.Contains(candidates, c =>
            c.Path == root.Combine("LocalAppData", "SomeApp") &&
            !c.RequiresAdmin);

        Assert.Contains(candidates, c =>
            c.Path == root.Combine("AppData", "SomeApp") &&
            c.GroupName == "Осиротевшие каталоги в %APPDATA%" &&
            !c.RequiresAdmin);

        Assert.Contains(candidates, c =>
            c.Path == root.Combine("UserProfile", ".leftover-dir") &&
            c.GroupName == "Осиротевшие каталоги в %USERPROFILE%" &&
            !c.RequiresAdmin);
    }

    [Fact]
    public void Scan_UpdaterMaskVariants_FoundWithSizeAndLastWriteTime()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var lmStudioUpdater = root.Combine("LocalAppData", "lm-studio-updater");
        root.CreateFile("LocalAppData\\lm-studio-updater\\Update.exe", 300);
        root.CreateFile("LocalAppData\\lm-studio-updater\\stub\\payload.bin", 200);

        root.CreateFile("LocalAppData\\kimi-desktop_updater\\update.exe", 50);
        root.CreateFile("LocalAppData\\acme-app-update\\Setup.exe", 150);
        root.CreateFile("LocalAppData\\Programs\\arduino-ide-updater\\Setup.exe", 500);
        root.CreateFile("ProgramData\\ViewPlayCap-updater\\Update.exe", 80);

        var stamped = new DateTime(2026, 8, 14, 10, 30, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(lmStudioUpdater, stamped);

        var scanner = Scanner(environment);
        var candidates = scanner.Scan(Array.Empty<InstalledApp>());

        var updaters = candidates
            .Where(c => c.GroupName == LeftoverCandidateScanner.UpdatersGroupName)
            .ToList();

        Assert.Equal(5, updaters.Count);

        var lmStudio = Assert.Single(updaters, c =>
            c.Path == lmStudioUpdater);
        Assert.Equal(500, lmStudio.SizeBytes);
        Assert.Equal(stamped, lmStudio.LastWriteTimeUtc);
        Assert.Equal(CleanupRisk.Low, lmStudio.Risk);
        Assert.Equal(CleanupCategory.Leftover, lmStudio.Category);
        Assert.False(lmStudio.RequiresAdmin);

        Assert.Single(updaters, c => c.DisplayName == "kimi-desktop_updater");
        Assert.Single(updaters, c => c.DisplayName == "acme-app-update");
        Assert.Single(updaters, c => c.Path == root.Combine("LocalAppData", "Programs", "arduino-ide-updater"));

        var programDataUpdater = Assert.Single(updaters, c => c.DisplayName == "ViewPlayCap-updater");
        Assert.True(programDataUpdater.RequiresAdmin);
        Assert.Equal(80, programDataUpdater.SizeBytes);

        Assert.Single(candidates, c => c.Path == lmStudioUpdater);
    }

    [Fact]
    public void Scan_InstalledAppUpdaterFolder_StillFlaggedAsUpdater()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var location = root.Combine("LocalAppData", "lm-studio");
        root.CreateFile("LocalAppData\\lm-studio-updater\\Update.exe", 100);
        root.CreateFile("LocalAppData\\lm-studio\\app.exe", 900);

        var scanner = Scanner(environment);
        var candidates = scanner.Scan([App("LM Studio", location)]);

        Assert.DoesNotContain(candidates, c => c.Path == root.Combine("LocalAppData", "lm-studio"));
        var updater = Assert.Single(candidates, c => c.Path == root.Combine("LocalAppData", "lm-studio-updater"));
        Assert.Equal(LeftoverReason.UpdaterFolder, updater.Reason);
        Assert.Equal(100, updater.SizeBytes);
    }

    [Fact]
    public void Scan_NfrTimeBudget_TopLevelScanIsFast()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        for (var i = 0; i < 60; i++)
        {
            root.CreateFile($"LocalAppData\\App {i:000}\\data.bin", 10);
            root.CreateFile($"AppData\\RoamApp {i:000}\\cfg.txt", 5);
            root.CreateFile($"ProgramFiles\\PFTool {i:000}\\tool.exe", 20);
        }

        for (var i = 0; i < 15; i++)
        {
            root.CreateFile($"LocalAppData\\agent-{i:000}-updater\\Update.exe", 100);
            root.CreateFile($"LocalAppData\\agent-{i:000}-updater\\meta\\m.dat", 100);
        }

        var scanner = Scanner(environment);
        var stopwatch = Stopwatch.StartNew();
        var candidates = scanner.Scan([App("PFTool 007", root.ProgramFiles + "\\PFTool 007")]);
        stopwatch.Stop();

        Assert.Contains(candidates, c => c.DisplayName == "PFTool 000");
        Assert.DoesNotContain(candidates, c => c.DisplayName == "PFTool 007");
        Assert.Contains(candidates, c => c.DisplayName == "agent-014-updater");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Скан верхних уровней занял {stopwatch.Elapsed.TotalSeconds:F1} с — цель ≤ 60 с (NFR).");
    }
}
