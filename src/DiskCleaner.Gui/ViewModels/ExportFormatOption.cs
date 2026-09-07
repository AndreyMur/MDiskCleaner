using DiskCleaner.Core.Reports;

namespace DiskCleaner.Gui.ViewModels;

/// <summary>
/// Вариант формата экспорта плана для выпадающего списка (FR-1.12): отображаемое имя,
/// расширение файла и фильтр диалога сохранения. Тонкое отображение без бизнес-логики.
/// </summary>
public sealed class ExportFormatOption
{
    private ExportFormatOption(
        string displayName,
        PlanExportFormat format,
        string fileExtension,
        string filter)
    {
        DisplayName = displayName;
        Format = format;
        FileExtension = fileExtension;
        Filter = filter;
    }

    public string DisplayName { get; }

    public PlanExportFormat Format { get; }

    public string FileExtension { get; }

    public string Filter { get; }

    /// <summary>Доступные форматы экспорта плана (FR-1.12).</summary>
    public static IReadOnlyList<ExportFormatOption> All { get; } =
    [
        new("Markdown (*.md)", PlanExportFormat.Markdown, ".md", "Markdown (*.md)|*.md"),
        new("JSON (*.json)", PlanExportFormat.Json, ".json", "JSON (*.json)|*.json"),
        new("CSV (*.csv)", PlanExportFormat.Csv, ".csv", "CSV (*.csv)|*.csv")
    ];
}
