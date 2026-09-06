namespace DiskCleaner.Core.Uninstall;

public static class RegistryDeletePathBuilder
{
    public const string LocalMachinePrefix = "HKEY_LOCAL_MACHINE";
    public const string CurrentUserPrefix = "HKEY_CURRENT_USER";

    public static string Build(InstalledApp app) => Build(app.RequiresAdmin
        ? RegistryHiveKind.LocalMachine
        : RegistryHiveKind.CurrentUser, BranchRelativePath(app));

    public static string Build(RegistryHiveKind hive, string relativePath) =>
        $"{HivePrefix(hive)}\\{relativePath}";

    public static RegistryHiveKind HiveOf(string deletePath) =>
        deletePath.StartsWith(LocalMachinePrefix, StringComparison.OrdinalIgnoreCase)
            ? RegistryHiveKind.LocalMachine
            : RegistryHiveKind.CurrentUser;

    public static string RelativePathOf(string deletePath)
    {
        var index = deletePath.IndexOf('\\');
        return index < 0 ? deletePath : deletePath[(index + 1)..];
    }

    private static string BranchRelativePath(InstalledApp app)
    {
        var branch = app.ScopeKey switch
        {
            nameof(InstalledAppScope.LocalMachine64) => @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
            nameof(InstalledAppScope.LocalMachine32) => @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            _ => @"Software\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        return Path.Combine(branch, app.ProductCode);
    }

    private static string HivePrefix(RegistryHiveKind hive) => hive == RegistryHiveKind.LocalMachine
        ? LocalMachinePrefix
        : CurrentUserPrefix;
}
