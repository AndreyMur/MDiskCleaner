using DiskCleaner.Core.Leftovers;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

/// <summary>
/// Тесты проверок безопасности остатков (фаза 18, модуль 03, план-03 «Фаза 3»): кандидат,
/// путь которого использует запущенный процесс / входит в <c>%PATH%</c> / содержит
/// исполняемый файл зарегистрированной службы, не предлагается к удалению (§5);
/// группа Windows.old с размером и флагами «требует админа»/«обязательное подтверждение»
/// (FR-3.5); определение флагов кандидатов (FR-3.4).
/// </summary>
public class LeftoverSafetyTests
{
    private static InstalledApp App(string name, string? location = null) => new()
    {
        ProductCode = "{" + Guid.NewGuid() + "}",
        ScopeKey = nameof(InstalledAppScope.LocalMachine64),
        DisplayName = name,
        InstallLocation = location
    };

    [Fact]
    public void Scan_TempFolderWithRunningProcess_NotOffered()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var liveDir = root.Combine("ProgramFiles", "LiveTool");
        root.CreateFile("ProgramFiles\\LiveTool\\live.exe", 100);

        var running = new RunningProcessInfo(liveDir + "\\live.exe", "live");
        var scanner = new LeftoverCandidateScanner(
            environment,
            new FakeProcessInspector(running),
            new FakeServiceInspector());

        var candidates = scanner.Scan([]);

        Assert.DoesNotContain(candidates, c => c.Path == liveDir);
    }

    [Fact]
    public void Scan_OrphanFolderInPath_NotOffered()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = root.Combine("ProgramFiles", "PathTool")
        });

        var onPath = root.Combine("ProgramFiles", "PathTool");
        root.CreateFile("ProgramFiles\\PathTool\\tool.exe", 100);

        var scanner = new LeftoverCandidateScanner(
            environment,
            new FakeProcessInspector(),
            new FakeServiceInspector());

        var candidates = scanner.Scan([]);

        Assert.DoesNotContain(candidates, c => c.Path == onPath);
    }

    [Fact]
    public void Scan_OrphanFolderWithRegisteredServiceInside_NotOffered()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var serviceDir = root.Combine("ProgramData", "SvcHost");
        root.CreateFile("ProgramData\\SvcHost\\svc.exe", 100);

        var registered = new RegisteredServiceInfo("FakeSvc", serviceDir + "\\svc.exe");
        var scanner = new LeftoverCandidateScanner(
            environment,
            new FakeProcessInspector(),
            new FakeServiceInspector(registered));

        var candidates = scanner.Scan([]);

        Assert.DoesNotContain(candidates, c => c.Path == serviceDir);
    }

    [Fact]
    public void Scan_SameFolderWithoutLiveUsage_Offered()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var safeDir = root.Combine("ProgramFiles", "SafeOrphan");
        root.CreateFile("ProgramFiles\\SafeOrphan\\orphan.exe", 100);

        var scanner = new LeftoverCandidateScanner(
            environment,
            new FakeProcessInspector(),
            new FakeServiceInspector());

        var candidates = scanner.Scan([]);

        var candidate = Assert.Single(candidates, c => c.Path == safeDir);
        Assert.True(candidate.RequiresAdmin);
        Assert.True(candidate.RequiresConfirmation);
    }

    [Fact]
    public void Scan_WindowsOldFixture_FoundWithSizeAndFlags()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var windowsOldPath = root.Combine("Windows.old");
        root.CreateFile("Windows.old\\Windows\\System32\\config\\SYSTEM", 300);
        root.CreateFile("Windows.old\\Windows\\System32\\config\\software", 200);
        root.Combine("Windows.old.000");

        var scanner = new LeftoverCandidateScanner(
            environment,
            new FakeProcessInspector(),
            new FakeServiceInspector());

        var candidates = scanner.Scan([]);

        var windowsOld = Assert.Single(candidates, c => c.DisplayName == "Windows.old");
        Assert.Equal(LeftoverRuleEngine.WindowsOldGroupName, windowsOld.GroupName);
        Assert.Equal(500, windowsOld.SizeBytes);
        Assert.True(windowsOld.RequiresAdmin);
        Assert.True(windowsOld.RequiresConfirmation);
        Assert.Equal(CleanupCategory.SystemFile, windowsOld.Category);
        Assert.Contains("Storage Sense", windowsOld.RecommendedRemovalMethod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cleanmgr", windowsOld.RecommendedRemovalMethod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DISM", windowsOld.RecommendedRemovalMethod, StringComparison.OrdinalIgnoreCase);

        var suffixVariant = Assert.Single(candidates, c => c.DisplayName == "Windows.old.000");
        Assert.Equal(0, suffixVariant.SizeBytes);
    }

    [Fact]
    public void Scan_FlagsRequireAdminAndConfirmation_ForProgramFilesProgramDataAndWindowsOld()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        root.CreateFile("ProgramFiles\\OrphanTool\\t.exe", 100);
        root.CreateFile("ProgramData\\Windows4DDiGFileRepair\\d.dat", 50);
        root.CreateFile("LocalAppData\\lm-studio-updater\\Update.exe", 80);
        root.CreateFile("LocalAppData\\Google\\AndroidStudio2025.3.2\\options\\opt.xml", 10);
        root.CreateFile("Windows.old\\Windows\\System32\\config\\SYSTEM", 100);

        var scanner = new LeftoverCandidateScanner(
            environment,
            new FakeProcessInspector(),
            new FakeServiceInspector());

        var candidates = scanner.Scan([App("Android Studio")]);

        var programFiles = Assert.Single(candidates, c => c.DisplayName == "OrphanTool");
        Assert.True(programFiles.RequiresAdmin);
        Assert.True(programFiles.RequiresConfirmation);

        var programData = Assert.Single(candidates, c => c.DisplayName == "Windows4DDiGFileRepair");
        Assert.True(programData.RequiresAdmin);
        Assert.True(programData.RequiresConfirmation);

        var updater = Assert.Single(candidates, c => c.DisplayName == "lm-studio-updater");
        Assert.False(updater.RequiresAdmin);
        Assert.False(updater.RequiresConfirmation);

        var windowsOld = Assert.Single(candidates, c => c.Reason == LeftoverReason.WindowsOld);
        Assert.True(windowsOld.RequiresAdmin);
        Assert.True(windowsOld.RequiresConfirmation);
    }

    [Fact]
    public void Scan_WindowsOldNotMatchedAsGenericOrphanUnderProgramFiles()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        // Каталог Windows.old лежит на корне диска (родитель Program Files), а не внутри него.
        root.CreateFile("Windows.old\\Windows\\System32\\config\\SYSTEM", 100);
        // Не near-empty: в Program Files папка с префиксом Windows.old не считается системной.
        for (var i = 0; i < LeftoverRuleEngine.NearEmptyMaxTopLevelEntries + 1; i++)
        {
            root.CreateFile($"ProgramFiles\\Windows.old.fake\\sub {i:000}\\f.dat", 10);
        }

        var scanner = new LeftoverCandidateScanner(
            environment,
            new FakeProcessInspector(),
            new FakeServiceInspector());

        var candidates = scanner.Scan([]);

        var real = Assert.Single(candidates, c => c.Reason == LeftoverReason.WindowsOld);
        Assert.Equal(root.Combine("Windows.old"), real.Path);
        Assert.DoesNotContain(candidates, c => c.DisplayName == "Windows.old.fake");
    }
}
