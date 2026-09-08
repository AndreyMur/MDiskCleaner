using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Поиск приложений без записи в реестре Uninstall (фаза 24, модуль 04, FR-4.10) — кейс
/// «4DDiG-типа»: программа удалена/установлена без записи в ARP, но её каталоги остались в
/// <c>Program Files</c>/<c>Program Files (x86)</c>/<c>ProgramData</c>. Кандидаты — верхнеуровневые
/// каталоги, которые не упомянуты ни одной записью установленного ПО и содержат исполняемые
/// файлы. Удаление выполняется только по подтверждению, для каталогов
/// <c>Program Files</c>/<c>ProgramData</c> — через повышенный процесс (FR-4.15); занятость
/// проверяется по процессам (<see cref="CleanupItem.OwnerProcessNames"/> и расположению exe).
/// </summary>
public sealed class UnrecordedAppScanner
{
    public const string GroupName = "Приложения без записи в реестре";

    /// <summary>
    /// Системные/общие каталоги, которые никогда не предлагаются как приложение без записи
    /// (сравнение имён верхнеуровневых каталогов без учёта регистра).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExcludedFolderNames =
    [
        "common files",
        "internet explorer",
        "windows defender",
        "windows kits",
        "windows mail",
        "windows media player",
        "windows nt",
        "windows photo viewer",
        "windows portable devices",
        "windows security",
        "windows side-by-side",
        "windowspowershell",
        "windows multimedia platform",
        "reference assemblies",
        "microsoft",
        "microsoft shared",
        "microsoft sdks",
        "microsoft visual studio",
        "microsoft sql server",
        "microsoft.net",
        "microsoft help",
        "microsoft onedrive",
        "package cache",
        "uso shared",
        "software distribution",
        "nuget packages",
        "dotnet"
    ];

    private readonly IUninstallFolderSizeProvider? _folderSize;

    public UnrecordedAppScanner(IUninstallFolderSizeProvider? folderSize = null)
    {
        _folderSize = folderSize;
    }

    /// <summary>
    /// Корни сканирования по умолчанию: существующие <c>Program Files</c>, <c>Program Files (x86)</c>
    /// и <c>ProgramData</c> (FR-4.10: удаление из них — только с админ-правами).
    /// </summary>
    public static IReadOnlyList<string> ResolveDefaultRoots(
        string? programFiles,
        string? programFilesX86,
        string? programData)
    {
        var roots = new List<string>(3);
        AddExistingRoot(roots, programFiles);
        AddExistingRoot(roots, programFilesX86);
        AddExistingRoot(roots, programData);
        return roots;
    }

    public IReadOnlyList<UnrecordedAppCandidate> Find(
        IReadOnlyList<string> roots,
        IReadOnlyList<InstalledApp> installedApps,
        UnrecordedAppScanOptions? options = null)
    {
        var opts = options ?? new UnrecordedAppScanOptions();
        Func<string, bool> exists = opts.DirectoryExists ?? (path => Directory.Exists(path));
        var excluded = new HashSet<string>(
            DefaultExcludedFolderNames.Concat(opts.ExcludedFolderNames ?? Array.Empty<string>()),
            StringComparer.OrdinalIgnoreCase);
        var referenced = ReferencedPaths(installedApps);
        var result = new List<UnrecordedAppCandidate>();

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !exists(root))
            {
                continue;
            }

            foreach (var directory in EnumerateTopLevelDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(name) ||
                    name.StartsWith('.') ||
                    excluded.Contains(name) ||
                    IsReferenced(directory, referenced))
                {
                    continue;
                }

                if (!TryCollectExecutables(directory, opts.MaxExeSearchDepth, out var owners))
                {
                    continue;
                }

