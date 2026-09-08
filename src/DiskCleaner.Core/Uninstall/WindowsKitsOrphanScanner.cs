using System.Text.RegularExpressions;

namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Разметка каталога <c>Windows Kits\10</c> (фаза 23, модуль 04, FR-4.9): поиск каталогов версии
/// SDK под <c>Include</c>/<c>Lib</c>/<c>bin</c> и определение осиротевших из них — тех, на которые
/// больше не ссылается ни одна запись реестра (InstallLocation). Используется после удаления
/// компонентов версии SDK: если записей этой версии в реестре больше нет, её каталоги удаляются;
/// каталоги другой установленной версии (например 26100) остаются нетронутыми.
/// </summary>
public sealed partial class WindowsKitsOrphanScanner
{
    public static string[] TypeRootNames { get; } = ["Include", "Lib", "bin"];

    /// <summary>Ищет корень <c>&lt;Program Files (x86)&gt;\Windows Kits\10</c> среди переданных корней Program Files.</summary>
    public static string? FindKitsRoot(params string?[] programFilesRoots)
    {
        foreach (var root in programFilesRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var candidate = Path.Combine(root, "Windows Kits", "10");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Каталоги версии SDK (под Include/Lib/bin), на которые больше не ссылается ни одна
    /// оставшаяся запись реестра и которые относятся к удаляемой версии (совпадает «build»-токен
    /// с <paramref name="displayVersion"/>). Возвращаются полные пути для удаления.
    /// </summary>
    public IReadOnlyList<string> FindOrphanedVersionFolders(
        string kitsRoot,
        IReadOnlyList<InstalledApp> remainingApps,
        string? displayVersion)
    {
        if (!Directory.Exists(kitsRoot))
        {
            return Array.Empty<string>();
        }

        var referenced = ReferencedVersionNames(kitsRoot, remainingApps);
        var result = new List<string>();

        foreach (var typeRoot in TypeRootNames)
        {
            var typeDir = Path.Combine(kitsRoot, typeRoot);
            if (!Directory.Exists(typeDir))
            {
                continue;
            }

            foreach (var versionDir in Directory.EnumerateDirectories(typeDir))
            {
                var name = Path.GetFileName(versionDir);
                if (!IsVersionLike(name) || referenced.Contains(name))
                {
                    continue;
                }

                if (displayVersion is null || MatchesDisplayVersion(name, displayVersion))
                {
                    result.Add(versionDir);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Имена каталогов версий (например <c>10.0.26100.0</c>), на которые ссылаются оставшиеся
    /// записи реестра через <see cref="InstalledApp.InstallLocation"/> внутри корня Windows Kits.
    /// </summary>
    public static HashSet<string> ReferencedVersionNames(string kitsRoot, IEnumerable<InstalledApp> apps)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.InstallLocation))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(app.InstallLocation);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!full.StartsWith(kitsRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = full[(kitsRoot.Length + 1)..];
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (IsVersionLike(segment))
                {
                    referenced.Add(segment);
                }
            }
        }

        return referenced;
    }

    /// <summary>Совпадает ли каталог версии (например <c>10.0.19041.0</c>) с версией записи ARP (например <c>10.1.19041.5609</c>) по «build»-токену.</summary>
    public static bool MatchesDisplayVersion(string folderName, string displayVersion)
    {
        var folderToken = ThirdNumber(folderName);
        if (folderToken is null)
        {
            return false;
        }

        var versionToken = ThirdNumber(displayVersion);
        return versionToken is not null && folderToken == versionToken;
    }

    private static string? ThirdNumber(string value)
    {
        var numbers = DigitRegex().Matches(value)
            .Select(match => match.Value)
            .ToList();
        return numbers.Count >= 3 ? numbers[2] : null;
    }

    private static bool IsVersionLike(string name) =>
        VersionLikeRegex().IsMatch(name);

    [GeneratedRegex(@"^\d+(\.\d+){2,}$")]
    private static partial Regex VersionLikeRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitRegex();
}
