using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Leftovers;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Core.Analysis;

public sealed class ScanSeedsProvider
{
    private readonly CacheCatalogService _catalog;
    private readonly UninstallRegistryService _registry;
    private readonly UninstallPlannerService _planner;
    private readonly LeftoverScannerService _leftoverScanner;
    private readonly ExclusionsStore _exclusions;
    private readonly SystemScanSeedsProvider _systemSeeds;
    private readonly bool _includeAllApps;

    public ScanSeedsProvider(
        CacheCatalogService? catalog = null,
        UninstallRegistryService? registry = null,
        UninstallPlannerService? planner = null,
        LeftoverScannerService? leftoverScanner = null,
        ExclusionsStore? exclusions = null,
        SystemScanSeedsProvider? systemSeeds = null,
        bool includeAllApps = false)
    {
        _catalog = catalog ?? new CacheCatalogService();
        _registry = registry ?? new UninstallRegistryService();
        _planner = planner ?? new UninstallPlannerService(registry: _registry);
        _leftoverScanner = leftoverScanner ?? new LeftoverScannerService();
        _exclusions = exclusions ?? new ExclusionsStore();
        _systemSeeds = systemSeeds ?? new SystemScanSeedsProvider();
        _includeAllApps = includeAllApps;
    }

    public async Task<IReadOnlyList<CleanupItem>> BuildSeedsAsync(CancellationToken cancellationToken = default)
    {
        var cacheSeeds = await _catalog.BuildSeedsAsync(cancellationToken);
        var apps = _registry.ReadInstalledApps();
        var seeds = new List<CleanupItem>(cacheSeeds);

        var uninstallOptions = new UninstallPlannerOptions
        {
            IncludeAllApps = _includeAllApps,
            IncludeOrphanedRegistryEntries = true
        };
        seeds.AddRange(_planner.BuildSeedsFrom(apps, uninstallOptions));

        var excluded = _exclusions.Load();
        var leftovers = _leftoverScanner.Scan(apps, excluded);
        seeds.AddRange(leftovers);

        seeds.AddRange(_systemSeeds.BuildSeeds());

        return seeds;
    }
}
