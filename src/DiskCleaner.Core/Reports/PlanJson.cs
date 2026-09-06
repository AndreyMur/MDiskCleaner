using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskCleaner.Core.Reports;

/// <summary>
/// Сериализация единой JSON-схемы плана очистки (<see cref="PlanDocument"/>, FR-1.12):
/// System.Text.Json, корректный UTF-8 для кириллицы, camelCase-ключи, отступы для чтения.
/// Тем же десериализатором читается снапшот предыдущего скана из scancache (FR-1.13).
/// </summary>
public static class PlanJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(PlanDocument document) =>
        JsonSerializer.Serialize(document, Options);

    public static PlanDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<PlanDocument>(json, Options)
        ?? new PlanDocument();

    public static void WriteFile(string path, PlanDocument document) =>
        File.WriteAllText(path, Serialize(document), new UTF8Encoding(false));

    public static PlanDocument ReadFile(string path) =>
        Deserialize(File.ReadAllText(path, Encoding.UTF8));
}
