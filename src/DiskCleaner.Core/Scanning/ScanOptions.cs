namespace DiskCleaner.Core.Scanning;

public sealed class ScanOptions
{
    public int MaxAsyncDepth { get; set; } = 4;

    public int MaxRecordedErrors { get; set; } = 100;

    public int ProgressDirectoryStep { get; set; } = 128;

    public long ProgressBytesStep { get; set; } = 64L * 1024 * 1024;
}
