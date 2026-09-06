using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

public class BundleFallbackTests
{
    private static InstalledApp SdkBundleApp() => new()
    {
        ProductCode = "{33333333-3333-3333-3333-333333333333}",
        ScopeKey = nameof(InstalledAppScope.LocalMachine64),
        DisplayName = "Windows Software Development Kit",
        DisplayVersion = "10.1.26100.1",
        Publisher = "Microsoft Corporation",
        UninstallString = "MsiExec.exe /X{33333333-3333-3333-3333-333333333333}",
        InstallLocation = @"C:\Program Files (x86)\Windows Kits\10"
    };

    [Fact]
    public void Resolve_WhenPackageCacheHasEngine_ReturnsEngineExe()
    {
        using var root = new TempRoot();
        var cacheDir = root.Combine("Package Cache\\33333333-3333-3333-3333-333333333333");
        var engine = root.CreateFile("ProgramData\\Package Cache\\33333333-3333-3333-3333-333333333333\\winsdksetup.exe", 100);

        var resolver = new BundleFallbackResolver();
        var result = resolver.TryResolveUninstallerExe(SdkBundleApp(), root.ProgramData);

        Assert.Equal(engine, result);
    }

    [Fact]
    public void Resolve_WhenNoPackageCache_ReturnsNull()
    {
        using var root = new TempRoot();
        var resolver = new BundleFallbackResolver();

        var result = resolver.TryResolveUninstallerExe(SdkBundleApp(), root.ProgramData);

        Assert.Null(result);
    }

    [Fact]
    public void Planner_PrefersBundleEngineOverMsiexec_WhenPackageCacheExists()
    {
        using var root = new TempRoot();
        var cacheDir = root.Combine("Package Cache\\33333333-3333-3333-3333-333333333333");
        var engine = root.CreateFile("ProgramData\\Package Cache\\33333333-3333-3333-3333-333333333333\\winsdksetup.exe", 100);

        var planner = new UninstallPlannerService(
            registry: new UninstallRegistryService(branches: []),
            programDataRoot: root.ProgramData);

        var seeds = planner.BuildSeedsFrom(
            [SdkBundleApp()],
            new UninstallPlannerOptions { IncludeAllApps = true });

        var seed = Assert.Single(seeds, s => s.UninstallMode);
        Assert.Equal(engine, seed.CleanCommandFile);
        Assert.Contains("/uninstall", seed.CleanCommandArgs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("msiexec.exe", seed.CleanCommandFile, StringComparison.OrdinalIgnoreCase);
    }
}
