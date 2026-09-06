using System.IO;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Scanning;

namespace DiskCleaner.Core.Cleaning;

public sealed class DeletionOutcome
{
    public long FreedBytes { get; init; }

    public int DeletedFiles { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public bool Denied { get; init; }

    public bool FullyDeleted => Errors.Count == 0 && !Denied;
}

/// <summary>
/// Исполнитель удаления с try/catch по элементам: заблокированные/недоступные файлы пропускаются
/// (IOException/UnauthorizedAccessException) без прерывания прогона (FR-5.1, FR-5.9).
/// Защищает deny-список (FR-5.6) и поддерживает Корзина-режим (FR-NFR) и очистку содержимого.
/// </summary>
public sealed class DirectoryDeleter
{
    private const int MaxRecordedErrors = 100;

    private readonly IRecycleBin? _recycleBin;

    public DirectoryDeleter(IRecycleBin? recycleBin = null)
    {
        _recycleBin = recycleBin;
    }

    public async Task<DeletionOutcome> DeleteAsync(CleanupItem item, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return new DeletionOutcome();
        }

        if (DenyList.IsProtectedPath(item.Path))
        {
            return new DeletionOutcome { Denied = true, Errors = [DenyList.Describe(item.Path)] };
        }

        if (item.MoveToRecycleBin)
        {
            return await MoveToRecycleBinAsync(item.Path, cancellationToken);
        }

        return await DeletePathAsync(
            item.Path,
            item.Target,
            item.DeleteContentsOnly,
            cancellationToken);
    }

    public async Task<DeletionOutcome> DeletePathAsync(
        string path,
        CleanupTarget target,
        bool deleteContentsOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            return new DeletionOutcome();
        }

        var fullPath = Path.GetFullPath(path);

        if (DenyList.IsProtectedPath(fullPath))
        {
            return new DeletionOutcome { Denied = true, Errors = [DenyList.Describe(fullPath)] };
        }

        if (target == CleanupTarget.File)
        {
            return await DeleteFileAsync(fullPath, cancellationToken);
        }

        return deleteContentsOnly
            ? await ClearDirectoryContentsAsync(fullPath, cancellationToken)
            : await DeleteDirectoryAsync(fullPath, cancellationToken);
    }

    private async Task<DeletionOutcome> MoveToRecycleBinAsync(string path, CancellationToken cancellationToken)
    {
        if (_recycleBin is null)
        {
            return new DeletionOutcome { Errors = ["Корзина-режим недоступен: не настроен сервис Корзины."] };
        }

        var fullPath = Path.GetFullPath(path);
        return await Task.Run(() =>
        {
            try
            {
                _recycleBin.MoveToRecycleBin(fullPath);
                return new DeletionOutcome { DeletedFiles = 0, FreedBytes = 0 };
            }
            catch (Exception ex)
            {
                return new DeletionOutcome { Errors = [$"{fullPath}: {ex.Message}"] };
            }
        }, cancellationToken);
    }

    private static async Task<DeletionOutcome> DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        var state = new DeletionState();
        await Task.Run(() => TryDeleteFile(path, state), cancellationToken);
        return ToOutcome(state);
    }

    private async Task<DeletionOutcome> DeleteDirectoryAsync(string root, CancellationToken cancellationToken)
    {
        var state = new DeletionState();
        await DeleteTreeAsync(root, 0, state, removeSelf: true, cancellationToken);
        TryRemoveEmptyDirectory(root, state);
        return ToOutcome(state);
    }

    private async Task<DeletionOutcome> ClearDirectoryContentsAsync(string root, CancellationToken cancellationToken)
    {
        var state = new DeletionState();
        List<NativeDirectory.Entry> entries;
        try
        {
            entries = NativeDirectory.Enumerate(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.RecordError(root, ex.Message);
            return ToOutcome(state);
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
            {
                if (entry.IsReparsePoint)
                {
                    continue;
                }

                await DeleteTreeAsync(
                    Path.Combine(root, entry.Name),
                    0,
                    state,
                    removeSelf: true,
                    cancellationToken);
            }
            else
            {
                await Task.Run(
                    () => TryDeleteFile(Path.Combine(root, entry.Name), state),
                    cancellationToken);
            }
        }

        return ToOutcome(state);
    }

    private static DeletionOutcome ToOutcome(DeletionState state) => new()
    {
        FreedBytes = state.FreedBytes,
        DeletedFiles = state.DeletedFiles,
        Errors = state.Errors
    };

    private static async Task DeleteTreeAsync(
        string directoryPath,
        int depth,
        DeletionState state,
        bool removeSelf,
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
                    await DeleteTreeAsync(subDirectory, depth + 1, state, removeSelf: true, cancellationToken);
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
                        await DeleteTreeAsync(subDirectory, depth + 1, state, removeSelf: true, token);
                        TryRemoveEmptyDirectory(subDirectory, state);
                    });
            }
        }

        if (removeSelf)
        {
            TryRemoveEmptyDirectory(directoryPath, state);
        }
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
