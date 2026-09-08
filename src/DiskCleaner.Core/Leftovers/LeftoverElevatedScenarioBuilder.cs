using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// Формирует elevated-сценарий (FR-3.5, PRD 05) для подтверждённых остатков, требующих
/// прав администратора (Program Files / ProgramData / Windows.old): каждый кандидат —
/// шаг <see cref="ElevatedStepKind.DeletePath"/>. Все шаги выполняются одним elevated-процессом
/// (один UAC-подъём) и сопоставляются с результатами по <see cref="ElevatedStep.Id"/> = пути.
/// </summary>
public static class LeftoverElevatedScenarioBuilder
{
    public static ElevatedScenario Build(IReadOnlyList<LeftoverPlanItem> items)
    {
        var steps = items
            .Select(item => new ElevatedStep
            {
                Id = item.Key,
                Kind = ElevatedStepKind.DeletePath,
                Path = item.Path,
                Target = CleanupTarget.Directory
            })
            .ToList();

        return new ElevatedScenario { Steps = steps };
    }
}
