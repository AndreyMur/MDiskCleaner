using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Analysis;

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
        ".rustup", ".jdks", "jdks", "android", "sdk", "program files (x86)", "android studio"
    };

    public CleanupCategory CategorizePath(string path)
    {
        var segments = SplitSegments(path);
        if (segments.Any(IsUpdater) || segments.Any(IsAndroidStudioLeftover))
        {
            return CleanupCategory.Leftover;
        }

        if (segments.Any(s => CacheSegments.Contains(s)))
        {
            return CleanupCategory.Cache;
        }

        if (segments.Any(s => ToolchainSegments.Contains(s)))
        {
            return CleanupCategory.DevToolchain;
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
        CleanupCategory.DevToolchain => CleanupRisk.Medium,
        CleanupCategory.Leftover => CleanupRisk.Medium,
        CleanupCategory.SystemFile => CleanupRisk.Medium,
        _ => CleanupRisk.High
    };

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
