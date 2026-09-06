using System.IO;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Scanning;

namespace DiskCleaner.Core.Cleaning;

public sealed class DeletionOutcome
{
    public long FreedBytes { get; init; }

    public int DeletedFiles { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public bool FullyDeleted => Errors.Count == 0;
}

public sealed class DirectoryDeleter
{
    private const int MaxRecordedErrors = 100;

    public async Task<DeletionOutcome> DeleteAsync(CleanupItem item, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return new DeletionOutcome();
        }

        var fullPath = Path.GetFullPath(item.Path);
        if (item.Target == CleanupTarget.File)
        {
            return await DeleteFileAsync(fullPath, cancellationToken);
        }

        return await DeleteDirectoryAsync(fullPath, cancellationToken);
    }

    private static async Task<DeletionOutcome> DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        var state = new DeletionState();
        await Task.Run(() => TryDeleteFile(path, state), cancellationToken);
        return new DeletionOutcome
        {
            FreedBytes = state.FreedBytes,
            DeletedFiles = state.DeletedFiles,
            Errors = state.Errors
        };
    }

    private async Task<DeletionOutcome> DeleteDirectoryAsync(string root, CancellationToken cancellationToken)
    {
        var state = new DeletionState();
        await DeleteTreeAsync(root, 0, state, cancellationToken);
        TryRemoveEmptyDirectory(root, state);
        return new DeletionOutcome
        {
            FreedBytes = state.FreedBytes,
            DeletedFiles = state.DeletedFiles,
            Errors = state.Errors
        };
    }

    private static async Task DeleteTreeAsync(
        string directoryPath,
        int depth,
        DeletionState state,
        CancellationToken cancellationToken)
    {
        List<NativeDirectory.Entry> entries;
        try
        {
            entries = NativeDirectory.Enumerate(directoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.RecordError(directoryPath, ex.Message);
            return;
        }

        var files = new List<string>();
        var subDirectories = new List<string>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsDirectory)
            {
                if (!entry.IsReparsePoint)
                {
                    subDirectories.Add(Path.Combine(directoryPath, entry.Name));
                }
            }
            else
            {
                files.Add(Path.Combine(directoryPath, entry.Name));
            }
        }

        if (files.Count > 0)
        {
            await Parallel.ForEachAsync(
                files,
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 8 },
                (file, token) =>
                {
                    TryDeleteFile(file, state);
                    return ValueTask.CompletedTask;
                });
        }

        if (subDirectories.Count > 0)
        {
            if (depth >= 4)
            {
                foreach (var subDirectory in subDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await DeleteTreeAsync(subDirectory, depth + 1, state, cancellationToken);
                    TryRemoveEmptyDirectory(subDirectory, state);
                }
            }
            else
            {
                await Parallel.ForEachAsync(
                    subDirectories,
                    new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 8 },
                    async (subDirectory, token) =>
                    {
                        await DeleteTreeAsync(subDirectory, depth + 1, state, token);
                        TryRemoveEmptyDirectory(subDirectory, state);
                    });
            }
        }

        TryRemoveEmptyDirectory(directoryPath, state);
    }

    private static void TryDeleteFile(string path, DeletionState state)
    {
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            state.RecordError(path, ex.Message);
            return;
        }

        try
        {
            File.Delete(path);
            state.AddFile(length);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                state.AddFile(length);
                return;
            }
            catch (Exception retryEx) when (retryEx is IOException or UnauthorizedAccessException)
            {
                state.RecordError(path, retryEx.Message);
            }
        }
    }

    private static void TryRemoveEmptyDirectory(string path, DeletionState state)
    {
        try
        {
            Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            if (ex is UnauthorizedAccessException)
            {
                state.RecordError(path, ex.Message);
            }
        }
    }

    private sealed class DeletionState
    {
        private readonly object _sync = new();
        private readonly List<string> _errors = new();
        private long _freedBytes;
        private int _deletedFiles;

        public long FreedBytes => Interlocked.Read(ref _freedBytes);

        public int DeletedFiles => Volatile.Read(ref _deletedFiles);

        public IReadOnlyList<string> Errors => _errors;

        public void AddFile(long length)
        {
            Interlocked.Add(ref _freedBytes, length);
            Interlocked.Increment(ref _deletedFiles);
        }

        public void RecordError(string path, string message)
        {
            lock (_sync)
            {
                if (_errors.Count >= MaxRecordedErrors)
                {
                    return;
                }

                _errors.Add($"{path}: {message}");
            }
        }
    }
}
