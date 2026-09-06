namespace DiskCleaner.Cli;

public sealed class CliOptions
{
    public IReadOnlyList<string> OriginalArgs { get; init; } = Array.Empty<string>();

    public CliMode? Mode { get; set; }

    public List<string> Categories { get; } = new();

    public List<string> Keys { get; } = new();

    public List<string> Names { get; } = new();

    public bool Yes { get; set; }

    public bool DryRun { get; set; }

    public string? JsonPath { get; set; }

    public string? TargetsPath { get; set; }

    public bool IncludeAllApps { get; set; } = true;

    public bool ShowHelp { get; set; }

    public bool ShowVersion { get; set; }
}
