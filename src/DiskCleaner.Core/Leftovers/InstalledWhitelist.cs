using DiskCleaner.Core.Abstractions;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// «Белый список» установленного ПО для сопоставления каталогов (FR-3.1).
/// Источник — записи Uninstall (сервис PRD 01, <see cref="UninstallRegistryService"/>),
/// сопоставление идёт по InstallLocation и по нормализованному DisplayName.
/// Системные компоненты Windows (SystemComponent) в белый список не входят — они не
/// являются «установленным ПО», защищающим каталог от пометки остатком.
/// </summary>
public sealed class InstalledWhitelist
{
    private sealed record NameEntry(IReadOnlyList<string> Tokens);

    private readonly List<NameEntry> _names = new();
    private readonly HashSet<string> _namesRaw = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _installLocations = new();
    private readonly HashSet<string> _installLeafRaw = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IReadOnlyList<string>> _installLeafTokens = new();
    private readonly HashSet<string> _allTokens = new(StringComparer.OrdinalIgnoreCase);

    public InstalledWhitelist(
        IReadOnlyList<InstalledApp> installedApps,
        Abstractions.IEnvironment? environment = null)
    {
        foreach (var app in installedApps)
        {
            AddApp(app, environment);
        }
    }

    private void AddApp(InstalledApp app, Abstractions.IEnvironment? environment)
    {
        if (app.IsSystemComponent)
        {
            return;
        }

        var displayName = app.DisplayName?.Trim();
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            _namesRaw.Add(displayName);
            var tokens = LeftoverNameNormalizer.Tokens(displayName);
            if (tokens.Count > 0)
            {
                _names.Add(new NameEntry(tokens));
                _allTokens.UnionWith(tokens);
            }
        }

        var location = app.InstallLocation?.Trim();
        if (string.IsNullOrWhiteSpace(location))
        {
            return;
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase) ||
            IsContainerRoot(full, environment))
        {
            return;
        }

        _installLocations.Add(full);

        var leaf = Path.GetFileName(full);
        if (!string.IsNullOrWhiteSpace(leaf))
        {
            _installLeafRaw.Add(leaf);
            var leafTokens = LeftoverNameNormalizer.Tokens(leaf);
            if (leafTokens.Count > 0)
            {
                _installLeafTokens.Add(leafTokens);
                _allTokens.UnionWith(leafTokens);
            }
        }
    }

    private static bool IsContainerRoot(string fullPath, Abstractions.IEnvironment? environment)
    {
        if (environment is null)
        {
            return false;
        }

        return new[]
        {
            environment.ProgramFiles,
            environment.ProgramFilesX86,
            environment.ProgramData,
            environment.LocalApplicationData,
            environment.ApplicationData,
            environment.UserProfile
        }.Any(root =>
            !string.IsNullOrWhiteSpace(root) &&
            string.Equals(TrimRoot(root), fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string TrimRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>
    /// Известен ли каталог-кандидат по одному из корней InstallLocation установленного ПО.
    /// Путь считается известным, когда он находится внутри InstallLocation или сам содержит
    /// его (верхний каталог бренда, например <c>Program Files\Google</c> при установке в
    /// <c>Program Files\Google\Chrome\Application</c>).
    /// </summary>
    public bool IsKnownPath(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var location in _installLocations)
        {
            if (PathsNested(full, location))
            {
                return true;
            }
        }

        return IsKnownFolderName(Path.GetFileName(full));
    }

    private static bool PathsNested(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
        first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Точное/нормализованное совпадение имени каталога с DisplayName или корнем InstallLocation.</summary>
    public bool IsKnownFolderName(string folderName)
    {
        var name = folderName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return false;
        }

        if (_namesRaw.Contains(name) || _installLeafRaw.Contains(name))
        {
            return true;
        }

        var folderTokens = LeftoverNameNormalizer.Tokens(name);
        if (folderTokens.Count == 0)
        {
            return false;
        }

        foreach (var entry in _names)
        {
            if (LeftoverNameNormalizer.TokensPrefix(folderTokens, entry.Tokens) ||
                LeftoverNameNormalizer.TokensPrefixWithVersionTail(entry.Tokens, folderTokens))
            {
                return true;
            }
        }

        foreach (var leafTokens in _installLeafTokens)
        {
            if (leafTokens.Count == folderTokens.Count &&
                LeftoverNameNormalizer.TokensPrefix(folderTokens, leafTokens))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Совместимая проверка имени верхнего каталога (используется сканером остатков).</summary>
    public bool IsKnownFolder(string folderName) => IsKnownFolderName(folderName);

    /// <summary>
    /// Присутствует ли ПО с именем, покрывающим <paramref name="name"/> (нормализованное
    /// сравнение токенов, например «Android Studio» внутри «Android Studio 2024.1»).
    /// </summary>
    public bool ContainsName(string name)
    {
        var queryTokens = LeftoverNameNormalizer.Tokens(name);
        if (queryTokens.Count == 0)
        {
            return false;
        }

        foreach (var entry in _names)
        {
            if (LeftoverNameNormalizer.TokensPrefixOrEqual(entry.Tokens, queryTokens))
            {
                return true;
            }
        }

        return false;
    }

    public bool MatchesAnyToken(string token)
    {
        var normalized = token.Trim();
        return normalized.Length > 2 && _allTokens.Any(t =>
            t.Contains(normalized, StringComparison.OrdinalIgnoreCase));
    }
}
