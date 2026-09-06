using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Core.Leftovers;

public sealed class InstalledWhitelist
{
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _installRootNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allTokens = new(StringComparer.OrdinalIgnoreCase);

    public InstalledWhitelist(IReadOnlyList<InstalledApp> installedApps)
    {
        foreach (var app in installedApps)
        {
            if (!string.IsNullOrWhiteSpace(app.DisplayName))
            {
                _names.Add(app.DisplayName.Trim());
                _allTokens.Add(app.DisplayName.Trim());
            }

            if (!string.IsNullOrWhiteSpace(app.InstallLocation))
            {
                var folder = Path.GetFileName(app.InstallLocation.TrimEnd('\\'));
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    _installRootNames.Add(folder);
                    _allTokens.Add(folder);
                }
            }
        }
    }

    public bool ContainsName(string name)
    {
        var normalized = name.Trim();
        foreach (var known in _names)
        {
            if (string.Equals(known, normalized, StringComparison.OrdinalIgnoreCase) ||
                known.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(known, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsKnownFolder(string folderName) =>
        _installRootNames.Contains(folderName) || _names.Contains(folderName);

    public bool MatchesAnyToken(string token)
    {
        var normalized = token.Trim();
        return normalized.Length > 2 && _allTokens.Any(t =>
            t.Contains(normalized, StringComparison.OrdinalIgnoreCase));
    }
}
