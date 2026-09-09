using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Caches;

public enum CleanOutcome
{
    DryRun,
    InUseSkipped,
    NativeCleaned,
    DirectDeleted,
    CommandOnlyCleaned,
    Partial,
    MovedToRecycleBin,
    Denied,
    Error,
    Uninstalled,
    RegistryEntryDeleted,
    RebootRequired,
    AlreadyUninstalled,
    ElevationDeclined,

    /// <summary>
    /// Шагу не хватило прав в пользовательском контексте (Win32 error 5 / Access Denied),
    /// и он перенесён в админ-пачку (FR-5.12). Исполнитель плана повторяет такой шаг через
    /// единый повышенный процесс; остальные шаги продолжаются.
    /// </summary>
    RequiresAdmin,

    /// <summary>
    /// Опасный шаг не подтверждён пользователем (FR-5.17): движок плана спрашивает
    /// подтверждение для шагов с риском выше низкого / системных; при отказе шаг
    /// пропускается с этим исходом и не исполняется ни локально, ни в админ-пачке.
    /// </summary>
    NotConfirmed
}

public sealed record CleanEntry(
    CleanupItem Item,
    CleanOutcome Outcome,
    long FreedBytes,
    string? Note,
    int? ExitCode = null);

public sealed record CleanProgress(
    string CurrentName,
    int CompletedItems,
    int TotalItems,
    long BytesCleaned,
    string? Detail);

public sealed class CleanOptions
{
    public bool DryRun { get; set; }

    public bool AllowNativeCommands { get; set; } = true;
}

public sealed class CleanReport
{
    public IReadOnlyList<CleanEntry> Entries { get; init; } = Array.Empty<CleanEntry>();

    public bool DryRun { get; init; }

    public TimeSpan Elapsed { get; init; }

    public long TotalFreedBytes => Entries.Sum(e => Math.Max(0, e.FreedBytes));

    /// <summary>
    /// Шаги, завершившиеся ошибкой. «Частично» (FR-5.9) и «пропущен» не считаются ошибками плана:
    /// частично очищенный шаг — легитимный результат с перечислением неудалённых файлов.
    /// </summary>
    public int FailedItems => Entries.Count(e => e.Outcome == CleanOutcome.Error);

    /// <summary>Шаги со статусом «частично»: часть файлов удалена, часть заблокирована/недоступна (FR-5.9).</summary>
    public int PartialItems => Entries.Count(e => e.Outcome == CleanOutcome.Partial);

    /// <summary>Шаги, пропущенные из-за занятости процессами (IN_USE), deny-списка или отказа UAC.</summary>
    public int SkippedItems => Entries.Count(e => e.Outcome is CleanOutcome.InUseSkipped or CleanOutcome.Denied or CleanOutcome.ElevationDeclined);

    /// <summary>Шаги, перенесённые в «требует админа» из-за нехватки прав (код 5 / Access Denied, FR-5.12).</summary>
    public int RequiresAdminItems => Entries.Count(e => e.Outcome == CleanOutcome.RequiresAdmin);

    public int DeferredItems => Entries.Count(e => e.Outcome == CleanOutcome.InUseSkipped);
}
