namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Параметры массового удаления MSI-компонентов SDK одной версии (фаза 23, модуль 04, FR-4.7/4.8/4.9/4.14).
/// </summary>
public sealed class SdkBulkUninstallOptions
{
    /// <summary>
    /// Сколько проходов удаления выполняется. После каждого прохода реестр перечитывается и
    /// компоненты, которые «вернулись» или не удалились с первого раза (exit 0 при оставшейся
    /// записи), удаляются повторно (FR-4.8). По умолчанию 3 прохода.
    /// </summary>
    public int MaxPasses { get; init; } = 3;

    /// <summary>Таймаут одного деинсталлятора (NFR: «не зависать»).</summary>
    public int CommandTimeoutSec { get; init; } = 600;

    /// <summary>
    /// Удалять осиротевшие каталоги версии SDK (Include/Lib/bin), когда соответствующих записей
    /// в реестре больше нет (FR-4.9).
    /// </summary>
    public bool CleanupOrphanedSdkFolders { get; init; } = true;

    /// <summary>
    /// Очищать <c>Package Cache\{code}</c> удалённых bundle/MSI-продуктов, если они больше не
    /// установлены (FR-4.14).
    /// </summary>
    public bool CleanupPackageCache { get; init; } = true;

    /// <summary>Таймаут одного шага удаления каталогов при зачистке (Package Cache/Include/Lib/bin).</summary>
    public int CleanupTimeoutSec { get; init; } = 300;
}

/// <summary>
/// Результат удаления одного компонента версии SDK в рамках одного прохода.
/// Успешными считаются исходы <see cref="UninstallExecutionOutcome.Uninstalled"/>,
/// <see cref="UninstallExecutionOutcome.RebootRequired"/>, а также идемпотентные
/// «не установлено» (1605/1612) и «деинсталлятор отсутствует» (NFR).
/// </summary>
public sealed record SdkComponentResult(
    string ProductCode,
    string DisplayName,
    string DisplayVersion,
    int Pass,
    UninstallExecutionOutcome Outcome,
    int? ExitCode,
    string? Note)
{
    public bool IsOk =>
        Outcome is UninstallExecutionOutcome.Uninstalled
            or UninstallExecutionOutcome.RebootRequired
            or UninstallExecutionOutcome.NotInstalled
            or UninstallExecutionOutcome.AlreadyUninstalled;
}

/// <summary>Результат удаления одного осиротевшего каталога версии SDK (FR-4.9).</summary>
public sealed record SdkFolderCleanupResult(string Path, bool Removed, string? Note);

/// <summary>Результат очистки одной папки <c>Package Cache\{code}</c> (FR-4.14).</summary>
public sealed record SdkPackageCacheCleanupResult(string ProductCode, string Path, bool Removed, string? Note);

/// <summary>
/// Снимок прогресса массового удаления компонентов версии SDK: проход, индекс и общее число
/// компонентов прохода, имя текущего компонента и сообщение.
/// </summary>
public sealed record SdkBulkUninstallProgress(
    int Pass,
    int ComponentIndex,
    int ComponentTotal,
    string DisplayName,
    string Message);

/// <summary>
/// Итог массового удаления версии SDK (фаза 23): журнал по компонентам и проходам, оставшиеся
/// ProductCode после перечитывания реестра, результаты зачистки каталогов и Package Cache.
/// <see cref="FullyRemoved"/> соответствует критерию приёмки «реестр чист» по данной версии.
/// </summary>
public sealed class SdkBulkUninstallReport
{
    public string DisplayVersion { get; init; } = string.Empty;

    /// <summary>Сколько проходов удаления фактически выполнено (0 — записи уже отсутствовали).</summary>
    public int PassesUsed { get; init; }

    public IReadOnlyList<SdkComponentResult> Components { get; init; } = Array.Empty<SdkComponentResult>();

    /// <summary>ProductCode записей версии, оставшихся в реестре после всех проходов.</summary>
    public IReadOnlyList<string> RemainingProductCodes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RemainingDisplayNames { get; init; } = Array.Empty<string>();

    public bool FullyRemoved => RemainingProductCodes.Count == 0;

    public int OkCount => Components.Count(c => c.IsOk);

    public int FailedCount => Components.Count(c => !c.IsOk);

    public IReadOnlyList<SdkFolderCleanupResult> FolderCleanups { get; init; } = Array.Empty<SdkFolderCleanupResult>();

    public IReadOnlyList<SdkPackageCacheCleanupResult> PackageCacheCleanups { get; init; } = Array.Empty<SdkPackageCacheCleanupResult>();
}
