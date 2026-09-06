namespace DiskCleaner.Core.Reports;

/// <summary>Состояние объекта при сравнении двух сканов «было/стало» (FR-1.13).</summary>
public enum PlanObjectChangeKind
{
    Unchanged,
    Added,
    Removed,
    Changed
}

/// <summary>
/// Сравнение двух сканов «было/стало» (FR-1.13): дельта суммарных размеров, по категориям
/// и по объектам. Чистое вычисление без IO — вход и выход (снапшот) используют единую схему
/// <see cref="PlanDocument"/>, поэтому сравнение строится после повторного скана.
/// </summary>
public sealed class PlanComparison
{
    public DateTime PreviousGeneratedAtUtc { get; init; }

    public DateTime CurrentGeneratedAtUtc { get; init; }

    public long PreviousTotalBytes { get; init; }

    public long CurrentTotalBytes { get; init; }

    /// <summary>Было → стало: отрицательное значение означает, что место освободилось.</summary>
    public long DeltaBytes => CurrentTotalBytes - PreviousTotalBytes;

    public int PreviousTotalItems { get; init; }

    public int CurrentTotalItems { get; init; }

    public int DeltaItems => CurrentTotalItems - PreviousTotalItems;

    public IReadOnlyList<PlanCategoryComparison> Categories { get; init; } = Array.Empty<PlanCategoryComparison>();

    public IReadOnlyList<PlanObjectComparison> Objects { get; init; } = Array.Empty<PlanObjectComparison>();
}

public sealed class PlanCategoryComparison
{
    public string Category { get; init; } = string.Empty;

    public string CategoryText { get; init; } = string.Empty;

    public long PreviousBytes { get; init; }

    public long CurrentBytes { get; init; }

    public long DeltaBytes => CurrentBytes - PreviousBytes;

    public int PreviousItems { get; init; }

    public int CurrentItems { get; init; }

    public int DeltaItems => CurrentItems - PreviousItems;
}

public sealed class PlanObjectComparison
{
    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Group { get; init; }

    public string CategoryText { get; init; } = string.Empty;

    public long PreviousBytes { get; init; }

    public long CurrentBytes { get; init; }

    public long DeltaBytes => CurrentBytes - PreviousBytes;

    public PlanObjectChangeKind ChangeKind { get; init; }
}

/// <summary>Чистый расчёт дельты «было/стало» между двумя снапшотами скана (FR-1.13).</summary>
public static class PlanComparer
{
    public static PlanComparison Compare(PlanDocument previous, PlanDocument current)
    {
        var previousByKey = previous.Items.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        var currentByKey = current.Items.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);

        var objects = new List<PlanObjectComparison>(current.Items.Count + previous.Items.Count);
        foreach (var pair in currentByKey)
        {
            var previousItem = previousByKey.TryGetValue(pair.Key, out var prev) ? prev : null;
            objects.Add(BuildObjectComparison(previousItem, pair.Value));
        }

        foreach (var pair in previousByKey)
        {
            if (!currentByKey.ContainsKey(pair.Key))
            {
                objects.Add(BuildObjectComparison(pair.Value, null));
            }
        }

        var categories = BuildCategories(previous, current);

        return new PlanComparison
        {
            PreviousGeneratedAtUtc = previous.GeneratedAtUtc,
            CurrentGeneratedAtUtc = current.GeneratedAtUtc,
            PreviousTotalBytes = previous.Summary.TotalBytes,
            CurrentTotalBytes = current.Summary.TotalBytes,
            PreviousTotalItems = previous.Summary.TotalItems,
            CurrentTotalItems = current.Summary.TotalItems,
            Categories = categories,
            Objects = objects
                .OrderByDescending(o => Math.Abs(o.DeltaBytes))
                .ThenBy(o => o.Name, StringComparer.CurrentCulture)
                .ToList()
        };
    }

    private static PlanObjectComparison BuildObjectComparison(PlanItemJson? previous, PlanItemJson? current)
    {
        var baseItem = current ?? previous!;
        var previousBytes = previous?.SizeBytes ?? 0;
        var currentBytes = current?.SizeBytes ?? 0;
        var changeKind = previous is null
            ? PlanObjectChangeKind.Added
            : current is null
                ? PlanObjectChangeKind.Removed
                : previousBytes == currentBytes
                    ? PlanObjectChangeKind.Unchanged
                    : PlanObjectChangeKind.Changed;

        return new PlanObjectComparison
        {
            Key = baseItem.Key,
            Name = baseItem.Name,
            Group = baseItem.Group,
            CategoryText = baseItem.CategoryText,
            PreviousBytes = previousBytes,
            CurrentBytes = currentBytes,
            ChangeKind = changeKind
        };
    }

    private static IReadOnlyList<PlanCategoryComparison> BuildCategories(
        PlanDocument previous,
        PlanDocument current)
    {
        var categoryNames = previous.Categories
            .Select(c => c.Category)
            .Concat(current.Categories.Select(c => c.Category))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return categoryNames.Select(category =>
        {
            var prev = previous.Categories.FirstOrDefault(c => c.Category == category);
            var curr = current.Categories.FirstOrDefault(c => c.Category == category);
            return new PlanCategoryComparison
            {
                Category = category,
                CategoryText = curr?.CategoryText ?? prev?.CategoryText ?? category,
                PreviousBytes = prev?.Bytes ?? 0,
                CurrentBytes = curr?.Bytes ?? 0,
                PreviousItems = prev?.Items ?? 0,
                CurrentItems = curr?.Items ?? 0
            };
        }).OrderByDescending(c => Math.Abs(c.DeltaBytes)).ToList();
    }
}
