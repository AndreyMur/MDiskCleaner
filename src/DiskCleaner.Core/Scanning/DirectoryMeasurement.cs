namespace DiskCleaner.Core.Scanning;

/// <param name="TimedOut">Ветка не успела измериться за отведённый BranchTimeout
/// (результат неполный, запись об этом добавлена в журнал).</param>
public sealed record DirectoryMeasurement(
    string Path,
    long SizeBytes,
    long FileCount,
    bool Exists,
    bool TimedOut = false);
