using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Caches;

/// <summary>
/// Способ очистки кэша, выбранный планировщиком (FR-2.6, FR-2.7).
/// Приоритет — штатная команда менеджера; прямое удаление каталога — fallback.
/// </summary>
public enum CacheCleanMethod
{
    /// <summary>Только штатная команда менеджера (docker prune, powercfg и т.п.); каталога-цели нет.</summary>
    NativeCommand,

    /// <summary>
    /// Штатная команда менеджера; если после неё размер не изменится/изменится незначительно —
    /// будет предложено прямое удаление каталога (FR-2.7).
    /// </summary>
    NativeWithDirectFallback,

    /// <summary>Прямое удаление каталога: штатной команды нет или кэш осиротевший (FR-2.4).</summary>
    DirectDelete,

    /// <summary>Объект используется запущенным процессом — отложить (FR-2.8).</summary>
    DeferredInUse,

    /// <summary>Нельзя чистить: не входит в whitelist VS Code, категория Leftover и т.п. (FR-2.9, FR-2.10).</summary>
    NotAllowed
}

/// <summary>Уровень согласия на очистку кэша (структура FR-2.10–2.11).</summary>
public enum CacheConsent
{
    /// <summary>Штатная массовая очистка после отметки пользователя в UI (чекбокс, NFR G3).</summary>
    Auto,

    /// <summary>Требует явного подтверждения (Gradle <c>jdks</c>, тулчейн rustup).</summary>
    Ask
}

/// <summary>Сведения о свободном месте на диске для защитного режима (FR-2.12).</summary>
public sealed record CacheDriveSpace(string RootPath, long FreeBytes, long TotalBytes)
{
    /// <summary>Доля свободного места (0..1); при TotalBytes ≤ 0 — 0.</summary>
    public double FreeFraction => TotalBytes <= 0 ? 0 : FreeBytes / (double)TotalBytes;
}

/// <summary>
/// Источник данных о свободном месте диска. Абстракция позволяет unit-тестам
/// подменять <see cref="DriveInfo"/> (FR-2.12).
/// </summary>
public interface IDriveSpaceService
{
    /// <summary>Свободное место диска для пути; <c>null</c>, если определить нельзя.</summary>
    CacheDriveSpace? GetDriveSpace(string path);
}

/// <summary>Реализация по умолчанию через <see cref="DriveInfo"/>.</summary>
public sealed class DriveInfoDriveSpaceService : IDriveSpaceService
{
    public CacheDriveSpace? GetDriveSpace(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return null;
            }

            return new CacheDriveSpace(drive.Name, drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// «Карточка действия» для кэша (FR-2.1, FR-2.5): способ очистки, уровень согласия,
/// оценка экономии (размер из PRD 01), «что произойдёт после очистки» и оценка
/// времени/трафика на восстановление.
/// </summary>
public sealed class CacheCleanAction
{
    /// <summary>Объект очистки (измеренный модулем 01).</summary>
    public required CleanupItem Item { get; init; }

    /// <summary>Выбранный способ очистки (матрица решений, FR-2.6/2.7).</summary>
    public CacheCleanMethod Method { get; init; }

    /// <summary>Уровень согласия на очистку (FR-2.11).</summary>
    public CacheConsent Consent { get; init; }

    /// <summary>Оценка экономии — размер кэша из анализа PRD 01 (FR-2.1).</summary>
    public long EstimatedSavingsBytes { get; init; }

    /// <summary>Человекочитаемая оценка экономии («Освободит ≈ 1,4 ГБ»).</summary>
    public string SavingsText { get; init; } = string.Empty;

    /// <summary>Штатная команда менеджера (если есть), иначе <c>null</c>.</summary>
    public string? NativeCommandText { get; init; }

    /// <summary>«Что произойдёт после очистки» (последствия для пользователя).</summary>
    public string ConsequencesText { get; init; } = string.Empty;

    /// <summary>Оценка времени/трафика на восстановление кэша.</summary>
    public string RestoreEstimateText { get; init; } = string.Empty;

    /// <summary>Причина, по которой объект нельзя чистить (Leftover, вне whitelist и т.п.).</summary>
    public string? NotAllowedReason { get; init; }

    /// <summary>
    /// Причина пометки «не трогать»: защитный режим при &lt; 15% свободного места (FR-2.12)
    /// либо занятость процессом (IN_USE). Объект не включается в авто-план.
    /// </summary>
    public string? HoldReason { get; init; }

    public bool Cleanable =>
        Method is CacheCleanMethod.NativeCommand
            or CacheCleanMethod.NativeWithDirectFallback
            or CacheCleanMethod.DirectDelete;

    /// <summary>Пометка «не трогать»: низкий риск повредить кэш есть, но трогать нельзя.</summary>
    public bool IsHeld => HoldReason is not null;

    /// <summary>Действие по умолчанию для UI (FR-1.6): Clean/Ask/Keep.</summary>
    public CleanupDefaultAction DefaultAction =>
        Method == CacheCleanMethod.NotAllowed || IsHeld
            ? CleanupDefaultAction.Keep
            : Consent == CacheConsent.Ask
                ? CleanupDefaultAction.Ask
                : CleanupDefaultAction.Clean;
}

/// <summary>
/// Результат планирования очистки кэшей (фаза «Планировщик действий очистки»):
/// действия с последствиями и корректным уровнем согласия; защитный режим FR-2.12.
/// </summary>
public sealed class CacheCleanPlan
{
    public IReadOnlyList<CacheCleanAction> Actions { get; init; } = Array.Empty<CacheCleanAction>();

    /// <summary>Сколько входных объектов вне области очистки кэшей (не Cache/DevToolchain).</summary>
    public int OutOfScopeCount { get; init; }

    /// <summary>Сколько объектов заблокировано правилами (Leftover, вне whitelist VS Code).</summary>
    public int NotAllowedCount { get; init; }

    /// <summary>Диски, на которых включён защитный режим «не трогать» (&lt; 15% свободно).</summary>
    public IReadOnlyList<CacheDriveSpace> LowSpaceDrives { get; init; } = Array.Empty<CacheDriveSpace>();

    /// <summary>Сводка защитного режима (пусто, если места достаточно).</summary>
    public string? GuardNote { get; init; }

    public IReadOnlyList<CacheCleanAction> CleanableActions =>
        Actions.Where(a => a.Cleanable && !a.IsHeld).ToList();

    /// <summary>Суммарная оценка экономии по объектам, предложенным к очистке.</summary>
    public long TotalEstimatedSavingsBytes =>
        CleanableActions.Sum(a => Math.Max(0, a.EstimatedSavingsBytes));
}
