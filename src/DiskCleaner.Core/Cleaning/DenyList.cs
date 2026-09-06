namespace DiskCleaner.Core.Cleaning;

/// <summary>
/// Deny-список «никогда не удалять вручную» (FR-5.6): pagefile.sys, swapfile.sys,
/// hiberfil.sys, каталог Windows (кроме разрешённых подкаталогов системной очистки),
/// System Volume Information. Защита применяется в исполнителе перед любым удалением.
/// </summary>
public static class DenyList
{
    private const string SystemDirectoryName = "Windows";
    private const string SystemVolumeInformationName = "System Volume Information";

    private static readonly string[] ProtectedSystemFiles = ["pagefile.sys", "swapfile.sys", "hiberfil.sys"];

    private static readonly string[] AllowedWindowsSubtrees =
    [
        "Temp",
        "SoftwareDistribution\\Download"
    ];

    public static bool IsProtectedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var segments = Split(full);
        if (segments.Length < 2)
        {
            return false;
        }

        var fileName = segments[^1];

        // Системные файлы только в корне диска (реальный кейс: pagefile.sys на D:).
        if (segments.Length == 2 &&
            ProtectedSystemFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            if (string.Equals(segments[i], SystemVolumeInformationName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.Equals(segments[i], SystemDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Сам каталог Windows удалять нельзя.
            if (i == segments.Length - 1)
            {
                return true;
            }

            // Подкаталоги Windows разрешены только для системной очистки (Temp, SoftwareDistribution\Download).
            var relative = string.Join(
                Path.DirectorySeparatorChar,
                segments.Skip(i + 1));
            if (!AllowedWindowsSubtrees.Any(subtree =>
                    relative.StartsWith(subtree, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            break;
        }

        return false;
    }

    public static string Describe(string path)
    {
        var full = Path.GetFullPath(path);
        return $"'{full}' входит в deny-список «никогда не удалять вручную» (pagefile.sys, swapfile.sys, hiberfil.sys, Windows, System Volume Information).";
    }

    private static string[] Split(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(segment => segment.Length > 0)
            .ToArray();
}
