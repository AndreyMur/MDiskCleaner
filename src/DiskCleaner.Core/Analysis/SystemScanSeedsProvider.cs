using DiskCleaner.Core.Environment;
using DiskCleaner.Core.Leftovers;
using DiskCleaner.Core.Models;
using Microsoft.Win32;

namespace DiskCleaner.Core.Analysis;

/// <summary>
/// Seeds модуля 05 «Системная очистка» и Корзина-режима: Temp пользователя/системы,
/// SoftwareDistribution\Download (со службой wuauserv), Корзина по дискам, hiberfil.sys
/// (powercfg /h off), Windows.old, пользовательские папки (Downloads, Documents) в Корзину-режиме.
/// </summary>
public sealed class SystemScanSeedsProvider
{
    private const string WindowsUpdateServiceName = "wuauserv";

    private readonly Abstractions.IEnvironment _environment;
    private readonly string _windowsDirectory;
    private readonly string? _systemRoot;
    private readonly string? _currentUserSid;
    private readonly IReadOnlyList<string>? _recycleBinDirectories;

    public SystemScanSeedsProvider(
        Abstractions.IEnvironment? environment = null,
        string? windowsDirectory = null,
        string? systemRoot = null,
        string? currentUserSid = null,
        IReadOnlyList<string>? recycleBinDirectories = null)
    {
        _environment = environment ?? new EnvironmentProvider();
        _windowsDirectory = windowsDirectory ?? ResolveWindowsDirectory(_environment);
        _systemRoot = systemRoot;
        _currentUserSid = currentUserSid;
        _recycleBinDirectories = recycleBinDirectories;
    }

    public IReadOnlyList<CleanupItem> BuildSeeds()
    {
        var seeds = new List<CleanupItem>();

        AddUserTempSeed(seeds);
        AddWindowsTempSeed(seeds);
        AddSoftwareDistributionSeed(seeds);
        AddRecycleBinSeeds(seeds);
        AddHibernationSeed(seeds);
        AddWindowsOldSeed(seeds);
        AddUserDataSeeds(seeds);

        return seeds;
    }

    private void AddUserTempSeed(ICollection<CleanupItem> seeds)
    {
        var userTemp = _environment.Temp;
        if (!Directory.Exists(userTemp))
        {
            return;
        }

        seeds.Add(new CleanupItem
        {
            Key = "system:temp:user",
            Path = Path.GetFullPath(userTemp),
            DisplayName = "Временные файлы пользователя (%TEMP%)",
            GroupName = "Системная очистка",
            Category = CleanupCategory.Temp,
            Risk = CleanupRisk.Low,
            Target = CleanupTarget.Directory,
            DeleteContentsOnly = true,
            Description = "Содержимое временного каталога пользователя.",
            Warning = "Сам каталог %TEMP% не удаляется; файлы, занятые процессами, будут пропущены."
        });
    }

    private void AddWindowsTempSeed(ICollection<CleanupItem> seeds)
    {
        var windowsTemp = Path.Combine(_windowsDirectory, "Temp");
        if (!Directory.Exists(windowsTemp))
        {
            return;
        }

        seeds.Add(new CleanupItem
        {
            Key = "system:temp:windows",
            Path = Path.GetFullPath(windowsTemp),
            DisplayName = "Системные временные файлы (Windows\\Temp)",
            GroupName = "Системная очистка",
            Category = CleanupCategory.Temp,
            Risk = CleanupRisk.Low,
            Target = CleanupTarget.Directory,
            DeleteContentsOnly = true,
            RequiresAdmin = true,
            Description = "Содержимое системного временного каталога C:\\Windows\\Temp.",
            Warning = "Требуются права администратора (UAC). Файлы, занятые процессами, будут пропущены."
        });
    }

    private void AddSoftwareDistributionSeed(ICollection<CleanupItem> seeds)
    {
        var download = Path.Combine(_windowsDirectory, "SoftwareDistribution", "Download");
        if (!Directory.Exists(download))
        {
            return;
        }

        seeds.Add(new CleanupItem
        {
            Key = "system:windows-update:download",
            Path = Path.GetFullPath(download),
            DisplayName = "Кэш Windows Update (SoftwareDistribution\\Download)",
            GroupName = "Системная очистка",
            Category = CleanupCategory.SystemFile,
            Risk = CleanupRisk.Medium,
            Target = CleanupTarget.Directory,
            DeleteContentsOnly = true,
            RequiresAdmin = true,
            ServiceName = WindowsUpdateServiceName,
            Description = "Скачанные обновления Windows; служба wuauserv временно останавливается и запускается обратно.",
            Warning = "Требуются права администратора (UAC). Служба Windows Update будет перезапущена автоматически. Очищается только каталог Download."
        });
    }

