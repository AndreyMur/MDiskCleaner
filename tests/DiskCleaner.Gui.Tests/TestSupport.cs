using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Scanning;

namespace DiskCleaner.Gui.Tests;

/// <summary>Подмена координатора анализа: возвращает синтетический план без IO.</summary>
internal sealed class FakeAnalysisCoordinator : IAnalysisCoordinator
{
    public Func<AnalysisRunOptions, AnalysisResult>? Handler { get; set; }

    public AnalysisRunOptions? LastOptions { get; private set; }

    public int Calls { get; private set; }

    public Task<AnalysisResult> RunAsync(
        AnalysisRunOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastOptions = options;
        return Task.FromResult(Handler?.Invoke(options) ?? new AnalysisResult());
    }
}

/// <summary>Синтетический план очистки на «синтетических данных» для smoke-тестов экрана (#40).</summary>
internal static class SamplePlan
{
    public static CleanupItem CacheLeaf(
        string key,
        string displayName,
        long sizeBytes,
        bool inUse = false,
        string? group = "Менеджеры пакетов") =>
        Leaf(key, displayName, CleanupCategory.Cache, CleanupRisk.Low, sizeBytes, group, inUse: inUse);

    public static AnalysisResult Build()
    {
        var leaves = new List<CleanupItem>
        {
            CacheLeaf("cache:npm", "npm cache", 2_000_000),
            CacheLeaf("cache:pip", "pip cache", 1_000_000),
            CacheLeaf("cache:vscode", "VS Code Cache", 500_000, inUse: true, group: "VS Code"),
            Leaf(
                "dev:android-ndk",
                "Android SDK (ndk)",
                CleanupCategory.DevToolchain,
                CleanupRisk.Medium,
                3_000_000,
                group: "Android SDK"),
            Leaf(
                "app:old-jdk8",
                "Oracle JDK 8",
                CleanupCategory.InstalledApp,
                CleanupRisk.Medium,
                400_000_000,
                group: "Oracle",
                reviewReason: "Review manually: подозрительный издатель",
                requiresAdmin: true),
            Leaf(
                "recycle:c",
                "Корзина (C:)",
                CleanupCategory.RecycleBin,
                CleanupRisk.Low,
                7_000,
                group: "Системная очистка")
        };

        var tree = new TreeBuilder().Build(leaves);
        return new AnalysisResult
        {
            CategoryTree = tree,
            Items = leaves,
            InUseItems = leaves.Count(l => l.InUse),
            Elapsed = TimeSpan.FromMilliseconds(120)
        };
    }

    private static CleanupItem Leaf(
        string key,
        string displayName,
        CleanupCategory category,
        CleanupRisk risk,
        long? sizeBytes,
        string? group = null,
        string? reviewReason = null,
        bool inUse = false,
        bool requiresAdmin = false)
    {
        return new CleanupItem
        {
            Key = key,
            Path = @"C:\fake\" + key,
            DisplayName = displayName,
            GroupName = group,
            Category = category,
            Risk = risk,
            SizeBytes = sizeBytes,
            ReviewReason = reviewReason,
            InUse = inUse,
            RequiresAdmin = requiresAdmin
        };
    }
}
