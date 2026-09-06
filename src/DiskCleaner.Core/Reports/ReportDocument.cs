using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Localization;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Reports;

/// <summary>
/// Единая machine-readable схема результата (FR-0.4, FR-0.5): используется одинаково
/// для отчёта GUI (экспорт JSON) и CLI (--scan / --clean). Ключевые поля:
/// summary (освобождено), items (список объектов/удалённого) и blocked (ошибки/заблокированные файлы).
/// </summary>
public sealed class ReportDocument
{
    public string Schema { get; init; } = "diskcleaner.report";

    public int SchemaVersion { get; init; } = 1;

    /// <summary>scan | dry-run | clean</summary>
    public string Mode { get; init; } = "clean";

    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;

    public double ElapsedSeconds { get; init; }

    public ReportSummaryJson Summary { get; init; } = new();

    public IReadOnlyList<ReportCategoryJson> Categories { get; init; } = Array.Empty<ReportCategoryJson>();

    public IReadOnlyList<ReportItemJson> Items { get; init; } = Array.Empty<ReportItemJson>();

    /// <summary>Список удалённого/очищенного (подмножество Items с успешным исходом).</summary>
    public IReadOnlyList<ReportItemJson> Removed { get; init; } = Array.Empty<ReportItemJson>();

    public IReadOnlyList<ReportBlockedJson> Blocked { get; init; } = Array.Empty<ReportBlockedJson>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public sealed class ReportSummaryJson
{
    public long FreedBytes { get; init; }

    public string FreedText { get; init; } = string.Empty;

    /// <summary>Суммарный размер всех объектов отчёта (для scan — объём кандидатов).</summary>
    public long TotalBytes { get; init; }

    public string TotalText { get; init; } = string.Empty;

    public int TotalItems { get; init; }

    public int DeletedItems { get; init; }

    public int FailedItems { get; init; }

    public int BlockedItems { get; init; }

    public int DeferredItems { get; init; }

    public int InUseItems { get; init; }

    public int SkippedNonexistent { get; init; }
}

public sealed class ReportCategoryJson
{
    public string Category { get; init; } = string.Empty;

    public string CategoryText { get; init; } = string.Empty;

    public long Bytes { get; init; }

    public int Items { get; init; }
}

public sealed class ReportItemJson
{
    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Group { get; init; }

    public string Category { get; init; } = string.Empty;

    public string CategoryText { get; init; } = string.Empty;

    public string Risk { get; init; } = string.Empty;

    public string RiskText { get; init; } = string.Empty;

    public string Target { get; init; } = string.Empty;

    public string? Path { get; init; }

    public string? Command { get; init; }

    public bool RequiresAdmin { get; init; }

    public bool InUse { get; init; }

    /// <summary>Причина ручной проверки (FR-1.10), когда запись помечена как «Review manually».</summary>
    public string? ReviewReason { get; init; }

    public long? SizeBytes { get; init; }

    public long? FileCount { get; init; }

    public string? Outcome { get; init; }

    public string? OutcomeText { get; init; }

    public long FreedBytes { get; init; }

    public string? Note { get; init; }
}

public sealed class ReportBlockedJson
{
    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Path { get; init; }

    public string Reason { get; init; } = string.Empty;

    public string? Note { get; init; }
}

/// <summary>Строит <see cref="ReportDocument"/> из результатов анализа и очистки ядра.</summary>
public static class ReportDocumentBuilder
{
    public static ReportDocument FromScan(AnalysisResult result)
    {
        var leaves = result.Items
            .Where(i => !i.IsGroup)
            .OrderByDescending(i => i.EffectiveSizeBytes)
            .ToList();

        return new ReportDocument
        {
            Mode = "scan",
            ElapsedSeconds = result.Elapsed.TotalSeconds,
            Summary = new ReportSummaryJson
            {
                TotalItems = leaves.Count,
                TotalBytes = leaves.Sum(i => i.EffectiveSizeBytes),
                TotalText = CleanReportFormatter.FormatBytes(leaves.Sum(i => i.EffectiveSizeBytes)),
                FreedBytes = 0,
                FreedText = "0 Б",
                InUseItems = result.InUseItems,
                SkippedNonexistent = result.SkippedNonexistent
            },
            Categories = BuildCategories(leaves),
            Items = leaves.Select(ToItemJson).ToList(),
            Removed = Array.Empty<ReportItemJson>(),
            Blocked = Array.Empty<ReportBlockedJson>(),
            Errors = result.Errors.ToList()
        };
    }

