using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;
using DiskCleaner.Core.Scanning;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Core.Analysis;

/// <summary>
/// Единая точка запуска анализа для GUI (FR-1.1, FR-1.11). Два режима:
/// <list type="bullet">
/// <item><b>Профиль/известные объекты</b> (<see cref="AnalysisRunOptions.DiskRootPath"/> пуст) —
/// сканирование известных кэшей, реестра Uninstall, остатков и системных объектов
/// без полного обхода диска;</item>
/// <item><b>Полный скан диска</b> — обход корня выбранного диска с игнор-списком системных
/// каталогов (FR-1.2), детализацией крупных объектов (FR-1.5), системными объектами
/// и записями установленного ПО (FR-1.8).</item>
/// </list>
/// Оба режима завершаются одинаковым <see cref="AnalysisResult"/> (дерево категорий + объекты
/// плана), поэтому GUI строится поверх единой модели без собственной бизнес-логики.
/// </summary>
public sealed class AnalysisCoordinator : IAnalysisCoordinator
{
    private readonly ScanSeedsProvider _profileSeeds;
    private readonly AnalysisService _analysis;
    private readonly DiskScanService _diskScan;
    private readonly DiskScanPlanBuilder _diskPlanBuilder;
    private readonly UninstallPlannerService _uninstallPlanner;
    private readonly IProcessInspector _processInspector;
    private readonly InUseDetector _inUseDetector;
    private readonly TreeBuilder _treeBuilder;

    public AnalysisCoordinator(
        ScanSeedsProvider? profileSeeds = null,
        AnalysisService? analysis = null,
        DiskScanService? diskScan = null,
        DiskScanPlanBuilder? diskPlanBuilder = null,
        UninstallPlannerService? uninstallPlanner = null,
        IProcessInspector? processInspector = null,
        InUseDetector? inUseDetector = null,
        TreeBuilder? treeBuilder = null)
    {
        _profileSeeds = profileSeeds ?? new ScanSeedsProvider(includeAllApps: true);
        _analysis = analysis ?? new AnalysisService();
        _diskScan = diskScan ?? new DiskScanService(new ScanCacheStore());
        _diskPlanBuilder = diskPlanBuilder ?? new DiskScanPlanBuilder();
        _uninstallPlanner = uninstallPlanner ?? new UninstallPlannerService();
        _processInspector = processInspector ?? new ProcessInspector();
        _inUseDetector = inUseDetector ?? new InUseDetector();
        _treeBuilder = treeBuilder ?? new TreeBuilder();
    }

    public async Task<AnalysisResult> RunAsync(
        AnalysisRunOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AnalysisRunOptions();
        return options.IsProfileMode
            ? await RunProfileAsync(progress, cancellationToken)
            : await RunDiskAsync(options, progress, cancellationToken);
    }

    private async Task<AnalysisResult> RunProfileAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var seeds = await _profileSeeds.BuildSeedsAsync(cancellationToken);
        return await _analysis.AnalyzeAsync(seeds, progress, cancellationToken);
    }

    private async Task<AnalysisResult> RunDiskAsync(
        AnalysisRunOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var request = new DiskScanRequest
        {
            RootPath = options.DiskRootPath!,
            IncludeSystemDirectories = options.IncludeSystemDirectories
        };

        var scan = await _diskScan.ScanAsync(request, cancellationToken, progress);
        var leaves = new List<CleanupItem>(_diskPlanBuilder.BuildItems(scan));

        if (options.IncludeInstalledApps)
        {
            leaves.AddRange(_uninstallPlanner.BuildSeeds(new UninstallPlannerOptions
            {
                IncludeAllApps = true,
                IncludeOrphanedRegistryEntries = false
            }));
        }

        var runningProcesses = _processInspector.GetRunningProcesses();
        var inUseItems = _inUseDetector.MarkInUse(leaves, runningProcesses);

        return new AnalysisResult
        {
            CategoryTree = _treeBuilder.Build(leaves),
            Items = leaves,
            Errors = scan.Errors,
            Elapsed = DateTime.UtcNow - startedAt,
            InUseItems = inUseItems
        };
    }
}
