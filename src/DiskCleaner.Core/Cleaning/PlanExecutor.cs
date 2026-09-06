using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Core.Cleaning;

public sealed class PlanExecutor
{
    private readonly CacheCleanerService _localCleaner;
    private readonly IElevatedRunner _elevatedRunner;
    private readonly ElevatedScenarioBuilder _scenarioBuilder;
    private readonly ICommandRunner _commandRunner;
    private readonly LocalRegistryCleaner _registryCleaner;

    public PlanExecutor(
        CacheCleanerService? localCleaner = null,
        IElevatedRunner? elevatedRunner = null,
        ElevatedScenarioBuilder? scenarioBuilder = null,
        ICommandRunner? commandRunner = null,
        LocalRegistryCleaner? registryCleaner = null)
    {
        _localCleaner = localCleaner ?? new CacheCleanerService();
        _elevatedRunner = elevatedRunner ?? new ElevatedProcessLauncher();
        _scenarioBuilder = scenarioBuilder ?? new ElevatedScenarioBuilder();
        _commandRunner = commandRunner ?? new ProcessCommandRunner();
        _registryCleaner = registryCleaner ?? new LocalRegistryCleaner();
    }

    public async Task<CleanReport> CleanAsync(
        IEnumerable<CleanupItem> selectedItems,
        CleanOptions? options = null,
        IProgress<CleanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var cleanOptions = options ?? new CleanOptions();
        var startedAt = DateTime.UtcNow;
        var leaves = Analysis.AnalysisService.DescendantLeaves(selectedItems)
            .DistinctBy(i => i.Key)
            .ToList();

        if (cleanOptions.DryRun)
        {
            return CreateDryRunReport(leaves, startedAt);
        }

        var progressState = new ProgressState(leaves.Count, progress);

        var elevatedLeaves = leaves.Where(RequiresElevation).ToList();
        var registryLeaves = leaves
            .Where(l => l.RegistryDeletePath is not null && !l.RequiresAdmin)
            .ToList();
        var localUninstallLeaves = leaves
            .Where(l => l.UninstallMode && !l.RequiresAdmin)
            .ToList();
        var cacheLeaves = leaves
            .Where(l => !RequiresElevation(l) &&
                        l.RegistryDeletePath is null &&
                        !l.UninstallMode)
            .ToList();

        var entries = new List<CleanEntry>(leaves.Count);

        if (cacheLeaves.Count > 0)
        {
            var report = await _localCleaner.CleanAsync(
                cacheLeaves,
                cleanOptions,
                null,
                cancellationToken);
            entries.AddRange(report.Entries);
            progressState.AddCompleted(cacheLeaves.Count, report.TotalFreedBytes, "Локальная очистка");
        }

        foreach (var leaf in registryLeaves)
        {
            var entry = _registryCleaner.Delete(leaf, cancellationToken);
            entries.Add(entry);
            progressState.AddCompleted(1, Math.Max(0, entry.FreedBytes), leaf.DisplayName);
        }

        foreach (var leaf in localUninstallLeaves)
        {
            var entry = await RunLocalUninstallAsync(leaf, cancellationToken);
            entries.Add(entry);
            progressState.AddCompleted(1, 0, leaf.DisplayName);
        }

        if (elevatedLeaves.Count > 0)
        {
            var elevatedEntries = await RunElevatedBatchAsync(elevatedLeaves, cancellationToken);
            entries.AddRange(elevatedEntries);
            progressState.AddCompleted(
                elevatedLeaves.Count,
                elevatedEntries.Sum(e => Math.Max(0, e.FreedBytes)),
                "Системные шаги");
        }

        return new CleanReport
        {
            Entries = entries,
            DryRun = false,
            Elapsed = DateTime.UtcNow - startedAt
        };
    }

    private async Task<IReadOnlyList<CleanEntry>> RunElevatedBatchAsync(
        IReadOnlyList<CleanupItem> items,
        CancellationToken cancellationToken)
    {
        var scenario = _scenarioBuilder.Build(items);

        ElevatedJournal journal;
        try
        {
            journal = await _elevatedRunner.RunAsync(scenario, cancellationToken);
        }
        catch (ElevationDeclinedException)
        {
            return items
                .Select(item => new CleanEntry(item, CleanOutcome.ElevationDeclined, 0, "Повышение прав отменено пользователем."))
                .ToList();
        }

        return _scenarioBuilder.MapResults(items, journal);
    }

    private async Task<CleanEntry> RunLocalUninstallAsync(CleanupItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.CleanCommandFile))
        {
            return new CleanEntry(item, CleanOutcome.Error, 0, "Не задан деинсталлятор.");
        }

        var result = await _commandRunner.RunAsync(new CommandDefinition
        {
            FileName = item.CleanCommandFile!,
            Arguments = item.CleanCommandArgs ?? string.Empty,
            TimeoutSec = 600
        }, cancellationToken);

        var isMsiexec = Path.GetFileName(item.CleanCommandFile!)
            .Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);

        if (result.TimedOut)
        {
            return new CleanEntry(item, CleanOutcome.Error, 0, "Деинсталлятор превысил время ожидания и был остановлен.");
        }

        var exitCode = result.ExitCode;
        if (isMsiexec && exitCode is UninstallExitCodes.ProductNotInstalled or UninstallExitCodes.InstallSourceAbsent)
        {
            return new CleanEntry(item, CleanOutcome.AlreadyUninstalled, 0, $"Продукт уже не установлен (код {exitCode}).");
        }

        if (exitCode == 0)
        {
            return new CleanEntry(item, CleanOutcome.Uninstalled, item.SizeBytes ?? 0, "Деинсталляция завершена успешно.");
        }

        if (isMsiexec && exitCode == UninstallExitCodes.RebootRequired)
        {
            return new CleanEntry(item, CleanOutcome.RebootRequired, 0, "Требуется перезагрузка (код 3010).");
        }

        return new CleanEntry(
            item,
            CleanOutcome.Error,
            0,
            $"Деинсталлятор завершился с кодом {exitCode}: {Truncate(result.Output, 200)}");
    }

    private static bool RequiresElevation(CleanupItem item) =>
        item.RequiresAdmin;

    private static CleanReport CreateDryRunReport(IReadOnlyList<CleanupItem> leaves, DateTime startedAt)
    {
        var entries = leaves
            .Select(leaf => new CleanEntry(
                leaf,
                CleanOutcome.DryRun,
                leaf.EffectiveSizeBytes,
                leaf.Warning is null ? "Будет очищено" : $"Будет очищено. {leaf.Warning}"))
            .ToList();

        return new CleanReport
        {
            Entries = entries,
            DryRun = true,
            Elapsed = DateTime.UtcNow - startedAt
        };
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";

    private sealed class ProgressState
    {
        private readonly int _total;
        private readonly IProgress<CleanProgress>? _progress;
        private int _completed;
        private long _freed;

        public ProgressState(int total, IProgress<CleanProgress>? progress)
        {
            _total = total;
            _progress = progress;
        }

        public void AddCompleted(int count, long freed, string current)
        {
            _completed += count;
            _freed += freed;
            _progress?.Report(new CleanProgress(current, _completed, _total, _freed, null));
        }
    }
}
