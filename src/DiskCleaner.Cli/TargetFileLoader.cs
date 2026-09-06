using System.Text.Json;
using System.Text.Json.Serialization;
using DiskCleaner.Core.Localization;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Cli;

public static class TargetFileLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static IReadOnlyList<CleanupItem> Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Файл объектов не найден: {fullPath}", fullPath);
        }

        var items = JsonSerializer.Deserialize<List<TargetEntry>>(File.ReadAllText(fullPath, System.Text.Encoding.UTF8), JsonOptions)
            ?? new List<TargetEntry>();

        var seeds = new List<CleanupItem>(items.Count);
        foreach (var entry in items)
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
            {
                continue;
            }

            var resolved = Path.GetFullPath(entry.Path);
            var name = string.IsNullOrWhiteSpace(entry.Name)
                ? Path.GetFileName(resolved.TrimEnd('\\', '/'))
                : entry.Name;

            seeds.Add(new CleanupItem
            {
                Key = entry.Key ?? "target:" + resolved,
                Path = resolved,
                DisplayName = name,
                GroupName = entry.Group,
                Category = entry.Category,
                Risk = entry.Risk,
                Target = entry.Target,
                Description = entry.Description
            });
        }

        return seeds;
    }

    private sealed class TargetEntry
    {
        public string? Key { get; init; }

        public string? Path { get; init; }

        public string? Name { get; init; }

        public string? Group { get; init; }

        public CleanupCategory Category { get; init; } = CleanupCategory.Cache;

        public CleanupRisk Risk { get; init; } = CleanupRisk.Low;

        public CleanupTarget Target { get; init; } = CleanupTarget.Directory;

        public string? Description { get; init; }
    }
}
