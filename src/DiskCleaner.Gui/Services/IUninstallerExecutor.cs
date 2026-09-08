using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Gui.Services;

/// <summary>
/// Исполнитель операций экрана «Деинсталляция и зачистка» (модуль 04, FR-4.5–4.14):
/// удаление отмеченных приложений (<see cref="UninstallExecutionService"/>), массовое удаление
/// версии Windows SDK (<see cref="SdkBulkUninstallService"/>, FR-4.7/4.8/4.9/4.14) и удаление
/// осиротевших записей Uninstall (только ветки реестра, FR-4.13). Абстракция позволяет
/// unit-тестам ViewModel подменять исполнение синтетическими отчётами.
/// </summary>
public interface IUninstallerExecutor
{
    /// <summary>Удаляет отмеченные приложения (пачка: HKCU — без повышения, LM — один UAC-подъём).</summary>
    Task<UninstallExecutionReport> UninstallAppsAsync(
        IReadOnlyList<InstalledApp> apps,
        UninstallExecutionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Массовое удаление всех MSI-компонентов версии Windows SDK с прогрессом по компонентам (FR-4.7).</summary>
    Task<SdkBulkUninstallReport> UninstallSdkVersionAsync(
        string displayVersion,
        SdkBulkUninstallOptions? options = null,
        IProgress<SdkBulkUninstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Удаляет осиротевшие записи Uninstall — только ветки реестра, по подтверждению (FR-4.13).</summary>
    Task<CleanReport> RemoveOrphanRecordsAsync(
        IReadOnlyList<UninstallOrphanRegistryMatch> orphanRecords,
        CancellationToken cancellationToken = default);
}

/// <summary>Адаптер поверх сервисов исполнения ядра модуля 04.</summary>
public sealed class UninstallerExecutor : IUninstallerExecutor
{
    private readonly UninstallExecutionService _executor;
    private readonly SdkBulkUninstallService _sdkBulk;
    private readonly PlanExecutor _planExecutor;
    private readonly UninstallOrphanRegistryScanner _orphanScanner;

    public UninstallerExecutor(
        UninstallExecutionService? executor = null,
        SdkBulkUninstallService? sdkBulk = null,
        PlanExecutor? planExecutor = null,
        UninstallOrphanRegistryScanner? orphanScanner = null)
    {
        _executor = executor ?? new UninstallExecutionService();
        _sdkBulk = sdkBulk ?? new SdkBulkUninstallService();
        _planExecutor = planExecutor ?? new PlanExecutor();
        _orphanScanner = orphanScanner ?? new UninstallOrphanRegistryScanner();
    }

    public Task<UninstallExecutionReport> UninstallAppsAsync(
        IReadOnlyList<InstalledApp> apps,
        UninstallExecutionOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _executor.UninstallAsync(apps, options, cancellationToken);

    public Task<SdkBulkUninstallReport> UninstallSdkVersionAsync(
        string displayVersion,
        SdkBulkUninstallOptions? options = null,
        IProgress<SdkBulkUninstallProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _sdkBulk.UninstallVersionAsync(displayVersion, options: options, progress: progress, cancellationToken: cancellationToken);

    public async Task<CleanReport> RemoveOrphanRecordsAsync(
        IReadOnlyList<UninstallOrphanRegistryMatch> orphanRecords,
        CancellationToken cancellationToken = default)
    {
        if (orphanRecords.Count == 0)
        {
            return new CleanReport
            {
                Entries = Array.Empty<CleanEntry>(),
                DryRun = false,
                Elapsed = TimeSpan.Zero
            };
        }

        var apps = orphanRecords.Select(m => m.App).ToList();
        var items = _orphanScanner.BuildCleanupItems(apps);
        return await _planExecutor.CleanAsync(items, new CleanOptions { DryRun = false }, cancellationToken: cancellationToken);
    }
}
