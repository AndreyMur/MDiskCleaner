using DiskCleaner.Core.Commanding;

namespace DiskCleaner.Core.Caches;

public sealed class CacheCatalogDocument
{
    public List<CacheTargetDefinition> Targets { get; set; } = new();
}

public sealed class CacheTargetDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string? Group { get; set; }

    public string Category { get; set; } = "Cache";

    public string Risk { get; set; } = "Low";

    public List<string> EnvPaths { get; set; } = new();

    public bool CommandOnly { get; set; }

    public bool AllowDirectDelete { get; set; } = true;

    public List<string> OwnerProcessNames { get; set; } = new();

    public CommandDefinition? PathQuery { get; set; }

    public CommandDefinition? CleanCommand { get; set; }

    public string? Description { get; set; }

    public string? Warning { get; set; }
}
