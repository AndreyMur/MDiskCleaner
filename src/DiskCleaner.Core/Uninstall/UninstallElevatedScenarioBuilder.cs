using DiskCleaner.Core.Elevated;

namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Формирует elevated-сценарий (FR-4.15, PRD 05) для пачки удалений, требующих прав
/// администратора (записи LocalMachine): каждый объект — шаг
/// <see cref="ElevatedStepKind.RunProcess"/> с командой из <see cref="UninstallCommand"/>.
/// Все шаги выполняются одним elevated-процессом (одна UAC-проверка на пачку) и
/// сопоставляются с результатами по <see cref="ElevatedStep.Id"/> = ключу шага.
/// Для MSI-команд действует политика exit-кодов <see cref="ExitCodePolicy.Msiexec"/>
/// (0/3010 — успех, 1605/1612 — «не установлено», не ошибка).
/// </summary>
public static class UninstallElevatedScenarioBuilder
{
    public static ElevatedScenario Build(
        IReadOnlyList<UninstallExecutionItem> items,
        int timeoutSec = 600)
    {
        var steps = items
            .Where(item => item.Command is not null)
            .Select(item => new ElevatedStep
            {
                Id = item.Key,
                Kind = ElevatedStepKind.RunProcess,
                FileName = item.Command!.FileName,
                Arguments = item.Command.Arguments,
                TimeoutSec = timeoutSec,
                ExitCodes = item.IsMsiexec ? ExitCodePolicy.Msiexec : ExitCodePolicy.Generic
            })
            .ToList();

        return new ElevatedScenario { Steps = steps };
    }
}
