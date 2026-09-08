using System.Windows;
using DiskCleaner.Core.Leftovers;

namespace DiskCleaner.Gui.Services;

/// <summary>
/// Реализация диалогов экрана «Остатки» через WPF (<see cref="MessageBox"/> и
/// <see cref="ReportWindow"/>). Владелец — активное окно приложения (или главное), чтобы
/// вложенные диалоги корректно открывались поверх окна «Остатки».
/// </summary>
public sealed class MessageBoxLeftoverDialogService : ILeftoverDialogService
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

    public LeftoverExclusionChoice AskAddExclusion(string path, string brand)
    {
        var owner = ActiveWindow();
        var message =
            $"Добавить «{brand}» в исключения остатков?\n\n" +
            $"Да — по полному пути:\n{path}\n\n" +
            $"Нет — по имени (бренду):\n{brand}\n\n" +
            "Исключённые кандидаты больше не будут предлагаться в плане (FR-3.7). " +
            "Исключения можно просмотреть и удалить в «Исключения…».";

        var result = MessageBox.Show(
            owner,
            message,
            "Добавить в исключения",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return result switch
        {
            MessageBoxResult.Yes => LeftoverExclusionChoice.Path,
            MessageBoxResult.No => LeftoverExclusionChoice.Brand,
            _ => LeftoverExclusionChoice.None
        };
    }

    public void ShowReport(LeftoverCleanReport report) =>
        ReportWindow.Show(
            ActiveWindow(),
            LeftoverReportFormatter.ToMarkdown(report),
            LeftoverReportFormatter.ToJson(report),
            "Отчёт об удалении остатков");

    private static Window? ActiveWindow() =>
        Application.Current.Windows
            .Cast<Window>()
            .FirstOrDefault(w => w.IsActive)
        ?? Application.Current.MainWindow;
}
