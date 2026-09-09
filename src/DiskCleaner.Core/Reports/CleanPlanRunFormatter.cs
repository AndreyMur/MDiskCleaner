using System.Text;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Localization;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Reports;

/// <summary>
/// Итоговый отчёт исполнения плана (Фаза 30, FR-5.14/5.16): «освобождено X», сводка по фазам
/// (таблица), пропущенные/заблокированные объекты с причинами, снимок до/после (FR-5.15),
/// кандидаты второго эшелона (FR-5.19) и предложение «повторить позже». Markdown и JSON —
/// общий генератор ядра, UTF-8 (NFR); JSON — единая схема <c>diskcleaner.plan-run</c>
/// через <see cref="CleanPlanRunJson"/>.
/// </summary>
public static class CleanPlanRunFormatter
{
    public static string ToMarkdown(CleanPlanRunReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Отчёт о выполнении плана — DiskCleaner");
        builder.AppendLine();
        builder.AppendLine(report.DryRun
            ? "Режим: **предпросмотр (dry-run)** — ничего не удалено"
            : report.Canceled
                ? "Режим: выполнение плана (**отменено пользователем между шагами**) — выполнены не все фазы"
                : "Режим: выполнение плана");
        builder.AppendLine($"Освобождено: **{CleanReportFormatter.FormatBytes(report.FreedBytes)}**");
        builder.AppendLine($"Затрачено: {report.Elapsed.TotalSeconds:F1} с");
        builder.AppendLine();

        AppendPlanVsFact(builder, report);
        AppendPhases(builder, report);
        AppendSkippedOrBlocked(builder, report);
        AppendDriveSnapshots(builder, report);
        AppendSecondEchelon(builder, report);

        return builder.ToString();
    }

    public static string ToJson(CleanPlanRunReport report) =>
        CleanPlanRunJson.Serialize(report);

    private static void AppendPlanVsFact(StringBuilder builder, CleanPlanRunReport report)
    {
        builder.AppendLine("## План против факта (FR-5.15)");
        builder.AppendLine();
        builder.AppendLine("| Показатель | Значение |");
        builder.AppendLine("|---|---|");
        builder.Append("| Оценка плана | ").AppendLine(CleanReportFormatter.FormatBytes(report.PlannedBytes));
        builder.Append("| Освобождено по записям | ").AppendLine(CleanReportFormatter.FormatBytes(report.FreedBytes));
        builder.Append("| Прирост свободного места по диску | ")
            .AppendLine(CleanReportFormatter.FormatBytes(report.DiskFreedBytes));

        if (report.GapBytes > 0 && !report.DryRun)
        {
            builder.Append("| Расхождение (факт меньше плана) | ")
                .AppendLine(CleanReportFormatter.FormatBytes(report.GapBytes));
            builder.AppendLine();
            builder.AppendLine("> Освобождено меньше плана. Ниже — пропущенные/заблокированные шаги и кандидаты «второго эшелона».");
        }
        else
        {
            builder.Append("| Расхождение | ").AppendLine("в пределах плана");
        }

        builder.AppendLine();
    }

    private static void AppendPhases(StringBuilder builder, CleanPlanRunReport report)
    {
        builder.AppendLine("## Сводка по фазам");
        builder.AppendLine();
        builder.AppendLine("| Фаза | Объектов | План | Освобождено | Выполнено | Пропущено | Ошибок |");
        builder.AppendLine("|---|---|---|---|---|---|---|");

        foreach (var phase in report.Phases)
        {
            builder.Append("| ")
                .Append(EscapeCell(phase.PhaseText)).Append(" | ")
                .Append(phase.Items.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" | ")
                .Append(CleanReportFormatter.FormatBytes(phase.PlannedBytes)).Append(" | ")
                .Append(CleanReportFormatter.FormatBytes(phase.FreedBytes)).Append(" | ")
                .Append(phase.Completed.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" | ")
                .Append(phase.Skipped.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" | ")
                .Append(phase.Failed.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine(" |");
        }

        builder.AppendLine();
    }

    private static void AppendSkippedOrBlocked(StringBuilder builder, CleanPlanRunReport report)
    {
        if (report.SkippedOrBlocked.Count == 0)
        {
            return;
        }

        builder.AppendLine("## Пропущенные / заблокированные объекты (FR-5.14)");
        builder.AppendLine();
        builder.AppendLine("| Объект | Категория | Причина | Пояснение |");
        builder.AppendLine("|---|---|---|---|");

        foreach (var entry in report.SkippedOrBlocked)
        {
            builder.Append("| ")
                .Append(EscapeCell(DisplayObject(entry.Item))).Append(" | ")
                .Append(EscapeCell(LocalizedNames.Category(entry.Item.Category))).Append(" | ")
                .Append(EscapeCell(CleanReportFormatter.OutcomeText(entry.Outcome))).Append(" | ")
                .Append(EscapeCell(entry.Note ?? string.Empty)).AppendLine(" |");
        }

        builder.AppendLine();
        builder.AppendLine("> **Повторить позже**: закройте используемые приложения и выполните план снова"
            + " (FR-5.8, FR-5.14) — повторный запуск безопасен и идемпотентен.");
        builder.AppendLine();
    }

    private static void AppendDriveSnapshots(StringBuilder builder, CleanPlanRunReport report)
    {
        if (report.DriveSnapshots.Count == 0)
        {
            return;
        }

        builder.AppendLine("## Снимок свободного места до/после (FR-5.15)");
        builder.AppendLine();
        builder.AppendLine("| Диск | До | После | Изменение |");
        builder.AppendLine("|---|---|---|---|");

        foreach (var snapshot in report.DriveSnapshots)
        {
            builder.Append("| ")
                .Append(EscapeCell(snapshot.RootPath)).Append(" | ")
                .Append(CleanReportFormatter.FormatBytes(snapshot.FreeBeforeBytes)).Append(" | ")
                .Append(CleanReportFormatter.FormatBytes(snapshot.FreeAfterBytes)).Append(" | ")
                .Append(CleanReportFormatter.FormatBytes(snapshot.DeltaBytes)).AppendLine(" |");
        }

        builder.AppendLine();
    }

    private static void AppendSecondEchelon(StringBuilder builder, CleanPlanRunReport report)
    {
        if (report.SecondEchelonCandidates.Count == 0)
        {
            return;
        }

        builder.AppendLine("## Кандидаты «второго эшелона» (FR-5.19)");
        builder.AppendLine();
        builder.AppendLine("Свободного места после плана меньше ожидаемого. Крупные приложения, которые вы решили не удалять:");
        builder.AppendLine();
        builder.AppendLine("| Приложение | Размер |");
        builder.AppendLine("|---|---|");

        foreach (var candidate in report.SecondEchelonCandidates)
        {
            builder.Append("| ")
                .Append(EscapeCell(candidate.DisplayName)).Append(" | ")
                .Append(CleanReportFormatter.FormatBytes(candidate.EffectiveSizeBytes)).AppendLine(" |");
        }

        builder.AppendLine();
    }

    private static string DisplayObject(CleanupItem item) =>
        string.IsNullOrEmpty(item.GroupName) ? item.DisplayName : item.GroupName + " — " + item.DisplayName;

    private static string EscapeCell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).Replace(System.Environment.NewLine, " ");
}
