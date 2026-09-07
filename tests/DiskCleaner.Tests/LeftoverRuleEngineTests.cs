using DiskCleaner.Core.Leftovers;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

/// <summary>
/// Тесты эвристик движка остатков (фаза 17, модуль 03): конфиги удалённых программ (FR-3.3),
/// осиротевшие папки Program Files/ProgramData с условиями (FR-3.4), «пустые/почти пустые»
/// каталоги и порог &gt; 1 ГБ (§5), portable/SDK-каталоги как источник ложных срабатываний,
/// основание (почему это остаток) у каждого кандидата (FR-3.6).
/// </summary>
public class LeftoverRuleEngineTests
{
    private static InstalledApp App(string name, string? location = null) => new()
    {
        ProductCode = "{" + Guid.NewGuid() + "}",
        ScopeKey = nameof(InstalledAppScope.LocalMachine64),
        DisplayName = name,
        InstallLocation = location
    };

    private static LeftoverCandidateScanner Scanner(FakeEnvironment environment, params RunningProcessInfo[] processes) =>
        new(environment, new FakeProcessInspector(processes));

    [Fact]
    public void Scan_AndroidStudioConfigFolder_ProposedWhenProductNotInstalled()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var configDir = root.Combine("LocalAppData", "Google", "AndroidStudio2025.3.2");
        root.CreateFile("LocalAppData\\Google\\AndroidStudio2025.3.2\\options\\opt.xml", 10);
        root.CreateFile("LocalAppData\\Google\\AndroidStudio2025.3.2\\logs\\idea.log", 20);

        var candidates = Scanner(environment).Scan([App("Some unrelated app")]);

        var config = Assert.Single(candidates, c => c.Path == configDir);
        Assert.Equal(LeftoverRuleEngine.RemovedAppConfigsGroupName, config.GroupName);
        Assert.Equal(LeftoverReason.ConfigOfRemovedApp, config.Reason);
        Assert.Equal(CleanupRisk.Medium, config.Risk);
        Assert.False(config.RequiresAdmin);
        Assert.Equal(30, config.SizeBytes);
        Assert.Equal(2, config.FileCount);
        Assert.Contains("Android Studio", config.ReasonText, StringComparison.OrdinalIgnoreCase);

