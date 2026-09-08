using DiskCleaner.Core.Scanning;

namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Провайдер фактического размера каталога установки (FR-4.4 «занимает на C:»). Возвращает
/// реальный размер папки <c>InstallLocation</c> отдельно от оценки записи реестра
/// (<c>EstimatedSize</c>). Позволяет подменить измерение в unit-тестах (фаза 2).
/// </summary>
public interface IUninstallFolderSizeProvider
{
    /// <summary>Измеряет фактический размер каталога в байтах; null — каталог недоступен/не существует.</summary>
    long? MeasureFolderBytes(string path);
}

/// <summary>
/// Реальная реализация измерения каталога через общий сканер ядра
/// (<see cref="DirectoryScanner"/>): рекурсивно, с обходом reparse-точек и защитой от сбоев.
/// </summary>
public sealed class DirectoryScannerFolderSizeProvider : IUninstallFolderSizeProvider
{
    private readonly DirectoryScanner _scanner;

    public DirectoryScannerFolderSizeProvider(DirectoryScanner? scanner = null)
    {
        _scanner = scanner ?? new DirectoryScanner();
    }

    public long? MeasureFolderBytes(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var outcome = _scanner.MeasureAsync([path]).GetAwaiter().GetResult();
        if (!outcome.Results.TryGetValue(path, out var measurement) || !measurement.Exists || measurement.TimedOut)
        {
            return null;
        }

        return measurement.SizeBytes;
    }
}
