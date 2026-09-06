using Microsoft.Win32;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

public class UninstallRegistryTests
{
    private static bool IsWindows() => OperatingSystem.IsWindows();

    [Fact]
    public void ReadInstalledApps_ReadsValuesFromIsolatedHkcuBranch()
    {
        if (!IsWindows())
        {
            return;
        }

        var guid = Guid.NewGuid().ToString("N");
        var branchRelative = $@"Software\DiskCleaner.Tests\{guid}\Uninstall";

        using var uninstall = Registry.CurrentUser.CreateSubKey(branchRelative);
        using (var product = uninstall.CreateSubKey("{11111111-1111-1111-1111-111111111111}"))
        {
            product.SetValue("DisplayName", "Тестовое приложение");
            product.SetValue("DisplayVersion", "1.2.3");
            product.SetValue("Publisher", "ACME");
            product.SetValue("EstimatedSize", 2048);
            product.SetValue("InstallDate", "20240115");
            product.SetValue("InstallLocation", @"C:\Program Files\Тест");
            product.SetValue("UninstallString", "MsiExec.exe /X{11111111-1111-1111-1111-111111111111}");
        }

        try
        {
            var environment = new FakeEnvironment(new TempRoot());
            var service = new UninstallRegistryService(
                environment,
                branches: [new RegistryBranchSpec(RegistryHiveKind.CurrentUser, branchRelative)]);

            var apps = service.ReadInstalledApps();

            var app = Assert.Single(apps);
            Assert.Equal("{11111111-1111-1111-1111-111111111111}", app.ProductCode);
            Assert.Equal("Тестовое приложение", app.DisplayName);
            Assert.Equal("1.2.3", app.DisplayVersion);
            Assert.Equal("ACME", app.Publisher);
            Assert.Equal(2048L * 1024, app.EstimatedSizeBytes);
            Assert.True(app.IsWindowsInstaller);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(branchRelative, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Analyze_FlagsDuplicateOldVersion_AndSuspiciousPublisher()
    {
        var older = InstalledApp("Java SE Development Kit", "20230101", "Oracle");
        var newer = InstalledApp("Java SE Development Kit", "20240101", "Oracle");
        var suspicious = InstalledApp("SomeTool", "20240101", "${PRODUCT_PUBLISHER}");

        var analysis = new InstalledAppAnalyzer().Analyze([older, newer, suspicious]);

        Assert.Contains(analysis, a => a.App.ProductCode == older.ProductCode && a.IsOldVersion);
        Assert.Contains(analysis, a => a.App.ProductCode == newer.ProductCode && !a.IsOldVersion);
        Assert.Contains(analysis, a => a.App.ProductCode == suspicious.ProductCode && a.NeedsReview);
    }

    [Fact]
    public void Planner_BuildSeedsFrom_CreatesUninstallLeavesNeverDirectDelete()
    {
        var app = InstalledApp("Приложение", "20240101", "ACME", uninstall: "\"C:\\App\\unins000.exe\"");
        var planner = new UninstallPlannerService(
            registry: new UninstallRegistryService(branches: []),
            programDataRoot: Path.Combine(Path.GetTempPath(), "DiskCleaner.Tests", "nopackagecache-" + Guid.NewGuid().ToString("N")));

        var seeds = planner.BuildSeedsFrom(
            [app],
            new UninstallPlannerOptions { IncludeAllApps = true });

        var seed = Assert.Single(seeds, s => s.UninstallMode);
        Assert.Equal(CleanupCategory.InstalledApp, seed.Category);
        Assert.True(seed.UninstallMode);
        Assert.False(seed.AllowDirectDelete);
        Assert.True(seed.CommandOnly);
        Assert.Equal(CleanupTarget.Directory, seed.Target);
        Assert.False(seed.InUse);
    }

    [Fact]
    public void Planner_BuildSeeds_DetectsOrphanedRegistryEntry_WhenPathMissing()
    {
        var app = InstalledApp("Ghost", "20240101", "ACME",
            location: @"C:\Program Files\GhostApp",
            uninstall: @"C:\Program Files\GhostApp\unins000.exe");
        var planner = new UninstallPlannerService(registry: new UninstallRegistryService(branches: []));

        var seeds = planner.BuildSeedsFrom(
            [app],
            new UninstallPlannerOptions
            {
                IncludeAllApps = false,
                IncludeOrphanedRegistryEntries = true,
                PathExists = _ => false
            });

        var orphan = Assert.Single(seeds);
        Assert.NotNull(orphan.RegistryDeletePath);
        Assert.Equal(CleanupCategory.Leftover, orphan.Category);
    }

    [Fact]
    public void ReadInstalledApps_ReadsMultipleBranchesAndNormalizesValues()
    {
        if (!IsWindows())
        {
            return;
        }

        var guid = Guid.NewGuid().ToString("N");
        var firstBranch = $@"Software\DiskCleaner.Tests\{guid}\First\Uninstall";
        var secondBranch = $@"Software\DiskCleaner.Tests\{guid}\Second\Uninstall";

        using (var first = Registry.CurrentUser.CreateSubKey(firstBranch))
        using (var second = Registry.CurrentUser.CreateSubKey(secondBranch))
        {
            using (var product = first.CreateSubKey("{11111111-1111-1111-1111-111111111111}"))
            {
                product.SetValue("DisplayName", "  Приложение с пробелами  ");
                product.SetValue("Publisher", "  ACME  ");
                product.SetValue("EstimatedSize", 1024);
                product.SetValue("InstallLocation", @"%PROGRAMFILES%\ACME App");
            }

            using (var product = second.CreateSubKey("{22222222-2222-2222-2222-222222222222}"))
            {
                product.SetValue("DisplayName", "Второе приложение");
                product.SetValue("Publisher", "ACME");
                product.SetValue("EstimatedSize", 2048);
            }
        }

        try
        {
            using var root = new TempRoot();
            var service = new UninstallRegistryService(
                new FakeEnvironment(root),
                branches:
                [
                    new RegistryBranchSpec(RegistryHiveKind.CurrentUser, firstBranch),
                    new RegistryBranchSpec(RegistryHiveKind.CurrentUser, secondBranch)
                ]);

            var apps = service.ReadInstalledApps();

            Assert.Equal(2, apps.Count);
            var normalized = apps.Single(a => a.ProductCode == "{11111111-1111-1111-1111-111111111111}");
            Assert.Equal("Приложение с пробелами", normalized.DisplayName);
            Assert.Equal("ACME", normalized.Publisher);
            Assert.Equal(Path.Combine(root.ProgramFiles, "ACME App"), normalized.InstallLocation);
            Assert.Equal(nameof(InstalledAppScope.CurrentUser), normalized.ScopeKey);
            Assert.All(apps, a => Assert.Equal(nameof(InstalledAppScope.CurrentUser), a.ScopeKey));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\DiskCleaner.Tests\{guid}", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Analyze_WhenInstallDatesEqual_KeepsNewerDisplayVersion()
    {
        var jdk11 = InstalledApp("Java(TM) SE Development Kit 11.0.19", "20240101", "Oracle", version: "11.0.19");
        var jdk17 = InstalledApp("Java(TM) SE Development Kit 17.0.9", "20240101", "Oracle", version: "17.0.9");

        var analysis = new InstalledAppAnalyzer().Analyze([jdk11, jdk17]);

        Assert.Contains(analysis, a => a.App.ProductCode == jdk11.ProductCode && a.IsOldVersion && a.IsDuplicate);
        Assert.Contains(analysis, a => a.App.ProductCode == jdk17.ProductCode && !a.IsOldVersion && a.IsDuplicate);
    }

    [Fact]
    public void Planner_IncludeAllAppsFalse_RecommendsOnlyOlderDuplicate()
    {
        var jdk11 = InstalledApp("Java(TM) SE Development Kit 11.0.19", "20230101", "Oracle", version: "11.0.19");
        var jdk17 = InstalledApp("Java(TM) SE Development Kit 17.0.9", "20240101", "Oracle", version: "17.0.9");
        var planner = new UninstallPlannerService(registry: new UninstallRegistryService(branches: []));

        var seeds = planner.BuildSeedsFrom(
            [jdk11, jdk17],
            new UninstallPlannerOptions { IncludeAllApps = false });

        var seed = Assert.Single(seeds);
        Assert.Equal(jdk11.ProductCode, seed.Key.Split(':').Last());
        Assert.Contains("11.0.19", seed.Description);
        Assert.Contains("2023-01-01", seed.Description);
    }

    [Fact]
    public void Analyze_FlagsEmptyPublisherAndDomainLikeName_WithReviewManually()
    {
        var emptyPublisher = InstalledApp("SomeTool", "20240101", "   ");
        var domainNamed = InstalledApp("installer.svc.host.tool", "20240101", "ACME");

        var analysis = new InstalledAppAnalyzer().Analyze([emptyPublisher, domainNamed]);

        Assert.Contains(
            analysis,
            a => a.App.ProductCode == emptyPublisher.ProductCode && a.NeedsReview &&
                 a.Anomalies.Contains(AppAnomalyKind.ReviewManually));
        Assert.Contains(
            analysis,
            a => a.App.ProductCode == domainNamed.ProductCode && a.NeedsReview &&
                 a.Anomalies.Contains(AppAnomalyKind.ReviewManually));
    }

    [Fact]
    public void Planner_SetsReviewReason_ForSuspiciousPublisher()
    {
        var suspicious = InstalledApp("SomeTool", "20240101", "${PRODUCT_PUBLISHER}");
        var planner = new UninstallPlannerService(registry: new UninstallRegistryService(branches: []));

        var seeds = planner.BuildSeedsFrom(
            [suspicious],
            new UninstallPlannerOptions { IncludeAllApps = true });

        var seed = Assert.Single(seeds);
        Assert.True(seed.ReviewManually);
        Assert.StartsWith("Review manually", seed.ReviewReason);
        Assert.Contains("ручной проверки", seed.Warning);
    }

    [Fact]
    public async Task LocalRegistryCleaner_DeletesHkcuOrphanKey()
    {
        if (!IsWindows())
        {
            return;
        }

        var guid = Guid.NewGuid().ToString("N");
        var branchRelative = $@"Software\DiskCleaner.Tests\{guid}\Uninstall";
        var productRelative = branchRelative + @"\{22222222-2222-2222-2222-222222222222}";

        using (var uninstall = Registry.CurrentUser.CreateSubKey(branchRelative))
        {
            using var product = uninstall.CreateSubKey("{22222222-2222-2222-2222-222222222222}");
            product.SetValue("DisplayName", "Ghost");
        }

        try
        {
            var deletePath = RegistryDeletePathBuilder.Build(
                RegistryHiveKind.CurrentUser,
                productRelative);
            var item = new CleanupItem
            {
                Key = "orphan-test",
                DisplayName = "Ghost",
                Category = CleanupCategory.Leftover,
                RegistryDeletePath = deletePath,
                CommandOnly = false
            };

            var cleaner = new LocalRegistryCleaner();
            var entry = cleaner.Delete(item);

            Assert.Equal(CleanOutcome.RegistryEntryDeleted, entry.Outcome);
            using var root = Registry.CurrentUser.OpenSubKey(branchRelative);
            Assert.NotNull(root);
            Assert.Null(root.OpenSubKey("{22222222-2222-2222-2222-222222222222}"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(branchRelative, throwOnMissingSubKey: false);
        }
    }

    private static InstalledApp InstalledApp(
        string name,
        string installDate,
        string publisher,
        string? location = null,
        string? uninstall = null,
        string? version = null) => new()
    {
        ProductCode = "{" + Guid.NewGuid() + "}",
        ScopeKey = nameof(InstalledAppScope.LocalMachine64),
        DisplayName = name,
        InstallDate = installDate,
        Publisher = publisher,
        DisplayVersion = version,
        InstallLocation = location,
        UninstallString = uninstall ?? "MsiExec.exe /I{00000000-0000-0000-0000-000000000000}"
    };
}