        // Контейнер бренда сам по себе остатком не является.
        Assert.DoesNotContain(candidates, c => c.Path == root.Combine("LocalAppData", "Google"));
    }

    [Fact]
    public void Scan_AndroidStudioConfigFolder_NotOfferedWhenProductInstalled()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        root.CreateFile("LocalAppData\\Google\\AndroidStudio2025.3.2\\options\\opt.xml", 10);

        var candidates = Scanner(environment).Scan(
            [App("Android Studio 2025.1.2", root.ProgramFiles + "\\Google\\Android Studio")]);

        Assert.DoesNotContain(candidates, c =>
            c.Path == root.Combine("LocalAppData", "Google", "AndroidStudio2025.3.2"));
    }

    [Fact]
    public void Scan_AndroidStudioConfigFolder_NotOfferedWhenProcessRunning()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var configDir = root.Combine("LocalAppData", "Google", "AndroidStudio2025.3.2");
        root.CreateFile("LocalAppData\\Google\\AndroidStudio2025.3.2\\bin\\studio64.exe", 100);

        var running = new RunningProcessInfo(configDir + "\\bin\\studio64.exe", "studio64");
        var candidates = Scanner(environment, running).Scan([]);

        Assert.DoesNotContain(candidates, c => c.Path == configDir);
    }

    [Fact]
    public void Scan_DotAndroidConfig_ProposedWithHighRisk_WhenNoAppAndNoProcess()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var androidDir = root.Combine("UserProfile", ".android");
        root.CreateFile("UserProfile\\.android\\avd\\pixel_6.avd\\img.dat", 300);

        var candidates = Scanner(environment).Scan([]);

        var config = Assert.Single(candidates, c => c.Path == androidDir);
        Assert.Equal(LeftoverRuleEngine.RemovedAppConfigsGroupName, config.GroupName);
        Assert.Equal(LeftoverReason.ConfigOfRemovedApp, config.Reason);
        Assert.Equal(CleanupRisk.High, config.Risk);
        Assert.False(config.RequiresAdmin);
        Assert.Contains("реестр", config.ReasonText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_OrphanProgramFiles_NotOfferedWhenRunningProcessInside()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var orphanDir = root.Combine("ProgramFiles", "OrphanTool");
        root.CreateFile("ProgramFiles\\OrphanTool\\orphan.exe", 100);

        var running = new RunningProcessInfo(orphanDir + "\\orphan.exe", "orphan");
        var candidates = Scanner(environment, running).Scan([]);

        Assert.DoesNotContain(candidates, c => c.Path == orphanDir);
    }

    [Fact]
    public void Scan_OrphanProgramDataJunkBrand_ProposedEvenWhenEmpty()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var junkDir = root.Combine("ProgramData", "Windows4DDiGFileRepair");
        root.Combine("ProgramData\\Windows4DDiGFileRepair");

        var candidates = Scanner(environment).Scan([]);

        var candidate = Assert.Single(candidates, c => c.Path == junkDir);
        Assert.Equal("Осиротевшие папки в ProgramData", candidate.GroupName);
        Assert.Equal(LeftoverReason.OrphanProgramData, candidate.Reason);
        Assert.Equal(CleanupRisk.Medium, candidate.Risk);
        Assert.True(candidate.RequiresAdmin);
        Assert.Contains("бренд", candidate.ReasonText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("реестр", candidate.ReasonText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("процессов", candidate.ReasonText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_OrphanProgramFilesJunkBrand_Proposed()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var junkDir = root.Combine("ProgramFilesX86", "4DDiG File Repair");
        root.Combine("ProgramFilesX86\\4DDiG File Repair");

        var candidates = Scanner(environment).Scan([]);

        var candidate = Assert.Single(candidates, c => c.Path == junkDir);
        Assert.Equal(LeftoverReason.OrphanProgramFiles, candidate.Reason);
        Assert.Equal(CleanupRisk.Medium, candidate.Risk);
        Assert.True(candidate.RequiresAdmin);
    }

    [Fact]
    public void Scan_EmptyNearEmptyFolderInProgramFiles_ProposedWithBasis()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var emptyDir = root.Combine("ProgramFiles", "EmptyTool");
        root.Combine("ProgramFiles\\EmptyTool");
        root.CreateFile("ProgramFiles\\SmallTool\\data.bin", 200);

        var candidates = Scanner(environment).Scan([]);

        var empty = Assert.Single(candidates, c => c.Path == emptyDir);
        Assert.Equal(LeftoverReason.NearEmptyDirectory, empty.Reason);
        Assert.Equal(CleanupRisk.Low, empty.Risk);
        Assert.Equal(0, empty.SizeBytes);

        var small = Assert.Single(candidates, c =>
            c.Path == root.Combine("ProgramFiles", "SmallTool"));
        Assert.Equal(LeftoverReason.NearEmptyDirectory, small.Reason);
        Assert.Equal(200, small.SizeBytes);
    }

    [Fact]
    public void Scan_UnmatchedFolderWithoutExplicitCategory_BeyondNearEmptyThreshold_NotOffered()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        // Единственный файл больше порога «почти пустого» каталога (64 МБ): папка не остаток.
        root.CreateFile("ProgramFiles\\BigFile\\payload.dat", (int)LeftoverRuleEngine.NearEmptyDirectoryMaxBytes + 1);

        // Много объектов верхнего уровня (больше NearEmptyMaxTopLevelEntries): папка не «почти пустая».
        for (var i = 0; i < LeftoverRuleEngine.NearEmptyMaxTopLevelEntries + 1; i++)
        {
            root.CreateFile($"ProgramFiles\\ManyEntries\\sub {i:000}\\f.dat", 10);
        }

        var candidates = Scanner(environment).Scan([]);

        Assert.DoesNotContain(candidates, c =>
            c.Path == root.Combine("ProgramFiles", "BigFile"));
        Assert.DoesNotContain(candidates, c =>
            c.Path == root.Combine("ProgramFiles", "ManyEntries"));
    }

    [Fact]
    public void Engine_LargeFolderThreshold_IsOneGigabyte_WithoutCategoryNotAutoOffered()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        // Папка из единственного вложенного каталога, но с большим содержимым: за порогом
        // «почти пустого» каталога, явной категории нет — остатком не предлагается.
        root.CreateFile("ProgramFiles\\BigNested\\sub\\payload.dat", (int)LeftoverRuleEngine.NearEmptyDirectoryMaxBytes + 1);

        var candidates = Scanner(environment).Scan([]);

        Assert.Equal(1L * 1024 * 1024 * 1024, LeftoverRuleEngine.LargeDirectoryMaxBytes);
        Assert.DoesNotContain(candidates, c =>
            c.Path == root.Combine("ProgramFiles", "BigNested"));
    }

    [Fact]
    public void Scan_PortableOrManualSdkFolders_NeverOfferedAsOrphan()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        root.Combine("ProgramFiles\\nodejs");
        root.CreateFile("ProgramFiles\\go\\bin\\go.exe", 50);
        root.CreateFile("ProgramFiles\\jdk-21\\bin\\java.exe", 100);

        var candidates = Scanner(environment).Scan([]);

        Assert.DoesNotContain(candidates, c =>
            c.Path == root.Combine("ProgramFiles", "nodejs"));
        Assert.DoesNotContain(candidates, c =>
            c.Path == root.Combine("ProgramFiles", "go"));
        Assert.DoesNotContain(candidates, c =>
            c.Path == root.Combine("ProgramFiles", "jdk-21"));
    }

    [Fact]
    public void Scan_UpdaterFolderInAdminRoot_StillOfferedWithSizeAndDate()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var updater = root.Combine("ProgramData", "acme-app-updater");
        root.CreateFile("ProgramData\\acme-app-updater\\Update.exe", 80);

        var candidates = Scanner(environment).Scan([]);

        var candidate = Assert.Single(candidates, c => c.Path == updater);
        Assert.Equal(LeftoverCandidateScanner.UpdatersGroupName, candidate.GroupName);
        Assert.Equal(LeftoverReason.UpdaterFolder, candidate.Reason);
        Assert.True(candidate.RequiresAdmin);
        Assert.Equal(80, candidate.SizeBytes);
        Assert.Equal(CleanupRisk.Low, candidate.Risk);
    }
}
