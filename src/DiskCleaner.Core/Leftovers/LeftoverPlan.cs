using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// План очистки остатков (FR-3.8, модель плана PRD 00): список «карточек» остатков,
/// найденных сканером. По умолчанию все кандидаты <b>выключены</b>
/// (<see cref="LeftoverPlanItem.IsEnabled"/> == false) — пользователь явно включает
/// (подтверждает) только те объекты, которые согласен удалить. Кандидаты из
/// <c>exclusions.json</c> (FR-3.7) в план не попадают.
/// </summary>
public sealed class LeftoverPlan
{
    /// <summary>Момент построения плана (UTC).</summary>
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    public IReadOnlyList<LeftoverPlanItem> Items { get; init; } = Array.Empty<LeftoverPlanItem>();

    /// <summary>Кандидаты, включённые пользователем (подтверждённые к удалению).</summary>
    public IReadOnlyList<LeftoverPlanItem> EnabledItems => Items.Where(i => i.IsEnabled).ToList();

    public int EnabledCount => Items.Count(i => i.IsEnabled);

    /// <summary>Суммарный объём всех найденных остатков.</summary>
    public long TotalBytes => Items.Sum(i => Math.Max(0, i.SizeBytes ?? 0));

    /// <summary>Суммарный объём включённых (подтверждённых) остатков.</summary>
    public long EnabledBytes => Items.Where(i => i.IsEnabled).Sum(i => Math.Max(0, i.SizeBytes ?? 0));

    /// <summary>Сколько включённых кандидатов требуют обязательного подтверждения (FR-3.4, §5).</summary>
    public int EnabledRequiresConfirmationCount => Items.Count(i => i.IsEnabled && i.RequiresConfirmation);
}

/// <summary>
/// Один кандидат плана остатков: данные найденного остатка (<see cref="LeftoverCandidate"/>) и
/// состояние выбора. По умолчанию выключен (FR-3.8); опасные объекты
/// (Program Files/ProgramData/Windows.old) дополнительно помечены обязательным подтверждением.
/// </summary>
public sealed class LeftoverPlanItem
{
    /// <summary>Уникальный ключ объекта в плане (путь каталога).</summary>
    public required string Key { get; init; }

    /// <summary>Полный путь удаляемого каталога.</summary>
    public required string Path { get; init; }

    public required string DisplayName { get; init; }

    public required string GroupName { get; init; }

    /// <summary>Тип основания («почему это остаток», FR-3.6).</summary>
    public required LeftoverReason Reason { get; init; }

    /// <summary>Человекочитаемое основание — фиксируется в журнале удаления (FR-3.6, NFR).</summary>
    public required string ReasonText { get; init; }

    public CleanupRisk Risk { get; init; } = CleanupRisk.Medium;

    public CleanupCategory Category { get; init; } = CleanupCategory.Leftover;

    /// <summary>Удаление требует прав администратора (Program Files/ProgramData/Windows.old).</summary>
    public bool RequiresAdmin { get; init; }

    /// <summary>Обязательное пообъектное подтверждение перед удалением (FR-3.4, §5).</summary>
    public bool RequiresConfirmation { get; init; }

    public long? SizeBytes { get; init; }

    public long? FileCount { get; init; }

    public DateTime? LastWriteTimeUtc { get; init; }

    /// <summary>Рекомендуемый способ удаления (Windows.old: Storage Sense / cleanmgr / DISM, FR-3.5).</summary>
    public string? RecommendedRemovalMethod { get; init; }

    /// <summary>Включён ли кандидат пользователем. По умолчанию — выключен (FR-3.8).</summary>
    public bool IsEnabled { get; set; }

    public static LeftoverPlanItem From(LeftoverCandidate candidate)
    {
        return new LeftoverPlanItem
        {
            Key = candidate.Path,
            Path = candidate.Path,
            DisplayName = candidate.DisplayName,
            GroupName = candidate.GroupName,
            Reason = candidate.Reason,
            ReasonText = candidate.ReasonText,
            Risk = candidate.Risk,
            Category = candidate.Category,
            RequiresAdmin = candidate.RequiresAdmin,
            RequiresConfirmation = candidate.RequiresConfirmation,
            SizeBytes = candidate.SizeBytes,
            FileCount = candidate.FileCount,
            LastWriteTimeUtc = candidate.LastWriteTimeUtc,
            RecommendedRemovalMethod = candidate.RecommendedRemovalMethod,
            IsEnabled = false
        };
    }
}
