using System.IO;
using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Models;
using DiskCleaner.Gui.Services;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

/// <summary>Контекст теста экрана очистки кэшей: подмены + ViewModel.</summary>
internal sealed record CacheCleanHarness(
    FakeAnalysisCoordinator Coordinator,
    FakeCacheCleanExecutor Executor,
    FakeCacheCleanDialogs Dialogs,
    CacheCleanViewModel ViewModel);

/// <summary>Помощник отметки «карточек» по заголовку для тестов выбора (NFR G3).</summary>
internal static class HarnessHelper
{
    public static void Select(CacheCleanViewModel viewModel, params string[] titles)
    {
        var byTitle = new HashSet<string>(titles, System.StringComparer.Ordinal);
        var actions = viewModel.Groups.SelectMany(g => g.Actions).Where(a => byTitle.Contains(a.Title)).ToList();
        Assert.Equal(titles.Length, actions.Count);
        foreach (var action in actions)
        {
            action.IsSelected = true;
        }
    }
}

/// <summary>
/// Подмены и синтетические данные для тестов экрана «Очистка кэшей» (фаза 5 модуля 02):
/// листья кэшей с метаданными справочника (ключ, команда, согласие), фейковый диск для
/// защитного режима FR-2.12, фейковые исполнитель и диалоги.
/// </summary>
internal static class CacheCleanTestSupport
{
    /// <summary>Диск с 60% свободного места — защитный режим «не трогать» не срабатывает.</summary>
    public static IDriveSpaceService PlentyOfSpace() => new FakeDriveSpace();

    public static CacheCleanHarness CreateHarness(
        IReadOnlyList<CleanupItem> leaves,
        string? scannedDiskRoot = null)
    {
        var coordinator = new FakeAnalysisCoordinator
        {
            Handler = _ => new AnalysisResult
            {
                Items = leaves,
                CategoryTree = Array.Empty<CleanupItem>(),
                Elapsed = TimeSpan.FromMilliseconds(80),
                InUseItems = leaves.Count(l => l.InUse)
            }
        };
        var executor = new FakeCacheCleanExecutor();
        var dialogs = new FakeCacheCleanDialogs();

        var vm = new CacheCleanViewModel(
            coordinator,
            new CacheCleanPlanner(driveSpace: PlentyOfSpace()),
            executor,
            dialogs,
            scannedDiskRoot);

        return new CacheCleanHarness(coordinator, executor, dialogs, vm);
    }

    public static CleanupItem NpmCache(
        string path = @"C:\Users\dev\AppData\Local\npm-cache",
        long size = 1_500_000_000,
        bool inUse = false) =>
        Cache(
            id: "npm-cache",
            path: path,
            displayName: "npm cache",
            group: "npm",
            category: CleanupCategory.Cache,
            risk: CleanupRisk.Low,
            size: size,
            inUse: inUse,
            cleanCommand: "cmd.exe /c npm cache clean --force",
            cleanFile: "cmd.exe",
            cleanArgs: "/c npm cache clean --force",
            warning: "Пакеты будут загружены заново при следующей установке/сборке.");

    public static CleanupItem PnpmStoreOnD(string path = @"D:\dev\pnpm-store", long size = 700_000_000) =>
        Cache(
            id: "pnpm-store",
            path: path,
            displayName: "pnpm store",
            group: "pnpm",
            category: CleanupCategory.Cache,
            risk: CleanupRisk.Low,
            size: size,
            cleanCommand: "cmd.exe /c pnpm store prune",
            cleanFile: "cmd.exe",
            cleanArgs: "/c pnpm store prune",
            warning: "Пакеты, не используемые проектами, будут загружены заново.");

    public static CleanupItem GradleCaches(string path = @"C:\Users\dev\.gradle\caches", long size = 600_000_000) =>
        Cache(
            id: "gradle-caches",
            path: path,
            displayName: "Gradle caches",
            group: "Gradle",
            category: CleanupCategory.Cache,
            risk: CleanupRisk.Low,
            size: size,
            warning: "Зависимости будут скачаны заново при следующей сборке.");

    public static CleanupItem GradleJdks(string path = @"C:\Users\dev\.gradle\jdks", long size = 900_000_000) =>
        Cache(
            id: "gradle-jdks",
            path: path,
            displayName: "Gradle JDK toolchains",
            group: "Gradle",
            category: CleanupCategory.DevToolchain,
            risk: CleanupRisk.Medium,
            size: size,
            warning: "JDK будет установлен повторно при следующей сборке, требующей toolchain.");

    public static CleanupItem VscodeGpuCache(string path = @"C:\Users\dev\AppData\Roaming\Code\GPUCache", long size = 30_000_000) =>
        Cache(
            id: "vscode-gpucache",
            path: path,
            displayName: "VS Code GPUCache",
            group: "VS Code",
            category: CleanupCategory.Cache,
            risk: CleanupRisk.Low,
            size: size);

