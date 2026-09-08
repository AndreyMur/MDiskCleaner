namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// Результат исполнения одного удаления остатка (журнал, NFR PRD 03): объект, итог,
/// освобождённый объём и пояснение. Основание удаления (FR-3.6) доступно через
/// <see cref="Item"/> (LeftoverPlanItem.ReasonText) и продублировано в <see cref="Note"/>.
/// </summary>
public sealed record LeftoverDeletionEntry(
    LeftoverPlanItem Item,
    LeftoverCleanOutcome Outcome,
    long FreedBytes,
    string? Note)
{
    /// <summary>Основание удаления объекта (почему это остаток, FR-3.6).</summary>
    public string Basis => Item.ReasonText;
}

/// <summary>
/// Итог исполнения плана остатков: журнал пообъектных записей с основаниями удаления
/// (FR-3.6, NFR PRD 03) и сводка. При dry-run удаление не выполняется (FR-3.8).
/// </summary>
public sealed class LeftoverCleanReport
{
    public IReadOnlyList<LeftoverDeletionEntry> Entries { get; init; } = Array.Empty<LeftoverDeletionEntry>();

    /// <summary>Предпросмотр (dry-run): ничего не удалялось (FR-3.8).</summary>
    public bool DryRun { get; init; }

    public TimeSpan Elapsed { get; init; }

    public long TotalFreedBytes => Entries.Sum(e => Math.Max(0, e.FreedBytes));

    public int DeletedCount => Entries.Count(e =>
        e.Outcome is LeftoverCleanOutcome.DirectDeleted or LeftoverCleanOutcome.ElevatedDeleted);

    /// <summary>Сколько объектов пропущено (живые объекты, отказ UAC, нет подтверждения, ошибки).</summary>
    public int SkippedCount => Entries.Count(e =>
        e.Outcome is LeftoverCleanOutcome.LiveObjectSkipped
            or LeftoverCleanOutcome.ConfirmationRequired
            or LeftoverCleanOutcome.ElevationDeclined
            or LeftoverCleanOutcome.Error
            or LeftoverCleanOutcome.Partial);
}

/// <summary>
/// Параметры исполнения плана остатков. По умолчанию опасные объекты
/// (<see cref="LeftoverPlanItem.RequiresConfirmation"/>) требуют явного подтверждения —
/// исполнитель помечает их как <see cref="LeftoverCleanOutcome.ConfirmationRequired"/>.
/// </summary>
public sealed class LeftoverCleanOptions
{
    /// <summary>Предпросмотр (dry-run): объекты показываются, но ничего не удаляется (FR-3.8).</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Опасные объекты (Program Files/ProgramData/Windows.old) подтверждены пользователем
    /// пообъектно (FR-3.8, §5). Без этого флага они не удаляются.
    /// </summary>
    public bool ConfirmDangerous { get; set; }
}

public enum LeftoverCleanOutcome
{
    /// <summary>Предпросмотр (dry-run) — объект не удалялся.</summary>
    DryRun,

    /// <summary>Объект уже отсутствует на диске — успех без действий (идемпотентно).</summary>
    AlreadyAbsent,

    /// <summary>Удалено напрямую в контексте пользователя.</summary>
    DirectDeleted,

    /// <summary>Удалено через elevated-процесс (Program Files/ProgramData/Windows.old, один UAC-подъём).</summary>
    ElevatedDeleted,

    /// <summary>Удалено частично (часть файлов заблокирована/недоступна).</summary>
    Partial,

    /// <summary>Повторная проверка перед удалением выявила «живой» объект (процесс/%PATH%/служба) — пропущен.</summary>
    LiveObjectSkipped,

    /// <summary>Опасный объект не подтверждён пользователем (FR-3.8, §5).</summary>
    ConfirmationRequired,

    /// <summary>UAC-подъём отклонён пользователем.</summary>
    ElevationDeclined,

    /// <summary>Ошибка удаления.</summary>
    Error
}
