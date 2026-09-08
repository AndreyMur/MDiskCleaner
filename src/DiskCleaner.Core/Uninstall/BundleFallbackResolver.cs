using System.Text.RegularExpressions;

namespace DiskCleaner.Core.Uninstall;

public sealed partial class BundleFallbackResolver
{
    /// <summary>Каталог-источник пакетов по умолчанию (<c>%ProgramData%\Package Cache</c>).</summary>
    public static string DefaultPackageCacheRoot =>
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData);

    public string? TryResolveUninstallerExe(InstalledApp app, string? programDataRoot = null) =>
        TryResolveUninstallerExe(ProductCodeOf(app), programDataRoot);

    public string? TryResolveUninstallerExe(string? productCode, string? programDataRoot = null)
    {
        if (string.IsNullOrWhiteSpace(productCode))
        {
            return null;
        }

        var root = programDataRoot ?? DefaultPackageCacheRoot;
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

    /// <summary>
    /// Штатная команда удаления пакетной (bundle) установки (FR-4.6): исполняемый файл
    /// мастера из <c>%ProgramData%\Package Cache\{code}</c> с ключом <c>/uninstall</c>.
    /// Команда не тихая — для <c>winsdksetup.exe</c> и подобных мастеров открывается GUI,
    /// который должен завершить пользователь.
    /// </summary>
    public UninstallCommand? TryResolveBundleUninstallCommand(string? productCode, string? programDataRoot = null)
    {
        var engine = TryResolveUninstallerExe(productCode, programDataRoot);
        if (engine is null)
        {
            return null;
        }

        return new UninstallCommand(
            UninstallerKind.Generic,
            engine,
            "/uninstall",
            Silent: false);
    }

    /// <summary>Извлекает ProductCode из записи (GUID в UninstallString либо имя ключа ARP).</summary>
    public static string? ProductCodeOf(InstalledApp app)
    {
        var text = app.UninstallString ?? string.Empty;
        var match = ProductCodeRegex().Match(text);
        if (match.Success)
        {
            return match.Value;
        }

        return ProductCodeRegex().IsMatch(app.ProductCode) ? app.ProductCode : null;
    }

    /// <summary>Извлекает GUID продукта из командной строки <c>msiexec /x {GUID}</c>.</summary>
    public static string? ExtractProductCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = ProductCodeRegex().Match(text);
        return match.Success ? match.Value : null;
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
