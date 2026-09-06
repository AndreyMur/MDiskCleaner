using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskCleaner.Core.Reports;

/// <summary>
/// Сериализация единой JSON-схемы отчёта (System.Text.Json): корректный UTF-8
/// для кириллицы, camelCase-ключи, отступы для чтения человеком.
/// </summary>
public static class ReportJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(ReportDocument document) =>
        JsonSerializer.Serialize(document, Options);

    public static ReportDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<ReportDocument>(json, Options)
        ?? new ReportDocument();

    public static void WriteFile(string path, ReportDocument document) =>
        File.WriteAllText(path, Serialize(document), new UTF8Encoding(false));

    public static ReportDocument ReadFile(string path) =>
        Deserialize(File.ReadAllText(path, Encoding.UTF8));
}
