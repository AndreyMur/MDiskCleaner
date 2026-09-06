using System.Text.RegularExpressions;

namespace DiskCleaner.Core.Uninstall;

public sealed partial class BundleFallbackResolver
{
    public string? TryResolveUninstallerExe(InstalledApp app, string? programDataRoot = null)
    {
        var productCode = ProductCodeOf(app);
        if (string.IsNullOrWhiteSpace(productCode))
        {
            return null;
        }

        var root = programDataRoot
                   ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData);
        var packageDir = Path.Combine(root, "Package Cache", productCode.Trim('{', '}'));
        if (!Directory.Exists(packageDir))
        {
            return null;
        }

        var candidates = Directory
            .EnumerateFiles(packageDir, "*.exe")
            .OrderByDescending(IsBundleEngineName)
            .ToList();

        return candidates.Count == 0 ? null : candidates[0];
    }

    private static string? ProductCodeOf(InstalledApp app)
    {
        var text = app.UninstallString ?? string.Empty;
        var match = ProductCodeRegex().Match(text);
        if (match.Success)
        {
            return match.Value;
        }

        return ProductCodeRegex().IsMatch(app.ProductCode) ? app.ProductCode : null;
    }

    private static bool IsBundleEngineName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Equals("winsdksetup", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("winsdk", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Setup", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex ProductCodeRegex();
}
