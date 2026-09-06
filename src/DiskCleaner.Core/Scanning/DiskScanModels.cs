using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Scanning;

/// <summary>
/// Запрос на полный скан диска (FR-1.1, FR-1.2, FR-1.4): обход верхнего уровня
/// выбранного корня с игнор-списком системных каталогов.
/// </summary>
public sealed class DiskScanRequest
{
    /// <summary>
    /// Корень сканирования — обычно корень диска (например, <c>C:\</c>).
    /// Пустое значение — системный диск текущего пользователя.
    /// </summary>
    public string RootPath { get; init; } = string.Empty;

    /// <summary>
    /// Включать системные каталоги (<c>Windows</c>, <c>System Volume Information</c>,
    /// <c>$Recycle.Bin</c>, <c>WindowsApps</c>) в обход. По умолчанию они игнорируются (FR-1.2).
    /// </summary>
    public bool IncludeSystemDirectories { get; init; }

    /// <summary>
    /// Таймаут измерения одного каталога верхнего уровня: по истечении ветка помечается
    /// <see cref="DirectoryMeasurement.TimedOut"/>, остальные продолжают сканирование (FR-1.3).
    /// <c>null</c> или ≤ 0 — таймаут выключен.
    /// </summary>
    public TimeSpan? BranchTimeout { get; init; }

    /// <summary>Максимум запоминаемых ошибок доступа/чтения веток.</summary>
    public int MaxRecordedErrors { get; init; } = 100;
}

/// <summary>
/// Результат полного скана диска: каталоги верхнего уровня, системные объекты
/// (Корзина, hiberfil.sys), известные категории и детализация крупных объектов.
/// </summary>
public sealed class DiskScanResult
{
    public string ScanRoot { get; init; } = string.Empty;

    /// <summary>Измеренные каталоги верхнего уровня (без игнор-списка), по убыванию размера.</summary>
    public IReadOnlyList<DirectoryMeasurement> TopLevelDirectories { get; init; } = Array.Empty<DirectoryMeasurement>();

    /// <summary>Имена каталогов верхнего уровня, пропущенных из-за игнор-списка в этом прогоне.</summary>
    public IReadOnlyList<string> IgnoredDirectoryNames { get; init; } = Array.Empty<string>();

    /// <summary>Суммарный размер файлов в корне сканирования (pagefile.sys, hiberfil.sys и т.п.).</summary>
    public long RootFileBytes { get; init; }

    public int RootFileCount { get; init; }

    /// <summary>Системные объекты: Корзина выбранного диска и hiberfil.sys (FR-1.6, SystemFile, низкий риск).</summary>
    public IReadOnlyList<SystemObjectMeasurement> SystemObjects { get; init; } = Array.Empty<SystemObjectMeasurement>();

    /// <summary>Известные объекты категорий (кэши менеджеров и т.п.), найденные на сканируемом корне.</summary>
    public IReadOnlyList<DiskObjectMeasurement> Objects { get; init; } = Array.Empty<DiskObjectMeasurement>();

    /// <summary>Крупные объекты с детализацией по компонентам (Android SDK, .gradle, VS Code) (FR-1.5).</summary>
    public IReadOnlyList<LargeObjectDetail> LargeObjects { get; init; } = Array.Empty<LargeObjectDetail>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public TimeSpan Elapsed { get; init; }

    public DiskScanStatistics Statistics { get; init; } = DiskScanStatistics.Empty;

    public long TopLevelBytes => TopLevelDirectories.Sum(d => d.SizeBytes);

    /// <summary>Физически измерено в этом прогоне: каталоги верхнего уровня + файлы корня.</summary>
    public long ScannedBytes => TopLevelBytes + RootFileBytes;
}

/// <summary>
/// Измеренный объект известной категории (или компонент крупного объекта):
/// путь + размеры + категория/риск для отображения.
/// </summary>
public sealed record DiskObjectMeasurement(
    string Path,
    string Label,
    string Group,
    CleanupCategory Category,
    CleanupRisk Risk,
    DirectoryMeasurement Measurement);

/// <summary>
/// Крупный объект с детализацией по компонентам (FR-1.5): Android SDK, .gradle, VS Code.
/// <see cref="Total"/> — полный размер корня; <see cref="Components"/> — разбивка по подкаталогам.
/// </summary>
public sealed record LargeObjectDetail(
    string Key,
    string Label,
    string RootPath,
    CleanupCategory Category,
    CleanupRisk Risk,
    DirectoryMeasurement? Total,
    IReadOnlyList<DiskObjectMeasurement> Components);

/// <summary>
/// Системный объект скана (категория SystemFile): Корзина диска или hiberfil.sys.
/// Размер носит информационный характер (риск «низкий», FR-1.6).
/// </summary>
public sealed record SystemObjectMeasurement(
    string Key,
    string Label,
    string Path,
    long? SizeBytes,
    int? FileCount,
    bool Present);

/// <summary>Счётчики работы измерителя: что было пересчитано, а что взято из кэша.</summary>
public sealed record DiskScanStatistics(
    int DirectoriesEnumerated,
    int DirectoriesReused,
    int DirectoryAttributesChecked)
{
    public static DiskScanStatistics Empty => new(0, 0, 0);
}
