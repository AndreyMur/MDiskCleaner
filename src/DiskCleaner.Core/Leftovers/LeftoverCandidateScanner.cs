using DiskCleaner.Core.Abstractions;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// Скан «папка → кандидат-остаток» (модуль 03, Tracer Bullet):
/// перебираются только верхние уровни ключевых каталогов (без рекурсивного обхода,
/// NFR ≤ 60 с), каждый каталог сопоставляется с белым списком реестра Uninstall
/// (FR-3.1), а несопоставленные и каталоги-апдейтеры становятся кандидатами
/// <see cref="LeftoverCandidate"/> с размером и датой изменения (FR-3.2).
/// </summary>
public sealed class LeftoverCandidateScanner
{
    public const string UpdatersGroupName = "Остатки апдейтеров";

    private readonly Abstractions.IEnvironment _environment;

    public LeftoverCandidateScanner(Abstractions.IEnvironment? environment = null)
    {
        _environment = environment ?? new Environment.EnvironmentProvider();
    }

    public IReadOnlyList<LeftoverCandidate> Scan(
        IReadOnlyList<InstalledApp> installedApps,
        IReadOnlyCollection<string>? exclusions = null)
    {
        var whitelist = new InstalledWhitelist(installedApps, _environment);
        var excluded = exclusions ?? Array.Empty<string>();
        var candidates = new List<LeftoverCandidate>();

        foreach (var root in LeftoverScanRoots.Resolve(_environment))
        {
            ScanRoot(root, whitelist, excluded, candidates);
        }

        ScanLocalAppDataProgramsUpdaters(excluded, candidates);

        return candidates;
    }

    private void ScanRoot(
        LeftoverScanRoot root,
        InstalledWhitelist whitelist,
        IReadOnlyCollection<string> excluded,
        List<LeftoverCandidate> candidates)
    {
        IReadOnlyList<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(root.Path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            if (ReservedFolderNames.IsReserved(root.Kind, name) ||
                ExclusionsStore.IsExcluded(excluded, directory, name))
            {
                continue;
            }

            if (UpdaterFolderNames.IsUpdaterFolder(name))
            {
                AddUpdater(root, directory, name, candidates);
            }
            else if (!whitelist.IsKnownPath(directory))
            {
                AddUnmatched(root, directory, name, candidates);
            }
        }
    }

    private void AddUpdater(
        LeftoverScanRoot root,
        string directory,
        string name,
        List<LeftoverCandidate> candidates)
    {
        candidates.Add(new LeftoverCandidate
        {
            Path = directory,
            DisplayName = name,
            GroupName = UpdatersGroupName,
            Reason = LeftoverReason.UpdaterFolder,
            ReasonText = "Имя соответствует маске апдейтера (*-updater/updater/update/_updater): каталог остаётся после установки приложения из инсталлятора (FR-3.2).",
            Risk = CleanupRisk.Low,
            Category = CleanupCategory.Leftover,
            RequiresAdmin = root.RequiresAdmin,
            SizeBytes = LeftoverDirectoryMeasurer.MeasureSizeBytes(directory),
            LastWriteTimeUtc = LeftoverDirectoryMeasurer.GetLastWriteTimeUtc(directory)
        });
    }

    private void AddUnmatched(
        LeftoverScanRoot root,
        string directory,
        string name,
        List<LeftoverCandidate> candidates)
    {
        candidates.Add(new LeftoverCandidate
        {
            Path = directory,
            DisplayName = name,
            GroupName = root.GroupName,
            Reason = LeftoverReason.NotInUninstallRegistry,
            ReasonText = "Каталог верхнего уровня не сопоставлен ни с одним InstallLocation/DisplayName из реестра Uninstall (FR-3.1).",
            Risk = CleanupRisk.Medium,
            Category = CleanupCategory.Leftover,
            RequiresAdmin = root.RequiresAdmin
        });
    }

    /// <summary>
    /// Апдейтеры, установленные вместе с приложением в <c>%LOCALAPPDATA%\Programs</c>
    /// (например <c>lm-studio-updater</c>) — находятся на уровень глубже верхнего корня.
    /// </summary>
    private void ScanLocalAppDataProgramsUpdaters(
        IReadOnlyCollection<string> excluded,
        List<LeftoverCandidate> candidates)
    {
        var programs = Path.Combine(_environment.LocalApplicationData, "Programs");
        if (!Directory.Exists(programs))
        {
            return;
        }

        var root = new LeftoverScanRoot(
            LeftoverRootKind.LocalApplicationData,
            programs,
            "Осиротевшие каталоги в %LOCALAPPDATA%",
            RequiresAdmin: false);

        IReadOnlyList<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(programs).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            if (!UpdaterFolderNames.IsUpdaterFolder(name) ||
                ExclusionsStore.IsExcluded(excluded, directory, name))
            {
                continue;
            }

            AddUpdater(root, directory, name, candidates);
        }
    }
}
