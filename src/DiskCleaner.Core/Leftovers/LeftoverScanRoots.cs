using DiskCleaner.Core.Abstractions;

namespace DiskCleaner.Core.Leftovers;

public enum LeftoverRootKind
{
    ProgramFiles,
    ProgramFilesX86,
    ProgramData,
    LocalApplicationData,
    ApplicationData,
    UserProfile
}

/// <param name="GroupName">Группа для несопоставленных каталогов этого корня.</param>
/// <param name="RequiresAdmin">Удаление из корня требует прав администратора (UAC).</param>
public sealed record LeftoverScanRoot(LeftoverRootKind Kind, string Path, string GroupName, bool RequiresAdmin)
{
    public string Name =>
        Kind switch
        {
            LeftoverRootKind.ProgramFiles => "Program Files",
            LeftoverRootKind.ProgramFilesX86 => "Program Files (x86)",
            LeftoverRootKind.ProgramData => "ProgramData",
            LeftoverRootKind.LocalApplicationData => "%LOCALAPPDATA%",
            LeftoverRootKind.ApplicationData => "%APPDATA%",
            _ => "%USERPROFILE%"
        };
}

/// <summary>
/// Корни сканирования остатков: только верхние уровни, без рекурсивного обхода (FR-3.1, NFR ≤ 60 с).
/// Каждый корень задаёт группу несопоставленных каталогов и признак необходимости админ-прав.
/// </summary>
public static class LeftoverScanRoots
{
    public static IReadOnlyList<LeftoverScanRoot> Resolve(Abstractions.IEnvironment environment)
    {
        var roots = new List<LeftoverScanRoot>();
        Add(roots, LeftoverRootKind.ProgramFiles,
            environment.ProgramFiles, "Осиротевшие папки в Program Files", requiresAdmin: true);
        Add(roots, LeftoverRootKind.ProgramFilesX86,
            environment.ProgramFilesX86, "Осиротевшие папки в Program Files (x86)", requiresAdmin: true);
        Add(roots, LeftoverRootKind.ProgramData,
            environment.ProgramData, "Осиротевшие папки в ProgramData", requiresAdmin: true);
        Add(roots, LeftoverRootKind.LocalApplicationData,
            environment.LocalApplicationData, "Осиротевшие каталоги в %LOCALAPPDATA%", requiresAdmin: false);
        Add(roots, LeftoverRootKind.ApplicationData,
            environment.ApplicationData, "Осиротевшие каталоги в %APPDATA%", requiresAdmin: false);
        Add(roots, LeftoverRootKind.UserProfile,
            environment.UserProfile, "Осиротевшие каталоги в %USERPROFILE%", requiresAdmin: false);

        return roots;
    }

    private static void Add(
        List<LeftoverScanRoot> roots,
        LeftoverRootKind kind,
        string path,
        string groupName,
        bool requiresAdmin)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (roots.Any(r => string.Equals(r.Path, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        roots.Add(new LeftoverScanRoot(kind, normalized, groupName, requiresAdmin));
    }
}
