using System.Text;
using System.Text.Json;

namespace DiskCleaner.Core.Scanning;

/// <summary>Запись кэша скана для одного каталога (ветки).</summary>
internal sealed class ScanCacheEntry
{
    public string Path { get; set; } = string.Empty;

    public long DirectoryLastWriteTicks { get; set; }

    public long OwnFileBytes { get; set; }

    public long OwnFileCount { get; set; }

    public long SubtreeBytes { get; set; }

    public long SubtreeFileCount { get; set; }

    public List<string> Subdirectories { get; set; } = new();
}

internal sealed class ScanCacheFile
{
    public int Version { get; set; } = 1;

    public List<ScanCacheEntry> Entries { get; set; } = new();
}

/// <summary>
/// Инкрементальный кэш результатов обхода по веткам (mtime каталога + размер) в каталоге
/// scancache. Повторный скан пересчитывает только изменённые ветки (FR-1.13 / NFR,
/// инкрементальность): размеры и число файлов каталога доверяются кэшу, если
/// <c>LastWriteTime</c> каталога не изменился. Файл — UTF-8 (NFR).
/// </summary>
public sealed class ScanCacheStore
{
    public const string DefaultCacheFileName = "scan-cache.json";

    private const int CacheFileVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();

    private readonly string _filePath;
    private readonly Dictionary<string, ScanCacheEntry> _loaded =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScanCacheEntry> _live =
        new(StringComparer.OrdinalIgnoreCase);

    public ScanCacheStore(string? cacheDirectory = null)
    {
        cacheDirectory ??= DefaultDirectory();
        Directory.CreateDirectory(cacheDirectory);
        _filePath = Path.Combine(cacheDirectory, DefaultCacheFileName);
        Load();
    }

    /// <summary>Каталог по умолчанию для кэша и снапшотов скана (scancache).</summary>
    public static string DefaultDirectory() => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "DiskCleaner",
        "scancache");

    /// <summary>Число записей, актуальных после последнего <see cref="Save"/>.</summary>
    public int LiveEntryCount
    {
        get
        {
            lock (_sync)
            {
                return _live.Count;
            }
        }
    }

    public string FilePath => _filePath;

    /// <summary>Вернуть запись ветки, если она есть и ещё не пересчитана в текущем прогоне.</summary>
    internal ScanCacheEntry? TryGet(string directoryPath)
    {
        lock (_sync)
        {
            if (_live.TryGetValue(directoryPath, out var live))
            {
                return live;
            }

            return _loaded.TryGetValue(directoryPath, out var cached) ? cached : null;
        }
    }

    /// <summary>Зафиксировать актуальную запись ветки в текущем прогоне.</summary>
    internal void Record(string directoryPath, ScanCacheEntry entry)
    {
        lock (_sync)
        {
            _live[directoryPath] = entry;
        }
    }

    /// <summary>
    /// Записать кэш на диск. Сохраняются только каталоги, посещённые текущим прогоном:
    /// исчезнувшие ветки естественным образом выпадают из файла.
    /// </summary>
    public void Save()
    {
        List<ScanCacheEntry> snapshot;
        lock (_sync)
        {
            snapshot = _live.Values
                .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var file = new ScanCacheFile
        {
            Version = CacheFileVersion,
            Entries = snapshot
        };

        var json = JsonSerializer.Serialize(file, JsonOptions);
        File.WriteAllText(_filePath, json, new UTF8Encoding(false));
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            var file = JsonSerializer.Deserialize<ScanCacheFile>(json, JsonOptions);
            if (file is null || file.Version != CacheFileVersion)
            {
                return;
            }

            foreach (var entry in file.Entries)
            {
                if (!string.IsNullOrEmpty(entry.Path))
                {
                    _loaded[entry.Path] = entry;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _loaded.Clear();
            Serilog.Log.Warning(ex, "Scan cache load failed; cache will be rebuilt: {Path}", _filePath);
        }
    }
}
