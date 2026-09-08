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

    /// <summary>
    /// Для шага <see cref="ElevatedStepKind.RunProcess"/> с политикой
    /// <see cref="ExitCodePolicy.Msiexec"/>: код пакетной (bundle) установки, для которой при
    /// exit-коде «не установлено» (1605/1612) запускается штатный деинсталлятор из
    /// <c>Package Cache\{code}</c> (FR-4.6).
    /// </summary>
    public string? BundleProductCode { get; init; }

    /// <summary>Корень, под которым ищется <c>Package Cache</c> (по умолчанию <c>%ProgramData%</c>).</summary>
    public string? PackageCacheRoot { get; init; }

    public RegistryHiveKind? RegistryHive { get; init; }

    public string? RegistrySubKeyPath { get; init; }

    /// <summary>Имя службы для шага ServiceCleanDirectory (например, wuauserv).</summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Для шага <see cref="ElevatedStepKind.RunProcess"/>: после успешного выполнения команды
    /// (exit-код по политике) elevated-исполнитель проверяет, что файл по этому пути исчез
    /// (например, <c>hiberfil.sys</c> после <c>powercfg /h off</c>, FR-5.4). Если файл остался —
    /// шаг не считается успешным. <c>null</c> — проверка не выполняется.
    /// </summary>
    public string? VerifyPathAbsent { get; init; }
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
