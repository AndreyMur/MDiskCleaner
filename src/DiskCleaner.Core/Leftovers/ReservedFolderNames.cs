namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// Служебные/системные имена каталогов верхнего уровня, которые никогда не являются
/// остатками установленного ПО (структура ОС, данные пользователя, системные хранилища).
/// </summary>
public static class ReservedFolderNames
{
    private static readonly HashSet<string> ProgramFilesFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "common files", "internet explorer", "windows nt", "windows defender", "windows photo viewer",
        "windows mail", "windows media player", "windows portable devices", "reference assemblies",
        "msbuild", "uninstall information", "windowsapps", "windowspowershell", "microsoft shared",
        "windows kits", "microsoft visual studio", "dotnet", "git", "package manager", "windows security",
        "modifiablewindowsapps", "windows update"
    };

    private static readonly HashSet<string> ProgramDataFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "package cache", "usoshared"
    };

    private static readonly HashSet<string> LocalApplicationDataFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "packages", "temp", "d3dscache", "crashdumps", "publishers",
        "connecteddevicesplatform", "devicemetadatastore", "application data", "history",
        "temporary internet files", "programs"
    };

    private static readonly HashSet<string> ApplicationDataFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft"
    };

    private static readonly HashSet<string> UserProfileFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "appdata", "desktop", "documents", "downloads", "favorites", "links", "music", "pictures",
        "saved games", "searches", "videos", "contacts", "onedrive", "3d objects"
    };

    /// <summary>Каталоги Program Files, которые не предлагаются как остатки (общие для сканеров).</summary>
    public static bool IsReservedProgramFilesFolder(string folderName) =>
        ProgramFilesFolders.Contains(folderName);

    public static bool IsReserved(LeftoverRootKind kind, string folderName)
    {
        if (folderName.StartsWith("regid.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return kind switch
        {
            LeftoverRootKind.ProgramFiles or LeftoverRootKind.ProgramFilesX86 =>
                ProgramFilesFolders.Contains(folderName),
            LeftoverRootKind.ProgramData => ProgramDataFolders.Contains(folderName),
            LeftoverRootKind.LocalApplicationData => LocalApplicationDataFolders.Contains(folderName),
            LeftoverRootKind.ApplicationData => ApplicationDataFolders.Contains(folderName),
            LeftoverRootKind.UserProfile => UserProfileFolders.Contains(folderName),
            _ => false
        };
    }
}
