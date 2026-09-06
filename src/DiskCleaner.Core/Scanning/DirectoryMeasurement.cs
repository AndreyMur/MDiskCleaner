namespace DiskCleaner.Core.Scanning;

public sealed record DirectoryMeasurement(string Path, long SizeBytes, long FileCount, bool Exists);
