using System.IO;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Scanning;

namespace DiskCleaner.Core.Cleaning;

public sealed class DeletionOutcome
{
    public long FreedBytes { get; init; }

    public int DeletedFiles { get; init; }

    /// <summary>Сколько файлов не удалось удалить (заблокировано/нет доступа).</summary>
    public int SkippedFiles { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public bool Denied { get; init; }

    public bool FullyDeleted => SkippedFiles == 0 && Errors.Count == 0 && !Denied;
}

/// <summary>
/// Снимок прогресса прямого удаления каталога (FR-2.6 fallback, NFR «показывать прогресс»):
/// накопительные счётчики удалённых/пропущенных файлов и освобождённых байт.
/// </summary>
public sealed record DirectoryDeletionProgress(
    long DeletedFiles,
    long DeletedBytes,
    long SkippedFiles,
    string? CurrentPath);

/// <summary>
/// Исполнитель удаления с try/catch по элементам: заблокированные/недоступные файлы пропускаются
/// (IOException/UnauthorizedAccessException) без прерывания прогона (FR-5.1, FR-5.9).
/// Защищает deny-список (FR-5.6) и поддерживает Корзина-режим (FR-NFR) и очистку содержимого.
/// Для больших каталогов (&gt; 1 ГБ) дерево удаляется параллельными партиями с прогрессом
/// (NFR «удаление 14,5 ГБ ≤ 3 минут»).
/// </summary>
public sealed class DirectoryDeleter
{
    private const int MaxRecordedErrors = 100;

    /// <summary>Порог «большого» каталога: для него включается удаление параллельными партиями (NFR).</summary>
    public const long LargeDirectoryThresholdBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>Параллелизм удаления обычного каталога.</summary>
    public const int DefaultConcurrency = 8;

    /// <summary>Параллелизм удаления «большого» каталога (&gt; 1 ГБ) партиями.</summary>
    public const int LargeDirectoryConcurrency = 16;

    /// <summary>Как часто отдавать снимок прогресса (по числу удалённых файлов).</summary>
    private const int ReportEveryFiles = 256;

    private readonly IRecycleBin? _recycleBin;

    public DirectoryDeleter(IRecycleBin? recycleBin = null)
    {
        _recycleBin = recycleBin;
    }

