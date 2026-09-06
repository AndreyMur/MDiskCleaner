namespace DiskCleaner.Core.Scanning;

/// <summary>
/// Рекурсивный измеритель каталога с инкрементальным кэшем по веткам (mtime + размер).
/// Если <c>LastWriteTime</c> каталога совпадает с записью кэша и у него нет подкаталогов —
/// размер переиспользуется без перечисления файлов. Если подкаталоги есть — они
/// проверяются рекурсивно (по одному системному вызову на каталог), пересчитываются
/// только изменившиеся ветки (FR-1.3, NFR инкрементальности).
/// </summary>
internal sealed class CachedDirectoryMeasurer
{
    private const int MaxParallelChildDepth = 5;

    private readonly ScanCacheStore? _cacheStore;
    private Func<string, bool> _isExcluded;

    public CachedDirectoryMeasurer(ScanCacheStore? cacheStore)
    {
        _cacheStore = cacheStore;
        _isExcluded = static _ => false;
    }

    /// <summary>
    /// Предикат исключения каталога из обхода по имени (игнор-список системных каталогов).
    /// Применяется на любом уровне вложенности, кроме самого корня измеряемой ветки.
    /// </summary>
    public void SetExcludedNames(Func<string, bool> isExcluded) =>
        _isExcluded = isExcluded ?? throw new ArgumentNullException(nameof(isExcluded));

    public async Task<DirectoryMeasurement> MeasureBranchAsync(
        string directory,
        DiskScanContext ctx,
        CancellationToken cancellationToken)
    {
        var (bytes, files) = await MeasureDirectoryAsync(directory, 0, ctx, cancellationToken);
        return new DirectoryMeasurement(directory, bytes, files, Directory.Exists(directory));
    }

    private async Task<(long Bytes, long Files)> MeasureDirectoryAsync(
        string directory,
        int depth,
        DiskScanContext ctx,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ctx.DirectoryChecked();

        long mtimeTicks;
        try
        {
            mtimeTicks = File.GetLastWriteTimeUtc(directory).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.RecordError(directory, ex.Message);
            return (0, 0);
        }

        var cached = _cacheStore?.TryGet(directory);
        if (cached is not null && cached.DirectoryLastWriteTicks == mtimeTicks)
        {
            return await ReuseDirectoryAsync(directory, cached, ctx, cancellationToken);
        }

        List<NativeDirectory.Entry> entries;
        try
        {
            entries = NativeDirectory.Enumerate(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.RecordError(directory, ex.Message);
            return (0, 0);
        }

        ctx.DirectoryEnumerated();

        long ownBytes = 0;
        long ownFiles = 0;
        var subdirectories = new List<string>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
            {
                if (!entry.IsReparsePoint && !_isExcluded(entry.Name))
                {
                    subdirectories.Add(entry.Name);
                }
            }
            else
            {
                ownBytes += entry.Size;
                ownFiles++;
            }
        }

        var (bytes, files) = await MeasureChildrenAsync(
            directory,
            subdirectories,
            depth,
            ctx,
            cancellationToken);

        bytes += ownBytes;
        files += ownFiles;
        ctx.AddBytes(ownBytes);
        ctx.DirectoryCompleted(directory);

        Record(directory, mtimeTicks, ownBytes, ownFiles, bytes, files, subdirectories);
        return (bytes, files);
    }

    private async Task<(long Bytes, long Files)> ReuseDirectoryAsync(
        string directory,
        ScanCacheEntry cached,
        DiskScanContext ctx,
        CancellationToken cancellationToken)
    {
        if (cached.Subdirectories.Count == 0)
        {
            ctx.DirectoryReused();
            ctx.DirectoryCompleted(directory);

            Record(
                directory,
                cached.DirectoryLastWriteTicks,
                cached.OwnFileBytes,
                cached.OwnFileCount,
                cached.SubtreeBytes,
                cached.SubtreeFileCount,
                cached.Subdirectories);
            return (cached.SubtreeBytes, cached.SubtreeFileCount);
        }

        long bytes = cached.OwnFileBytes;
        long files = cached.OwnFileCount;

        foreach (var subdirectory in cached.Subdirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_isExcluded(subdirectory))
            {
                continue;
            }

            var (childBytes, childFiles) = await MeasureDirectoryAsync(
                Path.Combine(directory, subdirectory),
                0,
                ctx,
                cancellationToken);
            bytes += childBytes;
            files += childFiles;
        }

        ctx.DirectoryReused();
        ctx.DirectoryCompleted(directory);

        Record(
            directory,
            cached.DirectoryLastWriteTicks,
            cached.OwnFileBytes,
            cached.OwnFileCount,
            bytes,
            files,
            cached.Subdirectories);
        return (bytes, files);
    }

    private async Task<(long Bytes, long Files)> MeasureChildrenAsync(
        string directory,
        IReadOnlyList<string> subdirectories,
        int depth,
        DiskScanContext ctx,
        CancellationToken cancellationToken)
    {
        if (subdirectories.Count == 0)
        {
            return (0, 0);
        }

        if (depth < MaxParallelChildDepth)
        {
            var results = await Task.WhenAll(
                subdirectories.Select(name => Task.Run(
                    () => MeasureDirectoryAsync(Path.Combine(directory, name), depth + 1, ctx, cancellationToken),
                    cancellationToken)));
            return (
                results.Sum(r => r.Bytes),
                results.Sum(r => r.Files));
        }

        long bytes = 0;
        long files = 0;
        foreach (var name in subdirectories)
        {
            var (childBytes, childFiles) = await MeasureDirectoryAsync(
                Path.Combine(directory, name),
                depth + 1,
                ctx,
                cancellationToken);
            bytes += childBytes;
            files += childFiles;
        }

        return (bytes, files);
    }

    private void Record(
        string directory,
        long mtimeTicks,
        long ownBytes,
        long ownFiles,
        long subtreeBytes,
        long subtreeFiles,
        IReadOnlyList<string> subdirectories)
    {
        _cacheStore?.Record(directory, new ScanCacheEntry
        {
            Path = directory,
            DirectoryLastWriteTicks = mtimeTicks,
            OwnFileBytes = ownBytes,
            OwnFileCount = ownFiles,
            SubtreeBytes = subtreeBytes,
            SubtreeFileCount = subtreeFiles,
            Subdirectories = subdirectories.ToList()
        });
    }
}
