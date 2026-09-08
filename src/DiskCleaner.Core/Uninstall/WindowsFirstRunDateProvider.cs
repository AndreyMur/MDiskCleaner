using Microsoft.Win32;

namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Источник даты первой загрузки Windows (FR-4.3 «дата установки = дате первого запуска»).
/// Берётся значение <c>InstallDate</c> (Unix-секунды) ветки
/// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion</c> — это момент первой установки/первого
/// запуска ОС. PUPs/бандлы, саморегистрирующиеся при первом включении машины, получают InstallDate,
/// совпадающий с этой датой. Метод возвращает локальную дату (без времени), null — если прочитать нельзя.
/// </summary>
public interface IWindowsFirstRunDateProvider
{
    DateTime? GetFirstRunDate();
}

public sealed class RegistryWindowsFirstRunDateProvider : IWindowsFirstRunDateProvider
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string InstallDateValue = "InstallDate";

    public DateTime? GetFirstRunDate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
            var raw = key?.GetValue(InstallDateValue);
            if (raw is null)
            {
                return null;
            }

            var unixSeconds = raw switch
            {
                int intValue => intValue,
                long longValue => longValue,
                _ => throw new InvalidCastException($"Unexpected InstallDate type: {raw.GetType()}")
            };

            if (unixSeconds <= 0)
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.Date;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidCastException)
        {
            return null;
        }
    }
}
