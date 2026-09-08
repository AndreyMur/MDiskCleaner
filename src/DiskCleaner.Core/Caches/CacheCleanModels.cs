using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Caches;

public enum CleanOutcome
{
    DryRun,
    InUseSkipped,
    NativeCleaned,
    DirectDeleted,
    CommandOnlyCleaned,
    Partial,
    MovedToRecycleBin,
    Denied,
    Error,
    Uninstalled,
    RegistryEntryDeleted,
    RebootRequired,
    AlreadyUninstalled,
    ElevationDeclined
}

public sealed record CleanEntry(
    CleanupItem Item,
    CleanOutcome Outcome,
    long FreedBytes,
    string? Note,
    int? ExitCode = null);

public sealed record CleanProgress(
    string CurrentName,
    int CompletedItems,
    int TotalItems,
    long BytesCleaned,
    string? Detail);

public sealed class CleanOptions
{
    public bool DryRun { get; set; }

    public bool AllowNativeCommands { get; set; } = true;
}

public sealed class CleanReport
{
    public IReadOnlyList<CleanEntry> Entries { get; init; } = Array.Empty<CleanEntry>();

    public bool DryRun { get; init; }

    public TimeSpan Elapsed { get; init; }

    public long TotalFreedBytes => Entries.Sum(e => Math.Max(0, e.FreedBytes));

    /// <summary>
    /// Шаги, завершившиеся ошибкой. «Частично» (FR-5.9) и «пропущен» не считаются ошибками плана:
    /// частично очищенный шаг — легитимный результат с перечислением неудалённых файлов.
    /// </summary>
    public int FailedItems => Entries.Count(e => e.Outcome == CleanOutcome.Error);

    /// <summary>Шаги со статусом «частично»: часть файлов удалена, часть заблокирована/недоступна (FR-5.9).</summary>
    public int PartialItems => Entries.Count(e => e.Outcome == CleanOutcome.Partial);

    /// <summary>Шаги, пропущенные из-за занятости процессами (IN_USE), deny-списка или отказа UAC.</summary>
    public int SkippedItems => Entries.Count(e => e.Outcome is CleanOutcome.InUseSkipped or CleanOutcome.Denied or CleanOutcome.ElevationDeclined);

    public int DeferredItems => Entries.Count(e => e.Outcome == CleanOutcome.InUseSkipped);
}
