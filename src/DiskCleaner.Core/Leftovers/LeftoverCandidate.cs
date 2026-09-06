using DiskCleaner.Core.Models;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Core.Leftovers;

public enum LeftoverReason
{
    UpdaterFolder,
    ConfigOfRemovedApp,
    OrphanProgramFiles,
    OrphanProgramData,
    OrphanRegistryEntry,
    WindowsOld,
    NearEmptyDirectory
}

public sealed class LeftoverCandidate
{
    public required string Path { get; init; }

    public required string DisplayName { get; init; }

    public required string GroupName { get; init; }

    public required LeftoverReason Reason { get; init; }

    public string ReasonText { get; init; } = string.Empty;

    public bool RequiresAdmin { get; init; }

    public CleanupRisk Risk { get; init; } = CleanupRisk.Medium;

    public CleanupCategory Category { get; init; } = CleanupCategory.Leftover;

    public string? RegistryDeletePath { get; init; }
}
