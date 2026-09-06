namespace DiskCleaner.Core.Elevated;

using DiskCleaner.Core.Models;
using DiskCleaner.Core.Uninstall;

public enum ElevatedStepKind
{
    DeletePath,
    RunProcess,
    DeleteRegistryKey,
    ServiceCleanDirectory
}

public sealed class ElevatedScenario
{
    public IReadOnlyList<ElevatedStep> Steps { get; init; } = Array.Empty<ElevatedStep>();
}

public sealed class ElevatedStep
{
    public required string Id { get; init; }

    public ElevatedStepKind Kind { get; init; }

    public string? Path { get; init; }

    public CleanupTarget? Target { get; init; }

    public bool DeleteContentsOnly { get; init; }

    public string? FileName { get; init; }

    public string? Arguments { get; init; }

    public int TimeoutSec { get; init; } = 300;

    public ExitCodePolicy ExitCodes { get; init; } = ExitCodePolicy.Generic;

    public string? BundleProductCode { get; init; }

    public string? PackageCacheRoot { get; init; }

    public RegistryHiveKind? RegistryHive { get; init; }

    public string? RegistrySubKeyPath { get; init; }

    /// <summary>Имя службы для шага ServiceCleanDirectory (например, wuauserv).</summary>
    public string? ServiceName { get; init; }
}

public enum ExitCodePolicy
{
    Generic,
    Msiexec
}

public sealed class ElevatedStepResult
{
    public required string Id { get; init; }

    public bool Success { get; init; }

    public bool RebootRequired { get; init; }

    public int? ExitCode { get; init; }

    public long FreedBytes { get; init; }

    public string? Error { get; init; }

    public string? Note { get; init; }
}

public sealed class ElevatedJournal
{
    public DateTime StartedAt { get; init; }

    public DateTime FinishedAt { get; init; }

    public IReadOnlyList<ElevatedStepResult> Results { get; init; } = Array.Empty<ElevatedStepResult>();

    public bool AllSucceeded => Results.All(r => r.Success);
}
