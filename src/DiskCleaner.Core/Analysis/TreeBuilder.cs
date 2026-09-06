using DiskCleaner.Core.Localization;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Analysis;

public sealed class TreeBuilder
{
    public IReadOnlyList<CleanupItem> Build(IEnumerable<CleanupItem> items)
    {
        var itemList = items.ToList();
        var orderedCategories = itemList
            .GroupBy(i => i.Category)
            .OrderByDescending(g => g.Sum(i => i.EffectiveSizeBytes))
            .ToList();

        var roots = new List<CleanupItem>();
        foreach (var categoryGroup in orderedCategories)
        {
            var category = categoryGroup.Key;
            var children = BuildCategoryChildren(categoryGroup);
            var root = new CleanupItem
            {
                Key = "category:" + category,
                DisplayName = LocalizedNames.Category(category),
                Category = category,
                Risk = CleanupRisk.Low,
                Children = children
            };
            roots.Add(root);
        }

        return roots;
    }

    private static IReadOnlyList<CleanupItem> BuildCategoryChildren(IEnumerable<CleanupItem> items)
    {
        var result = new List<CleanupItem>();
        var grouped = items
            .GroupBy(i => string.IsNullOrEmpty(i.GroupName) ? string.Empty : i.GroupName)
            .OrderByDescending(g => g.Sum(i => i.EffectiveSizeBytes));

        foreach (var group in grouped)
        {
            var orderedItems = group
                .OrderByDescending(i => i.EffectiveSizeBytes)
                .ToList();

            if (group.Key.Length == 0)
            {
                result.AddRange(orderedItems);
                continue;
            }

            var first = orderedItems[0];
            var groupNode = new CleanupItem
            {
                Key = "group:" + first.Key,
                DisplayName = group.Key,
                Category = first.Category,
                Risk = first.Risk,
                GroupName = group.Key,
                Children = orderedItems
            };
            result.Add(groupNode);
        }

        return result;
    }
}
