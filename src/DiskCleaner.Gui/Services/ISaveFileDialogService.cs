namespace DiskCleaner.Gui.Services;

/// <summary>
/// Диалог сохранения файла (FR-1.12). Вынесен в интерфейс, чтобы ViewModel оставался
/// тестируемым без модального окна: тест подставляет реализацию с фиксированным путём.
/// </summary>
public interface ISaveFileDialogService
{
    /// <summary>Показать диалог сохранения; <c>null</c> — пользователь отменил.</summary>
    string? Show(string title, string defaultFileName, string defaultExtension, string filter);
}
