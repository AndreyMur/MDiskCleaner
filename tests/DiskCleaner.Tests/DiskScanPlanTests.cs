using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Scanning;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

public class DiskScanPlanTests
{
    [Fact]
    public void DiskScanPlanBuilder_MapsObjectsLargeComponentsAndSystemObjects()
    {
        var npm = @"C:\scan\npm-cache";
        var gradleRoot = @"C:\Users\test\.gradle";
        var gradleCaches = gradleRoot + @"\caches";
        var gradleJdks = gradleRoot + @"\jdks";

        var scan = new DiskScanResult
        {
            ScanRoot = @"C:\scan",
            Objects =
            [
                new DiskObjectMeasurement(
                    npm,
                    "npm cache",
                    "Менеджеры пакетов",
                    CleanupCategory.Cache,
                    CleanupRisk.Low,
                    new DirectoryMeasurement(npm, 500, 3, Exists: true))
            ],
            LargeObjects =
            [
                new LargeObjectDetail(
                    "gradle",
                    "Gradle (.gradle)",
                    gradleRoot,
                    CleanupCategory.Cache,
                    CleanupRisk.Low,
                    new DirectoryMeasurement(gradleRoot, 600, 4, Exists: true),
                    new[]
                    {
                        new DiskObjectMeasurement(
                            gradleCaches,
                            "caches",
                            "Gradle (.gradle)",
                            CleanupCategory.Cache,
                            CleanupRisk.Low,
                            new DirectoryMeasurement(gradleCaches, 300, 2, Exists: true)),
                        new DiskObjectMeasurement(
                            gradleJdks,
                            "jdks",
                            "Gradle (.gradle)",
                            CleanupCategory.DevToolchain,
                            CleanupRisk.Medium,
                            new DirectoryMeasurement(gradleJdks, 0, 0, Exists: true, TimedOut: true))
                    }),
                new LargeObjectDetail(
                    "empty",
                    "Пустой крупный объект",
                    @"C:\Users\test\.empty",
                    CleanupCategory.Other,
                    CleanupRisk.High,
                    new DirectoryMeasurement(@"C:\Users\test\.empty", 0, 0, Exists: true),
                    Array.Empty<DiskObjectMeasurement>())
            ],
            SystemObjects =
            [
                new SystemObjectMeasurement(
                    "recycle-bin:C:\\",
                    "Корзина (C:)",
                    @"C:\$Recycle.Bin\S-1-5-21-1",
                    700,
                    1,
                    Present: true),
                new SystemObjectMeasurement(
                    "hibernation:C:\\",
                    "hiberfil.sys (файл гибернации)",
                    @"C:\hiberfil.sys",
                    1000,
                    1,
                    Present: true)
            ]
        };

        var items = new DiskScanPlanBuilder().BuildItems(scan);

        var npmItem = items.Single(i => i.Key == "disk:known:" + npm);
        Assert.Equal("npm cache", npmItem.DisplayName);
        Assert.Equal(CleanupCategory.Cache, npmItem.Category);
        Assert.Equal(CleanupRisk.Low, npmItem.Risk);
        Assert.Equal(500, npmItem.SizeBytes);

        var caches = items.Single(i => i.Key == "disk:large:" + gradleCaches);
        Assert.Equal("caches", caches.DisplayName);
        Assert.Equal("Gradle (.gradle)", caches.GroupName);

        var recycle = items.Single(i => i.Key == "disk:recycle-bin:C:\\");
        Assert.Equal(CleanupCategory.RecycleBin, recycle.Category);
        Assert.Equal(CleanupRisk.Low, recycle.Risk);
        Assert.Equal(@"C:\", recycle.EmptyRecycleBinDrive);
        Assert.Equal(700, recycle.SizeBytes);

        var hibernation = items.Single(i => i.Key == "disk:hibernation:C:\\");
        Assert.Equal(CleanupCategory.SystemFile, hibernation.Category);
        Assert.Null(hibernation.Path);
        Assert.True(hibernation.CommandOnly);
        Assert.True(hibernation.RequiresAdmin);
        Assert.Equal("/h off", hibernation.CleanCommandArgs);
        Assert.Equal(@"C:\hiberfil.sys", hibernation.VerifyPathAbsent);
        Assert.Equal(1000, hibernation.SizeBytes);

        Assert.DoesNotContain(items, i => i.Path == gradleJdks);
        Assert.Equal(items.Count, items.Select(i => i.Key).Distinct().Count());
    }

    [Fact]
    public async Task AnalysisCoordinator_DiskScanBuildsPlanWithCacheItems()
    {
        using var root = new TempRoot();
        root.CreateFile("LocalAppData\\npm-cache\\a.bin", 250);

        var environment = new FakeEnvironment(root);
        var coordinator = new AnalysisCoordinator(
            diskScan: new DiskScanService(environment: environment),
            uninstallPlanner: new UninstallPlannerService(
                registry: new UninstallRegistryService(branches: [])),
            processInspector: new FakeProcessInspector());

        var result = await coordinator.RunAsync(new AnalysisRunOptions { DiskRootPath = root.Path });

        var item = Assert.Single(result.Items);
        Assert.Equal(250, item.SizeBytes);
        Assert.Equal(CleanupCategory.Cache, item.Category);
        Assert.Equal("npm cache", item.DisplayName);
        Assert.Equal(0, result.InUseItems);
        Assert.Empty(result.Errors);
        Assert.NotEmpty(result.CategoryTree);
    }

    [Fact]
    public async Task AnalysisCoordinator_DiskScanWithInstalledAppsDisabled_KeepsOnlyScanItems()
    {
        using var root = new TempRoot();
        root.CreateFile("UserProfile\\.cargo\\registry\\crate.crate", 100);

        var environment = new FakeEnvironment(root);
        var coordinator = new AnalysisCoordinator(
            diskScan: new DiskScanService(environment: environment),
            uninstallPlanner: new UninstallPlannerService(
                registry: new UninstallRegistryService(branches: [])),
            processInspector: new FakeProcessInspector());

        var result = await coordinator.RunAsync(new AnalysisRunOptions
        {
            DiskRootPath = root.Path,
            IncludeInstalledApps = false
        });

        Assert.Single(result.Items);
        Assert.Equal(CleanupCategory.Cache, result.Items[0].Category);
    }
}
