using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Gui.Services;

/// <summary>
/// Исполнитель полного плана для главного экрана (FR-5.17–5.19): запускает выбранные объекты
/// через ядро <see cref="Core.Cleaning.CleanPlanRunner"/> по фазам «кэши → остатки → ПО →
/// системные шаги» с подтверждением опасных шагов, отменой между шагами, снимком свободного
/// места и итоговым отчётом. Абстракция позволяет unit-тестам ViewModel подменять исполнение
/// синтетическим отчётом без реальных операций очистки.
/// </summary>
public interface IPlanRunExecutor
{
    Task<CleanPlanRunReport> RunAsync(
        IEnumerable<CleanupItem> planItems,
        CleanPlanRunOptions? options = null,
        IProgress<CleanPlanRunProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
