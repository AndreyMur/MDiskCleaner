using System.Text;
using System.Windows;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Localization;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Gui.Services;

/// <summary>
/// Реализация диалогов выполнения полного плана через WPF (<see cref="MessageBox"/> и
/// <see cref="ReportWindow"/>). Подтверждение каждого опасного шага показывает последствия
/// и инструкцию «требует админа»; итоговый отчёт открывается как Markdown/JSON в
/// <see cref="ReportWindow"/> с сохранением в файл (FR-5.14/5.16).
/// </summary>
public sealed class MessageBoxPlanRunDialogService : IPlanRunDialogService
{
    public Task<bool> ConfirmDangerousStepAsync(CleanupItem item)
    {
        var owner = ActiveWindow();
        var result = MessageBox.Show(
            owner,
            BuildMessage(item),
            "Подтверждение опасного шага",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    public void ShowRunReport(CleanPlanRunReport report)
    {
        var title = report.DryRun
            ? "Предпросмотр плана (dry-run)"
            : report.Canceled
                ? "Выполнение плана прервано"
                : "Отчёт о выполнении плана";

        ReportWindow.Show(
            ActiveWindow(),
            CleanPlanRunFormatter.ToMarkdown(report),
            CleanPlanRunJson.Serialize(report),
            title);
    }

    private static string BuildMessage(CleanupItem item)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Выполнить опасный шаг «{Title(item)}»?");
        builder.AppendLine(
            $"Категория: {LocalizedNames.Category(item.Category)} · риск: {LocalizedNames.Risk(item.Risk)} · " +
            $"размер: {CleanReportFormatter.FormatBytes(item.EffectiveSizeBytes)}");

        if (item.RequiresAdmin)
        {
            builder.AppendLine();
            builder.AppendLine("Инструкция: шаг требует прав администратора (UAC) — будет выполнен в повышенном процессе.");
        }

        var detail = string.IsNullOrWhiteSpace(item.Warning) ? item.Description : item.Warning;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            builder.AppendLine();
            builder.AppendLine(detail);
        }

        builder.AppendLine();
        builder.AppendLine("Не подтверждённый шаг будет пропущен, выполнение плана продолжится (FR-5.17).");
        return builder.ToString();
    }

    private static string Title(CleanupItem item) =>
        string.IsNullOrEmpty(item.GroupName)
            ? item.DisplayName
            : $"{item.GroupName} — {item.DisplayName}";

    private static Window? ActiveWindow() =>
        Application.Current.Windows
            .Cast<Window>()
            .FirstOrDefault(w => w.IsActive)
        ?? Application.Current.MainWindow;
}
