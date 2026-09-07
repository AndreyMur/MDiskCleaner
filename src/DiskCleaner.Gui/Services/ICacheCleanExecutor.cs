using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Gui.Services;

/// <summary>
/// Исполнитель очистки для экрана «Очистка кэшей»: запускает выбранные объекты через ядро
/// (локальная очистка / штатные команды / прямое удаление) с прогрессом и отменой. Абстракция
/// позволяет unit-тестам ViewModel подменять исполнение синтетическим отчётом.
/// </summary>
public interface ICacheCleanExecutor
{
    Task<CleanReport> CleanAsync(
        IEnumerable<CleanupItem> selectedItems,
        CleanOptions? options = null,
        IProgress<CleanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
