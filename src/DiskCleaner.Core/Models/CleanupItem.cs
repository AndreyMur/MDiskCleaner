using DiskCleaner.Core.Processes;

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

    /// <summary>Запись требует ручной проверки (подозрительный издатель/имя), FR-1.10.</summary>
    public string? ReviewReason { get; init; }

    public bool ReviewManually => !string.IsNullOrWhiteSpace(ReviewReason);

    public string? ManagerName { get; init; }

    public string? CleanCommand { get; init; }

    public string? CleanCommandFile { get; init; }

    public string? CleanCommandArgs { get; init; }

    /// <summary>
    /// Максимальное время ожидания штатной команды (секунды) из справочника кэшей
    /// (<c>cleanCommand.timeoutSec</c>). Используется локальным и elevated-исполнителем;
    /// при <c>null</c> применяется значение по умолчанию исполнителя.
    /// </summary>
    public int? CleanCommandTimeoutSec { get; init; }

    public bool RequiresAdmin { get; init; }

    public bool CommandOnly { get; init; }

    public bool AllowDirectDelete { get; init; } = true;

    /// <summary>
    /// «Осиротевший» кэш (FR-2.4): путь по умолчанию существует и содержит данные,
    /// но менеджер теперь использует другой путь (конфиг/команда вернули иной каталог).
    /// Чистится только напрямую — штатная команда менеджера его не знает.
    /// </summary>
    public bool IsOrphan { get; init; }

    /// <summary>Означает «удалить запись реестра» (осиротевшая ветка Uninstall).</summary>
    public string? RegistryDeletePath { get; init; }

    /// <summary>Означает «деинсталлировать ПО» через штатный деинсталлятор (никогда не прямое удаление).</summary>
    public bool UninstallMode { get; init; }

    /// <summary>Удалять содержимое каталога, сохраняя сам каталог (очистка Temp/SoftwareDistribution).</summary>
    public bool DeleteContentsOnly { get; init; }

    /// <summary>Переместить в Корзину вместо безвозвратного удаления (Корзина-режим для пользовательских данных).</summary>
    public bool MoveToRecycleBin { get; init; }

    /// <summary>
    /// Очистить Корзину выбранного диска (FR-5.2): корень диска (например, <c>C:\</c>).
    /// Исполняется штатным API оболочки Windows (<see cref="Cleaning.RecycleBinService.Empty"/>)
    /// — не прямое удаление каталога <c>$Recycle.Bin</c>. <c>null</c> — обычный объект очистки.
    /// </summary>
    public string? EmptyRecycleBinDrive { get; init; }

    /// <summary>Имя службы, которую требуется остановить на время очистки каталога (elevated-шаг ServiceCleanDirectory).</summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// После успешного выполнения штатной команды (exit-код 0) elevated-исполнитель проверяет,
    /// что файл по этому пути исчез (например, <c>hiberfil.sys</c> после <c>powercfg /h off</c>,
    /// FR-5.4). Если файл остался — шаг не считается успешным. <c>null</c> — проверка не выполняется.
    /// </summary>
    public string? VerifyPathAbsent { get; init; }

    public bool IsGroup => string.IsNullOrEmpty(Path) && string.IsNullOrEmpty(RegistryDeletePath) && !CommandOnly;

    public IReadOnlyList<string> OwnerProcessNames { get; init; } = Array.Empty<string>();

    public long? SizeBytes { get; set; }

    public long? FileCount { get; set; }

    public bool InUse { get; set; }

    /// <summary>
    /// Процессы, из-за которых объект помечен <c>IN_USE</c> и не удаляется (FR-5.7):
    /// их ExecutablePath находится внутри удаляемого пути либо имя совпадает с
    /// <see cref="OwnerProcessNames"/>. Заполняет <see cref="Processes.InUseDetector"/>;
    /// список возвращается исполнителем для показа пользователю.
    /// </summary>
    public IReadOnlyList<RunningProcessInfo> BlockingProcesses { get; set; } = Array.Empty<RunningProcessInfo>();

    /// <summary>
    /// Рекомендуемая стратегия для занятого объекта (FR-5.8): закрыть «долгоживущее»
    /// приложение и повторить либо отложить шаг. <c>null</c> — объект не помечен IN_USE.
    /// </summary>
    public InUseAdvice? InUseAdvice { get; set; }

    public bool IsExpanded { get; set; }

    public IReadOnlyList<CleanupItem> Children { get; init; } = Array.Empty<CleanupItem>();

    public long EffectiveSizeBytes =>
        SizeBytes ?? (Children.Count > 0 ? Children.Sum(c => c.EffectiveSizeBytes) : 0);

    public long EffectiveFileCount =>
        FileCount ?? (Children.Count > 0 ? Children.Sum(c => c.EffectiveFileCount) : 0);
}
