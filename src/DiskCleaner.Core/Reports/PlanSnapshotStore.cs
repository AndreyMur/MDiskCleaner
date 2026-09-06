using DiskCleaner.Core.Scanning;

namespace DiskCleaner.Core.Reports;

/// <summary>
/// Хранилище снапшота предыдущего скана (категорий/объектов с размерами) в каталоге scancache
/// (FR-1.13): план последнего анализа сериализуется единой JSON-схемой <see cref="PlanDocument"/>
/// (UTF-8). После повторного скана снапшот загружается для расчёта сравнения «было/стало».
/// </summary>
public sealed class PlanSnapshotStore
{
    public const string DefaultSnapshotFileName = "plan-snapshot.json";

    private readonly string _filePath;

    public PlanSnapshotStore(string? cacheDirectory = null)
    {
        cacheDirectory ??= ScanCacheStore.DefaultDirectory();
        Directory.CreateDirectory(cacheDirectory);
        _filePath = Path.Combine(cacheDirectory, DefaultSnapshotFileName);
    }

    public string FilePath => _filePath;

    public bool Exists => File.Exists(_filePath);

    /// <summary>Записать снапшот текущего скана (UTF-8, единая JSON-схема плана).</summary>
    public void Save(PlanDocument snapshot) =>
        PlanJson.WriteFile(_filePath, snapshot);

    /// <summary>
    /// Загрузить снапшот предыдущего скана. <c>null</c>, если снапшота нет или файл повреждён
    /// (в этом случае снапшот пересоздаётся при следующем скане).
    /// </summary>
    public PlanDocument? TryLoad()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            return PlanJson.ReadFile(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Serilog.Log.Warning(ex, "Plan snapshot load failed; previous scan comparison unavailable: {Path}", _filePath);
            return null;
        }
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Plan snapshot delete failed: {Path}", _filePath);
        }
    }
}
