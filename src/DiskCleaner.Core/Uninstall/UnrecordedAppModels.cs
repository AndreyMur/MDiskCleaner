namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Параметры поиска каталогов приложений без записи в реестре Uninstall (фаза 24, модуль 04,
/// FR-4.10) — «4DDiG-типа». Сканируются верхние уровни <c>Program Files</c>/<c>Program Files
/// (x86)</c>/<c>ProgramData</c>; кандидаты — каталоги, не упомянутые ни в одной записи
/// установленного ПО и содержащие исполняемые файлы.
/// </summary>
public sealed class UnrecordedAppScanOptions
{
    /// <summary>Проверка существования каталога (по умолчанию — файловая система).</summary>
    public Func<string, bool>? DirectoryExists { get; set; }

    /// <summary>
    /// Дополнительные имена каталогов, которые не предлагаются как кандидаты (сравнение без
    /// учёта регистра), поверх встроенного защитного списка системных каталогов.
    /// </summary>
    public IReadOnlyList<string>? ExcludedFolderNames { get; set; }

    /// <summary>Насколько глубоко искать исполняемые файлы (по умолчанию 3 уровня).</summary>
    public int MaxExeSearchDepth { get; set; } = 3;
}

/// <summary>
/// Кандидат на удаление как приложение без записи в реестре (FR-4.10): каталог в
/// <c>Program Files</c>/<c>ProgramData</c>, который не упомянут записями Uninstall. Удаляется
/// только по подтверждению; требует прав администратора (FR-4.15); перед удалением проверяются
/// процессы (<see cref="OwnerProcessNames"/> и каталог в целом).
/// </summary>
public sealed record UnrecordedAppCandidate(
    string Path,
    string Name,
    long? SizeBytes,
    bool RequiresAdmin,
    IReadOnlyList<string> OwnerProcessNames);
