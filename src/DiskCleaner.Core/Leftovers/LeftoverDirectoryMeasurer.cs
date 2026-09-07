namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// Рекурсивный подсчёт размера каталога, числа файлов и даты последнего изменения для
/// кандидатов остатков (FR-3.2). Reparse-точки не обходятся (защита от циклов);
/// недоступные вложенные объекты пропускаются, не прерывая измерение. Метод
/// <see cref="Measure"/> поддерживает досрочную остановку при превышении бюджета
/// (байты/файлы) — используется порогами движка эвристик (фаза 17), чтобы не обходить
/// полностью большие каталоги без явной категории.
/// </summary>
public static class LeftoverDirectoryMeasurer
{
    public static long MeasureSizeBytes(string directory) =>
        Measure(directory).SizeBytes;

    /// <summary>
    /// Рекурсивное измерение каталога с возможностью досрочной остановки, когда накоплено
    /// <paramref name="abortAfterBytes"/> байт или <paramref name="abortAfterFiles"/> файлов.
    /// При остановке <see cref="DirectoryMeasurement.Complete"/> равен false.
    /// </summary>
    public static DirectoryMeasurement Measure(
        string directory,
        long? abortAfterBytes = null,
        int? abortAfterFiles = null)
    {
        if (!Directory.Exists(directory))
        {
            return default;
        }

        long bytes = 0;
        int files = 0;
        var aborted = false;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                MeasureEntry(entry, abortAfterBytes, abortAfterFiles, ref bytes, ref files, ref aborted);
                if (aborted)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            aborted = true;
        }

        return new DirectoryMeasurement(bytes, files, !aborted);
    }

    public static DateTime? GetLastWriteTimeUtc(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetLastWriteTimeUtc(directory)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static void MeasureEntry(
        string path,
        long? abortAfterBytes,
        int? abortAfterFiles,
        ref long bytes,
        ref int files,
        ref bool aborted)
    {
        if (aborted)
        {
            return;
        }

        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    MeasureEntry(child, abortAfterBytes, abortAfterFiles, ref bytes, ref files, ref aborted);
                    if (aborted)
                    {
                        return;
                    }
                }

                return;
            }

            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return;
            }

            bytes += info.Length;
            files++;
            if ((abortAfterBytes.HasValue && bytes >= abortAfterBytes.Value) ||
                (abortAfterFiles.HasValue && files >= abortAfterFiles.Value))
            {
                aborted = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }
}

/// <summary>Результат измерения каталога (<see cref="LeftoverDirectoryMeasurer.Measure"/>).</summary>
public readonly record struct DirectoryMeasurement(long SizeBytes, int FileCount, bool Complete);
