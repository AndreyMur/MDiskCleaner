using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;

namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// Контекст одного сканирования остатков: белый список установленного ПО, запущенные
/// процессы, исключения пользователя и окружение. Передаётся движку эвристик
/// (<see cref="LeftoverRuleEngine"/>), чтобы правила могли решать по пути и имени каталога.
/// </summary>
public sealed class LeftoverRuleContext
{
    public required Abstractions.IEnvironment Environment { get; init; }

    public required InstalledWhitelist Whitelist { get; init; }

    public required IReadOnlyList<RunningProcessInfo> RunningProcesses { get; init; }

    public required IReadOnlyCollection<string> Exclusions { get; init; }

    public bool IsExcluded(string path, string folderName) =>
        ExclusionsStore.IsExcluded(Exclusions, path, folderName);

    /// <summary>Запущен ли процесс с исполняемым файлом внутри каталога (FR-3.3/FR-3.4).</summary>
    public bool HasRunningProcessUnder(string directory)
    {
        string path;
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var process in RunningProcesses)
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
}

/// <summary>
/// Движок эвристик остатков (фаза 17, план 03, «Движок эвристик»): по каталогу верхнего
/// уровня и его корню решает, является ли каталог остатком, и в какую группу/с каким
/// основанием его поместить. Правила опираются на справочник брендов
/// (<see cref="LeftoverBrandCatalog"/>) и защитные пороги от ложных срабатываний:
/// каталоги &gt; <see cref="LargeDirectoryMaxBytes"/> без явной категории (апдейтер /
/// конфиг удалённого продукта / известный мусорный бренд) не предлагаются (FR-3.2–3.6, §5).
/// </summary>
public sealed class LeftoverRuleEngine
{
    public const string UpdatersGroupName = "Остатки апдейтеров";

    public const string RemovedAppConfigsGroupName = "Конфиги удалённых программ";

    /// <summary>Порог §5 PRD 03: папки больше 1 ГБ без явной категории не предлагаются к автоудалению.</summary>
    public const long LargeDirectoryMaxBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>Максимум объектов верхнего уровня для правила «пустой/почти пустой каталог».</summary>
    public const int NearEmptyMaxTopLevelEntries = 12;

    /// <summary>Максимальный суммарный размер «почти пустого» каталога.</summary>
    public const long NearEmptyDirectoryMaxBytes = 64L * 1024 * 1024;

    /// <summary>Максимальное число файлов «почти пустого» каталога (защита от каталогов из множества мелких файлов).</summary>
    public const int NearEmptyMaxFileCount = 2_000;

    private sealed record ConfigMatch(RemovedAppProductConfig Product, RemovedAppConfigFolder Folder);

    private readonly LeftoverBrandCatalog _catalog;

    public LeftoverRuleEngine(LeftoverBrandCatalog? catalog = null)
    {
        _catalog = catalog ?? LeftoverBrandCatalog.Default;
    }

