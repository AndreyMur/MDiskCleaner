using System.Text;
using System.Text.Json;

namespace DiskCleaner.Core.Leftovers;

public sealed class ExclusionsStore
{
    public const string FileName = "exclusions.json";

    private readonly string _filePath;

    public ExclusionsStore(string? directory = null)
    {
        directory ??= Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "DiskCleaner");
        _filePath = Path.Combine(directory, FileName);
    }

    public IReadOnlyList<string> Load()
    {
        if (!File.Exists(_filePath))
        {
            return Array.Empty<string>();
        }

        try
        {
            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<string>>(json)
                   ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public void Add(string entry)
    {
        var entries = Load().ToList();
        if (entries.Any(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        entries.Add(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    public static bool IsExcluded(
        IReadOnlyCollection<string> exclusions,
        string path,
        string? folderName = null)
    {
        if (exclusions.Count == 0)
        {
            return false;
        }

        foreach (var exclusion in exclusions)
        {
            var normalized = exclusion.Trim();
            if (normalized.Length == 0)
            {
                continue;
            }

            if (path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (folderName is not null &&
                string.Equals(folderName, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
