using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Analysis;

public sealed class AnalysisResult
{
    public IReadOnlyList<CleanupItem> CategoryTree { get; init; } = Array.Empty<CleanupItem>();

    public IReadOnlyList<CleanupItem> Items { get; init; } = Array.Empty<CleanupItem>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public TimeSpan Elapsed { get; init; }

    public int InUseItems { get; init; }

    public int SkippedNonexistent { get; init; }

    public int TimedOutBranches { get; init; }

    public long TotalBytes => Items.Sum(i => i.EffectiveSizeBytes);

    public long TotalFiles => Items.Sum(i => i.EffectiveFileCount);
}
