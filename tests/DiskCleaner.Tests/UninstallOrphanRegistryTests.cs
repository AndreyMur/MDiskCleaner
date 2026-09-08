using DiskCleaner.Core.Models;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

/// <summary>
/// Модуль 04, фаза 4 (milestone «Фаза 24») — поиск осиротевших записей Uninstall (FR-4.13):
/// только записи под машинными ветками Uninstall/WOW6432Node, защита SystemComponent=1,
/// удаление только по подтверждению отдельной группой.
/// </summary>
public class UninstallOrphanRegistryTests
{
    private const string MissingInstallLocation = @"C:\Program Files\GhostApp";

    [Fact]
    public void Find_FlagsLocalMachineRecord_WhenInstallLocationMissing()
    {
        var app = InstalledApp(
            "Ghost",
            scope: nameof(InstalledAppScope.LocalMachine64),
            location: MissingInstallLocation,
            uninstall: @"C:\Program Files\GhostApp\unins000.exe");
        var scanner = new UninstallOrphanRegistryScanner();

        var match = Assert.Single(scanner.Find([app], Options(pathExists: _ => false)));

        Assert.Equal(app.ProductCode, match.App.ProductCode);
        Assert.False(match.IsSystemComponent);
        Assert.Contains(match.MissingPaths, path => path.Contains("GhostApp", StringComparison.Ordinal));
    }

    [Fact]
    public void Find_IgnoresCurrentUserRecords_EvenWhenPathsAreMissing()
    {
        var userApp = InstalledApp(
            "Ghost",
            scope: nameof(InstalledAppScope.CurrentUser),
            location: MissingInstallLocation,
            uninstall: @"C:\Program Files\GhostApp\unins000.exe");
        var scanner = new UninstallOrphanRegistryScanner();

        var matches = scanner.Find([userApp], Options(pathExists: _ => false));

        Assert.Empty(matches);
    }

    [Fact]
    public void Find_DoesNotFlagMsiRecord_WhenOnlyUninstallerLooksMissing()
    {
        var msiApp = InstalledApp(
            "MSI Product",
            scope: nameof(InstalledAppScope.LocalMachine64),
            location: @"C:\Program Files\RealApp",
            uninstall: "MsiExec.exe /I{11111111-1111-1111-1111-111111111111}");
        var scanner = new UninstallOrphanRegistryScanner();

        var matches = scanner.Find([msiApp], Options(pathExists: path => path.Contains("RealApp", StringComparison.Ordinal)));

        Assert.Empty(matches);
    }