    /// <summary>
    /// Классифицирует один каталог верхнего уровня корня <paramref name="root"/>.
    /// Возвращает null, когда каталог не является остатком (установленное ПО, portable/SDK,
    /// служебный/исключённый, живой процесс, большой каталог без явной категории).
    /// </summary>
    public LeftoverCandidate? Classify(LeftoverRuleContext context, LeftoverScanRoot root, string directoryPath)
    {
        var name = Path.GetFileName(directoryPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (context.IsExcluded(directoryPath, name) ||
            ReservedFolderNames.IsReserved(root.Kind, name))
        {
            return null;
        }

        if (UpdaterFolderNames.IsUpdaterFolder(name))
        {
            return CreateUpdaterCandidate(root, directoryPath, name);
        }

        // Portable-программы и вручную распакованные SDK не регистрируются в Uninstall и не
        // являются остатками — исключаем до проверки «нет в реестре» (§5 PRD 03).
        if (_catalog.IsPortableOrSdkFolderName(name))
        {
            return null;
        }

        if (context.Whitelist.IsKnownPath(directoryPath))
        {
            return null;
        }

        var configMatch = TryMatchTopLevelConfig(root.Kind, name);
        if (configMatch is not null &&
            !context.Whitelist.ContainsName(configMatch.Product.InstalledProductName) &&
            !context.HasRunningProcessUnder(directoryPath))
        {
            return CreateRemovedAppConfigCandidate(directoryPath, name, configMatch);
        }

        if (root.Kind is not (LeftoverRootKind.ProgramFiles or LeftoverRootKind.ProgramFilesX86 or LeftoverRootKind.ProgramData))
        {
            // Каталоги пользовательских корней (LocalAppData/AppData/UserProfile) предлагаются
            // только как апдейтеры/конфиги удалённых программ — generic-остатками не являются.
            return null;
        }

        if (context.HasRunningProcessUnder(directoryPath))
        {
            return null;
        }

        if (_catalog.IsJunkBrandFolderName(name))
        {
            return CreateOrphanCandidate(root, directoryPath, name, junkBrand: true, sizeBytes: null, fileCount: null);
        }

        if (TryMeasureNearEmpty(directoryPath, out var sizeBytes, out var fileCount))
        {
            return CreateOrphanCandidate(root, directoryPath, name, junkBrand: false, sizeBytes, fileCount);
        }

        return null;
    }

    /// <summary>
    /// Конфиг-каталоги удалённых продуктов, лежащие внутри контейнеров брендов в AppData
    /// (например <c>%LOCALAPPDATA%\Google\AndroidStudio2025.3.2</c>, FR-3.3). Каталог
    /// предлагается только когда продукт отсутствует в реестре Uninstall и из него не
    /// запущено процессов.
    /// </summary>
    public IReadOnlyList<LeftoverCandidate> ScanRemovedAppConfigs(LeftoverRuleContext context)
    {
        var candidates = new List<LeftoverCandidate>();
        foreach (var product in _catalog.RemovedAppProducts)
        {
            if (context.Whitelist.ContainsName(product.InstalledProductName))
            {
                continue;
            }

            foreach (var folder in product.ConfigFolders)
            {
                if (folder.ContainerRelativePath is null)
                {
                    continue;
                }

                var containerPath = ResolveContainerPath(context.Environment, folder);
                if (!Directory.Exists(containerPath))
                {
                    continue;
                }

                ScanConfigContainer(context, product, folder, containerPath, candidates);
            }
        }

        return candidates;
    }

    private void ScanConfigContainer(
        LeftoverRuleContext context,
        RemovedAppProductConfig product,
        RemovedAppConfigFolder folder,
        string containerPath,
        List<LeftoverCandidate> candidates)
    {
        IReadOnlyList<string> children;
        try
        {
            children = Directory.EnumerateDirectories(containerPath).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (!folder.MatchesName(name))
            {
                continue;
            }

            if (context.IsExcluded(child, name) || context.HasRunningProcessUnder(child))
            {
                continue;
            }

            candidates.Add(CreateRemovedAppConfigCandidate(child, name, new ConfigMatch(product, folder)));
        }
    }

    private static string ResolveContainerPath(Abstractions.IEnvironment environment, RemovedAppConfigFolder folder)
    {
        var rootPath = folder.RootKind switch
        {
            LeftoverRootKind.ProgramFiles => environment.ProgramFiles,
            LeftoverRootKind.ProgramFilesX86 => environment.ProgramFilesX86,
            LeftoverRootKind.ProgramData => environment.ProgramData,
            LeftoverRootKind.LocalApplicationData => environment.LocalApplicationData,
            LeftoverRootKind.ApplicationData => environment.ApplicationData,
            _ => environment.UserProfile
        };

        return string.IsNullOrWhiteSpace(rootPath)
            ? string.Empty
            : Path.Combine(rootPath, folder.ContainerRelativePath!);
    }

    private ConfigMatch? TryMatchTopLevelConfig(LeftoverRootKind kind, string name)
    {
        foreach (var product in _catalog.RemovedAppProducts)
        {
            foreach (var folder in product.ConfigFolders)
            {
                if (folder.ContainerRelativePath is null &&
                    folder.RootKind == kind &&
                    folder.MatchesName(name))
                {
                    return new ConfigMatch(product, folder);
                }
            }
        }

        return null;
    }

    private static LeftoverCandidate CreateUpdaterCandidate(LeftoverScanRoot root, string directoryPath, string name)
    {
        return new LeftoverCandidate
        {
            Path = directoryPath,
            DisplayName = name,
            GroupName = UpdatersGroupName,
            Reason = LeftoverReason.UpdaterFolder,
            ReasonText = "Имя соответствует маске апдейтера (*-updater/updater/update/_updater): каталог остаётся после установки приложения из инсталлятора (FR-3.2).",
            Risk = CleanupRisk.Low,
            Category = CleanupCategory.Leftover,
            RequiresAdmin = root.RequiresAdmin,
            SizeBytes = LeftoverDirectoryMeasurer.MeasureSizeBytes(directoryPath),
            LastWriteTimeUtc = LeftoverDirectoryMeasurer.GetLastWriteTimeUtc(directoryPath)
        };
    }

    private static LeftoverCandidate CreateRemovedAppConfigCandidate(
        string directoryPath,
        string name,
        ConfigMatch match)
    {
        var measurement = LeftoverDirectoryMeasurer.Measure(directoryPath);
        return new LeftoverCandidate
        {
            Path = directoryPath,
            DisplayName = name,
            GroupName = RemovedAppConfigsGroupName,
            Reason = LeftoverReason.ConfigOfRemovedApp,
            ReasonText = $"Конфиг-каталог «{name}» продукта {match.Product.ProductDisplayName}: приложение отсутствует в реестре Uninstall, процессов из каталога не запущено (FR-3.3).",
            Risk = match.Folder.Risk,
            Category = CleanupCategory.Leftover,
            SizeBytes = measurement.SizeBytes,
            FileCount = measurement.FileCount,
            LastWriteTimeUtc = LeftoverDirectoryMeasurer.GetLastWriteTimeUtc(directoryPath)
        };
    }

    private static LeftoverCandidate CreateOrphanCandidate(
        LeftoverScanRoot root,
        string directoryPath,
        string name,
        bool junkBrand,
        long? sizeBytes,
        int? fileCount)
    {
        if (junkBrand)
        {
            var measurement = LeftoverDirectoryMeasurer.Measure(directoryPath);
            sizeBytes = measurement.SizeBytes;
            fileCount = measurement.FileCount;
        }

        var reason = root.Kind switch
        {
            LeftoverRootKind.ProgramData when junkBrand => LeftoverReason.OrphanProgramData,
            LeftoverRootKind.ProgramData => LeftoverReason.NearEmptyDirectory,
            _ when junkBrand => LeftoverReason.OrphanProgramFiles,
            _ => LeftoverReason.NearEmptyDirectory
        };

        var basis = junkBrand
            ? $"известный бренд удалённого продукта «{name}»"
            : $"каталог пуст или почти пуст ({fileCount ?? 0} объектов, {FormatBytes(sizeBytes ?? 0)})";

        var reasonText =
            "Осиротевший каталог: отсутствует в реестре Uninstall, " + basis +
            ", запущенных процессов из каталога нет (FR-3.4).";

        return new LeftoverCandidate
        {
            Path = directoryPath,
            DisplayName = name,
            GroupName = root.GroupName,
            Reason = reason,
            ReasonText = reasonText,
            Risk = junkBrand ? CleanupRisk.Medium : CleanupRisk.Low,
            Category = CleanupCategory.Leftover,
            RequiresAdmin = root.RequiresAdmin,
            SizeBytes = sizeBytes,
            FileCount = fileCount,
            LastWriteTimeUtc = LeftoverDirectoryMeasurer.GetLastWriteTimeUtc(directoryPath)
        };
    }

    private static bool TryMeasureNearEmpty(string directoryPath, out long sizeBytes, out int fileCount)
    {
        sizeBytes = 0;
        fileCount = 0;

        if (CountTopLevelEntries(directoryPath, NearEmptyMaxTopLevelEntries + 1) > NearEmptyMaxTopLevelEntries)
        {
            return false;
        }

        var measurement = LeftoverDirectoryMeasurer.Measure(
            directoryPath,
            abortAfterBytes: NearEmptyDirectoryMaxBytes,
            abortAfterFiles: NearEmptyMaxFileCount);
        if (!measurement.Complete)
        {
            return false;
        }

        sizeBytes = measurement.SizeBytes;
        fileCount = measurement.FileCount;
        return true;
    }

    private static int CountTopLevelEntries(string directoryPath, int limit)
    {
        var count = 0;
        try
        {
            foreach (var _ in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                count++;
                if (count >= limit)
                {
                    return count;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return limit;
        }

        return count;
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} Б",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} МБ",
            _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} ГБ"
        };
    }
}
