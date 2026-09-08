namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Параметры поиска осиротевших записей Uninstall (фаза 24, модуль 04, FR-4.13): запись
/// считается осиротевшей, когда её <c>InstallLocation</c> и/или файл деинсталлятора ссылаются
/// на несуществующие пути. Удаление таких записей выполняется только по явному подтверждению
/// (записи никогда не предвыбираются); системные компоненты (<c>SystemComponent=1</c>) по
/// умолчанию не предлагаются вовсе.
/// </summary>
public sealed class UninstallOrphanRegistryOptions
{
    /// <summary>Проверка существования пути (по умолчанию — файловая система).</summary>
    public Func<string, bool> PathExists { get; set; } = static path =>
        Directory.Exists(path) || File.Exists(path);

    /// <summary>
    /// Включать системные компоненты (<c>SystemComponent=1</c>) в результаты поиска. По умолчанию
    /// false — такие записи не предлагаются без явного согласия пользователя (FR-4.13).
    /// </summary>
    public bool IncludeSystemComponents { get; set; }
}

/// <summary>
/// Осиротевшая запись Uninstall (FR-4.13): запись из ветки
/// <c>Software\Microsoft\Windows\CurrentVersion\Uninstall</c> (или WOW6432Node), ссылающаяся на
/// отсутствующие пути. <see cref="MissingPaths"/> — человекочитаемые описания отсутствующих путей.
/// </summary>
public sealed record UninstallOrphanRegistryMatch(
    InstalledApp App,
    IReadOnlyList<string> MissingPaths,
    bool IsSystemComponent)
{
    public string DisplayName => App.DisplayName;

    public bool RequiresAdmin => App.RequiresAdmin;
}
