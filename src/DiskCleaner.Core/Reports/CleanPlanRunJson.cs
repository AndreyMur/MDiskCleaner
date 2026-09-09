using System.Text.Json;
using System.Text.Json.Serialization;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Localization;

namespace DiskCleaner.Core.Reports;

/// <summary>
/// JSON-схема итогового отчёта исполнения плана (FR-5.16): <c>diskcleaner.plan-run</c>.
/// Включает сводку план/факт (FR-5.15), фазы, пропущенные/заблокированные с причинами
/// (FR-5.14), снимок свободного места до/после и кандидатов второго эшелона (FR-5.19).
/// Сериализация — System.Text.Json, UTF-8, camelCase, кириллица без экранирования (NFR).
/// </summary>
public static class CleanPlanRunJson
{
    public static string Serialize(CleanPlanRunReport report) =>
        JsonSerializer.Serialize(BuildDocument(report), Options);

    public static CleanPlanRunDocument BuildDocument(CleanPlanRunReport report)
    {
        var removed = report.Entries.Where(e => IsRemoved(e.Outcome)).ToList();
        var deferred = report.Entries.Where(e => IsDeferred(e.Outcome)).ToList();

        return new CleanPlanRunDocument
        {
            DryRun = report.DryRun,
            Canceled = report.Canceled,
            ElapsedSeconds = report.Elapsed.TotalSeconds,
            PlanVsFact = new PlanVsFactJson
            {
                PlannedBytes = report.PlannedBytes,
                PlannedText = CleanReportFormatter.FormatBytes(report.PlannedBytes),
                FreedBytes = report.FreedBytes,
                FreedText = CleanReportFormatter.FormatBytes(report.FreedBytes),
                DiskFreedBytes = report.DiskFreedBytes,
                DiskFreedText = CleanReportFormatter.FormatBytes(report.DiskFreedBytes),
                GapBytes = report.GapBytes,
                GapText = CleanReportFormatter.FormatBytes(report.GapBytes),
                WithinPlanTolerance = report.WithinPlanTolerance
            },
            Summary = new CleanPlanRunSummaryJson
            {
                TotalItems = report.Entries.Count,
                DeletedItems = removed.Count,
                BlockedItems = report.SkippedOrBlocked.Count,
                DeferredItems = deferred.Count
            },
            Phases = report.Phases.Select(phase => new CleanPlanRunPhaseJson
            {
                Phase = phase.Phase.ToString(),
                PhaseText = phase.PhaseText,
                Items = phase.Items,
                PlannedBytes = phase.PlannedBytes,
                FreedBytes = phase.FreedBytes,
                Completed = phase.Completed,
                Skipped = phase.Skipped,
                Failed = phase.Failed
            }).ToList(),
            Items = report.Entries.Select(ToItemJson).ToList(),
            SkippedOrBlocked = report.SkippedOrBlocked.Select(ToItemJson).ToList(),
            DriveSnapshots = report.DriveSnapshots.Select(s => new DriveSnapshotJson
            {
                RootPath = s.RootPath,
                FreeBeforeBytes = s.FreeBeforeBytes,
                FreeAfterBytes = s.FreeAfterBytes,
                DeltaBytes = s.DeltaBytes
            }).ToList(),
            SecondEchelonCandidates = report.SecondEchelonCandidates.Select(c => new SecondEchelonJson
            {
                Name = c.DisplayName,
                SizeBytes = c.EffectiveSizeBytes,
                SizeText = CleanReportFormatter.FormatBytes(c.EffectiveSizeBytes)
            }).ToList()
        };
    }

    private static CleanPlanRunItemJson ToItemJson(CleanEntry entry) => new()
    {
        Key = entry.Item.Key,
        Name = entry.Item.DisplayName,
        Group = entry.Item.GroupName,
        Category = entry.Item.Category.ToString(),
        CategoryText = LocalizedNames.Category(entry.Item.Category),
        Path = entry.Item.Path,
        Outcome = entry.Outcome.ToString(),
        OutcomeText = CleanReportFormatter.OutcomeText(entry.Outcome),
        FreedBytes = entry.FreedBytes,
        Note = entry.Note
    };

