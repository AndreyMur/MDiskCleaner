using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Cleaning;

/// <summary>
/// Категорийный порядок исполнения плана (FR-5.17): «кэши → остатки → ПО → системные шаги».
/// Категории <see cref="CleanupCategory"/> сводятся к четырём последовательным фазам
/// (внутри фазы сохраняется порядок следования объектов плана).
/// </summary>
public enum CleanPlanPhase
{
    /// <summary>Кэши пакетных менеджеров и тулчейны (Cache, DevToolchain, Temp-пользователя не трогаем).</summary>
    Caches,

    /// <summary>Остатки удалённых программ и осиротевшие записи реестра (Leftover).</summary>
    Leftovers,

    /// <summary>Установленные программы / деинсталляция (InstalledApp).</summary>
    Software,

    /// <summary>Системные шаги: Temp, Корзина, гибернация, Windows.old и пр. (SystemFile/Temp/RecycleBin/UserData).</summary>
    SystemSteps
}

public static class CleanPlanPhases
{
    /// <summary>Категория → фаза плана. Категории за пределами списка (Other) уходят в «системные шаги».</summary>
    public static CleanPlanPhase PhaseOf(CleanupCategory category) => category switch
    {
        CleanupCategory.Cache or CleanupCategory.DevToolchain => CleanPlanPhase.Caches,
        CleanupCategory.Leftover => CleanPlanPhase.Leftovers,
        CleanupCategory.InstalledApp => CleanPlanPhase.Software,
        _ => CleanPlanPhase.SystemSteps
    };

    /// <summary>Фазы в порядке исполнения плана (FR-5.17).</summary>
    public static IReadOnlyList<CleanPlanPhase> Ordered { get; } =
        [CleanPlanPhase.Caches, CleanPlanPhase.Leftovers, CleanPlanPhase.Software, CleanPlanPhase.SystemSteps];

    public static string DisplayName(CleanPlanPhase phase) => phase switch
    {
        CleanPlanPhase.Caches => "Кэши",
        CleanPlanPhase.Leftovers => "Остатки удалённых программ",
        CleanPlanPhase.Software => "Программы (деинсталляция)",
        CleanPlanPhase.SystemSteps => "Системные шаги",
        _ => "Системные шаги"
    };

    /// <summary>
    /// Раскладывает листья плана по фазам в порядке FR-5.17. Внутри фазы сохраняется
    /// исходный порядок объектов. Групповые узлы (IsGroup) разворачиваются в листья.
    /// </summary>
    public static IReadOnlyList<CleanPlanPhaseGroup> GroupByPhase(IEnumerable<CleanupItem> planItems)
    {
        var leaves = AnalysisService.DescendantLeaves(planItems)
            .DistinctBy(i => i.Key)
            .ToList();

        return Ordered
            .Select(phase => new CleanPlanPhaseGroup(
                phase,
                leaves.Where(i => PhaseOf(i.Category) == phase).ToList()))
            .Where(g => g.Items.Count > 0)
            .ToList();
    }
}

/// <summary>Одна фаза плана с её листьями (порядок объектов внутри фазы сохраняется).</summary>
public sealed record CleanPlanPhaseGroup(CleanPlanPhase Phase, IReadOnlyList<CleanupItem> Items)
{
    public string PhaseText => CleanPlanPhases.DisplayName(Phase);

    public long EstimatedBytes => Items.Sum(i => Math.Max(0, i.EffectiveSizeBytes));
}

/// <summary>
/// «Опасный шаг» плана (FR-5.17): риск выше низкого либо объект, который удаляется безвозвратно
/// и/или меняет системное состояние (гибернация, Windows.old, Корзина-режим). Для таких шагов
/// движок запрашивает подтверждение перед исполнением; не подтверждённый шаг пропускается
/// с исходом <see cref="CleanOutcome.NotConfirmed"/>.
/// </summary>
public static class DangerousPlanStep
{
    public static bool IsDangerous(CleanupItem item) =>
        item.Risk != CleanupRisk.Low ||
        item.RequiresAdmin ||
        item.Category is CleanupCategory.SystemFile or CleanupCategory.RecycleBin or CleanupCategory.UserData;
}