    public static ReportDocument FromCleanReport(CleanReport report)
    {
        var items = report.Entries
            .OrderByDescending(e => e.FreedBytes)
            .ThenBy(e => e.Item.DisplayName, StringComparer.CurrentCulture)
            .ToList();

        var deleted = items.Where(e => IsRemoved(e.Outcome)).ToList();
        var blocked = items.Where(e => IsBlocked(e.Outcome)).ToList();
        var deferred = items.Where(e => e.Outcome == CleanOutcome.InUseSkipped).ToList();
        var failed = items.Where(e => e.Outcome is CleanOutcome.Error or CleanOutcome.Partial).ToList();

        var leaves = items.Select(e => e.Item).ToList();

        return new ReportDocument
        {
            Mode = report.DryRun ? "dry-run" : "clean",
            ElapsedSeconds = report.Elapsed.TotalSeconds,
            Summary = new ReportSummaryJson
            {
                FreedBytes = report.TotalFreedBytes,
                FreedText = CleanReportFormatter.FormatBytes(report.TotalFreedBytes),
                TotalItems = items.Count,
                DeletedItems = deleted.Count,
                FailedItems = failed.Count,
                BlockedItems = blocked.Count,
                DeferredItems = deferred.Count
            },            Categories = BuildCategories(leaves, items),
            Items = items.Select(e => ToItemJson(e)).ToList(),
            Removed = deleted.Select(ToItemJson).ToList(),
            Blocked = blocked.Select(ToBlockedJson).ToList(),
            Errors = Array.Empty<string>()
        };
    }

    private static IReadOnlyList<ReportCategoryJson> BuildCategories(
        IReadOnlyList<CleanupItem> items,
        IReadOnlyList<CleanEntry>? entries = null)
    {
        var byCategory = items
            .GroupBy(i => i.Category)
            .OrderByDescending(g => g.Sum(i => i.EffectiveSizeBytes));

        return byCategory.Select(g =>
        {
            long freed = 0;
            if (entries is not null)
            {
                freed = entries
                    .Where(e => e.Item.Category == g.Key)
                    .Sum(e => Math.Max(0, e.FreedBytes));
            }

            return new ReportCategoryJson
            {
                Category = g.Key.ToString(),
                CategoryText = LocalizedNames.Category(g.Key),
                Bytes = entries is null ? g.Sum(i => i.EffectiveSizeBytes) : freed,
                Items = g.Count()
            };
        }).ToList();
    }

    private static ReportItemJson ToItemJson(CleanEntry entry) => new()
    {
        Key = entry.Item.Key,
        Name = entry.Item.DisplayName,
        Group = entry.Item.GroupName,
        Category = entry.Item.Category.ToString(),
        CategoryText = LocalizedNames.Category(entry.Item.Category),
        Risk = entry.Item.Risk.ToString(),
        RiskText = LocalizedNames.Risk(entry.Item.Risk),
        Target = entry.Item.Target.ToString(),
        Path = entry.Item.Path,
        Command = entry.Item.CleanCommand,
        RequiresAdmin = entry.Item.RequiresAdmin,
        InUse = entry.Item.InUse,
        ReviewReason = entry.Item.ReviewReason,
        SizeBytes = entry.Item.EffectiveSizeBytes,
        FileCount = entry.Item.EffectiveFileCount,
        Outcome = entry.Outcome.ToString(),
        OutcomeText = CleanReportFormatter.OutcomeText(entry.Outcome),
        FreedBytes = entry.FreedBytes,
        Note = entry.Note
    };

    private static ReportItemJson ToItemJson(CleanupItem item) => new()
    {
        Key = item.Key,
        Name = item.DisplayName,
        Group = item.GroupName,
        Category = item.Category.ToString(),
        CategoryText = LocalizedNames.Category(item.Category),
        Risk = item.Risk.ToString(),
        RiskText = LocalizedNames.Risk(item.Risk),
        Target = item.Target.ToString(),
        Path = item.Path,
        Command = item.CleanCommand,
        RequiresAdmin = item.RequiresAdmin,
        InUse = item.InUse,
        ReviewReason = item.ReviewReason,
        SizeBytes = item.EffectiveSizeBytes,
        FileCount = item.EffectiveFileCount,
        Outcome = null,
        OutcomeText = null,
        FreedBytes = 0,
        Note = item.Warning
    };

    private static ReportBlockedJson ToBlockedJson(CleanEntry entry) => new()
    {
        Key = entry.Item.Key,
        Name = entry.Item.DisplayName,
        Path = entry.Item.Path,
        Reason = CleanReportFormatter.OutcomeText(entry.Outcome),
        Note = entry.Note
    };

    private static bool IsRemoved(CleanOutcome outcome) => outcome switch
    {
        CleanOutcome.NativeCleaned or
        CleanOutcome.DirectDeleted or
        CleanOutcome.CommandOnlyCleaned or
        CleanOutcome.MovedToRecycleBin or
        CleanOutcome.Uninstalled or
        CleanOutcome.RegistryEntryDeleted => true,
        _ => false
    };

    private static bool IsBlocked(CleanOutcome outcome) => outcome switch
    {
        CleanOutcome.Error or
        CleanOutcome.Partial or
        CleanOutcome.Denied or
        CleanOutcome.InUseSkipped or
        CleanOutcome.ElevationDeclined or
        CleanOutcome.RebootRequired or
        CleanOutcome.AlreadyUninstalled => true,
        _ => false
    };
}
