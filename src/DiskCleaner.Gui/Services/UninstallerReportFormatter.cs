using System.Text;
using DiskCleaner.Core.Reports;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Gui.Services;

/// <summary>
/// Форматирование отчётов экрана «Деинсталляция и зачистка» для окна отчёта:
/// отчёт пачки деинсталляций (<see cref="UninstallExecutionReport"/>) и отчёт массового удаления
/// версии Windows SDK (<see cref="SdkBulkUninstallReport"/>). Зачистка реестра (FR-4.13)
/// использует общий формат <see cref="CleanReportFormatter"/> ядра.
/// </summary>
internal static class UninstallerReportFormatter
{
    public static string ToMarkdown(UninstallExecutionReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Отчёт о деинсталляции");
        builder.AppendLine();
        builder.AppendLine($"Выполнено шагов: {report.Entries.Count}; успешно (в т.ч. идемпотентно): {report.OkCount}; " +
                           $"ошибок: {report.ErrorCount}; отклонено UAC: {report.DeclinedCount}");
        builder.AppendLine($"Требуют перезагрузки: {report.RebootRequiredCount}");
        builder.AppendLine();
        builder.AppendLine("| Приложение | Исход | Exit-код | Время | Примечание |");
        builder.AppendLine("|---|---|---|---|---|");

        foreach (var entry in report.Entries)
        {
            builder.Append("| ")
                .Append(EscapeCell(entry.DisplayName)).Append(" | ")
                .Append(EscapeCell(OutcomeText(entry.Outcome))).Append(" | ")
                .Append(entry.ExitCode?.ToString() ?? "—").Append(" | ")
                .Append(entry.StartedAt.ToLocalTime().ToString("HH:mm:ss")).Append(" | ")
                .Append(EscapeCell(entry.Note ?? string.Empty)).AppendLine(" |");
        }

        return builder.ToString();
    }

    public static string ToJson(UninstallExecutionReport report)
    {
        var entries = report.Entries.Select(e => new
        {
            app = e.Item.DisplayName,
            scope = e.Item.App.ScopeKey,
            outcome = e.Outcome.ToString(),
            exitCode = e.ExitCode,
            note = e.Note,
            startedAtUtc = e.StartedAt
        });

        return System.Text.Json.JsonSerializer.Serialize(
            new { ok = report.OkCount, errors = report.ErrorCount, rebootRequired = report.NeedsReboot, entries },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    public static string ToMarkdown(SdkBulkUninstallReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Массовое удаление Windows SDK");
        builder.AppendLine();
        builder.AppendLine($"Версия: **{report.DisplayVersion}**");
        builder.AppendLine($"Проходов: {report.PassesUsed}; полностью удалена: {(report.FullyRemoved ? "да" : "нет")}; " +
                           $"успешно: {report.OkCount}; ошибок: {report.FailedCount}");
        if (report.RemainingProductCodes.Count > 0)
        {
            builder.AppendLine($"Осталось записей в реестре: {report.RemainingProductCodes.Count} ({string.Join("; ", report.RemainingDisplayNames)})");
        }

        builder.AppendLine();
        builder.AppendLine("| Компонент | Проход | Исход | Exit-код | Примечание |");
        builder.AppendLine("|---|---|---|---|---|");

        foreach (var component in report.Components)
        {
            builder.Append("| ")
                .Append(EscapeCell(component.DisplayName)).Append(" | ")
                .Append(component.Pass).Append(" | ")
                .Append(EscapeCell(OutcomeText(component.Outcome))).Append(" | ")
                .Append(component.ExitCode?.ToString() ?? "—").Append(" | ")
                .Append(EscapeCell(component.Note ?? string.Empty)).AppendLine(" |");
        }

        if (report.FolderCleanups.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Каталоги версии SDK (Include/Lib/bin):");
            foreach (var cleanup in report.FolderCleanups)
            {
                builder.AppendLine($"- {(cleanup.Removed ? "удалён" : "не удалён")}: {cleanup.Path} — {cleanup.Note}");
            }
        }

        if (report.PackageCacheCleanups.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Package Cache:");
            foreach (var cleanup in report.PackageCacheCleanups)
            {
                builder.AppendLine($"- {(cleanup.Removed ? "удалена" : "не удалена")}: {cleanup.Path} — {cleanup.Note}");
            }
        }

        return builder.ToString();
    }

    public static string ToJson(SdkBulkUninstallReport report)
    {
        var components = report.Components.Select(c => new
        {
            productCode = c.ProductCode,
            displayName = c.DisplayName,
            pass = c.Pass,
            outcome = c.Outcome.ToString(),
            exitCode = c.ExitCode,
            note = c.Note
        });

        return System.Text.Json.JsonSerializer.Serialize(
            new
            {
                displayVersion = report.DisplayVersion,
                passesUsed = report.PassesUsed,
                fullyRemoved = report.FullyRemoved,
                remainingProductCodes = report.RemainingProductCodes,
                remainingDisplayNames = report.RemainingDisplayNames,
                components,
                folderCleanups = report.FolderCleanups,
                packageCacheCleanups = report.PackageCacheCleanups
            },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static string OutcomeText(UninstallExecutionOutcome outcome) => outcome switch
    {
        UninstallExecutionOutcome.Uninstalled => "деинсталлировано",
        UninstallExecutionOutcome.RebootRequired => "требуется перезагрузка",
        UninstallExecutionOutcome.NotInstalled => "не установлено (идемпотентно)",
        UninstallExecutionOutcome.AlreadyUninstalled => "уже удалено (идемпотентно)",
        UninstallExecutionOutcome.NoUninstaller => "нет деинсталлятора",
        UninstallExecutionOutcome.ElevationDeclined => "отменено (UAC)",
        UninstallExecutionOutcome.TimedOut => "таймаут",
        UninstallExecutionOutcome.Failed => "ошибка",
        _ => outcome.ToString()
    };

    private static string EscapeCell(string value) =>
        value.Replace("|", "\\|").Replace(System.Environment.NewLine, " ");
}
