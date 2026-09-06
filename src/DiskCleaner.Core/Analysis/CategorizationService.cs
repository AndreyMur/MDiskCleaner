using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Analysis;

/// <summary>
/// Чистая логика «категория + риск» для путей и объектов очистки (FR-1.6).
/// Низкий риск: кэши, Temp, Корзина, `*-updater` (регенерируются).
/// Средний риск: тулчейны/SDK/остатки (перекачиваются/переустанавливаются, требуют согласия).
/// Высокий риск: папки программ и пользовательские данные (не предлагать к автоудалению).
/// </summary>
public sealed class CategorizationService
{
    private static readonly HashSet<string> CacheSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "npm-cache", "ms-playwright", "pnpm-cache", "pnpm-store", "dotslash", "cache", "caches",
        ".npm", ".yarn", ".gradle", ".m2", ".nuget", ".cargo", ".cache", "CachedData",
        "CachedExtensionVSIXs", "GPUCache", "Crashpad", "WebStorage"
    };

    private static readonly HashSet<string> TempSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "temp", "tmp"
    };

    private static readonly HashSet<string> ToolchainSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rustup", ".jdks", "jdks", "android", "sdk"
    };

    public CleanupCategory CategorizePath(string path)
    {
        var segments = SplitSegments(path);
        if (segments.Any(IsUpdater) || segments.Any(IsAndroidStudioLeftover))
        {
            return CleanupCategory.Leftover;
        }

        if (segments.Any(s => ToolchainSegments.Contains(s)))
        {
            return CleanupCategory.DevToolchain;
        }

        if (segments.Any(s => CacheSegments.Contains(s)))
        {
            return CleanupCategory.Cache;
        }

        if (segments.Any(s => TempSegments.Contains(s)))
        {
            return CleanupCategory.Temp;
        }

        return CleanupCategory.Other;
    }

    public CleanupRisk RiskForCategory(CleanupCategory category) => category switch
    {
        CleanupCategory.Cache => CleanupRisk.Low,
        CleanupCategory.Temp => CleanupRisk.Low,
        CleanupCategory.RecycleBin => CleanupRisk.Low,
        CleanupCategory.Leftover => CleanupRisk.Medium,
        CleanupCategory.DevToolchain => CleanupRisk.Medium,
        CleanupCategory.SystemFile => CleanupRisk.Medium,
        CleanupCategory.InstalledApp => CleanupRisk.High,
        CleanupCategory.UserData => CleanupRisk.High,
        _ => CleanupRisk.High
    };

    /// <summary>
    /// Риск для пути: папки апдейтеров (`*-updater`) регенерируются при следующем обновлении,
    /// поэтому относятся к низкому риску, хотя категоризируются как Leftover.
    /// Папки программ и пользовательские данные не имеют признаков кэша/Temp и получают
    /// категорию Other → высокий риск (не предлагать к автоудалению).
    /// </summary>
    public CleanupRisk RiskForPath(string path)
    {
        if (SplitSegments(path).Any(IsUpdater))
        {
            return CleanupRisk.Low;
        }

        return RiskForCategory(CategorizePath(path));
    }

    /// <summary>Действие по умолчанию, выводимое из уровня риска (FR-1.6).</summary>
    public CleanupDefaultAction DefaultAction(CleanupRisk risk) => risk switch
    {
        CleanupRisk.Low => CleanupDefaultAction.Clean,
        CleanupRisk.Medium => CleanupDefaultAction.Ask,
        _ => CleanupDefaultAction.Keep
    };

    /// <summary>
    /// Действие по умолчанию для объекта: используемые объекты (IN_USE) никогда не
    /// предлагаются к автоудалению (FR-1.7), остальные — по уровню риска.
    /// </summary>
    public CleanupDefaultAction DefaultActionFor(CleanupItem item) =>
        item.InUse ? CleanupDefaultAction.Keep : DefaultAction(item.Risk);

    private static bool IsUpdater(string segment) =>
        segment.EndsWith("-updater", StringComparison.OrdinalIgnoreCase);

    private static bool IsAndroidStudioLeftover(string segment) =>
        segment.StartsWith("androidstudio", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SplitSegments(string path)
    {
        foreach (var part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length > 0)
            {
                yield return part;
            }
        }
    }
}
