using System.IO;
using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Scanning;

namespace DiskCleaner.Core.Caches;

public sealed class CacheCleanerService
{
    private const double FallbackThresholdFraction = 0.1;

    private readonly DirectoryDeleter _deleter;
    private readonly DirectoryScanner _scanner;
    private readonly ICommandRunner _runner;

    public CacheCleanerService(
        DirectoryDeleter? deleter = null,
        DirectoryScanner? scanner = null,
        ICommandRunner? runner = null)
    {
        _deleter = deleter ?? new DirectoryDeleter();
        _scanner = scanner ?? new DirectoryScanner();
        _runner = runner ?? new ProcessCommandRunner();
    }

    public async Task<CleanReport> CleanAsync(
        IEnumerable<CleanupItem> selectedItems,
        CleanOptions? options = null,
        IProgress<CleanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var cleanOptions = options ?? new CleanOptions();
        var startedAt = DateTime.UtcNow;
        var items = AnalysisService.DescendantLeaves(selectedItems)
            .DistinctBy(i => i.Key)
            .ToList();

        var entries = new List<CleanEntry>(items.Count);
        var totalFreed = 0L;
        var completed = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CleanProgress(item.DisplayName, completed, items.Count, totalFreed, "Подготовка"));

            CleanEntry entry;
            if (cleanOptions.DryRun)
            {
                entry = CreateDryRunEntry(item);
            }
            else if (item.InUse)
            {
                entry = new CleanEntry(
                    item,
                    CleanOutcome.InUseSkipped,
                    0,
                    "Используется запущенным процессом — пропущено, план не прерван");
            }
            else
            {
                entry = await CleanOneAsync(item, cleanOptions, cancellationToken);
            }

            entries.Add(entry);
            totalFreed += Math.Max(0, entry.FreedBytes);
            completed++;

            progress?.Report(new CleanProgress(item.DisplayName, completed, items.Count, totalFreed, "Готово"));
        }

        return new CleanReport
        {
            Entries = entries,
            DryRun = cleanOptions.DryRun,
            Elapsed = DateTime.UtcNow - startedAt
        };
    }

    private async Task<CleanEntry> CleanOneAsync(
        CleanupItem item,
        CleanOptions options,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var startSize = item.SizeBytes ?? await MeasurePathAsync(item.Path);

        if (item.Path is null)
        {
            return await CleanCommandOnlyAsync(item, notes, cancellationToken);
        }

        var commandResult = (CommandResult?)null;
        var nativeRan = false;

        if (!string.IsNullOrEmpty(item.CleanCommandFile) && options.AllowNativeCommands)
        {
            commandResult = await _runner.RunAsync(new CommandDefinition
            {
                FileName = item.CleanCommandFile!,
                Arguments = item.CleanCommandArgs ?? string.Empty,
                TimeoutSec = 120
            }, cancellationToken);
            nativeRan = true;

            if (commandResult.TimedOut)
            {
                notes.Add("Команда превысила таймаут и была остановлена");
            }
            else if (commandResult.ExitCode != 0)
            {
                notes.Add($"Команда завершилась с кодом {commandResult.ExitCode}");
            }
        }

        var exists = NativeDirectory.Probe(item.Path).Exists;
        var remainingAfterNative = 0L;
        var nativeFreed = 0L;

        if (exists)
        {
            remainingAfterNative = await MeasurePathAsync(item.Path);
            nativeFreed = Math.Max(0, startSize - remainingAfterNative);
        }

        DeletionOutcome? directDeletion = null;
        var nativeEffective = nativeRan &&
                              commandResult is { ExitCode: 0, TimedOut: false } &&
                              nativeFreed >= startSize * FallbackThresholdFraction;

        if (exists && item.AllowDirectDelete && !nativeEffective)
        {
            directDeletion = await _deleter.DeleteAsync(item, cancellationToken);
            foreach (var error in directDeletion.Errors)
            {
                notes.Add(error);
            }
        }
        else if (exists && !item.AllowDirectDelete && nativeRan && nativeFreed < startSize * FallbackThresholdFraction)
        {
            notes.Add("Команда почти не уменьшила объект; прямое удаление недоступно");
        }

        var stillExists = NativeDirectory.Probe(item.Path).Exists;
        var finalRemaining = stillExists ? await MeasurePathAsync(item.Path) : 0L;
        var freed = Math.Max(0, startSize - finalRemaining);

        item.SizeBytes = stillExists ? finalRemaining : 0;

        var outcome = Classify(item, nativeRan, commandResult, directDeletion, freed, stillExists);
        if (outcome == CleanOutcome.Error && notes.Count == 0)
        {
            notes.Add("Не удалось очистить объект");
        }

        return new CleanEntry(item, outcome, freed, notes.Count == 0 ? null : string.Join("; ", notes));
    }

    private async Task<CleanEntry> CleanCommandOnlyAsync(
        CleanupItem item,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.CleanCommandFile))
        {
            return new CleanEntry(item, CleanOutcome.Error, 0, "Для объекта не задана команда очистки");
        }

        var result = await _runner.RunAsync(new CommandDefinition
        {
            FileName = item.CleanCommandFile!,
            Arguments = item.CleanCommandArgs ?? string.Empty,
            TimeoutSec = 600
        }, cancellationToken);

        if (result.TimedOut)
        {
            notes.Add("Команда превысила таймаут и была остановлена");
        }
        else if (result.ExitCode != 0)
        {
            notes.Add($"Команда завершилась с кодом {result.ExitCode}: {Truncate(result.Output, 200)}");
        }

        var outcome = result.ExitCode == 0 && !result.TimedOut
            ? CleanOutcome.CommandOnlyCleaned
            : CleanOutcome.Error;

        return new CleanEntry(
            item,
            outcome,
            0,
            notes.Count == 0 ? "Команда выполнена; объём уточняется повторным анализом" : string.Join("; ", notes));
    }

    private static CleanOutcome Classify(
        CleanupItem item,
        bool nativeRan,
        CommandResult? commandResult,
        DeletionOutcome? directDeletion,
        long freed,
        bool stillExists)
    {
        if (directDeletion is not null && !directDeletion.FullyDeleted)
        {
            return CleanOutcome.Partial;
        }

        if (directDeletion is not null)
        {
            return CleanOutcome.DirectDeleted;
        }

        if (freed > 0 && nativeRan && commandResult is { ExitCode: 0 })
        {
            return CleanOutcome.NativeCleaned;
        }

        if (freed > 0)
        {
            return CleanOutcome.Partial;
        }

        return stillExists ? CleanOutcome.Error : CleanOutcome.DirectDeleted;
    }

    private static CleanEntry CreateDryRunEntry(CleanupItem item)
    {
        if (item.InUse)
        {
            return new CleanEntry(
                item,
                CleanOutcome.DryRun,
                0,
                "Будет пропущено (объект используется процессом)");
        }

        return new CleanEntry(
            item,
            CleanOutcome.DryRun,
            item.EffectiveSizeBytes,
            item.Warning is null ? "Будет очищено" : $"Будет очищено. {item.Warning}");
    }

    private async Task<long> MeasurePathAsync(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return 0;
        }

        var outcome = await _scanner.MeasureAsync([path]);
        return outcome.Results.TryGetValue(path, out var measurement)
            ? measurement.SizeBytes
            : 0;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
