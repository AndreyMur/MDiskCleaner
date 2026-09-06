using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskCleaner.Core.Elevated;

public static class ElevatedJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static ElevatedScenario ReadScenario(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<ElevatedScenario>(text, Options)
            ?? new ElevatedScenario();
    }

    public static void WriteScenario(string path, ElevatedScenario scenario)
    {
        var json = JsonSerializer.Serialize(scenario, Options);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    public static ElevatedJournal ReadJournal(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<ElevatedJournal>(text, Options)
            ?? new ElevatedJournal();
    }

    public static void WriteJournal(string path, ElevatedJournal journal)
    {
        var json = JsonSerializer.Serialize(journal, Options);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }
}