    private void AddRecycleBinSeeds(ICollection<CleanupItem> seeds)
    {
        var directories = _recycleBinDirectories ?? DiscoverRecycleBinDirectories();
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var driveRoot = Path.GetPathRoot(directory);
            seeds.Add(new CleanupItem
            {
                Key = "system:recycle-bin:" + directory.TrimEnd('\\'),
                Path = Path.GetFullPath(directory),
                DisplayName = "Корзина (" + FormatDriveLabel(directory) + ")",
                GroupName = "Системная очистка",
                Category = CleanupCategory.RecycleBin,
                Risk = CleanupRisk.Medium,
                Target = CleanupTarget.Directory,
                EmptyRecycleBinDrive = driveRoot,
                Description = "Очистка Корзины выбранного диска штатным API оболочки Windows (SHEmptyRecycleBin).",
                Warning = "Содержимое Корзины будет удалено безвозвратно (восстановление станет невозможным)."
            });
        }
    }

    private void AddHibernationSeed(ICollection<CleanupItem> seeds)
    {
        var systemDriveRoot = _systemRoot ?? Path.GetPathRoot(_windowsDirectory);
        if (string.IsNullOrEmpty(systemDriveRoot))
        {
            return;
        }

        var hiberfil = Path.Combine(systemDriveRoot, "hiberfil.sys");
        var powerCfg = Path.Combine(System.Environment.SystemDirectory, "powercfg.exe");

        // Обратимость (FR-5.4): когда файл гибернации отсутствует, предлагается обратная
        // операция powercfg /h on (вернуть гибернацию/файл). Оба шага — CommandOnly в
        // админ-пачке; выбор всегда явный (ничего не выполняется «по умолчанию»).
        if (!File.Exists(hiberfil))
        {
            seeds.Add(new CleanupItem
            {
                Key = "system:hibernation:on",
                Path = null,
                DisplayName = "Включить гибернацию (hiberfil.sys)",
                GroupName = "Системная очистка",
                Category = CleanupCategory.SystemFile,
                Risk = CleanupRisk.Medium,
                Target = CleanupTarget.File,
                RequiresAdmin = true,
                CommandOnly = true,
                AllowDirectDelete = false,
                CleanCommand = "powercfg /h on",
                CleanCommandFile = powerCfg,
                CleanCommandArgs = "/h on",
                Description = "Обратная операция: создаёт файл гибернации hiberfil.sys на системном диске.",
                Warning = "Включает гибернацию (создаётся hiberfil.sys). Требуются права администратора (UAC). Только по явному выбору."
            });
            return;
        }

        long? sizeBytes = null;
        try
        {
            sizeBytes = new FileInfo(hiberfil).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        seeds.Add(new CleanupItem
        {
            Key = "system:hibernation:off",
            Path = null,
            DisplayName = sizeBytes is null
                ? "Отключить гибернацию (hiberfil.sys)"
                : $"Отключить гибернацию (hiberfil.sys, {Reports.CleanReportFormatter.FormatBytes(sizeBytes.Value)})",
            GroupName = "Системная очистка",
            Category = CleanupCategory.SystemFile,
            Risk = CleanupRisk.Medium,
            Target = CleanupTarget.File,
            RequiresAdmin = true,
            CommandOnly = true,
            AllowDirectDelete = false,
            CleanCommand = "powercfg /h off",
            CleanCommandFile = powerCfg,
            CleanCommandArgs = "/h off",
            SizeBytes = sizeBytes,
            VerifyPathAbsent = hiberfil,
            Description = "Файл гибернации hiberfil.sys (файл на системном диске) будет удалён.",
            Warning = "Отключает гибернацию; быстрый запуск сохраняется. Операция обратима (powercfg /h on). Требуются права администратора (UAC)."
        });
    }

    private void AddWindowsOldSeed(ICollection<CleanupItem> seeds)
    {
        // Каталоги предыдущей установки Windows (Windows.old, Windows.old.000 и т.п., FR-5.5)
        // ищутся на системном диске: корень = родитель Program Files (в фикстурах — временный корень,
        // на реальной машине — C:\ — как в движке остатков модуля 03).
        var scanRoot = WindowsOldSystemRoot();
        if (scanRoot is null)
        {
            return;
        }

        IReadOnlyList<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(scanRoot)
                .Where(name => Path.GetFileName(name).StartsWith(
                    LeftoverRuleEngine.WindowsOldNamePrefix,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            var measurement = LeftoverDirectoryMeasurer.Measure(directory);
            seeds.Add(new CleanupItem
            {
                Key = name.Equals("Windows.old", StringComparison.OrdinalIgnoreCase)
                    ? "system:windows-old"
                    : "system:windows-old:" + name,
                Path = Path.GetFullPath(directory),
                DisplayName = "Предыдущая установка Windows (" + name + ")",
                GroupName = "Системная очистка",
                Category = CleanupCategory.SystemFile,
                Risk = CleanupRisk.High,
                Target = CleanupTarget.Directory,
                RequiresAdmin = true,
                SizeBytes = measurement.SizeBytes,
                FileCount = measurement.FileCount,
                Description =
                    $"Файлы предыдущей установки Windows ({name}). Удаляются только штатными средствами " +
                    $"(Storage Sense/cleanmgr/DISM, {LeftoverRuleEngine.WindowsOldRemovalMethod}) или с админ-правами (UAC) по явному выбору.",
                Warning =
                    "Удаление делает невозможным откат к предыдущей версии Windows. Каталог удаляется целиком, " +
                    "только с админ-правами (UAC) и только по явному выбору (FR-5.5)."
            });
        }
    }

    /// <summary>
    /// Системный диск для поиска <c>Windows.old*</c>: родитель Program Files (в фикстурах тестов —
    /// временный корень, на реальной машине — корень системного диска <c>C:\</c>).
    /// </summary>
    private string? WindowsOldSystemRoot()
    {
        var programFiles = _environment.ProgramFiles;
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            return null;
        }

        try
        {
            var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFiles));
            return Path.GetDirectoryName(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private void AddUserDataSeeds(ICollection<CleanupItem> seeds)
    {
        AddUserDataSeed(seeds, "downloads", "Downloads", ResolveDownloadsDirectory());
        AddUserDataSeed(seeds, "documents", "Documents", System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments));
    }

    private void AddUserDataSeed(ICollection<CleanupItem> seeds, string id, string label, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        if (!HasAnyContent(directory))
        {
            return;
        }

        var full = Path.GetFullPath(directory);
        seeds.Add(new CleanupItem
        {
            Key = "user-data:" + id,
            Path = full,
            DisplayName = label + " — переместить в Корзину",
            GroupName = "Корзина-режим (пользовательские данные)",
            Category = CleanupCategory.UserData,
            Risk = CleanupRisk.High,
            Target = CleanupTarget.Directory,
            MoveToRecycleBin = true,
            Description = "Пользовательские данные. Удаляются только по явному выбору и только в Корзину.",
            Warning = "Папка целиком будет перемещена в Корзину (можно восстановить, пока Корзина не очищена). Безвозвратное удаление пользовательских данных запрещено."
        });
    }

    private IReadOnlyList<string> DiscoverRecycleBinDirectories()
    {
        var sid = _currentUserSid ?? ResolveCurrentUserSid();
        if (string.IsNullOrWhiteSpace(sid))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            result.Add(Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid));
        }

        return result;
    }

    private static string? ResolveCurrentUserSid()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool HasAnyContent(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ResolveWindowsDirectory(Abstractions.IEnvironment environment) =>
        environment.GetEnvironmentVariable("SystemRoot") is { Length: > 0 } systemRoot
            ? systemRoot
            : System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);

    private static string FormatDriveLabel(string directory) =>
        (Path.GetPathRoot(directory) ?? string.Empty).TrimEnd('\\', '/');

    private static string? ResolveDownloadsDirectory()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
            var value = key?.GetValue(DownloadsFolderGuid);
            if (value is string path && path.Length > 0)
            {
                return System.Environment.ExpandEnvironmentVariables(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        return Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Downloads");
    }

    private const string DownloadsFolderGuid = "{374DE290-123F-4565-9164-39C4925E467B}";
}