    public async Task<DeletionOutcome> DeleteAsync(
        CleanupItem item,
        CancellationToken cancellationToken = default,
        IProgress<DirectoryDeletionProgress>? progress = null)
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
            cancellationToken,
            progress,
            item.SizeBytes);
    }

    public async Task<DeletionOutcome> DeletePathAsync(
        string path,
        CleanupTarget target,
        bool deleteContentsOnly = false,
        CancellationToken cancellationToken = default,
        IProgress<DirectoryDeletionProgress>? progress = null,
        long? sizeHint = null)
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
            return await DeleteFileAsync(fullPath, cancellationToken, progress);
        }

        var concurrency = deleteContentsOnly ? DefaultConcurrency : ConcurrencyFor(sizeHint);
        var state = new DeletionState(progress, concurrency);
        if (deleteContentsOnly)
        {
            await ClearDirectoryContentsAsync(fullPath, state, cancellationToken);
            return ToOutcome(state);
        }

        await DeleteTreeAsync(fullPath, state, removeSelf: true, concurrency, cancellationToken);
        return ToOutcome(state);
    }

    private static int ConcurrencyFor(long? sizeHint) =>
        sizeHint is > LargeDirectoryThresholdBytes ? LargeDirectoryConcurrency : DefaultConcurrency;

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

    private static async Task<DeletionOutcome> DeleteFileAsync(
        string path,
        CancellationToken cancellationToken,
        IProgress<DirectoryDeletionProgress>? progress)
    {
        var state = new DeletionState(progress);
        await Task.Run(() => TryDeleteFile(path, state), cancellationToken);
        return ToOutcome(state);
    }

    private static async Task<DeletionOutcome> ClearDirectoryContentsAsync(
        string root,
        DeletionState state,
        CancellationToken cancellationToken)
    {
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

                var child = Path.Combine(root, entry.Name);
                await DeleteTreeAsync(child, state, removeSelf: true, DefaultConcurrency, cancellationToken);
            }
            else
            {
                await Task.Run(() => TryDeleteFile(Path.Combine(root, entry.Name), state), cancellationToken);
            }
        }

        return ToOutcome(state);
    }

    private static DeletionOutcome ToOutcome(DeletionState state) => new()
    {
        FreedBytes = state.FreedBytes,
        DeletedFiles = state.DeletedFiles,
        SkippedFiles = state.SkippedFiles,
        Errors = state.Errors
    };

    /// <summary>
    /// Рекурсивное удаление каталога параллельными партиями: подкаталоги обрабатываются параллельно,
    /// файлы внутри каталога удаляются параллельными партиями — для «больших» каталогов (&gt; 1 ГБ)
    /// параллелизм выше (16 вместо 8). Reparse-точки (симлинки/junction) не обходятся.
    /// Общая занятость ограничена глобальным семафором <paramref name="gate"/>: одновременно
    /// обрабатывается не больше <paramref name="concurrency"/> каталогов, поэтому глубина дерева
    /// не приводит к взрывному росту потоков.
    /// </summary>
    private static async Task DeleteTreeAsync(
        string directoryPath,
        DeletionState state,
        bool removeSelf,
        int concurrency,
        CancellationToken cancellationToken)
    {
        await state.AcquireDirectoryAsync(cancellationToken).ConfigureAwait(false);
        try
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

            var files = new List<string>(entries.Count);
            var subDirectories = new List<string>(entries.Count);

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
                    new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = concurrency },
                    (file, _) =>
                    {
                        TryDeleteFile(file, state);
                        return ValueTask.CompletedTask;
                    });
            }

            if (subDirectories.Count > 0)
            {
                await Parallel.ForEachAsync(
                    subDirectories,
                    new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = concurrency },
                    async (subDirectory, token) =>
                    {
                        await DeleteTreeAsync(subDirectory, state, removeSelf: true, concurrency, token);
                    });
            }

            if (removeSelf)
            {
                TryRemoveEmptyDirectory(directoryPath, state);
            }

            state.ReportProgress(directoryPath);
        }
        finally
        {
            state.ReleaseDirectory();
        }
    }

    private static void TryDeleteFile(string path, DeletionState state)
    {
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (FileNotFoundException)
        {
            // Файл уже удалён (гонка с другим процессом) — не ошибка и не пропуск.
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.RecordSkip(path, ex.Message);
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
                state.RecordSkip(path, retryEx.Message);
            }
        }
    }

    private static void TryRemoveEmptyDirectory(string path, DeletionState state)
    {
        try
        {
            Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or DirectoryNotFoundException)
        {
            // Каталог ещё не пуст (часть файлов пропущена) или уже удалён — не ошибка.
        }
        catch (UnauthorizedAccessException)
        {
            state.RecordError(path, "Нет доступа к каталогу");
        }
    }

    /// <summary>
    /// Накопительное состояние одного удаления: счётчики, журнал ошибок (первые
    /// <see cref="MaxRecordedErrors"/>), прогресс и семафор, ограничивающий число
    /// одновременно обрабатываемых каталогов (партий).
    /// </summary>
    private sealed class DeletionState
    {
        private readonly object _sync = new();
        private readonly List<string> _errors = new();
        private readonly IProgress<DirectoryDeletionProgress>? _progress;
        private readonly SemaphoreSlim _gate;
        private long _freedBytes;
        private long _deletedFiles;
        private long _skippedFiles;
        private long _lastReportedFiles = -ReportEveryFiles;

        public DeletionState(IProgress<DirectoryDeletionProgress>? progress, int concurrency = DefaultConcurrency)
        {
            _progress = progress;
            _gate = new SemaphoreSlim(concurrency, concurrency);
        }

        public long FreedBytes => Interlocked.Read(ref _freedBytes);

        public int DeletedFiles => (int)Volatile.Read(ref _deletedFiles);

        public int SkippedFiles => (int)Volatile.Read(ref _skippedFiles);

        public IReadOnlyList<string> Errors => _errors;

        public async Task AcquireDirectoryAsync(CancellationToken cancellationToken) =>
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        public void ReleaseDirectory() => _gate.Release();

        public void AddFile(long length)
        {
            Interlocked.Add(ref _freedBytes, length);
            Interlocked.Increment(ref _deletedFiles);
            ReportThrottled();
        }

        public void RecordSkip(string path, string message)
        {
            Interlocked.Increment(ref _skippedFiles);
            RecordError(path, message);
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

        /// <summary>Отдаёт снимок прогресса по достижении очередной «партии» удалённых файлов.</summary>
        private void ReportThrottled()
        {
            var deleted = Volatile.Read(ref _deletedFiles);
            var reported = Interlocked.Read(ref _lastReportedFiles);
            if (deleted - reported < ReportEveryFiles)
            {
                return;
            }

            var target = deleted - (deleted % ReportEveryFiles);
            if (Interlocked.CompareExchange(ref _lastReportedFiles, target, reported) == reported)
            {
                _progress?.Report(new DirectoryDeletionProgress(
                    Volatile.Read(ref _deletedFiles),
                    Interlocked.Read(ref _freedBytes),
                    Volatile.Read(ref _skippedFiles),
                    null));
            }
        }

        public void ReportProgress(string? currentPath)
        {
            _progress?.Report(new DirectoryDeletionProgress(
                Volatile.Read(ref _deletedFiles),
                Interlocked.Read(ref _freedBytes),
                Volatile.Read(ref _skippedFiles),
                currentPath));
        }
    }
}
