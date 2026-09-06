using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Reports;
using DiskCleaner.Core.Scanning;

namespace DiskCleaner.Tests;

/// <summary>
/// Снапшот скана в scancache (FR-1.13): round-trip сохранения/загрузки единой JSON-схемы
/// и расчёт дельты «было/стало» между повторными сканами.
/// </summary>
public class PlanSnapshotTests
{
    [Fact]
    public void Store_RoundTrips_CategoriesAndCyrillicPaths()
    {
        using var root = new TempRoot();
        var cacheDir = root.Combine("scancache");
        var store = new PlanSnapshotStore(cacheDir);

        var doc = BuildScan(
            Leaf("npm cache", 1000, CleanupCategory.Cache, CleanupRisk.Low, "Менеджеры пакетов", @"C:\Users\тест\AppData\Local\npm-cache"));

        store.Save(doc);
        Assert.True(store.Exists);

        var loaded = store.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal(doc.Summary.TotalBytes, loaded!.Summary.TotalBytes);
        Assert.Equal(doc.Summary.TotalItems, loaded.Summary.TotalItems);
        Assert.Equal(doc.Categories.Count, loaded.Categories.Count);
        Assert.Equal("Кэши", loaded.Categories[0].CategoryText);
        var item = Assert.Single(loaded.Items);
        Assert.Equal("npm cache", item.Name);
        Assert.Equal(@"C:\Users\тест\AppData\Local\npm-cache", item.Path);
        Assert.Equal("Низкий", item.RiskText);
        Assert.Equal("Очистить", item.DefaultActionText);
    }

    [Fact]
    public void Store_ReturnsNull_WhenSnapshotMissingOrDirectoryEmpty()
    {
        using var root = new TempRoot();
        var store = new PlanSnapshotStore(root.Combine("scancache"));

        Assert.False(store.Exists);
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public void Comparer_ComputesDelta_WasIs_ByTotalCategoryAndObject()
    {
        var previous = BuildScan(
            Leaf("npm cache", 1000, CleanupCategory.Cache, CleanupRisk.Low, "Менеджеры пакетов", @"C:\npm"),
            Leaf("updater", 400, CleanupCategory.Leftover, CleanupRisk.Medium, "Программы", @"C:\app-updater"),
            Leaf("JDK 8", 500, CleanupCategory.DevToolchain, CleanupRisk.Medium, "Java", @"C:\jdk8"));

        var current = BuildScan(
            Leaf("npm cache", 300, CleanupCategory.Cache, CleanupRisk.Low, "Менеджеры пакетов", @"C:\npm"),
            Leaf("JDK 8", 500, CleanupCategory.DevToolchain, CleanupRisk.Medium, "Java", @"C:\jdk8"),
            Leaf("новый кэш", 200, CleanupCategory.Cache, CleanupRisk.Low, "Менеджеры пакетов", @"C:\новый-кэш"));

        var comparison = PlanComparer.Compare(previous, current);

        Assert.Equal(previous.Summary.TotalBytes, comparison.PreviousTotalBytes);
        Assert.Equal(current.Summary.TotalBytes, comparison.CurrentTotalBytes);
        Assert.Equal(-900, comparison.DeltaBytes);
        Assert.Equal(0, comparison.DeltaItems);

        var cache = comparison.Categories.Single(c => c.Category == "Cache");
        Assert.Equal(1000, cache.PreviousBytes);
        Assert.Equal(500, cache.CurrentBytes);
        Assert.Equal(-500, cache.DeltaBytes);
        Assert.Equal(1, cache.PreviousItems);
        Assert.Equal(2, cache.CurrentItems);
        Assert.Equal(1, cache.DeltaItems);

        var leftover = comparison.Categories.Single(c => c.Category == "Leftover");
        Assert.Equal(400, leftover.PreviousBytes);
        Assert.Equal(0, leftover.CurrentBytes);
        Assert.Equal(-400, leftover.DeltaBytes);

        var removed = comparison.Objects.Single(o => o.Key == "updater");
        Assert.Equal(PlanObjectChangeKind.Removed, removed.ChangeKind);
        Assert.Equal(-400, removed.DeltaBytes);

        var shrunk = comparison.Objects.Single(o => o.Key == "npm cache");
        Assert.Equal(PlanObjectChangeKind.Changed, shrunk.ChangeKind);
        Assert.Equal(-700, shrunk.DeltaBytes);

        var added = comparison.Objects.Single(o => o.Key == "новый кэш");
        Assert.Equal(PlanObjectChangeKind.Added, added.ChangeKind);
        Assert.Equal(200, added.DeltaBytes);

        var unchanged = comparison.Objects.Single(o => o.Key == "JDK 8");
        Assert.Equal(PlanObjectChangeKind.Unchanged, unchanged.ChangeKind);
        Assert.Equal(0, unchanged.DeltaBytes);
    }

    [Fact]
    public async Task Service_Capture_FirstScanReturnsNull_SecondBuildsComparison()
    {
        using var root = new TempRoot();
        var store = new PlanSnapshotStore(root.Combine("scancache"));
        var service = new PlanSnapshotService(store);

        var first = await ScanOnDiskAsync(root, 1000);
        var firstComparison = service.Capture(first);

        Assert.Null(firstComparison);
        Assert.True(store.Exists);

        var second = await ScanOnDiskAsync(root, 100);
        var secondComparison = service.Capture(second);

        Assert.NotNull(secondComparison);
        Assert.Equal(-900, secondComparison!.DeltaBytes);

        var saved = store.TryLoad();
        Assert.NotNull(saved);
        Assert.Equal(100, saved!.Summary.TotalBytes);
    }

    private static PlanDocument BuildScan(params CleanupItem[] items)
    {
        var result = new AnalysisResult
        {
            Items = items,
            Elapsed = TimeSpan.FromSeconds(1)
        };
        return PlanDocumentBuilder.FromScan(result);
    }

    private static async Task<AnalysisResult> ScanOnDiskAsync(TempRoot root, int sizeBytes)
    {
        var path = Path.Combine(root.LocalAppData, "npm-cache");
        Directory.CreateDirectory(path);
        await File.WriteAllBytesAsync(Path.Combine(path, "a.bin"), new byte[sizeBytes]);

        var environment = new FakeEnvironment(root);
        var coordinator = new AnalysisCoordinator(
            diskScan: new DiskScanService(environment: environment),
            uninstallPlanner: new DiskCleaner.Core.Uninstall.UninstallPlannerService(
                registry: new DiskCleaner.Core.Uninstall.UninstallRegistryService(branches: [])),
            processInspector: new FakeProcessInspector());

        return await coordinator.RunAsync(new AnalysisRunOptions
        {
            DiskRootPath = root.Path,
            IncludeInstalledApps = false
        });
    }

    private static CleanupItem Leaf(
        string name,
        long size,
        CleanupCategory category,
        CleanupRisk risk,
        string? group,
        string path)
    {
        return new CleanupItem
        {
            Key = name,
            DisplayName = name,
            GroupName = group,
            Category = category,
            Risk = risk,
            Target = CleanupTarget.Directory,
            Path = path,
            SizeBytes = size,
            FileCount = 1
        };
    }
}
