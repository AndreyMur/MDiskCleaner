namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Каталог записей установленного ПО, относящихся к Windows SDK (фаза 23, модуль 04).
/// Пакетная установка SDK представлена записями двух видов: дочерние MSI-компоненты
/// (имена вида «Windows SDK …», каждый — отдельный MSI-продукт со своим GUID) и родительская
/// bundle-запись «Windows Software Development Kit». Все записи одной версии SDK имеют общий
/// <see cref="InstalledApp.DisplayVersion"/> (например <c>10.1.19041.5609</c>) — по нему
/// выполняется изоляция удаления (FR-4.7), поэтому компоненты другой версии (например
/// 26100) не затрагиваются.
/// </summary>
public static class WindowsSdkFamily
{
    /// <summary>Маркеры имени записи, по которым она относится к семейству Windows SDK.</summary>
    private static readonly string[] NameMarkers =
    [
        "windows sdk",
        "windows software development kit"
    ];

    public static bool IsSdkApp(InstalledApp app)
    {
        if (string.IsNullOrWhiteSpace(app.DisplayName))
        {
            return false;
        }

        var name = app.DisplayName.ToLowerInvariant();
        return NameMarkers.Any(marker => name.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>Является ли запись компонентом (или bundle-родителем) конкретной версии SDK.</summary>
    public static bool IsSdkComponentOfVersion(InstalledApp app, string displayVersion) =>
        !app.IsSystemComponent &&
        !string.IsNullOrWhiteSpace(app.DisplayVersion) &&
        string.Equals(app.DisplayVersion, displayVersion, StringComparison.Ordinal) &&
        IsSdkApp(app);

    /// <summary>Все установленные версии Windows SDK (общие DisplayVersion семейства), упорядоченные по имени.</summary>
    public static IReadOnlyList<string> FindVersions(IEnumerable<InstalledApp> apps) =>
        apps
            .Where(app => !app.IsSystemComponent && IsSdkApp(app) && !string.IsNullOrWhiteSpace(app.DisplayVersion))
            .Select(app => app.DisplayVersion!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(version => version, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// MSI-компоненты (и bundle-родитель) одной версии SDK по общему DisplayVersion (FR-4.7).
    /// Компоненты другой версии (например 26100 при удалении 19041) в набор не попадают.
    /// </summary>
    public static IReadOnlyList<InstalledApp> ComponentsOfVersion(
        IEnumerable<InstalledApp> apps,
        string displayVersion) =>
        apps
            .Where(app => IsSdkComponentOfVersion(app, displayVersion))
            .OrderBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(app => app.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
