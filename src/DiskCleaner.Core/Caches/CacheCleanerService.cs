using System.IO;
using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;
using DiskCleaner.Core.Reports;
using DiskCleaner.Core.Scanning;

namespace DiskCleaner.Core.Caches;

public sealed class CacheCleanerService
{
    private const double FallbackThresholdFraction = 0.1;

    /// <summary>Таймаут штатной команды по умолчанию, если в справочнике не задан (FR-2.6).</summary>
    private const int DefaultCleanCommandTimeoutSec = 300;

    private const string RequiresDirectDeletionMarker = "объект помечен «требует прямого удаления»";

    private readonly DirectoryDeleter _deleter;
    private readonly DirectoryScanner _scanner;
    private readonly ICommandRunner _runner;

    public CacheCleanerService(
        DirectoryDeleter? deleter = null,
        DirectoryScanner? scanner = null,
        ICommandRunner? runner = null)
    {
        _deleter = deleter ?? new DirectoryDeleter(new RecycleBinService());
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
                    InUseMessages.SkippedNote(item));
            }
            else
            {
                entry = await CleanOneAsync(
                    item,
                    cleanOptions,
                    totalFreed,
                    completed,
                    items.Count,
                    progress,
                    cancellationToken);
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
        long bytesBeforeItem,
        int completedItems,
        int totalItems,
        IProgress<CleanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (item.MoveToRecycleBin)
        {
            return await MoveToRecycleBinAsync(item, cancellationToken);
        }

        // Очистка Корзины выбранного диска (FR-5.2): штатное API оболочки Windows.
        if (item.EmptyRecycleBinDrive is not null)
        {
            return await EmptyRecycleBinAsync(item, cancellationToken);
        }

        var notes = new List<string>();
        var startSize = item.SizeBytes ?? await MeasurePathAsync(item.Path);

        if (item.Path is null)
        {
            return await CleanCommandOnlyAsync(item, notes, cancellationToken);
        }

        if (item.IsOrphan)
        {
            notes.Add("Осиротевший кэш прежней конфигурации: менеджер больше не использует этот путь — удаляется напрямую (FR-2.4)");
        }

        var commandResult = (CommandResult?)null;
        var nativeRan = false;

        // Осиротевший кэш менеджер не знает — штатная команда не выполняется (FR-2.4):
        // путь существует, но конфигурация менеджера указывает на другой каталог.
        if (!item.IsOrphan &&
            !string.IsNullOrEmpty(item.CleanCommandFile) &&
            options.AllowNativeCommands)
        {
            commandResult = await _runner.RunAsync(new CommandDefinition
            {
                FileName = item.CleanCommandFile!,
                Arguments = item.CleanCommandArgs ?? string.Empty,
                TimeoutSec = CleanCommandTimeoutOf(item)
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
            if (nativeRan)
            {
                notes.Add(NativeIneffectiveNote(commandResult, nativeFreed, startSize) + "; применено прямое удаление каталога");
            }

            directDeletion = await _deleter.DeleteAsync(
                item,
                cancellationToken,
                DeletionProgress(item, bytesBeforeItem, completedItems, totalItems, progress));

            foreach (var error in directDeletion.Errors)
            {
                notes.Add(error);
            }

            if (directDeletion.SkippedFiles > 0)
            {
                notes.Insert(0, $"Прямое удаление: пропущено {FileCountText(directDeletion.SkippedFiles)} — заблокировано или нет доступа (журнал: файл и причина по каждому пропуску)");
            }
        }
        else if (exists && !item.AllowDirectDelete && nativeRan && nativeFreed < startSize * FallbackThresholdFraction)
        {
            notes.Add(NativeIneffectiveNote(commandResult, nativeFreed, startSize) + "; прямое удаление недоступно по конфигурации");
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

        if (outcome == CleanOutcome.RequiresAdmin)
        {
            notes.Insert(0, "Недостаточно прав (Access Denied) — объект перенесён в «требует админа» (FR-5.12)");
        }

        return new CleanEntry(item, outcome, freed, notes.Count == 0 ? null : string.Join("; ", notes));
    }

    /// <summary>
    /// Адаптер прогресса прямого удаления (NFR «показывать прогресс»): снимки из
    /// <see cref="DirectoryDeleter"/> превращаются в события <see cref="CleanProgress"/>
    /// с текущим объектом и детализацией (удалено/пропущено).
    /// </summary>
    private static IProgress<DirectoryDeletionProgress>? DeletionProgress(
        CleanupItem item,
        long bytesBeforeItem,
        int completedItems,
        int totalItems,
        IProgress<CleanProgress>? progress)
    {
        if (progress is null)
        {
            return null;
        }

        return new Progress<DirectoryDeletionProgress>(deletion =>
            progress.Report(new CleanProgress(
                item.DisplayName,
                completedItems,
                totalItems,
                bytesBeforeItem + Math.Max(0, deletion.DeletedBytes),
                DeletionProgressText(deletion))));
    }

    private static string DeletionProgressText(DirectoryDeletionProgress deletion)
    {
        var freed = CleanReportFormatter.FormatBytes(deletion.DeletedBytes);
        if (deletion.SkippedFiles == 0)
        {
            return $"Прямое удаление: удалено файлов {deletion.DeletedFiles} (~{freed})";
        }

        return $"Прямое удаление: удалено файлов {deletion.DeletedFiles} (~{freed}), пропущено {FileCountText(deletion.SkippedFiles)}";
    }

    private static string FileCountText(long count)
    {
        var lastTwo = count % 100;
        var lastDigit = count % 10;
        if (lastTwo is >= 11 and <= 14)
        {
            return $"{count} файлов";
        }

        return lastDigit switch
        {
            1 => $"{count} файл",
            >= 2 and <= 4 => $"{count} файла",
            _ => $"{count} файлов"
        };
    }

    private async Task<CleanEntry> EmptyRecycleBinAsync(CleanupItem item, CancellationToken cancellationToken)
    {
        var startSize = item.SizeBytes ?? await MeasurePathAsync(item.Path);
        var outcome = await _deleter.DeleteAsync(item, cancellationToken);

        if (outcome.Denied)
        {
            return new CleanEntry(
                item,
                CleanOutcome.Denied,
                0,
                string.Join("; ", outcome.Errors));
        }

        var stillExists = item.Path is not null && NativeDirectory.Probe(item.Path).Exists;
        var remaining = stillExists ? await MeasurePathAsync(item.Path) : 0L;
        var freed = Math.Max(0, startSize - remaining);
        item.SizeBytes = stillExists ? remaining : 0;

        if (outcome.Errors.Count == 0)
        {
            return new CleanEntry(
                item,
                CleanOutcome.DirectDeleted,
                freed,
                $"Корзина диска '{item.EmptyRecycleBinDrive}' очищена штатным API оболочки Windows (SHEmptyRecycleBin).");
        }

        var failed = string.Join("; ", outcome.Errors);
        if (freed > 0)
        {
            return new CleanEntry(item, CleanOutcome.Partial, freed, $"Корзина очищена не полностью: {failed}");
        }

        return new CleanEntry(item, CleanOutcome.Error, 0, $"Не удалось очистить Корзину диска '{item.EmptyRecycleBinDrive}': {failed}");
    }

    private async Task<CleanEntry> MoveToRecycleBinAsync(CleanupItem item, CancellationToken cancellationToken)
    {
        var outcome = await _deleter.DeleteAsync(item, cancellationToken);
        if (outcome.Denied)
        {
            return new CleanEntry(
                item,
                CleanOutcome.Denied,
                0,
                string.Join("; ", outcome.Errors));
        }

        if (outcome.FullyDeleted)
        {
            return new CleanEntry(
                item,
                CleanOutcome.MovedToRecycleBin,
                0,
                "Перемещено в Корзину. Место освободится после её очистки.");
        }

        return new CleanEntry(
            item,
            CleanOutcome.Error,
            0,
            string.Join("; ", outcome.Errors));
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
            TimeoutSec = CleanCommandTimeoutOf(item)
        }, cancellationToken);

        if (result.TimedOut)
        {
            notes.Add("Команда превысила таймаут и была остановлена");
        }
        else if (result.ExitCode != 0)
        {
            notes.Add($"Команда завершилась с кодом {result.ExitCode}: {Truncate(result.Output, 200)}");
        }

        var succeeded = result.ExitCode == 0 && !result.TimedOut;
        var outcome = succeeded ? CleanOutcome.CommandOnlyCleaned : CleanOutcome.Error;

        long freedBytes = 0;
        if (succeeded)
        {
            var reclaimed = ManagerOutputParser.TryParseReclaimedBytes(result.Output);
            if (reclaimed is > 0)
            {
                freedBytes = reclaimed.Value;
                notes.Add($"По данным менеджера освобождено ≈ {CleanReportFormatter.FormatBytes(freedBytes)}");
            }
            else if (string.IsNullOrWhiteSpace(result.Output))
            {
                notes.Add("Объём уточняется повторным анализом");
            }
        }

        return new CleanEntry(
            item,
            outcome,
            freedBytes,
            notes.Count == 0 ? "Команда выполнена; объём уточняется повторным анализом" : string.Join("; ", notes));
    }

    private static int CleanCommandTimeoutOf(CleanupItem item) =>
        item.CleanCommandTimeoutSec is > 0 ? item.CleanCommandTimeoutSec.Value : DefaultCleanCommandTimeoutSec;

    /// <summary>
    /// Причина неэффективности штатной команды (FR-2.7): команда выполнилась, но размер кэша
    /// не изменился или уменьшился незначительно (&lt; порога fallback), — объект помечается
    /// «требует прямого удаления».
    /// </summary>
    private static string NativeIneffectiveNote(
        CommandResult? commandResult,
        long nativeFreed,
        long startSize)
    {
        var effect = nativeFreed <= 0
            ? "размер кэша не изменился"
            : "размер кэша уменьшился незначительно";

        var cause = commandResult is null
            ? "Штатная команда не выполнена"
            : commandResult.TimedOut
                ? "Штатная команда превысила время ожидания, размер кэша не изменился"
                : commandResult.ExitCode == 0
                    ? $"Штатная команда завершилась с кодом 0, {effect}"
                    : $"Штатная команда завершилась с кодом {commandResult.ExitCode}, {effect}";

        var startText = startSize > 0 ? $" (было {CleanReportFormatter.FormatBytes(startSize)})" : string.Empty;
        return $"{cause}{startText} — {RequiresDirectDeletionMarker} (FR-2.7)";
    }

    private static CleanOutcome Classify(
        CleanupItem item,
        bool nativeRan,
        CommandResult? commandResult,
        DeletionOutcome? directDeletion,
        long freed,
        bool stillExists)
    {
        if (directDeletion is not null && directDeletion.Denied)
        {
            return CleanOutcome.Denied;
        }

        if (directDeletion is not null &&
            directDeletion.AccessDenied &&
            directDeletion.FreedBytes == 0 &&
            !directDeletion.FullyDeleted)
        {
            // Ничего не удалось удалить из-за нехватки прав (Win32 error 5 / FR-5.12):
            // шаг переносится в «требует админа», остальные продолжаются.
            return CleanOutcome.RequiresAdmin;
        }

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
                $"Будет пропущено. {InUseMessages.SkippedNote(item)}");
        }

        if (item.MoveToRecycleBin)
        {
            return new CleanEntry(
                item,
                CleanOutcome.DryRun,
                0,
                "Будет перемещено в Корзину (не удаляется безвозвратно)");
        }

        var note = item.IsOrphan
            ? "Будет удалено напрямую (осиротевший кэш прежней конфигурации, FR-2.4)"
            : "Будет очищено";

        if (item.Warning is not null)
        {
            note += $". {item.Warning}";
        }

        return new CleanEntry(
            item,
            CleanOutcome.DryRun,
            item.EffectiveSizeBytes,
            note);
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
