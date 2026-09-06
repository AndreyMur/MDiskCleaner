namespace DiskCleaner.Core.Commanding;

public sealed class CommandDefinition
{
    public string FileName { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public int TimeoutSec { get; set; } = 120;
}
