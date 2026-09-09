using System.Text;
using DiskCleaner.Core.Caches;

namespace DiskCleaner.Core.Reports;

public static class CleanReportFormatter
{
    public static string ToMarkdown(CleanReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Отчёт DiskCleaner");
        builder.AppendLine();
        builder.AppendLine(report.DryRun ? "Режим: **предпросмотр (dry-run)** — ничего не удалено" : "Режим: выполнение плана");
        builder.AppendLine($"Освобождено: **{FormatBytes(report.TotalFreedBytes)}**");
        builder.AppendLine($"Затрачено: {report.Elapsed.TotalSeconds:F1} с");
        builder.AppendLine($"Объектов в плане: {report.Entries.Count}; с ошибками: {report.FailedItems}; частично: {report.PartialItems}; отложено (используется): {report.DeferredItems}; требует админа: {report.RequiresAdminItems}");
        builder.AppendLine();
        builder.AppendLine("| Объект | Результат | Освобождено | Примечание |");
        builder.AppendLine("|---|---|---|---|");

        foreach (var entry in report.Entries)
        {
            var item = entry.Item;
            var name = (item.GroupName is null ? string.Empty : item.GroupName + " / ") + item.DisplayName;
            builder.Append("| ")
                .Append(EscapeCell(name)).Append(" | ")
                .Append(EscapeCell(OutcomeText(entry.Outcome))).Append(" | ")
                .Append(FormatBytes(entry.FreedBytes)).Append(" | ")
                .Append(EscapeCell(entry.Note ?? string.Empty)).AppendLine(" |");
        }

        return builder.ToString();
    }

    public static string ToJson(CleanReport report) =>
        ReportJson.Serialize(ReportDocumentBuilder.FromCleanReport(report));

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "-";
        }

        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.##} {units[unit]}";
    }

    public static string OutcomeText(CleanOutcome outcome) => outcome switch
    {
        CleanOutcome.DryRun => "предпросмотр",
        CleanOutcome.InUseSkipped => "отложено (используется)",
        CleanOutcome.NativeCleaned => "очищено штатной командой",
        CleanOutcome.DirectDeleted => "удалено",
        CleanOutcome.CommandOnlyCleaned => "команда выполнена",
        CleanOutcome.Partial => "частично",
        CleanOutcome.MovedToRecycleBin => "перемещено в Корзину",
        CleanOutcome.Denied => "запрещено (deny-список)",
        CleanOutcome.Uninstalled => "деинсталлировано",
        CleanOutcome.RegistryEntryDeleted => "запись реестра удалена",
        CleanOutcome.RebootRequired => "требуется перезагрузка",
        CleanOutcome.AlreadyUninstalled => "уже не установлено",
        CleanOutcome.ElevationDeclined => "отменено пользователем (UAC)",
        CleanOutcome.RequiresAdmin => "требует админа (Access Denied)",
        CleanOutcome.NotConfirmed => "не подтверждено пользователем",
        _ => "ошибка"
    };

    private static string EscapeCell(string value) =>
        value.Replace("|", "\\|").Replace(System.Environment.NewLine, " ");
}