    [Fact]
    public void Find_FlagsRecord_WhenUninstallerExecutableMissing()
    {
        var app = InstalledApp(
            "Broken",
            scope: nameof(InstalledAppScope.LocalMachine32),
            uninstall: @"D:\Removed\unins000.exe");
        var scanner = new UninstallOrphanRegistryScanner();

        var match = Assert.Single(scanner.Find([app], Options(pathExists: _ => false)));

        Assert.Contains(match.MissingPaths, path => path.Contains("unins000.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void Find_SystemComponent_SkippedByDefault_AndIncludedOnlyWithExplicitOption()
    {
        var systemComponent = InstalledApp(
            "System Piece",
            scope: nameof(InstalledAppScope.LocalMachine64),
            location: MissingInstallLocation,
            isSystemComponent: true);
        var scanner = new UninstallOrphanRegistryScanner();

        var withoutConsent = scanner.Find([systemComponent], Options(pathExists: _ => false));
        Assert.Empty(withoutConsent);

        var withConsent = scanner.Find(
            [systemComponent],
            Options(pathExists: _ => false, includeSystemComponents: true));

        var match = Assert.Single(withConsent);
        Assert.True(match.IsSystemComponent);
    }

    [Fact]
    public void BuildCleanupItems_Group_Separated_AdminRegistryDelete_NeverDirectDelete()
    {
        var orphan = InstalledApp(
            "Ghost",
            scope: nameof(InstalledAppScope.LocalMachine64),
            location: MissingInstallLocation,
            uninstall: @"C:\Program Files\GhostApp\unins000.exe");
        var scanner = new UninstallOrphanRegistryScanner();

        var items = scanner.BuildCleanupItems([orphan], Options(pathExists: _ => false));

        var item = Assert.Single(items);
        Assert.Equal(UninstallOrphanRegistryScanner.GroupName, item.GroupName);
        Assert.Equal(CleanupCategory.Leftover, item.Category);
        Assert.NotNull(item.RegistryDeletePath);
        Assert.StartsWith(RegistryDeletePathBuilder.LocalMachinePrefix, item.RegistryDeletePath, StringComparison.Ordinal);
        Assert.True(item.RequiresAdmin);
        Assert.False(item.CommandOnly);
        Assert.False(item.AllowDirectDelete);
        Assert.False(item.ReviewManually);
        Assert.Contains("запись реестра", item.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCleanupItems_SystemComponent_WithConsent_IsHighRiskReviewItem()
    {
        var systemComponent = InstalledApp(
            "System Piece",
            scope: nameof(InstalledAppScope.LocalMachine64),
            location: MissingInstallLocation,
            isSystemComponent: true);
        var scanner = new UninstallOrphanRegistryScanner();

        var items = scanner.BuildCleanupItems(
            [systemComponent],
            Options(pathExists: _ => false, includeSystemComponents: true));

        var item = Assert.Single(items);
        Assert.Equal(CleanupRisk.High, item.Risk);
        Assert.True(item.ReviewManually);
        Assert.Contains("SystemComponent=1", item.ReviewReason, StringComparison.Ordinal);
        Assert.Contains("явному согласию", item.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Planner_IncludeOrphanedRegistryEntries_SystemComponentsProtectedByDefault()
    {
        var orphan = InstalledApp(
            "Ghost",
            scope: nameof(InstalledAppScope.LocalMachine64),
            location: MissingInstallLocation,
            uninstall: @"C:\Program Files\GhostApp\unins000.exe");
        var systemComponent = InstalledApp(
            "System Piece",
            scope: nameof(InstalledAppScope.LocalMachine64),
            location: MissingInstallLocation,
            isSystemComponent: true);
        var planner = new UninstallPlannerService(registry: new UninstallRegistryService(branches: []));

        var defaultSeeds = planner.BuildSeedsFrom(
            [orphan, systemComponent],
            new UninstallPlannerOptions
            {
                IncludeAllApps = false,
                IncludeOrphanedRegistryEntries = true,
                IncludeSystemComponentOrphans = false,
                PathExists = _ => false
            });

        var defaultItem = Assert.Single(defaultSeeds);
        Assert.Equal("Ghost", defaultItem.DisplayName);
        Assert.Equal(UninstallOrphanRegistryScanner.GroupName, defaultItem.GroupName);

        var explicitSeeds = planner.BuildSeedsFrom(
            [orphan, systemComponent],
            new UninstallPlannerOptions
            {
                IncludeAllApps = false,
                IncludeOrphanedRegistryEntries = true,
                IncludeSystemComponentOrphans = true,
                PathExists = _ => false
            });

        Assert.Equal(2, explicitSeeds.Count);
        Assert.Contains(explicitSeeds, item => item.DisplayName == "System Piece" && item.ReviewManually);
    }

    private static UninstallOrphanRegistryOptions Options(
        Func<string, bool> pathExists,
        bool includeSystemComponents = false) => new()
    {
        PathExists = pathExists,
        IncludeSystemComponents = includeSystemComponents
    };

    private static InstalledApp InstalledApp(
        string name,
        string scope,
        string? location = null,
        string? uninstall = null,
        bool isSystemComponent = false) => new()
    {
        ProductCode = "{" + Guid.NewGuid() + "}",
        ScopeKey = scope,
        DisplayName = name,
        Publisher = "ACME",
        DisplayVersion = "1.0",
        InstallLocation = location,
        UninstallString = uninstall ?? "MsiExec.exe /I{00000000-0000-0000-0000-000000000000}",
        IsSystemComponent = isSystemComponent
    };
}
