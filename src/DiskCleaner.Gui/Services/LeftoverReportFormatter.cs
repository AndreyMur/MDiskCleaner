using System.Text;
using DiskCleaner.Core.Leftovers;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Gui.Services;

/// <summary>
/// Форматирование отчёта об удалении остатков (журнал с основанием удаления, FR-3.6) для
/// окна отчёта и сохранения в файл. Ядро оставляет отчёт в виде объектов
/// (<see cref="LeftoverCleanReport"/>); текст — это представление для GUI.
/// </summary>
internal static class LeftoverReportFormatter
{
    public static string ToMarkdown(LeftoverCleanReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Отчёт об удалении остатков");
        builder.AppendLine();
        builder.AppendLine(report.DryRun
            ? "Режим: **предпросмотр (dry-run)** — ничего не удалено (FR-3.8)"
            : "Режим: выполнение подтверждённых удалений");
        builder.AppendLine($"Освобождено: **{CleanReportFormatter.FormatBytes(report.TotalFreedBytes)}**");
        builder.AppendLine($"Затрачено: {report.Elapsed.TotalSeconds:F1} с");
        builder.AppendLine($"Объектов: {report.Entries.Count}; удалено: {report.DeletedCount}; пропущено: {report.SkippedCount}");
        builder.AppendLine();
        builder.AppendLine("| Группа | Объект | Результат | Освобождено | Путь | Примечание (основание) |");
        builder.AppendLine("|---|---|---|---|---|---|");

        foreach (var entry in report.Entries)
        {
            builder.Append("| ")
                .Append(EscapeCell(entry.Item.GroupName)).Append(" | ")
                .Append(EscapeCell(entry.Item.DisplayName)).Append(" | ")
                .Append(EscapeCell(OutcomeText(entry.Outcome))).Append(" | ")
                .Append(CleanReportFormatter.FormatBytes(entry.FreedBytes)).Append(" | ")
                .Append(EscapeCell(entry.Item.Path)).Append(" | ")
                .Append(EscapeCell(entry.Note ?? entry.Basis)).AppendLine(" |");
        }

        return builder.ToString();
    }

    public static string ToJson(LeftoverCleanReport report)
    {
        var entries = report.Entries.Select(e => new
        {
            group = e.Item.GroupName,
            name = e.Item.DisplayName,
            path = e.Item.Path,
            reason = e.Item.Reason.ToString(),
            basis = e.Basis,
            outcome = e.Outcome.ToString(),
            freedBytes = e.FreedBytes,
            note = e.Note
        });

        return System.Text.Json.JsonSerializer.Serialize(
            new { dryRun = report.DryRun, freedBytes = report.TotalFreedBytes, elapsedSeconds = report.Elapsed.TotalSeconds, entries },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static string OutcomeText(LeftoverCleanOutcome outcome) => outcome switch
    {
        LeftoverCleanOutcome.DryRun => "предпросмотр",
        LeftoverCleanOutcome.AlreadyAbsent => "уже отсутствует (идемпотентно)",
        LeftoverCleanOutcome.DirectDeleted => "удалено",
        LeftoverCleanOutcome.ElevatedDeleted => "удалено (elevated)",
        LeftoverCleanOutcome.Partial => "частично",
        LeftoverCleanOutcome.LiveObjectSkipped => "пропущено (используется)",
        LeftoverCleanOutcome.ConfirmationRequired => "нет подтверждения",
        LeftoverCleanOutcome.ElevationDeclined => "отменено (UAC)",
        _ => "ошибка"
    };

    private static string EscapeCell(string value) =>
        value.Replace("|", "\\|").Replace(System.Environment.NewLine, " ");
}
