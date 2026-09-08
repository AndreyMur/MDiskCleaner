using DiskCleaner.Core.Elevated;
using Serilog;

namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Массовое удаление MSI-компонентов Windows SDK одной версии (фаза 23, модуль 04):
/// <list type="bullet">
/// <item>все GUID записей версии (общий <see cref="InstalledApp.DisplayVersion"/>, например
/// <c>10.1.19041.5609</c>) собираются и удаляются последовательно <c>msiexec /x {GUID}</c>
/// через общий исполнитель (FR-4.7); компоненты другой версии (например 26100) в набор
/// не попадают — изоляция по DisplayVersion;</item>
/// <item>после каждого прохода реестр перечитывается и компоненты, которые «вернулись» или не
/// удалились с первого раза (реальный кейс: exit 0 при оставшихся записях), удаляются повторно
/// (FR-4.8) — ограничено <see cref="SdkBulkUninstallOptions.MaxPasses"/>;</item>
/// <item>после полного удаления версии (записей в реестре больше нет) удаляются осиротевшие
/// каталоги версии (Include/Lib/bin, FR-4.9) и очищается <c>Package Cache\{code}</c> удалённых
/// продуктов (FR-4.14);</item>
/// <item>повторный прогон на уже удалённой версии идемпотентен: записей нет — это успех без
/// ошибок (NFR).</item>
/// </list>
/// </summary>
public sealed class SdkBulkUninstallService
{
    private readonly UninstallExecutionService _executor;
    private readonly IElevatedRunner _cleanupRunner;
    private readonly string? _programDataRoot;
    private readonly string? _programFilesRoot;
    private readonly string? _programFilesX86Root;

