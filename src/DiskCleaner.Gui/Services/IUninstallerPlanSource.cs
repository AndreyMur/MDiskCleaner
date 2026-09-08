using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Gui.Services;

/// <summary>
/// Снимок данных экрана «Деинсталляция и зачистка» (модуль 04, FR-4.1–4.13):
/// все записи реестра Uninstall, план деинсталляции с пометками-рекомендациями и
/// осиротевшие записи Uninstall. Снимок читается из реестра один раз.
/// </summary>
public sealed class UninstallerSnapshot
{
    /// <summary>Все записи установленного ПО (нужны для исполнения шагов плана).</summary>
    public required IReadOnlyList<InstalledApp> Apps { get; init; }

    /// <summary>План деинсталляции: шаги с размерами, путями и пометками (FR-4.1–4.4).</summary>
    public required UninstallPlan Plan { get; init; }

    /// <summary>Осиротевшие записи Uninstall — отдельная группа «Осиротевшие записи реестра» (FR-4.13).</summary>
    public required IReadOnlyList<UninstallOrphanRegistryMatch> Orphans { get; init; }
}

/// <summary>
/// Источник данных экрана «Деинсталляция и зачистка»: читает реестр Uninstall один раз и
/// строит план деинсталляции (<see cref="UninstallPlanService"/>), список осиротевших записей
/// (<see cref="UninstallOrphanRegistryScanner"/>) и версии Windows SDK
/// (<see cref="WindowsSdkFamily"/>). Абстракция позволяет unit-тестам ViewModel подставлять
/// синтетический снимок без обращения к реестру и диску.
/// </summary>
public interface IUninstallerPlanSource
{
    /// <summary>Строит снимок установленного ПО, плана и осиротевших записей (FR-4.1, FR-4.13).</summary>
    UninstallerSnapshot Build();
}

/// <summary>Адаптер поверх <see cref="UninstallRegistryService"/>, <see cref="UninstallPlanService"/> и сканера осиротевших записей.</summary>
public sealed class UninstallerPlanSource : IUninstallerPlanSource
{
    private readonly UninstallRegistryService _registry;
    private readonly UninstallPlanService _planService;
    private readonly UninstallOrphanRegistryScanner _orphanScanner;

    public UninstallerPlanSource(
        UninstallRegistryService? registry = null,
        UninstallPlanService? planService = null,
        UninstallOrphanRegistryScanner? orphanScanner = null)
    {
        _registry = registry ?? new UninstallRegistryService();
        _planService = planService ?? new UninstallPlanService(_registry);
        _orphanScanner = orphanScanner ?? new UninstallOrphanRegistryScanner();
    }

    public UninstallerSnapshot Build()
    {
        var apps = _registry.ReadInstalledApps();
        var plan = _planService.BuildPlan(apps);
        var orphans = _orphanScanner.Find(apps);
        return new UninstallerSnapshot
        {
            Apps = apps,
            Plan = plan,
            Orphans = orphans
        };
    }
}