    private static bool IsRemoved(CleanOutcome outcome) => outcome switch
    {
        CleanOutcome.NativeCleaned or
        CleanOutcome.DirectDeleted or
        CleanOutcome.CommandOnlyCleaned or
        CleanOutcome.MovedToRecycleBin or
        CleanOutcome.Uninstalled or
        CleanOutcome.RegistryEntryDeleted or
        CleanOutcome.AlreadyUninstalled => true,
        _ => false
    };

    private static bool IsDeferred(CleanOutcome outcome) => outcome == CleanOutcome.InUseSkipped;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

public sealed class CleanPlanRunDocument
{
    public string Schema { get; init; } = "diskcleaner.plan-run";

    public int SchemaVersion { get; init; } = 1;

    public bool DryRun { get; init; }

    public bool Canceled { get; init; }

    public double ElapsedSeconds { get; init; }

    public PlanVsFactJson PlanVsFact { get; init; } = new();

    public CleanPlanRunSummaryJson Summary { get; init; } = new();

    public IReadOnlyList<CleanPlanRunPhaseJson> Phases { get; init; } = Array.Empty<CleanPlanRunPhaseJson>();

    public IReadOnlyList<CleanPlanRunItemJson> Items { get; init; } = Array.Empty<CleanPlanRunItemJson>();

    public IReadOnlyList<CleanPlanRunItemJson> SkippedOrBlocked { get; init; } = Array.Empty<CleanPlanRunItemJson>();

    public IReadOnlyList<DriveSnapshotJson> DriveSnapshots { get; init; } = Array.Empty<DriveSnapshotJson>();

    public IReadOnlyList<SecondEchelonJson> SecondEchelonCandidates { get; init; } = Array.Empty<SecondEchelonJson>();
}

public sealed class PlanVsFactJson
{
    public long PlannedBytes { get; init; }

    public string PlannedText { get; init; } = string.Empty;

    public long FreedBytes { get; init; }

    public string FreedText { get; init; } = string.Empty;

    public long DiskFreedBytes { get; init; }

    public string DiskFreedText { get; init; } = string.Empty;

    public long GapBytes { get; init; }

    public string GapText { get; init; } = string.Empty;

    public bool WithinPlanTolerance { get; init; }
}

public sealed class CleanPlanRunSummaryJson
{
    public int TotalItems { get; init; }

    public int DeletedItems { get; init; }

    public int BlockedItems { get; init; }

    public int DeferredItems { get; init; }
}

public sealed class CleanPlanRunPhaseJson
{
    public string Phase { get; init; } = string.Empty;

    public string PhaseText { get; init; } = string.Empty;

    public int Items { get; init; }

    public long PlannedBytes { get; init; }

    public long FreedBytes { get; init; }

    public int Completed { get; init; }

    public int Skipped { get; init; }

    public int Failed { get; init; }
}

public sealed class CleanPlanRunItemJson
{
    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Group { get; init; }

    public string Category { get; init; } = string.Empty;

    public string CategoryText { get; init; } = string.Empty;

    public string? Path { get; init; }

    public string Outcome { get; init; } = string.Empty;

    public string OutcomeText { get; init; } = string.Empty;

    public long FreedBytes { get; init; }

    public string? Note { get; init; }
}

public sealed class DriveSnapshotJson
{
    public string RootPath { get; init; } = string.Empty;

    public long FreeBeforeBytes { get; init; }

    public long FreeAfterBytes { get; init; }

    public long DeltaBytes { get; init; }
}

public sealed class SecondEchelonJson
{
    public string Name { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public string SizeText { get; init; } = string.Empty;
}
