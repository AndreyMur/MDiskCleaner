using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;
using DiskCleaner.Core.Scanning;

namespace DiskCleaner.Core.Analysis;

public sealed class AnalysisService
{
    private readonly DirectoryScanner _scanner;
    private readonly InUseDetector _inUseDetector;
    private readonly IProcessInspector _processInspector;
    private readonly TreeBuilder _treeBuilder;

    public AnalysisService(
        DirectoryScanner? scanner = null,
        InUseDetector? inUseDetector = null,
        IProcessInspector? processInspector = null,
        TreeBuilder? treeBuilder = null)
    {
        _scanner = scanner ?? new DirectoryScanner();
        _inUseDetector = inUseDetector ?? new InUseDetector();
        _processInspector = processInspector ?? new ProcessInspector();
        _treeBuilder = treeBuilder ?? new TreeBuilder();
    }

    public async Task<AnalysisResult> AnalyzeAsync(
        IEnumerable<CleanupItem> seeds,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var leaves = DescendantLeaves(seeds).ToList();
        var pathLeaves = leaves.Where(l => !string.IsNullOrEmpty(l.Path)).ToList();
        var commandLeaves = leaves.Where(l => string.IsNullOrEmpty(l.Path)).ToList();

        var outcome = await _scanner.MeasureAsync(
            pathLeaves.Select(l => l.Path!).ToList(),
            cancellationToken,
            progress);

        var measured = new List<CleanupItem>(pathLeaves.Count + commandLeaves.Count);
        measured.AddRange(commandLeaves);
        var skippedNonexistent = 0;

        foreach (var leaf in pathLeaves)
        {
            if (leaf.Path is null || !outcome.Results.TryGetValue(leaf.Path, out var measurement))
            {
                continue;
            }

            if (!measurement.Exists)
            {
                skippedNonexistent++;
                continue;
            }

            leaf.SizeBytes = measurement.SizeBytes;
            leaf.FileCount = measurement.FileCount;
            measured.Add(leaf);
        }

        var runningProcesses = _processInspector.GetRunningProcesses();
        var inUseItems = _inUseDetector.MarkInUse(measured, runningProcesses);

        var tree = _treeBuilder.Build(measured);

        return new AnalysisResult
        {
            CategoryTree = tree,
            Items = measured,
            Errors = outcome.Errors,
            Elapsed = DateTime.UtcNow - startedAt,
            InUseItems = inUseItems,
            SkippedNonexistent = skippedNonexistent
        };
    }

    public static IReadOnlyList<CleanupItem> DescendantLeaves(IEnumerable<CleanupItem> items)
    {
        var result = new List<CleanupItem>();
        foreach (var item in items)
        {
            CollectLeaves(item, result);
        }

        return result;
    }

    private static void CollectLeaves(CleanupItem item, List<CleanupItem> leaves)
    {
        if (!item.IsGroup)
        {
            leaves.Add(item);
            return;
        }

        foreach (var child in item.Children)
        {
            CollectLeaves(child, leaves);
        }
    }
}
