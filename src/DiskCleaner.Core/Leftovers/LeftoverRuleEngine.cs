using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;

namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// Контекст одного сканирования остатков: белый список установленного ПО, запущенные
/// процессы, зарегистрированные службы, исключения пользователя и окружение. Передаётся
/// движку эвристик (<see cref="LeftoverRuleEngine"/>), чтобы правила могли решать по пути
/// и имени каталога.
/// </summary>
public sealed class LeftoverRuleContext
{
    public required Abstractions.IEnvironment Environment { get; init; }

    public required InstalledWhitelist Whitelist { get; init; }

    public required IReadOnlyList<RunningProcessInfo> RunningProcesses { get; init; }

    public required IReadOnlyList<RegisteredServiceInfo> RegisteredServices { get; init; }

    public required IReadOnlyCollection<string> Exclusions { get; init; }

    public bool IsExcluded(string path, string folderName) =>
        ExclusionsStore.IsExcluded(Exclusions, path, folderName);

    /// <summary>
    /// Запущен ли процесс с исполняемым файлом внутри каталога (FR-3.3/FR-3.4, механизм IN_USE).
    /// Такие каталоги — «живые» объекты и к удалению не предлагаются.
    /// </summary>
    public bool HasRunningProcessUnder(string directory) =>
        MatchesAnyUnder(directory, RunningProcesses.Select(p => p.ExecutablePath));

    /// <summary>
    /// Зарегистрирована ли служба, исполняемый файл которой лежит внутри каталога (§5 PRD 03).
    /// Каталог с исполняемым файлом службы не предлагается к удалению.
    /// </summary>
    public bool HasRegisteredServiceUnder(string directory) =>
        MatchesAnyUnder(directory, RegisteredServices.Select(s => s.ExecutablePath));

    /// <summary>
    /// Входит ли каталог в переменную окружения <c>%PATH%</c> или содержит каталог,
    /// который в неё входит (§5 PRD 03). Такой каталог используется из командной строки
    /// и не предлагается к удалению.
    /// </summary>
    public bool IsPartOfPathEnvironment(string directory)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var entry in pathValue.Split(Path.PathSeparator))
        {
            var candidate = Unquote(entry).Trim();
            if (candidate.Length == 0)
            {
                continue;
            }

            string expanded;
            try
            {
                expanded = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandPath(candidate)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (string.Equals(full, expanded, StringComparison.OrdinalIgnoreCase) ||
                expanded.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool MatchesAnyUnder(string directory, IEnumerable<string?> executables)
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

        foreach (var executable in executables)
        {
            if (!string.IsNullOrEmpty(executable) &&
                executable.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1];
        }

        return value;
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

    public const string WindowsOldGroupName = "Предыдущая версия Windows";

    /// <summary>Префикс каталогов предыдущей установки Windows на системном диске (FR-3.5).</summary>
    public const string WindowsOldNamePrefix = "Windows.old";

    /// <summary>
    /// Рекомендуемые способы удаления Windows.old (Storage Sense / <c>cleanmgr</c> / DISM, FR-3.5).
    /// </summary>
    public const string WindowsOldRemovalMethod =
        "Storage Sense (Параметры → Система → Память → Временные файлы) или Очистка диска (cleanmgr) → " +
        "«Очистить системные файлы» → «Предыдущие установки Windows», либо DISM (/Online /Cleanup-Image). " +
        "Прямое удаление каталога возможно только с админ-правами (UAC).";

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
            // «Живой» апдейтер (обновление прямо сейчас) к удалению не предлагается (механизм IN_USE).
            return IsLiveObject(context, directoryPath)
                ? null
                : CreateUpdaterCandidate(root, directoryPath, name);
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
            !IsLiveObject(context, directoryPath))
        {
            return CreateRemovedAppConfigCandidate(directoryPath, name, configMatch);
        }

        if (root.Kind is not (LeftoverRootKind.ProgramFiles or LeftoverRootKind.ProgramFilesX86 or LeftoverRootKind.ProgramData))
        {
            // Каталоги пользовательских корней (LocalAppData/AppData/UserProfile) предлагаются
            // только как апдейтеры/конфиги удалённых программ — generic-остатками не являются.
            return null;
        }

        if (IsLiveObject(context, directoryPath))
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
    /// предлагается только когда продукт отсутствует в реестре Uninstall и не является
    /// «живым» объектом (запущенный процесс / <c>%PATH%</c> / исполняемый файл службы).
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

            if (context.IsExcluded(child, name) || IsLiveObject(context, child))
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

    /// <summary>
    /// Каталоги предыдущей установки Windows (<c>Windows.old</c>, <c>Windows.old.000</c> и т.п.)
    /// на системном диске (FR-3.5): с размером, признаками «требует админа»/«обязательное
    /// подтверждение» и рекомендуемым способом удаления (Storage Sense / <c>cleanmgr</c> / DISM).
    /// Прямое удаление допускается только с админ-правами (§5 PRD 03).
    /// </summary>
    public IReadOnlyList<LeftoverCandidate> ScanWindowsOld(LeftoverRuleContext context)
    {
        var rootPath = WindowsOldRootPath(context.Environment);
        if (rootPath is null)
        {
            return Array.Empty<LeftoverCandidate>();
        }

        var candidates = new List<LeftoverCandidate>();
        IReadOnlyList<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(rootPath).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return candidates;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            if (!name.StartsWith(WindowsOldNamePrefix, StringComparison.OrdinalIgnoreCase) ||
                context.IsExcluded(directory, name))
            {
                continue;
            }

            var measurement = LeftoverDirectoryMeasurer.Measure(directory);
            candidates.Add(new LeftoverCandidate
            {
                Path = directory,
                DisplayName = name,
                GroupName = WindowsOldGroupName,
                Reason = LeftoverReason.WindowsOld,
                ReasonText =
                    $"Каталог предыдущей версии Windows «{name}» остался после обновления ОС. " +
                    "Удаление делает невозможным откат к предыдущей версии; выполняется только с админ-правами (FR-3.5).",
                RequiresAdmin = true,
                RequiresConfirmation = true,
                Risk = CleanupRisk.Medium,
                Category = CleanupCategory.SystemFile,
                SizeBytes = measurement.SizeBytes,
                FileCount = measurement.FileCount,
                LastWriteTimeUtc = LeftoverDirectoryMeasurer.GetLastWriteTimeUtc(directory),
                RecommendedRemovalMethod = WindowsOldRemovalMethod
            });
        }

        return candidates;
    }

    /// <summary>
    /// Системный диск, на котором ищется <c>Windows.old</c>: родитель каталога Program Files
    /// (на реальной машине — корень диска <c>C:\</c>, в фикстурах тестов — временный корень).
    /// </summary>
    private static string? WindowsOldRootPath(Abstractions.IEnvironment environment)
    {
        var programFiles = environment.ProgramFiles;
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            return null;
        }

        try
        {
            var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFiles));
            var parent = Path.GetDirectoryName(trimmed);
            return string.IsNullOrWhiteSpace(parent) ? null : parent;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private bool IsLiveObject(LeftoverRuleContext context, string directoryPath) =>
        context.HasRunningProcessUnder(directoryPath) ||
        context.HasRegisteredServiceUnder(directoryPath) ||
        context.IsPartOfPathEnvironment(directoryPath);

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
            RequiresConfirmation = root.RequiresAdmin,
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
            ", «живых» объектов нет — запущенных процессов из каталога нет, путь не входит в %PATH%, служба не зарегистрирована (FR-3.4, §5).";

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
            RequiresConfirmation = root.RequiresAdmin,
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
