using System.Text;
using System.Text.Json;
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
        builder.AppendLine($"Объектов в плане: {report.Entries.Count}; с ошибками/частично: {report.FailedItems}; отложено (используется): {report.DeferredItems}");
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
        JsonSerializer.Serialize(
            new
            {
                mode = report.DryRun ? "dry-run" : "clean",
                freedBytes = report.TotalFreedBytes,
                freedText = FormatBytes(report.TotalFreedBytes),
                elapsedSeconds = Math.Round(report.Elapsed.TotalSeconds, 1),
                failedItems = report.FailedItems,
                deferredItems = report.DeferredItems,
                entries = report.Entries.Select(e => new
                {
                    name = e.Item.DisplayName,
                    group = e.Item.GroupName,
                    category = e.Item.Category.ToString(),
                    risk = e.Item.Risk.ToString(),
                    path = e.Item.Path,
                    command = e.Item.CleanCommand,
                    outcome = e.Outcome.ToString(),
                    freedBytes = e.FreedBytes,
                    note = e.Note
                })
            },
            new JsonSerializerOptions { WriteIndented = true });

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

    private static string OutcomeText(CleanOutcome outcome) => outcome switch
    {
        CleanOutcome.DryRun => "предпросмотр",
        CleanOutcome.InUseSkipped => "отложено (используется)",
        CleanOutcome.NativeCleaned => "очищено штатной командой",
        CleanOutcome.DirectDeleted => "удалено",
        CleanOutcome.CommandOnlyCleaned => "команда выполнена",
        CleanOutcome.Partial => "частично",
        _ => "ошибка"
    };

    private static string EscapeCell(string value) =>
        value.Replace("|", "\\|").Replace(System.Environment.NewLine, " ");
}
