namespace DiskCleaner.Core.Uninstall;

public enum RegistryHiveKind
{
    LocalMachine,
    CurrentUser
}

public enum InstalledAppScope
{
    LocalMachine64,
    LocalMachine32,
    CurrentUser
}

public sealed class InstalledApp
{
    public required string ProductCode { get; init; }

    public required string ScopeKey { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string? Publisher { get; init; }

    public string? DisplayVersion { get; init; }

    public string? InstallDate { get; init; }

    public string? InstallLocation { get; init; }

    public string? UninstallString { get; init; }

    public string? QuietUninstallString { get; init; }

    public long EstimatedSizeBytes { get; init; }

    public bool IsSystemComponent { get; init; }

    public bool IsWindowsInstaller { get; init; }

    public bool RequiresAdmin =>
        ScopeKey is nameof(InstalledAppScope.LocalMachine64) or nameof(InstalledAppScope.LocalMachine32);
}
