using Microsoft.Win32;
using DiskCleaner.Core.Abstractions;
using DiskCleaner.Core.Environment;

namespace DiskCleaner.Core.Uninstall;

public sealed record RegistryBranchSpec(RegistryHiveKind Hive, string RelativePath)
{
    public static RegistryBranchSpec LocalMachine64() =>
        new(RegistryHiveKind.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall");

    public static RegistryBranchSpec LocalMachine32() =>
        new(RegistryHiveKind.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");

    public static RegistryBranchSpec CurrentUser() =>
        new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
}

public sealed class UninstallRegistryService
{
    private readonly IEnvironment _environment;
    private readonly IReadOnlyList<RegistryBranchSpec> _branches;

    public UninstallRegistryService(
        IEnvironment? environment = null,
        IEnumerable<RegistryBranchSpec>? branches = null)
    {
        _environment = environment ?? new EnvironmentProvider();
        _branches = (branches ?? DefaultBranches()).ToList();
    }

    public static IReadOnlyList<RegistryBranchSpec> DefaultBranches() =>
    [
        RegistryBranchSpec.LocalMachine64(),
        RegistryBranchSpec.LocalMachine32(),
        RegistryBranchSpec.CurrentUser()
    ];

    public IReadOnlyList<InstalledApp> ReadInstalledApps()
    {
        var apps = new List<InstalledApp>();
        foreach (var branch in _branches)
        {
            ReadBranch(branch, apps);
        }

        return apps;
    }

    public IReadOnlyList<InstalledApp> ReadInstalledAppsFrom(RegistryBranchSpec branch)
    {
        var apps = new List<InstalledApp>();
        ReadBranch(branch, apps);
        return apps;
    }

    private void ReadBranch(RegistryBranchSpec branch, List<InstalledApp> apps)
    {
        var root = OpenRoot(branch.Hive);
        using var uninstall = root?.OpenSubKey(branch.RelativePath);
        if (uninstall is null)
        {
            return;
        }

        foreach (var productCode in uninstall.GetSubKeyNames())
        {
            using var key = uninstall.OpenSubKey(productCode);
            if (key is null)
            {
                continue;
            }

            var displayName = ReadString(key, "DisplayName")?.Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            var uninstallString = ReadString(key, "UninstallString")?.Trim();
            var quietUninstallString = ReadString(key, "QuietUninstallString")?.Trim();
            var installLocation = ReadString(key, "InstallLocation")?.Trim();
            if (!string.IsNullOrWhiteSpace(installLocation))
            {
                installLocation = _environment.ExpandPath(installLocation);
            }

            apps.Add(new InstalledApp
            {
                ProductCode = productCode,
                ScopeKey = ScopeKeyOf(branch),
                DisplayName = displayName,
                Publisher = ReadString(key, "Publisher")?.Trim(),
                DisplayVersion = ReadString(key, "DisplayVersion")?.Trim(),
                InstallDate = ReadString(key, "InstallDate")?.Trim(),
                InstallLocation = string.IsNullOrWhiteSpace(installLocation) ? null : installLocation,
                UninstallString = string.IsNullOrWhiteSpace(uninstallString) ? null : uninstallString,
                QuietUninstallString = string.IsNullOrWhiteSpace(quietUninstallString) ? null : quietUninstallString,
                EstimatedSizeBytes = ReadEstimatedSizeBytes(key),
                IsSystemComponent = ReadInt(key, "SystemComponent") == 1,
                IsWindowsInstaller = IsWindowsInstallerKey(uninstallString, quietUninstallString, key)
            });
        }
    }

    private static bool IsWindowsInstallerKey(string? uninstallString, string? quietUninstallString, RegistryKey key)
    {
        if (ContainsMsiexec(uninstallString) || ContainsMsiexec(quietUninstallString))
        {
            return true;
        }

        return ReadString(key, "WindowsInstaller")?.Equals("1", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool ContainsMsiexec(string? value) =>
        value is not null &&
        value.Contains("msiexec", StringComparison.OrdinalIgnoreCase);

    private static string ScopeKeyOf(RegistryBranchSpec branch) => branch.Hive switch
    {
        RegistryHiveKind.LocalMachine when branch.RelativePath.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase)
            => nameof(InstalledAppScope.LocalMachine32),
        RegistryHiveKind.LocalMachine => nameof(InstalledAppScope.LocalMachine64),
        _ => nameof(InstalledAppScope.CurrentUser)
    };

    private static RegistryKey? OpenRoot(RegistryHiveKind hive) => hive switch
    {
        RegistryHiveKind.LocalMachine => Registry.LocalMachine,
        _ => Registry.CurrentUser
    };

    private static string? ReadString(RegistryKey key, string name) =>
        key.GetValue(name) is string value ? value : null;

    private static int ReadInt(RegistryKey key, string name) =>
        key.GetValue(name) is int value ? value : 0;

    private static long ReadEstimatedSizeBytes(RegistryKey key)
    {
        if (key.GetValue("EstimatedSize") is int kb && kb > 0)
        {
            return (long)kb * 1024;
        }

        return 0;
    }
}
