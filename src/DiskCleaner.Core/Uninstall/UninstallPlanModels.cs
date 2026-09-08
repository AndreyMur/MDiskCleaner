namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Пометка шага плана деинсталляции (FR-4.2/4.3): «дубль» (есть несколько установок одного
/// семейства), «старая версия» (рекомендуется удалить, оставить самую свежую) и
/// «проверить вручную» (аномалия — пустой/подозрительный Publisher, подозрительное имя,
/// InstallDate = дате первой загрузки Windows). Один объект может нести несколько пометок
/// (старая версия одновременно является дублем).
/// </summary>
public enum UninstallPlanMarkKind
{
    Duplicate,
    OldVersion,
    ReviewManually
}

/// <summary>
/// Шаг плана деинсталляции — одна запись установленного ПО с пометками-рекомендациями для
/// пообъектного подтверждения (FR-4.2, FR-4.11). Объекты <b>никогда не предвыбираются</b>:
/// <see cref="IsEnabled"/> по умолчанию false, пользователь явно включает (подтверждает)
/// шаги, которые согласен выполнить. Размер «занимает на C:» (фактический размер каталога
/// InstallLocation, когда он расположен на диске C:) показывается отдельно от общего размера
/// записи реестра (FR-4.4).
/// </summary>
public sealed class UninstallPlanItem
{
    public const string ReviewManuallyGroupName = "Проверить вручную";

    /// <summary>Стабильный ключ: <c>app:{ScopeKey}:{ProductCode}</c>.</summary>
    public required string Key { get; init; }

    /// <summary>ProductCode записи (подключ ветки Uninstall). Нужен для исполнения шага.</summary>
    public required string ProductCode { get; init; }

    /// <summary>Ветка реестра (<see cref="InstalledAppScope"/>). Нужна для исполнения шага.</summary>
    public required string ScopeKey { get; init; }

    public required string DisplayName { get; init; }

    public string? Publisher { get; init; }

    public string? DisplayVersion { get; init; }

    /// <summary>Дата установки в формате yyyy-MM-dd (как в интерфейсе).</summary>
    public string? InstallDateText { get; init; }

    /// <summary>Группа плана. Аномалии собираются в «Проверить вручную» (FR-4.3); прочие — по издателю.</summary>
    public required string GroupName { get; init; }

    /// <summary>Путь InstallLocation, если каталог существует (иначе null).</summary>
    public string? Path { get; init; }

    /// <summary>Общий размер записи (EstimatedSize из реестра) — «размер записи», FR-4.4.</summary>
    public long EstimatedSizeBytes { get; init; }

    /// <summary>Фактический размер каталога InstallLocation, когда он расположен на диске C: (FR-4.4).</summary>
    public long? SizeOnCDriveBytes { get; init; }

    public bool IsOnCDrive => SizeOnCDriveBytes.HasValue;

    public bool RequiresAdmin { get; init; }

    /// <summary>Пометки шага: дубль / старая версия / проверить вручную (FR-4.2, FR-4.3).</summary>
    public IReadOnlyList<UninstallPlanMarkKind> Marks { get; init; } = Array.Empty<UninstallPlanMarkKind>();

    public bool IsDuplicate => Marks.Contains(UninstallPlanMarkKind.Duplicate);

    public bool IsOldVersion => Marks.Contains(UninstallPlanMarkKind.OldVersion);

    public bool NeedsReview => Marks.Contains(UninstallPlanMarkKind.ReviewManually);

    /// <summary>Рекомендуется к удалению: старая версия или аномалия «проверить вручную» — но без предвыбора.</summary>
    public bool IsRecommended => IsOldVersion || NeedsReview;

    /// <summary>Человекочитаемое пояснение/рекомендация (версии и даты установки для дублей, FR-4.2).</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Названия ещё установленных продуктов, на которые может повлиять удаление этого шага —
    /// статический список известных зависимостей (VS Build Tools 2019 → SDK 19041, §7, FR-4.11).
    /// </summary>
    public IReadOnlyList<string> DependencyImpactNames { get; init; } = Array.Empty<string>();

    /// <summary>Есть ли предупреждение «удаление может затронуть X» (§7).</summary>
    public bool HasDependencyImpacts => DependencyImpactNames.Count > 0;

    /// <summary>Текст предупреждения о зависимостях для подтверждения шага (§7, FR-4.11).</summary>
    public string? DependencyImpactText => HasDependencyImpacts
        ? $"Удаление может затронуть: {string.Join(", ", DependencyImpactNames)}."
        : null;

    /// <summary>Включён ли шаг пользователем. По умолчанию false — никогда не предвыбор (FR-4.2).</summary>
    public bool IsEnabled { get; set; }
}

/// <summary>
/// План деинсталляции (FR-4.2, FR-4.11): шаги пообъектного подтверждения по всем записям
/// установленного ПО. Шаги с пометками «старая версия»/«проверить вручную» являются
/// рекомендациями, но остаются выключенными по умолчанию — пользователь сам отмечает объекты.
/// </summary>
public sealed class UninstallPlan
{
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    public IReadOnlyList<UninstallPlanItem> Items { get; init; } = Array.Empty<UninstallPlanItem>();

    public IReadOnlyList<UninstallPlanItem> EnabledItems => Items.Where(i => i.IsEnabled).ToList();

    public int EnabledCount => Items.Count(i => i.IsEnabled);

    /// <summary>Шаги-рекомендации (старые версии и «проверить вручную») без предвыбора.</summary>
    public IReadOnlyList<UninstallPlanItem> RecommendedItems => Items.Where(i => i.IsRecommended).ToList();

    public int RecommendedCount => RecommendedItems.Count;

    /// <summary>Шаги, собранные в группу «Проверить вручную» (FR-4.3).</summary>
    public IReadOnlyList<UninstallPlanItem> ReviewManuallyItems => Items.Where(i => i.NeedsReview).ToList();

    public int ReviewManuallyCount => ReviewManuallyItems.Count;

    public long TotalEstimatedBytes => Items.Sum(i => Math.Max(0, i.EstimatedSizeBytes));
}