    public static CleanupItem VscodeLogsInUse(string path = @"C:\Users\dev\AppData\Roaming\Code\logs", long size = 40_000_000) =>
        Cache(
            id: "vscode-logs",
            path: path,
            displayName: "VS Code logs",
            group: "VS Code",
            category: CleanupCategory.Cache,
            risk: CleanupRisk.Low,
            size: size,
            inUse: true,
            ownerProcessNames: ["Code", "Code - Insiders", "VSCodium"]);

    /// <summary>Каталог VS Code вне whitelist кэш-подпапок (например, User) — очистка запрещена (FR-2.10).</summary>
    public static CleanupItem VscodeUserPathBlocked()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Cache(
            id: "vscode-user",
            path: Path.Combine(appData, "Code", "User"),
            displayName: "VS Code User",
            group: "VS Code",
            category: CleanupCategory.Cache,
            risk: CleanupRisk.Low,
            size: 10_000_000);
    }

    /// <summary>Типовой набор кэшей эталонного профиля (без кэшей вне диска).</summary>
    public static IReadOnlyList<CleanupItem> TypicalProfileLeaves() =>
        new CleanupItem[]
        {
            NpmCache(),
            GradleCaches(),
            GradleJdks(),
            VscodeGpuCache(),
            VscodeLogsInUse()
        };

    private static CleanupItem Cache(
        string id,
        string path,
        string displayName,
        string? group,
        CleanupCategory category,
        CleanupRisk risk,
        long? size,
        bool inUse = false,
        IReadOnlyList<string>? ownerProcessNames = null,
        string? cleanCommand = null,
        string? cleanFile = null,
        string? cleanArgs = null,
        string? warning = null)
    {
        var fullPath = Path.GetFullPath(path);
        return new CleanupItem
        {
            Key = $"{id}:{fullPath}",
            Path = fullPath,
            DisplayName = displayName,
            GroupName = group,
            Category = category,
            Risk = risk,
            Target = CleanupTarget.Directory,
            SizeBytes = size,
            InUse = inUse,
            OwnerProcessNames = ownerProcessNames ?? Array.Empty<string>(),
            CleanCommand = cleanCommand,
            CleanCommandFile = cleanFile,
            CleanCommandArgs = cleanArgs,
            Warning = warning
        };
    }
}

/// <summary>Фейковый источник свободного места: всегда 60% свободно.</summary>
internal sealed class FakeDriveSpace : IDriveSpaceService
{
    public CacheDriveSpace? GetDriveSpace(string path)
    {
        string root;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(path)) ?? @"C:\";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return new CacheDriveSpace(root, 100_000_000_000, 160_000_000_000);
    }
}

/// <summary>Фейковый исполнитель: запоминает вход и возвращает синтетический отчёт.</summary>
internal sealed class FakeCacheCleanExecutor : ICacheCleanExecutor
{
    public Func<IReadOnlyList<CleanupItem>, CleanOptions, CleanReport>? Handler { get; set; }

    public int Calls { get; private set; }

    public IReadOnlyList<CleanupItem>? LastItems { get; private set; }

    public CleanOptions? LastOptions { get; private set; }

    public Task<CleanReport> CleanAsync(
        IEnumerable<CleanupItem> selectedItems,
        CleanOptions? options = null,
        IProgress<CleanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastItems = selectedItems.ToList();
        LastOptions = options ?? new CleanOptions();
        var report = Handler?.Invoke(LastItems, LastOptions) ?? DefaultReport(LastItems, LastOptions);
        return Task.FromResult(report);
    }

    private static CleanReport DefaultReport(IReadOnlyList<CleanupItem> items, CleanOptions options)
    {
        var entries = items.Select(item => new CleanEntry(
            item,
            options.DryRun ? CleanOutcome.DryRun : CleanOutcome.DirectDeleted,
            options.DryRun || item.InUse ? 0 : item.EffectiveSizeBytes,
            options.DryRun ? "Будет очищено" : "Удалено напрямую")).ToList();

        return new CleanReport
        {
            Entries = entries,
            DryRun = options.DryRun,
            Elapsed = TimeSpan.FromMilliseconds(50)
        };
    }
}

/// <summary>Фейковые диалоги: запоминают текст подтверждения и показанный отчёт.</summary>
internal sealed class FakeCacheCleanDialogs : ICacheCleanDialogService
{
    public bool ConfirmResult { get; set; } = true;

    public string? LastConfirmTitle { get; private set; }

    public string? LastConfirmMessage { get; private set; }

    public CleanReport? ShownReport { get; private set; }

    public bool Confirm(string title, string message)
    {
        LastConfirmTitle = title;
        LastConfirmMessage = message;
        return ConfirmResult;
    }

    public void ShowReport(CleanReport report) => ShownReport = report;
}
