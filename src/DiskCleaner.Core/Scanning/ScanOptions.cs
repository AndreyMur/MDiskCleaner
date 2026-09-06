namespace DiskCleaner.Core.Scanning;

public sealed class ScanOptions
{
    public int MaxAsyncDepth { get; set; } = 4;

    public int MaxRecordedErrors { get; set; } = 100;

    public int ProgressDirectoryStep { get; set; } = 128;

    public long ProgressBytesStep { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Таймаут измерения одной независимой ветки (корня). По истечении ветка
    /// помечается как неполная (TimedOut) с записью в журнал, а сканирование
    /// остальных веток продолжается (FR-1.3). Значение <c>null</c> или ≤ 0 — таймаут выключен.
    /// </summary>
    public TimeSpan? BranchTimeout { get; set; }
}
