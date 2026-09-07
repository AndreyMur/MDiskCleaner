namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// Рекурсивный подсчёт размера каталога и даты его последнего изменения для кандидатов
/// группы «Остатки апдейтеров» (FR-3.2). Reparse-точки не обходятся (защита от циклов);
/// недоступные вложенные объекты пропускаются, не прерывая измерение.
/// </summary>
public static class LeftoverDirectoryMeasurer
{
    public static long MeasureSizeBytes(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long bytes = 0;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                bytes += MeasureEntry(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return bytes;
        }

        return bytes;
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

    private static long MeasureEntry(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return 0;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                long bytes = 0;
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    bytes += MeasureEntry(child);
                }

                return bytes;
            }

            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return 0;
        }
    }
}
