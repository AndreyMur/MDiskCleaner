using DiskCleaner.Core.Leftovers;

namespace DiskCleaner.Gui.Services;

/// <summary>Адаптер поверх <see cref="LeftoverPlanService"/> для экрана «Остатки».</summary>
public sealed class LeftoverPlanSource : ILeftoverPlanSource
{
    private readonly LeftoverPlanService _service;

    public LeftoverPlanSource(LeftoverPlanService? service = null)
    {
        _service = service ?? new LeftoverPlanService();
    }

    public LeftoverPlan BuildPlan() => _service.BuildPlan();
}
