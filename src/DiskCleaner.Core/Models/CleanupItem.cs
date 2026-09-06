namespace DiskCleaner.Core.Models;

public sealed class CleanupItem
{
    public required string Key { get; init; }

    public string? Path { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string? GroupName { get; init; }

    public CleanupCategory Category { get; init; } = CleanupCategory.Cache;

    public CleanupRisk Risk { get; init; } = CleanupRisk.Low;

    public CleanupTarget Target { get; init; } = CleanupTarget.Directory;

    public string? Description { get; init; }

    public string? Warning { get; init; }

    public string? ManagerName { get; init; }

    public string? CleanCommand { get; init; }

    public string? CleanCommandFile { get; init; }

    public string? CleanCommandArgs { get; init; }

    public bool RequiresAdmin { get; init; }

    public bool CommandOnly { get; init; }

    public bool AllowDirectDelete { get; init; } = true;

    /// <summary>Означает «удалить запись реестра» (осиротевшая ветка Uninstall).</summary>
    public string? RegistryDeletePath { get; init; }

    /// <summary>Означает «деинсталлировать ПО» через штатный деинсталлятор (никогда не прямое удаление).</summary>
    public bool UninstallMode { get; init; }

    /// <summary>Удалять содержимое каталога, сохраняя сам каталог (очистка Temp/SoftwareDistribution).</summary>
    public bool DeleteContentsOnly { get; init; }

    /// <summary>Переместить в Корзину вместо безвозвратного удаления (Корзина-режим для пользовательских данных).</summary>
    public bool MoveToRecycleBin { get; init; }

    /// <summary>Имя службы, которую требуется остановить на время очистки каталога (elevated-шаг ServiceCleanDirectory).</summary>
    public string? ServiceName { get; init; }

    public bool IsGroup => string.IsNullOrEmpty(Path) && string.IsNullOrEmpty(RegistryDeletePath) && !CommandOnly;

    public IReadOnlyList<string> OwnerProcessNames { get; init; } = Array.Empty<string>();

    public long? SizeBytes { get; set; }

    public long? FileCount { get; set; }

    public bool InUse { get; set; }

    public bool IsExpanded { get; set; }

    public IReadOnlyList<CleanupItem> Children { get; init; } = Array.Empty<CleanupItem>();

    public long EffectiveSizeBytes =>
        SizeBytes ?? (Children.Count > 0 ? Children.Sum(c => c.EffectiveSizeBytes) : 0);

    public long EffectiveFileCount =>
        FileCount ?? (Children.Count > 0 ? Children.Sum(c => c.EffectiveFileCount) : 0);
}
