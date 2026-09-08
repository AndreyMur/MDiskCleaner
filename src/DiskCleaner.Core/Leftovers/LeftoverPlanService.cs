using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// Собирает план очистки остатков (FR-3.7, FR-3.8): запускает скан
/// (<see cref="LeftoverCandidateScanner"/>) с исключениями пользователя из
/// <c>exclusions.json</c> и превращает найденные кандидаты в «карточки» плана,
/// выключенные по умолчанию. Кандидаты, подпадающие под исключение (по пути или по
/// бренду), в план не попадают.
/// </summary>
public sealed class LeftoverPlanService
{
    private readonly LeftoverCandidateScanner _scanner;
    private readonly ExclusionsStore _exclusionsStore;
    private readonly UninstallRegistryService? _registry;

    public LeftoverPlanService(
        LeftoverCandidateScanner? scanner = null,
        ExclusionsStore? exclusionsStore = null,
        UninstallRegistryService? registry = null)
    {
        _scanner = scanner ?? new LeftoverCandidateScanner();
        _exclusionsStore = exclusionsStore ?? new ExclusionsStore();
        _registry = registry;
    }

    /// <summary>
    /// Строит план из приложений, прочитанных из реестра Uninstall. Требует наличия
    /// <see cref="UninstallRegistryService"/> (по умолчанию создаётся).
    /// </summary>
    public LeftoverPlan BuildPlan()
    {
        var registry = _registry ?? new UninstallRegistryService();
        return BuildPlan(registry.ReadInstalledApps());
    }

    /// <summary>Строит план из переданного белого списка установленного ПО.</summary>
    public LeftoverPlan BuildPlan(IReadOnlyList<InstalledApp> installedApps)
    {
        var exclusions = _exclusionsStore.Load();
        var candidates = _scanner.Scan(installedApps, exclusions);

        var items = candidates
            .Where(candidate => !ExclusionsStore.IsExcluded(
                exclusions,
                candidate.Path,
                Path.GetFileName(candidate.Path)))
            .Select(LeftoverPlanItem.From)
            .ToList();

        return new LeftoverPlan { Items = items };
    }
}
