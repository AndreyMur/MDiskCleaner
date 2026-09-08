using System.Windows;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Reports;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Gui.Services;

/// <summary>
/// Реализация диалогов экрана «Деинсталляция и зачистка» через WPF (<see cref="MessageBox"/> и
/// <see cref="ReportWindow"/>). Владелец — активное окно приложения (или главное).
/// Перезагрузка (FR-4.12) никогда не выполняется принудительно — только запрашивается согласие.
/// </summary>
public sealed class MessageBoxUninstallerDialogService : IUninstallerDialogService
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

    public bool ConfirmStep(UninstallPlanItem item)
    {
        var owner = ActiveWindow();
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"Удалить «{item.DisplayName}»?");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(item.Publisher) || !string.IsNullOrWhiteSpace(item.DisplayVersion))
        {
            builder.AppendLine(
                $"{item.Publisher ?? "Без издателя"}{(string.IsNullOrWhiteSpace(item.DisplayVersion) ? string.Empty : " · версия " + item.DisplayVersion)}");
        }

        builder.AppendLine($"Объём записи: {CleanReportFormatter.FormatBytes(item.EstimatedSizeBytes)}");
        if (item.IsOnCDrive)
        {
            builder.AppendLine($"Занимает на C:: {CleanReportFormatter.FormatBytes(item.SizeOnCDriveBytes ?? 0)}");
        }

        if (!string.IsNullOrWhiteSpace(item.InstallDateText))
        {
            builder.AppendLine($"Установлено: {item.InstallDateText}");
        }

        if (!string.IsNullOrWhiteSpace(item.Path))
        {
            builder.AppendLine($"Путь: {item.Path}");
        }

        builder.AppendLine();

        if (item.IsRecommended)
        {
            builder.AppendLine("Помечено как рекомендуемое к удалению (без предвыбора): " + (item.Note ?? string.Empty));
        }

        if (item.HasDependencyImpacts)
        {
            builder.AppendLine();
            builder.AppendLine("Предупреждение: " + item.DependencyImpactText);
        }

        builder.AppendLine();
        builder.AppendLine("Каждый шаг деинсталляции выполняется только после отдельного подтверждения (FR-4.11).");
        builder.AppendLine();
        builder.AppendLine("Если откроется мастер удаления деинсталлятора с интерфейсом (например, winsdksetup.exe) — " +
                           "завершите его вручную; программа будет ожидать завершения мастера с таймаутом (FR-4.11, §7).");

        return MessageBox.Show(
            owner,
            builder.ToString(),
            "Подтверждение удаления",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    public UninstallerRebootChoice AskReboot(UninstallRebootRequest request)
    {
        if (!request.Recommended)
        {
            return UninstallerRebootChoice.None;
        }

        var owner = ActiveWindow();
        var result = MessageBox.Show(
            owner,
            request.PromptMessage,
            "Требуется перезагрузка",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return result switch
        {
            MessageBoxResult.Yes => UninstallerRebootChoice.Now,
            MessageBoxResult.No => UninstallerRebootChoice.Later,
            _ => UninstallerRebootChoice.None
        };
    }

    public void ShowReport(UninstallExecutionReport report) =>
        ReportWindow.Show(
            ActiveWindow(),
            UninstallerReportFormatter.ToMarkdown(report),
            UninstallerReportFormatter.ToJson(report),
            "Отчёт о деинсталляции");

    public void ShowSdkReport(SdkBulkUninstallReport report) =>
        ReportWindow.Show(
            ActiveWindow(),
            UninstallerReportFormatter.ToMarkdown(report),
            UninstallerReportFormatter.ToJson(report),
            "Массовое удаление Windows SDK");

    public void ShowCleanReport(CleanReport report) => ReportWindow.Show(ActiveWindow(), report);

    private static Window? ActiveWindow() =>
        Application.Current.Windows
            .Cast<Window>()
            .FirstOrDefault(w => w.IsActive)
        ?? Application.Current.MainWindow;
}
