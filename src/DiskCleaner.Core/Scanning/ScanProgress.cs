namespace DiskCleaner.Core.Scanning;

public sealed record ScanProgress(
    string CurrentPath,
    long BytesMeasured,
    long DirectoriesCompleted,
    int RootsCompleted,
    int RootsTotal);
