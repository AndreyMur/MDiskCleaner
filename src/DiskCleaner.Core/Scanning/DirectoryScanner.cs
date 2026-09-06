using System.IO;
using Serilog;

namespace DiskCleaner.Core.Scanning;

public sealed class DirectoryScanner
{
    private readonly ScanOptions _options;

    public DirectoryScanner(ScanOptions? options = null)
    {
        _options = options ?? new ScanOptions();
    }

    public async Task<ScanOutcome> MeasureAsync(
        IReadOnlyCollection<string> roots,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        var startedAt = DateTime.UtcNow;
        var rootsList = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var context = new ScanContext(_options, cancellationToken, progress, rootsList.Count);

        context.ReportProgress();

        await Parallel.ForEachAsync(
            rootsList,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(8, Math.Max(2, System.Environment.ProcessorCount))
            },
            async (root, token) =>
            {
                var measurement = await MeasureRootAsync(root, context);
                context.AddRootResult(root, measurement);
            });

        return new ScanOutcome
        {
            Results = context.RootResults,
            Errors = context.Errors,
            Elapsed = DateTime.UtcNow - startedAt
        };
    }

    private async Task<DirectoryMeasurement> MeasureRootAsync(string root, ScanContext context)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var (exists, isDirectory) = NativeDirectory.Probe(fullRoot);
            if (!exists)
            {
                return new DirectoryMeasurement(fullRoot, 0, 0, false);
            }

            if (!isDirectory)
            {
                var size = NativeDirectory.GetFileSize(fullRoot);
                context.AddRootBytes(size);
                return new DirectoryMeasurement(fullRoot, size, 1, true);
            }

            var (bytes, files) = await MeasureDirectoryAsync(fullRoot, 0, context);
            return new DirectoryMeasurement(fullRoot, bytes, files, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            context.RecordError(root, ex.Message);
            return new DirectoryMeasurement(root, 0, 0, Directory.Exists(root));
        }
    }

    private async Task<(long Bytes, long Files)> MeasureDirectoryAsync(
        string directoryPath,
        int depth,
        ScanContext context)
    {
        List<NativeDirectory.Entry> entries;
        try
        {
            entries = NativeDirectory.Enumerate(directoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.RecordError(directoryPath, ex.Message);
            return (0, 0);
        }

        long levelBytes = 0;
        long levelFiles = 0;
        var subDirectories = new List<string>();

        foreach (var entry in entries)
        {
            context.ThrowIfCancelled();

            if (entry.IsDirectory)
            {
                if (!entry.IsReparsePoint)
                {
                    subDirectories.Add(Path.Combine(directoryPath, entry.Name));
                }
            }
            else
            {
                levelBytes += entry.Size;
                levelFiles++;
            }
        }

        long childrenBytes = 0;
        long childrenFiles = 0;

        if (subDirectories.Count > 0)
        {
            if (depth >= _options.MaxAsyncDepth)
            {
                foreach (var subDirectory in subDirectories)
                {
                    context.ThrowIfCancelled();
                    var child = await MeasureDirectoryAsync(subDirectory, depth + 1, context);
                    childrenBytes += child.Bytes;
                    childrenFiles += child.Files;
                }
            }
            else
            {
                var children = await Task.WhenAll(
                    subDirectories.Select(subDirectory =>
                        MeasureDirectoryAsync(subDirectory, depth + 1, context)));
                foreach (var child in children)
                {
                    childrenBytes += child.Bytes;
                    childrenFiles += child.Files;
                }
            }
        }

        context.DirectoryCompleted(directoryPath, levelBytes);
        return (levelBytes + childrenBytes, levelFiles + childrenFiles);
    }

    private sealed class ScanContext
    {
        private readonly ScanOptions _options;
        private readonly CancellationToken _cancellationToken;
        private readonly IProgress<ScanProgress>? _progress;

        private readonly object _sync = new();
        private readonly Dictionary<string, DirectoryMeasurement> _results = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _errors = new();

        private long _bytesMeasured;
        private long _directoriesCompleted;
        private long _lastReportedDirectories;
        private string _currentPath = string.Empty;

        public ScanContext(
            ScanOptions options,
            CancellationToken cancellationToken,
            IProgress<ScanProgress>? progress,
            int rootsTotal)
        {
            _options = options;
            _cancellationToken = cancellationToken;
            _progress = progress;
            RootsTotal = rootsTotal;
        }

        public int RootsTotal { get; }

        public int RootsCompleted { get; private set; }

        public IReadOnlyDictionary<string, DirectoryMeasurement> RootResults => _results;

        public IReadOnlyList<string> Errors => _errors;

        public void ThrowIfCancelled() => _cancellationToken.ThrowIfCancellationRequested();

        public void AddRootBytes(long bytes)
        {
            AddBytes(bytes);
            Interlocked.Exchange(ref _currentPath, string.Empty);
        }

        public void DirectoryCompleted(string path, long levelBytes)
        {
            Interlocked.Increment(ref _directoriesCompleted);
            _currentPath = path;
            AddBytes(levelBytes);
            ReportProgress();
        }

        public void RecordError(string path, string message)
        {
            lock (_sync)
            {
                if (_errors.Count >= _options.MaxRecordedErrors)
                {
                    return;
                }

                _errors.Add($"{path}: {message}");
            }

            Log.Warning("Scan error: {Path}: {Message}", path, message);
        }

        public void AddRootResult(string root, DirectoryMeasurement measurement)
        {
            lock (_sync)
            {
                _results[root] = measurement;
            }

            RootsCompleted++;
            ReportProgress();
        }

        public void ReportProgress()
        {
            var directories = Interlocked.Read(ref _directoriesCompleted);
            var bytes = Interlocked.Read(ref _bytesMeasured);
            var lastReported = Interlocked.Read(ref _lastReportedDirectories);

            if (directories - lastReported < _options.ProgressDirectoryStep &&
                bytes < _options.ProgressBytesStep)
            {
                return;
            }

            Interlocked.Exchange(ref _lastReportedDirectories, directories);

            _progress?.Report(new ScanProgress(
                _currentPath,
                bytes,
                directories,
                RootsCompleted,
                RootsTotal));
        }

        private void AddBytes(long bytes) => Interlocked.Add(ref _bytesMeasured, bytes);
    }
}
