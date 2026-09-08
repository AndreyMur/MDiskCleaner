using DiskCleaner.Core.Leftovers;

namespace DiskCleaner.Gui.Services;

/// <summary>
/// Источник плана очистки остатков для экрана «Остатки» (FR-3.2–3.8): строит план из
/// реестра Uninstall и сканирования верхних уровней каталогов через ядро
/// (<see cref="LeftoverPlanService"/>). Абстракция позволяет unit-тестам ViewModel
/// подставлять синтетический план без обращения к реальному реестру и диску.
/// </summary>
public interface ILeftoverPlanSource
{
    /// <summary>Строит план остатков (кандидаты выключены по умолчанию, исключения учтены).</summary>
    LeftoverPlan BuildPlan();
}
