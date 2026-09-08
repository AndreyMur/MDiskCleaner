using DiskCleaner.Core.Elevated;

namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Очистка <c>%ProgramData%\Package Cache</c> для удалённых bundle/MSI-продуктов (фаза 23,
/// модуль 04, FR-4.14): папка <c>Package Cache\{code}</c> удаляется только когда продукт
/// (код <c>{code}</c>) больше не присутствует в записях Uninstall реестра — то есть пакет
/// действительно удалён и кэш установщика больше не нужен. Папки продуктов, которые всё ещё
/// установлены, не затрагиваются (другая версия SDK остаётся в целости).
/// </summary>
public sealed class PackageCacheCleaner
{
    /// <summary>Корень по умолчанию (<c>%ProgramData%\Package Cache</c>).</summary>
    public static string DefaultCacheRoot(string? programDataRoot = null) =>
        Path.Combine(
            programDataRoot ?? BundleFallbackResolver.DefaultPackageCacheRoot,
            "Package Cache");

    /// <summary>
    /// Папки <c>{code}</c>, подлежащие очистке: папка существует в Package Cache, а код не
    /// найден среди установленных записей <paramref name="installedApps"/> (сравнение без фигурных
    /// скобок, без учёта регистра).
    /// </summary>
    public IReadOnlyList<string> FindOrphanedBundleFolders(
        string packageCacheRoot,
        IEnumerable<string> candidateProductCodes,
        IReadOnlyList<InstalledApp> installedApps)
    {
        var installed = new HashSet<string>(
            installedApps
                .Where(app => !string.IsNullOrWhiteSpace(app.ProductCode))
                .Select(app => Normalize(app.ProductCode!)),
            StringComparer.OrdinalIgnoreCase);

        var result = new List<string>();
        foreach (var code in candidateProductCodes)
        {
            if (string.IsNullOrWhiteSpace(code) || installed.Contains(Normalize(code)))
            {
                continue;
            }

            var folder = Path.Combine(packageCacheRoot, Normalize(code));
            if (Directory.Exists(folder))
            {
                result.Add(folder);
            }
        }

        return result;
    }

    /// <summary>
    /// Elevated-шаги удаления найденных папок Package Cache (FR-4.15: операции в ProgramData —
    /// через повышенный процесс, одна UAC-проверка на пачку шагов).
    /// </summary>
    public IReadOnlyList<ElevatedStep> BuildCleanupSteps(
        IReadOnlyList<string> folders,
        string stepIdPrefix = "cache",
        int timeoutSec = 300)
    {
        var steps = new List<ElevatedStep>(folders.Count);
        for (var index = 0; index < folders.Count; index++)
        {
            steps.Add(new ElevatedStep
            {
                Id = $"{stepIdPrefix}:{index}",
                Kind = ElevatedStepKind.DeletePath,
                Path = folders[index],
                TimeoutSec = timeoutSec
            });
        }

        return steps;
    }

    private static string Normalize(string productCode) =>
        productCode.Trim('{', '}').ToLowerInvariant();
}
