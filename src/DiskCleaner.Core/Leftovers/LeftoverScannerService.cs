using DiskCleaner.Core.Abstractions;
using DiskCleaner.Core.Environment;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Core.Leftovers;

public sealed class LeftoverScannerService
{
    private const int MaxEnumeratedEntriesForSmallCheck = 12;

    private static readonly HashSet<string> KnownJunkBrands = new(StringComparer.OrdinalIgnoreCase)
    {
        "4ddig file repair", "4ddig", "windows4ddig", "windows4ddigfilerepair", "wondershare",
        "wondershareupdate", "pdfconverter", "360", "viewplaycap", "activation-renewal", "pachca"
    };

    private static readonly HashSet<string> SystemProgramFilesFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "common files", "internet explorer", "windows nt", "windows defender", "windows photo viewer",
        "windows mail", "windows media player", "windows portable devices", "reference assemblies",
        "msbuild", "uninstall information", "windowsapps", "windowspowershell", "microsoft shared",
        "windows kits", "microsoft visual studio", "dotnet", "git", "package manager", "windows security",
        "modifiablewindowsapps", "windows update"
    };

    private readonly Abstractions.IEnvironment _environment;
    private readonly IProcessInspector _processInspector;

    public LeftoverScannerService(
        Abstractions.IEnvironment? environment = null,
        IProcessInspector? processInspector = null)
    {
        _environment = environment ?? new EnvironmentProvider();
        _processInspector = processInspector ?? new ProcessInspector();
    }

    public IReadOnlyList<CleanupItem> Scan(
        IReadOnlyList<InstalledApp> installedApps,
        IReadOnlyCollection<string>? exclusions = null)
    {
        var whitelist = new InstalledWhitelist(installedApps);
        var runningProcesses = _processInspector.GetRunningProcesses();
        var excluded = exclusions ?? Array.Empty<string>();
        var results = new List<CleanupItem>();

        ScanUpdaters(excluded, results);
        ScanOrphanFolders(whitelist, excluded, runningProcesses, results);
        ScanRemovedAppConfigs(whitelist, excluded, runningProcesses, results);
        ScanWindowsOld(excluded, runningProcesses, results);

        return results;
    }

    private void ScanUpdaters(IReadOnlyCollection<string> excluded, List<CleanupItem> results)
    {
        var roots = new[]
        {
            _environment.LocalApplicationData,
            Path.Combine(_environment.LocalApplicationData, "Programs")
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (!LooksLikeUpdaterFolder(name) ||
                    ExclusionsStore.IsExcluded(excluded, directory, name))
                {
                    continue;
                }

                results.Add(new CleanupItem
                {
                    Key = "leftover:updater:" + directory,
                    Path = directory,
                    DisplayName = name,
                    GroupName = "Остатки апдейтеров",
                    Category = CleanupCategory.Leftover,
                    Risk = CleanupRisk.Low,
                    Target = CleanupTarget.Directory,
                    Description = "Папка апдейтера осталась после установки/удаления приложения.",
                    Warning = "Обычно регенерируется при следующем обновлении приложения.",
                    RequiresAdmin = false,
                    AllowDirectDelete = true
                });
            }
        }
    }

    private void ScanOrphanFolders(
        InstalledWhitelist whitelist,
        IReadOnlyCollection<string> excluded,
        IReadOnlyList<RunningProcessInfo> runningProcesses,
        List<CleanupItem> results)
    {
        var roots = new[]
        {
            (Path: _environment.ProgramFiles, Group: "Осиротевшие папки в Program Files"),
            (Path: _environment.ProgramFilesX86, Group: "Осиротевшие папки в Program Files (x86)"),
            (Path: _environment.ProgramData, Group: "Осиротевшие папки в ProgramData")
        };

        foreach (var (root, group) in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (ExclusionsStore.IsExcluded(excluded, directory, name) ||
                    SystemProgramFilesFolders.Contains(name) ||
                    whitelist.IsKnownFolder(name))
                {
                    continue;
                }

                if (HasRunningProcessUnder(directory, runningProcesses))
                {
                    continue;
                }

                var isJunkBrand = KnownJunkBrands.Contains(name);
                var nearEmpty = IsNearEmpty(directory);
                if (!isJunkBrand && !nearEmpty)
                {
                    continue;
                }

                results.Add(new CleanupItem
                {
                    Key = "leftover:orphan:" + directory,
                    Path = directory,
                    DisplayName = name,
                    GroupName = group,
                    Category = CleanupCategory.Leftover,
                    Risk = isJunkBrand ? CleanupRisk.Medium : CleanupRisk.Low,
                    Target = CleanupTarget.Directory,
                    Description = isJunkBrand
                        ? "Известный бренд удалённой программы; в реестре Uninstall отсутствует."
                        : "Каталог пуст или почти пуст; в реестре Uninstall отсутствует.",
                    Warning = "Требуется подтверждение: убедитесь, что программа не используется.",
                    RequiresAdmin = true,
                    AllowDirectDelete = true
                });
            }
        }
    }

    private void ScanRemovedAppConfigs(
        InstalledWhitelist whitelist,
        IReadOnlyCollection<string> excluded,
        IReadOnlyList<RunningProcessInfo> runningProcesses,
        List<CleanupItem> results)
    {
        var androidStudioInstalled = whitelist.ContainsName("Android Studio");
        if (androidStudioInstalled)
        {
            return;
        }

        var configRoot = Path.Combine(_environment.LocalApplicationData, "Google");
        if (Directory.Exists(configRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(configRoot))
            {
                var name = Path.GetFileName(directory);
                if (ExclusionsStore.IsExcluded(excluded, directory, name) ||
                    !name.StartsWith("AndroidStudio", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(new CleanupItem
                {
                    Key = "leftover:config:" + directory,
                    Path = directory,
                    DisplayName = name,
                    GroupName = "Конфиги удалённых программ",
                    Category = CleanupCategory.Leftover,
                    Risk = CleanupRisk.Medium,
                    Target = CleanupTarget.Directory,
                    Description = "Конфигурация Android Studio, а само приложение не установлено.",
                    Warning = "Удаление сбросит настройки IDE, если она будет установлена заново.",
                    RequiresAdmin = false,
                    AllowDirectDelete = true
                });
            }
        }

        var androidDotFolder = Path.Combine(_environment.UserProfile, ".android");
        if (Directory.Exists(androidDotFolder) &&
            !ExclusionsStore.IsExcluded(excluded, androidDotFolder, ".android") &&
            !HasRunningProcessUnder(androidDotFolder, runningProcesses))
        {
            results.Add(new CleanupItem
            {
                Key = "leftover:config:.android",
                Path = androidDotFolder,
                DisplayName = ".android",
                GroupName = "Конфиги удалённых программ",
                Category = CleanupCategory.Leftover,
                Risk = CleanupRisk.High,
                Target = CleanupTarget.Directory,
                Description = "Конфигурация Android (AVD/ключи). Android Studio не установлена.",
                Warning = "Содержит виртуальные устройства и ключи подписи — удалять только при явном согласии.",
                RequiresAdmin = false,
                AllowDirectDelete = false
            });
        }
    }

    private void ScanWindowsOld(
        IReadOnlyCollection<string> excluded,
        IReadOnlyList<RunningProcessInfo> runningProcesses,
        List<CleanupItem> results)
    {
        var driveRoot = Path.GetPathRoot(_environment.ProgramFiles);
        if (string.IsNullOrEmpty(driveRoot) || !Directory.Exists(driveRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(driveRoot))
        {
            var name = Path.GetFileName(directory);
            if (!name.StartsWith("Windows.old", StringComparison.OrdinalIgnoreCase) ||
                ExclusionsStore.IsExcluded(excluded, directory, name) ||
                HasRunningProcessUnder(directory, runningProcesses))
            {
                continue;
            }

            results.Add(new CleanupItem
            {
                Key = "leftover:windowsold:" + directory,
                Path = directory,
                DisplayName = name,
                GroupName = "Предыдущая версия Windows",
                Category = CleanupCategory.SystemFile,
                Risk = CleanupRisk.Medium,
                Target = CleanupTarget.Directory,
                Description = "Каталог предыдущей установки Windows.",
                Warning = "Рекомендуется штатная очистка (Очистка диска / Storage Sense). Прямое удаление — только с админ-правами.",
                RequiresAdmin = true,
                AllowDirectDelete = true
            });
        }
    }

    private static bool HasRunningProcessUnder(
        string directory,
        IReadOnlyList<RunningProcessInfo> runningProcesses)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        foreach (var process in runningProcesses)
        {
            var executable = process.ExecutablePath;
            if (!string.IsNullOrEmpty(executable) &&
                executable.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsNearEmpty(string directory)
    {
        try
        {
            var count = 0;
            foreach (var _ in Directory.EnumerateFileSystemEntries(directory))
            {
                count++;
                if (count > MaxEnumeratedEntriesForSmallCheck)
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeUpdaterFolder(string name) =>
        name.EndsWith("-updater", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_updater", StringComparison.OrdinalIgnoreCase);
}
