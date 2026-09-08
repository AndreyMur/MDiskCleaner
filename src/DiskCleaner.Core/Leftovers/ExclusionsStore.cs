using System.Text;
using System.Text.Json;

namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// Хранилище исключений пользователя <c>%LOCALAPPDATA%\DiskCleaner\exclusions.json</c> (FR-3.7).
/// Исключение — либо путь (полный путь к каталогу, например <c>C:\Users\me\AppData\Local\pachca-updater</c>),
/// либо бренд/имя каталога верхнего уровня (например <c>pachca-updater</c> или <c>Wondershare</c>).
/// Записи читаются при сканировании (<see cref="Load"/>); кандидаты, попавшие под исключение,
/// не появляются в плане очистки остатков.
/// </summary>
public sealed class ExclusionsStore
{
    public const string FileName = "exclusions.json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    public ExclusionsStore(string? directory = null)
    {
        directory ??= Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "DiskCleaner");
        _filePath = Path.Combine(directory, FileName);
    }

    public string FilePath => _filePath;

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

    /// <summary>Исключение по полному пути каталога (FR-3.7).</summary>
    public void AddPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Add(TrimPath(path));
    }

    /// <summary>Исключение по бренду (имени каталога верхнего уровня, FR-3.7).</summary>
    public void AddBrand(string brandName)
    {
        if (string.IsNullOrWhiteSpace(brandName))
        {
            return;
        }

        Add(brandName.Trim());
    }

    public void Add(string entry)
    {
        var normalized = entry.Trim();
        if (normalized.Length == 0)
        {
            return;
        }

        var entries = Load().ToList();
        if (entries.Any(e => string.Equals(e, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        entries.Add(normalized);
        Write(entries);
    }

    /// <summary>Удаляет исключение (по пути или бренду), если оно было добавлено ранее.</summary>
    public void Remove(string entry)
    {
        var normalized = entry.Trim();
        if (normalized.Length == 0)
        {
            return;
        }

        var entries = Load()
            .Where(e => !string.Equals(e, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Write(entries);
    }

    public bool Contains(string entry)
    {
        var normalized = entry.Trim();
        return normalized.Length > 0 &&
               Load().Any(e => string.Equals(e, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Похожа ли запись на путь, а не на бренд (имя каталога): содержит разделители каталогов,
    /// корень диска/UNC либо переменную окружения (<c>%LOCALAPPDATA%</c>). Используется для
    /// пояснения записи в интерфейсе и для нормализации при добавлении.
    /// </summary>
    public static bool IsPathEntry(string entry)
    {
        var value = entry.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (value.StartsWith('%') || value.StartsWith("\\\\"))
        {
            return true;
        }

        if (value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar))
        {
            return true;
        }

        try
        {
            return !string.IsNullOrEmpty(Path.GetPathRoot(value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string TrimPath(string path) => Path.TrimEndingDirectorySeparator(path);

    private static string? NormalizePathOrNull(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Попадает ли объект (путь и/или имя каталога) под одно из исключений. Исключение-путь
    /// срабатывает как префикс полного пути; исключение-бренд — как имя каталога верхнего уровня
    /// (FR-3.7).
    /// </summary>
    public static bool IsExcluded(
        IReadOnlyCollection<string> exclusions,
        string path,
        string? folderName = null)
    {
        if (exclusions.Count == 0)
        {
            return false;
        }

        string? normalizedPath = null;
        foreach (var exclusion in exclusions)
        {
            var normalized = exclusion.Trim();
            if (normalized.Length == 0)
            {
                continue;
            }

            if (IsPathEntry(normalized))
            {
                normalizedPath ??= NormalizePathOrNull(path);
                if (normalizedPath is not null &&
                    normalizedPath.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

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

    private void Write(IReadOnlyList<string> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(
            _filePath,
            JsonSerializer.Serialize(entries, WriteOptions),
            new UTF8Encoding(false));
    }
}