    public SdkBulkUninstallService(
        UninstallExecutionService? executor = null,
        IElevatedRunner? cleanupRunner = null,
        string? programDataRoot = null,
        string? programFilesRoot = null,
        string? programFilesX86Root = null)
    {
        _executor = executor ?? new UninstallExecutionService(programDataRoot: programDataRoot);
        _cleanupRunner = cleanupRunner ?? new ElevatedProcessLauncher();
        _programDataRoot = programDataRoot;
        _programFilesRoot = programFilesRoot ??
                            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
        _programFilesX86Root = programFilesX86Root ??
                               System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);
    }

    /// <summary>
    /// Удаляет все записи версии SDK (общий DisplayVersion). По умолчанию инвентаризация
    /// перечитывается из реестра Uninstall; в тестах передаётся <paramref name="readInventory"/>.
    /// </summary>
    public async Task<SdkBulkUninstallReport> UninstallVersionAsync(
        string displayVersion,
        Func<IReadOnlyList<InstalledApp>>? readInventory = null,
        SdkBulkUninstallOptions? options = null,
        IProgress<SdkBulkUninstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new SdkBulkUninstallOptions();
        var componentResults = new List<SdkComponentResult>();
        var attemptedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pass = 0;

        while (pass < opts.MaxPasses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var apps = ReadInventory(readInventory);
            var components = WindowsSdkFamily.ComponentsOfVersion(apps, displayVersion);
            if (components.Count == 0)
            {
                break;
            }

            pass++;
            progress?.Report(new SdkBulkUninstallProgress(pass, 0, components.Count, string.Empty,
                $"Проход {pass}: удаление {components.Count} компонентов SDK {displayVersion}…"));

            var items = components
                .Select(app => _executor.BuildItem(app))
                .ToList();

            var executionReport = await _executor.UninstallAsync(
                items,
                new UninstallExecutionOptions { CommandTimeoutSec = opts.CommandTimeoutSec },
                cancellationToken);

            var index = 0;
            foreach (var entry in executionReport.Entries)
            {
                index++;
                var code = entry.Item.App.ProductCode;
                attemptedCodes.Add(code);
                componentResults.Add(new SdkComponentResult(
                    code,
                    entry.Item.DisplayName,
                    displayVersion,
                    pass,
                    entry.Outcome,
                    entry.ExitCode,
                    entry.Note));

                progress?.Report(new SdkBulkUninstallProgress(
                    pass,
                    index,
                    executionReport.Entries.Count,
                    entry.Item.DisplayName,
                    $"Компонент {entry.Item.DisplayName}: исход {entry.Outcome}."));
            }
        }

        // FR-4.8: итоговое перечитывание реестра после всех проходов.
        var finalApps = ReadInventory(readInventory);
        var remaining = WindowsSdkFamily.ComponentsOfVersion(finalApps, displayVersion);
        var fullyRemoved = remaining.Count == 0;

        // Зачистка выполняется только когда записей версии в реестре больше нет (FR-4.9/4.14).
        var folderCleanups = fullyRemoved
            ? await CleanupOrphanedSdkFoldersAsync(opts, finalApps, displayVersion, cancellationToken)
            : Array.Empty<SdkFolderCleanupResult>();

        var cacheCleanups = fullyRemoved
            ? await CleanupPackageCacheAsync(opts, attemptedCodes, finalApps, cancellationToken)
            : Array.Empty<SdkPackageCacheCleanupResult>();

        var report = new SdkBulkUninstallReport
        {
            DisplayVersion = displayVersion,
            PassesUsed = pass,
            Components = componentResults,
            RemainingProductCodes = remaining.Select(a => a.ProductCode).ToList(),
            RemainingDisplayNames = remaining.Select(a => a.DisplayName).ToList(),
            FolderCleanups = folderCleanups,
            PackageCacheCleanups = cacheCleanups
        };

        Audit(pass, componentResults, report);
        return report;
    }

    private async Task<IReadOnlyList<SdkFolderCleanupResult>> CleanupOrphanedSdkFoldersAsync(
        SdkBulkUninstallOptions opts,
        IReadOnlyList<InstalledApp> remainingApps,
        string displayVersion,
        CancellationToken cancellationToken)
    {
        if (!opts.CleanupOrphanedSdkFolders)
        {
            return Array.Empty<SdkFolderCleanupResult>();
        }

        var kitsRoot = WindowsKitsOrphanScanner.FindKitsRoot(_programFilesRoot, _programFilesX86Root);
        if (kitsRoot is null)
        {
            return Array.Empty<SdkFolderCleanupResult>();
        }

        var scanner = new WindowsKitsOrphanScanner();
        var orphans = scanner.FindOrphanedVersionFolders(kitsRoot, remainingApps, displayVersion);
        if (orphans.Count == 0)
        {
            return Array.Empty<SdkFolderCleanupResult>();
        }

        var steps = orphans
            .Select((folder, index) => new ElevatedStep
            {
                Id = $"sdk-folder:{index}",
                Kind = ElevatedStepKind.DeletePath,
                Path = folder,
                TimeoutSec = opts.CleanupTimeoutSec
            })
            .ToList();

        var journal = await _cleanupRunner.RunAsync(new ElevatedScenario { Steps = steps }, cancellationToken);
        var byId = journal.Results.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

        return orphans
            .Select((folder, index) =>
            {
                var key = $"sdk-folder:{index}";
                var stepResult = byId.TryGetValue(key, out var value) ? value : null;
                return new SdkFolderCleanupResult(
                    folder,
                    stepResult?.Success == true,
                    stepResult?.Note ?? stepResult?.Error ?? "Каталог версии SDK не удалён.");
            })
            .ToList();
    }

    private async Task<IReadOnlyList<SdkPackageCacheCleanupResult>> CleanupPackageCacheAsync(
        SdkBulkUninstallOptions opts,
        IReadOnlySet<string> attemptedCodes,
        IReadOnlyList<InstalledApp> finalApps,
        CancellationToken cancellationToken)
    {
        if (!opts.CleanupPackageCache || attemptedCodes.Count == 0)
        {
            return Array.Empty<SdkPackageCacheCleanupResult>();
        }

        var cleaner = new PackageCacheCleaner();
        var cacheRoot = PackageCacheCleaner.DefaultCacheRoot(_programDataRoot);
        var folders = cleaner.FindOrphanedBundleFolders(cacheRoot, attemptedCodes, finalApps);
        if (folders.Count == 0)
        {
            return Array.Empty<SdkPackageCacheCleanupResult>();
        }

        var steps = folders
            .Select((folder, index) => new ElevatedStep
            {
                Id = $"cache-folder:{index}",
                Kind = ElevatedStepKind.DeletePath,
                Path = folder,
                TimeoutSec = opts.CleanupTimeoutSec
            })
            .ToList();

        var journal = await _cleanupRunner.RunAsync(new ElevatedScenario { Steps = steps }, cancellationToken);
        var byId = journal.Results.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

        var results = new List<SdkPackageCacheCleanupResult>(folders.Count);
        for (var index = 0; index < folders.Count; index++)
        {
            var key = $"cache-folder:{index}";
            var stepResult = byId.TryGetValue(key, out var value) ? value : null;
            results.Add(new SdkPackageCacheCleanupResult(
                Path.GetFileName(folders[index].TrimEnd(Path.DirectorySeparatorChar)),
                folders[index],
                stepResult?.Success == true,
                stepResult?.Note ?? stepResult?.Error ?? "Папка Package Cache не удалена."));
        }

        return results;
    }

    private static IReadOnlyList<InstalledApp> ReadInventory(Func<IReadOnlyList<InstalledApp>>? readInventory)
    {
        if (readInventory is not null)
        {
            return readInventory();
        }

        return new UninstallRegistryService().ReadInstalledApps();
    }

    private static void Audit(
        int pass,
        IReadOnlyList<SdkComponentResult> components,
        SdkBulkUninstallReport report)
    {
        Log.Information(
            "Sdk bulk uninstall: version={Version} passes={Passes} ok={Ok} failed={Failed} remaining={Remaining} folders={Folders} cacheCleanups={Cache}",
            report.DisplayVersion,
            pass,
            report.OkCount,
            report.FailedCount,
            report.RemainingProductCodes.Count,
            report.FolderCleanups.Count,
            report.PackageCacheCleanups.Count);
    }
}
