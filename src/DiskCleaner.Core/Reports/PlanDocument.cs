using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Localization;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Reports;

/// <summary>
/// Единая machine-readable схема плана очистки (FR-1.12): категории и объекты с размерами
/// и рисками. Используется одинаково для JSON-экспорта плана (GUI/файл) и для снапшота
/// предыдущего скана в scancache (FR-1.13) — файл снапшота читается тем же десериализатором.
/// </summary>
public sealed class PlanDocument
{
    public string Schema { get; init; } = "diskcleaner.plan";

    public int SchemaVersion { get; init; } = 1;

    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;

    public double ElapsedSeconds { get; init; }

    public PlanSummaryJson Summary { get; init; } = new();

    public IReadOnlyList<PlanCategoryJson> Categories { get; init; } = Array.Empty<PlanCategoryJson>();

    public IReadOnlyList<PlanItemJson> Items { get; init; } = Array.Empty<PlanItemJson>();
}

public sealed class PlanSummaryJson
{
    public long TotalBytes { get; init; }

    public string TotalText { get; init; } = string.Empty;

    public int TotalItems { get; init; }

    public int InUseItems { get; init; }

    public int RequiresAdminItems { get; init; }

    public int ReviewItems { get; init; }

    public int SkippedNonexistent { get; init; }
}

public sealed class PlanCategoryJson
{
    public string Category { get; init; } = string.Empty;

    public string CategoryText { get; init; } = string.Empty;

    public long Bytes { get; init; }

    public int Items { get; init; }
}

public sealed class PlanItemJson
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

    public bool RequiresAdmin { get; init; }

    public bool InUse { get; init; }

    /// <summary>Причина ручной проверки (FR-1.10), когда запись помечена как «Review manually».</summary>
    public string? ReviewReason { get; init; }

    public string DefaultAction { get; init; } = string.Empty;

    public string DefaultActionText { get; init; } = string.Empty;

    public long? SizeBytes { get; init; }

    public long? FileCount { get; init; }

    public string? Warning { get; init; }
}

/// <summary>Строит <see cref="PlanDocument"/> из результата анализа (FR-1.12).</summary>
public static class PlanDocumentBuilder
{
    public static PlanDocument FromScan(AnalysisResult result)
    {
        var leaves = result.Items
            .Where(i => !i.IsGroup)
            .OrderByDescending(i => i.EffectiveSizeBytes)
            .ThenBy(i => i.DisplayName, StringComparer.CurrentCulture)
            .ToList();

        var categorizer = new CategorizationService();

        return new PlanDocument
        {
            GeneratedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = result.Elapsed.TotalSeconds,
            Summary = new PlanSummaryJson
            {
                TotalItems = leaves.Count,
                TotalBytes = leaves.Sum(i => i.EffectiveSizeBytes),
                TotalText = CleanReportFormatter.FormatBytes(leaves.Sum(i => i.EffectiveSizeBytes)),
                InUseItems = leaves.Count(i => i.InUse),
                RequiresAdminItems = leaves.Count(i => i.RequiresAdmin),
                ReviewItems = leaves.Count(i => i.ReviewManually),
                SkippedNonexistent = result.SkippedNonexistent
            },
            Categories = BuildCategories(leaves),
            Items = leaves.Select(i => ToItemJson(i, categorizer)).ToList()
        };
    }

    private static IReadOnlyList<PlanCategoryJson> BuildCategories(IReadOnlyList<CleanupItem> items)
    {
        var byCategory = items
            .GroupBy(i => i.Category)
            .OrderByDescending(g => g.Sum(i => i.EffectiveSizeBytes))
            .ThenBy(g => g.Key.ToString(), StringComparer.Ordinal);

        return byCategory.Select(g => new PlanCategoryJson
        {
            Category = g.Key.ToString(),
            CategoryText = LocalizedNames.Category(g.Key),
            Bytes = g.Sum(i => i.EffectiveSizeBytes),
            Items = g.Count()
        }).ToList();
    }

    private static PlanItemJson ToItemJson(CleanupItem item, CategorizationService categorizer) => new()
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
        RequiresAdmin = item.RequiresAdmin,
        InUse = item.InUse,
        ReviewReason = item.ReviewReason,
        DefaultAction = categorizer.DefaultActionFor(item).ToString(),
        DefaultActionText = LocalizedNames.DefaultAction(categorizer.DefaultActionFor(item)),
        SizeBytes = item.EffectiveSizeBytes,
        FileCount = item.EffectiveFileCount,
        Warning = item.Warning
    };
}