                result.Add(new UnrecordedAppCandidate(
                    directory,
                    name,
                    _folderSize?.MeasureFolderBytes(directory),
                    RequiresAdmin: true,
                    owners));
            }
        }

        return result;
    }

    /// <summary>
    /// Шаги плана очистки для найденных каталогов — отдельная группа <see cref="GroupName"/>.
    /// Каждый шаг удаляет каталог напрямую (после проверки процессов) только по подтверждению
    /// пользователя; каталоги Program Files/ProgramData помечены как требующие прав администратора
    /// (elevated-шаг DeletePath, FR-4.15).
    /// </summary>
    public IReadOnlyList<CleanupItem> BuildCleanupItems(
        IReadOnlyList<string> roots,
        IReadOnlyList<InstalledApp> installedApps,
        UnrecordedAppScanOptions? options = null)
    {
        var candidates = Find(roots, installedApps, options);
        var items = new List<CleanupItem>(candidates.Count);

        foreach (var candidate in candidates)
        {
            items.Add(new CleanupItem
            {
                Key = "unrecorded:" + candidate.Path.Replace('\\', '/'),
                Path = candidate.Path,
                DisplayName = candidate.Name,
                GroupName = GroupName,
                Category = CleanupCategory.Leftover,
                Risk = CleanupRisk.High,
                Description = "Каталог не упомянут в записях Uninstall реестра — приложение без записи (4DDiG-типа).",
                Warning = "Проверьте, что это приложение без записи в реестре и оно не требуется другим программам. " +
                          "Каталог будет удалён безвозвратно." +
                          (candidate.RequiresAdmin ? " Требуются права администратора (UAC)." : string.Empty),
                RequiresAdmin = candidate.RequiresAdmin,
                AllowDirectDelete = true,
                CommandOnly = false,
                Target = CleanupTarget.Directory,
                OwnerProcessNames = candidate.OwnerProcessNames,
                SizeBytes = candidate.SizeBytes
            });
        }

        return items;
    }

    private static void AddExistingRoot(List<string> roots, string? root)
    {
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            roots.Add(root);
        }
    }

    private static IEnumerable<string> EnumerateTopLevelDirectories(string root)
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            yield return directory;
        }
    }

    /// <summary>
    /// Пути, на которые ссылаются записи установленного ПО: <c>InstallLocation</c> и файлы
    /// деинсталляторов (для MSI — не путь, msiexec системный). Каталог, содержащий любой из
    /// этих путей, считается «упомянутым» и не предлагается.
    /// </summary>
    private static IReadOnlyList<string> ReferencedPaths(IEnumerable<InstalledApp> apps)
    {
        var parser = new UninstallStringParser();
        var paths = new List<string>();

        foreach (var app in apps)
        {
            if (!string.IsNullOrWhiteSpace(app.InstallLocation))
            {
                paths.Add(app.InstallLocation);
            }

            foreach (var text in new[] { app.UninstallString, app.QuietUninstallString })
            {
                var command = parser.Parse(text, app.ProductCode);
                if (command is not null && command.Kind != UninstallerKind.Msi)
                {
                    paths.Add(command.FileName);
                }
            }
        }

        return paths;
    }

    private static bool IsReferenced(string folder, IReadOnlyList<string> referencedPaths)
    {
        foreach (var path in referencedPaths)
        {
            if (IsSameOrUnder(path, folder))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSameOrUnder(string path, string folder)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (string.Equals(full, folder, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return full.StartsWith(
            folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ищет исполняемые файлы внутри каталога (до <paramref name="maxDepth"/> уровней, без обхода
    /// reparse-точек). Возвращает true, если найден хотя бы один .exe, и собирает имена exe-файлов
    /// без расширения — для проверки занятости по процессам (FR-4.10).
    /// </summary>
    private static bool TryCollectExecutables(string root, int maxDepth, out IReadOnlyList<string> owners)
    {
        var found = false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((root, 1));

        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Dequeue();

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.exe", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var file in files)
            {
                found = true;
                names.Add(Path.GetFileNameWithoutExtension(file));
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            string[] subDirectories;
            try
            {
                subDirectories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var subDirectory in subDirectories)
            {
                if ((File.GetAttributes(subDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                pending.Enqueue((subDirectory, depth + 1));
            }
        }

        owners = names.ToList();
        return found;
    }
}
