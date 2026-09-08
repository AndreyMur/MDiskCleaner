using DiskCleaner.Core.Leftovers;

namespace DiskCleaner.Gui.Services;

/// <summary>
/// Исполнитель удаления остатков для экрана «Остатки»: запускает отмеченные объекты через ядро
/// (<see cref="LeftoverCleanService"/>) — dry-run-предпросмотр, локальные удаления, удаление
/// Program Files/ProgramData/Windows.old через elevated-процесс с одним UAC-подъёмом и журнал
/// с основанием удаления (FR-3.5–3.8, §5). Абстракция позволяет unit-тестам ViewModel подменять
/// исполнение синтетическим отчётом.
/// </summary>
public interface ILeftoverCleanExecutor
{
    Task<LeftoverCleanReport> CleanAsync(
        IReadOnlyList<LeftoverPlanItem> selectedItems,
        LeftoverCleanOptions? options = null,
        CancellationToken cancellationToken = default);
}
