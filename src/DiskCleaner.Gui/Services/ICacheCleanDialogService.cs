using DiskCleaner.Core.Caches;

namespace DiskCleaner.Gui.Services;

/// <summary>
/// Пользовательские диалоги экрана «Очистка кэшей»: подтверждение перед выполнением (NFR G3)
/// и показ отчёта. Абстракция позволяет unit-тестам ViewModel подменять MessageBox/окна.
/// </summary>
public interface ICacheCleanDialogService
{
    /// <summary>Запрос подтверждения; <c>true</c> — пользователь согласен выполнить.</summary>
    bool Confirm(string title, string message);

    /// <summary>Показать отчёт (dry-run-предпросмотр или результат выполнения).</summary>
    void ShowReport(CleanReport report);
}
