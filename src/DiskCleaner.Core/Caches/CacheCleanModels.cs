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
    Error
}

public sealed record CleanEntry(
    CleanupItem Item,
    CleanOutcome Outcome,
    long FreedBytes,
    string? Note);

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

    public int FailedItems => Entries.Count(e => e.Outcome == CleanOutcome.Error || e.Outcome == CleanOutcome.Partial);

    public int DeferredItems => Entries.Count(e => e.Outcome == CleanOutcome.InUseSkipped);
}
