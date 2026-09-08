using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Models;
using Serilog;

namespace DiskCleaner.Core.Cleaning;

/// <summary>
/// Журнал каждого действия очистки через Serilog (FR-5.13): время, объект, размер,
/// тип операции, результат, exit-код, освобождено, ошибка. Записи пишутся в
/// <c>%LOCALAPPDATA%\DiskCleaner\logs\*.log</c> (UTF-8), сконфигурированный
/// <see cref="Logging.DiskCleanerLog"/>.
/// </summary>
public static class CleanActionJournal
{
    public static void Write(CleanEntry entry)
    {
        var item = entry.Item;
        Log.Information(
            "Clean action: time={Time:yyyy-MM-dd HH:mm:ss} object={Object} sizeBytes={Size} op={Operation} result={Outcome} exitCode={ExitCode} freedBytes={Freed} error={Error}",
            DateTime.Now,
            item.Path ?? item.DisplayName,
            item.EffectiveSizeBytes,
            OperationOf(item),
            entry.Outcome.ToString(),
            entry.ExitCode?.ToString() ?? "-",
            entry.FreedBytes,
            HasFailure(entry.Outcome) ? entry.Note : null);
    }

    private static bool HasFailure(CleanOutcome outcome) => outcome switch
    {
        CleanOutcome.Error or
        CleanOutcome.Partial or
        CleanOutcome.Denied or
        CleanOutcome.InUseSkipped or
        CleanOutcome.ElevationDeclined or
        CleanOutcome.RequiresAdmin => true,
        _ => false
    };

    private static string OperationOf(CleanupItem item)
    {
        if (item.MoveToRecycleBin)
        {
            return "move-to-recycle-bin";
        }

        if (item.UninstallMode)
        {
            return "uninstall";
        }

        if (item.RegistryDeletePath is not null)
        {
            return "delete-registry";
        }

        if (!string.IsNullOrEmpty(item.CleanCommandFile))
        {
            return "command";
        }

        if (item.DeleteContentsOnly)
        {
            return item.RequiresAdmin ? "clear-directory-elevated" : "clear-directory";
        }

        return "delete-path";
    }
}
