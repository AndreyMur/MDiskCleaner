namespace DiskCleaner.Core.Scanning;

public sealed class ScanOutcome
{
    public IReadOnlyDictionary<string, DirectoryMeasurement> Results { get; init; } =
        new Dictionary<string, DirectoryMeasurement>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public TimeSpan Elapsed { get; init; }
}
