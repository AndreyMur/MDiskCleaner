using System.Windows;
using DiskCleaner.Core.Caches;

namespace DiskCleaner.Gui.Services;

/// <summary>
/// Реализация диалогов через WPF (<see cref="MessageBox"/> и <see cref="ReportWindow"/>).
/// Владелец — активное окно приложения (или главное), чтобы вложенные диалоги корректно
/// открывались поверх окна «Очистка кэшей».
/// </summary>
public sealed class MessageBoxCacheCleanDialogService : ICacheCleanDialogService
{
    public bool Confirm(string title, string message)
    {
        var owner = ActiveWindow();
        return MessageBox.Show(
            owner,
            message,
            title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    public void ShowReport(CleanReport report) => ReportWindow.Show(ActiveWindow(), report);

    private static Window? ActiveWindow() =>
        Application.Current.Windows
            .Cast<Window>()
            .FirstOrDefault(w => w.IsActive)
        ?? Application.Current.MainWindow;
}
