using System.Globalization;
using System.Text;

namespace DiskCleaner.Core.Reports;

/// <summary>Форматы экспорта плана очистки (FR-1.12).</summary>
public enum PlanExportFormat
{
    Json,
    Markdown,
    Csv
}

/// <summary>
/// Собственные генераторы Markdown и CSV для экспорта плана (FR-1.12) поверх единой схемы
/// <see cref="PlanDocument"/>. CSV — разделитель «;» (открывается в Excel для русской локали),
/// файл записывается с BOM (UTF-8), чтобы Excel корректно распознал кириллицу; Markdown и JSON —
/// UTF-8 без BOM. Все файлы вывода — UTF-8 (NFR).
/// </summary>
public static class PlanExporter
{
    public static string Render(PlanDocument document, PlanExportFormat format) => format switch
    {
        PlanExportFormat.Json => PlanJson.Serialize(document),
        PlanExportFormat.Markdown => ToMarkdown(document),
        PlanExportFormat.Csv => ToCsv(document),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    public static void WriteFile(string path, PlanDocument document, PlanExportFormat format)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (format == PlanExportFormat.Csv)
        {
            File.WriteAllText(path, ToCsv(document), new UTF8Encoding(true));
            return;
        }

        File.WriteAllText(path, Render(document, format), new UTF8Encoding(false));
    }

    public static string ToMarkdown(PlanDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# План очистки — DiskCleaner");
        builder.AppendLine();
        builder.AppendLine(
            $"Сформирован: {document.GeneratedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}. " +
            $"Анализ: {document.ElapsedSeconds:F1} с.");
        builder.AppendLine();

        AppendSummary(builder, document);

        builder.AppendLine();
        builder.AppendLine("## Сводка по категориям");
        builder.AppendLine();
        builder.AppendLine("| Категория | Объектов | Размер |");
        builder.AppendLine("|---|---|---|");

        foreach (var category in document.Categories)
        {
            builder.Append("| ")
                .Append(EscapeCell(category.CategoryText)).Append(" | ")
                .Append(category.Items.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(CleanReportFormatter.FormatBytes(category.Bytes)).AppendLine(" |");
        }

        builder.AppendLine();

        var itemsByCategory = document.Items
            .GroupBy(i => i.CategoryText)
            .OrderByDescending(g => g.Sum(i => i.SizeBytes ?? 0));

        foreach (var category in itemsByCategory)
        {
            builder.AppendLine();
            builder.AppendLine($"## {category.Key}");
            builder.AppendLine();
            builder.AppendLine("| Объект | Размер | Файлов | Риск | Действие | Требует админа | Метки |");
            builder.AppendLine("|---|---|---|---|---|---|---|");

            foreach (var item in category.OrderByDescending(i => i.SizeBytes ?? 0))
            {
                builder.Append("| ")
                    .Append(EscapeCell(DisplayObject(item))).Append(" | ")
                    .Append(CleanReportFormatter.FormatBytes(item.SizeBytes ?? 0)).Append(" | ")
                    .Append(item.FileCount?.ToString(CultureInfo.InvariantCulture) ?? "—").Append(" | ")
                    .Append(EscapeCell(item.RiskText)).Append(" | ")
                    .Append(EscapeCell(item.DefaultActionText)).Append(" | ")
                    .Append(item.RequiresAdmin ? "да" : "нет").Append(" | ")
                    .Append(EscapeCell(Flags(item))).AppendLine(" |");
            }
        }

        return builder.ToString();
    }

    public static string ToCsv(PlanDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Категория;Группа;Объект;Путь;Размер байт;Размер;Файлов;Риск;Действие;Требует админа;IN_USE;Review;Команда");

        foreach (var item in document.Items.OrderByDescending(i => i.SizeBytes ?? 0))
        {
            builder.Append(Csv(item.CategoryText)).Append(';')
                .Append(Csv(item.Group ?? string.Empty)).Append(';')
                .Append(Csv(item.Name)).Append(';')
                .Append(Csv(item.Path ?? string.Empty)).Append(';')
                .Append(item.SizeBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(';')
                .Append(Csv(CleanReportFormatter.FormatBytes(item.SizeBytes ?? 0))).Append(';')
                .Append(item.FileCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(';')
                .Append(Csv(item.RiskText)).Append(';')
                .Append(Csv(item.DefaultActionText)).Append(';')
                .Append(item.RequiresAdmin ? "да" : "нет").Append(';')
                .Append(item.InUse ? "IN_USE" : string.Empty).Append(';')
                .Append(Csv(item.ReviewReason ?? string.Empty)).Append(';')
                .AppendLine(Csv(Flags(item)));
        }

        return builder.ToString();
    }

    private static void AppendSummary(StringBuilder builder, PlanDocument document)
    {
        var summary = document.Summary;
        builder.Append("**Объектов:** ")
            .Append(summary.TotalItems.ToString(CultureInfo.InvariantCulture))
            .Append(" · **Размер:** ")
            .AppendLine(CleanReportFormatter.FormatBytes(summary.TotalBytes));

        var notes = new List<string>(3);
        if (summary.InUseItems > 0)
        {
            notes.Add($"используется (IN_USE): {summary.InUseItems}");
        }

        if (summary.RequiresAdminItems > 0)
        {
            notes.Add($"требует админа: {summary.RequiresAdminItems}");
        }

        if (summary.ReviewItems > 0)
        {
            notes.Add($"проверить вручную: {summary.ReviewItems}");
        }

        if (summary.SkippedNonexistent > 0)
        {
            notes.Add($"пропущено (нет пути): {summary.SkippedNonexistent}");
        }

        if (notes.Count > 0)
        {
            builder.Append("**Примечания:** ").AppendLine(string.Join(" · ", notes));
        }
    }

    private static string DisplayObject(PlanItemJson item) =>
        string.IsNullOrEmpty(item.Group)
            ? item.Name
            : item.Group + " — " + item.Name;

    private static string Flags(PlanItemJson item)
    {
        var flags = new List<string>(2);
        if (item.InUse)
        {
            flags.Add("IN_USE");
        }

        if (!string.IsNullOrEmpty(item.ReviewReason))
        {
            flags.Add("Review manually");
        }

        return string.Join(", ", flags);
    }

    private static string Csv(string value) =>
        value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;

    private static string EscapeCell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).Replace(System.Environment.NewLine, " ");
}
