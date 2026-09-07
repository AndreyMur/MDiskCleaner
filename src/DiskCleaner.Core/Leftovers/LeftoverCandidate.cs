using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Leftovers;

public enum LeftoverReason
{
    UpdaterFolder,
    ConfigOfRemovedApp,
    OrphanProgramFiles,
    OrphanProgramData,
    OrphanRegistryEntry,
    WindowsOld,
    NearEmptyDirectory,

    /// <summary>Каталог верхнего уровня не сопоставлен ни с одной записью Uninstall (FR-3.1).</summary>
    NotInUninstallRegistry
}

public sealed class LeftoverCandidate
{
    public required string Path { get; init; }

    public required string DisplayName { get; init; }

    public required string GroupName { get; init; }

    public required LeftoverReason Reason { get; init; }

    public string ReasonText { get; init; } = string.Empty;

    public bool RequiresAdmin { get; init; }

    /// <summary>
    /// Обязательное подтверждение пользователя перед удалением (FR-3.4, §5): проставляется
    /// для «опасных» объектов — осиротевших папок Program Files/ProgramData и Windows.old.
    /// </summary>
    public bool RequiresConfirmation { get; init; }

    public CleanupRisk Risk { get; init; } = CleanupRisk.Medium;

    public CleanupCategory Category { get; init; } = CleanupCategory.Leftover;

    /// <summary>Размер каталога (с рекурсивным обходом) для объектов группы «Остатки апдейтеров» (FR-3.2).</summary>
    public long? SizeBytes { get; init; }

    public long? FileCount { get; init; }

    /// <summary>Дата последнего изменения каталога (FR-3.2).</summary>
    public DateTime? LastWriteTimeUtc { get; init; }

    /// <summary>
    /// Рекомендуемый способ удаления (например, для Windows.old: Storage Sense /
    /// <c>cleanmgr</c> / DISM, FR-3.5). Пусто для обычных остатков, удаляемых напрямую.
    /// </summary>
    public string? RecommendedRemovalMethod { get; init; }

    public string? RegistryDeletePath { get; init; }
}
