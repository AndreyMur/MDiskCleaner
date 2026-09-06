namespace DiskCleaner.Core.Scanning;

/// <summary>
/// Разделяемый контекст скана: ошибки, счётчики работы измерителя и прогресс.
/// Инкрементально: <see cref="DirectoryReused"/> — ветка взята из кэша без пересчёта,
/// <see cref="DirectoryEnumerated"/> — каталог реально перечислен (пересчитан).
/// </summary>
internal sealed class DiskScanContext
{
    private readonly object _sync = new();
    private readonly List<string> _errors = new();
    private readonly IProgress<ScanProgress>? _progress;

    private long _directoriesChecked;
    private long _directoriesEnumerated;
    private long _directoriesReused;
    private long _bytesAdded;
    private long _directoriesCompleted;
    private long _lastReportedDirectories;
    private long _lastReportedBytes;
    private string _currentPath = string.Empty;
    private int _rootsCompleted;

    public DiskScanContext(DiskScanRequest request, IProgress<ScanProgress>? progress)
    {
        Request = request;
        _progress = progress;
    }

    public DiskScanRequest Request { get; }

    public int RootsTotal { get; private set; }

    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_sync)
            {
                return _errors.ToArray();
            }
        }
    }

    public DiskScanStatistics Statistics =>
        new(
            (int)Interlocked.Read(ref _directoriesEnumerated),
            (int)Interlocked.Read(ref _directoriesReused),
            (int)Interlocked.Read(ref _directoriesChecked));

    public void BeginTopLevel(int rootsTotal)
    {
        RootsTotal = rootsTotal;
        ReportProgress();
    }

    public void DirectoryChecked() => Interlocked.Increment(ref _directoriesChecked);

    public void DirectoryEnumerated() => Interlocked.Increment(ref _directoriesEnumerated);

    public void DirectoryReused() => Interlocked.Increment(ref _directoriesReused);

    public void AddBytes(long bytes) => Interlocked.Add(ref _bytesAdded, bytes);

    public void DirectoryCompleted(string directoryPath)
    {
        Interlocked.Increment(ref _directoriesCompleted);
        lock (_sync)
        {
            _currentPath = directoryPath;
        }

        ReportProgress();
    }

    public void RootCompleted()
    {
        Interlocked.Increment(ref _rootsCompleted);
        ReportProgress();
    }

    public void RecordError(string path, string message)
    {
        lock (_sync)
        {
            if (_errors.Count >= Request.MaxRecordedErrors)
            {
                return;
            }

            _errors.Add($"{path}: {message}");
        }

        Serilog.Log.Warning("Disk scan error: {Path}: {Message}", path, message);
    }

    public void RecordBranchTimeout(string path, TimeSpan timeout)
    {
        lock (_sync)
        {
            if (_errors.Count >= Request.MaxRecordedErrors)
            {
                return;
            }

            _errors.Add($"Превышен таймаут измерения ветки ({timeout}): {path}");
        }

        Serilog.Log.Warning("Disk scan branch timed out after {Timeout}: {Path}", timeout, path);
    }

    private void ReportProgress()
    {
        var directories = Interlocked.Read(ref _directoriesCompleted);
        var bytes = Interlocked.Read(ref _bytesAdded);

        bool shouldReport;
        lock (_sync)
        {
            shouldReport =
                directories - _lastReportedDirectories >= 128 ||
                bytes - _lastReportedBytes >= 64L * 1024 * 1024;

            if (!shouldReport)
            {
                return;
            }

            _lastReportedDirectories = directories;
            _lastReportedBytes = bytes;
        }

        string currentPath;
        lock (_sync)
        {
            currentPath = _currentPath;
        }

        _progress?.Report(new ScanProgress(
            currentPath,
            bytes,
            directories,
            Math.Min(_rootsCompleted, RootsTotal),
            RootsTotal));
    }
}
