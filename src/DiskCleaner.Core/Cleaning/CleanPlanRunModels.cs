using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Cleaning;

/// <summary>
/// Параметры исполнения плана (FR-5.17/5.18): режим, снимок свободного места до/после
/// (FR-5.15) и «кандидаты второго эшелона» (FR-5.19). Исполнитель опрашивает диск до
/// и после прогона через <see cref="IDriveSpaceService"/>.
/// </summary>
public sealed class CleanPlanRunOptions
{
    /// <summary>Предпросмотр (dry-run): фазы показываются, но ничего не удаляется.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Подтверждение опасного шага (FR-5.17). <c>null</c> — опасный шаг не подтверждён
    /// пользователем и пропускается с исходом <see cref="CleanOutcome.NotConfirmed"/>.
    /// </summary>
    public Func<CleanupItem, Task<bool>>? ConfirmDangerousStep { get; set; }

    /// <summary>
    /// Источник свободного места диска (FR-5.15). По умолчанию — реальный
    /// <see cref="DriveInfo"/>. В unit-тестах подменяется фиктивным.
    /// </summary>
    public IDriveSpaceService? DriveSpace { get; set; }

    /// <summary>
    /// «Кандидаты второго эшелона» (FR-5.19): крупные установленные программы, которые
    /// пользователь решил НЕ удалять. Показываются в отчёте, если факт меньше плана.
    /// </summary>
    public IReadOnlyList<CleanupItem>? SecondEchelonCandidates { get; set; }

    /// <summary>Минимальный размер кандидата второго эшелона (по умолчанию 1 ГБ, FR-5.19).</summary>
    public long SecondEchelonMinBytes { get; set; } = 1L * 1024 * 1024 * 1024;
}

/// <summary>Прогресс исполнения плана по фазам (FR-5.17/5.18).</summary>
public sealed record CleanPlanRunProgress(
    string PhaseText,
    string CurrentStep,
    int CompletedSteps,
    int TotalSteps,
    long BytesCleaned,
    string? Detail);

/// <summary>Снимок свободного места диска до/после прогона (FR-5.15).</summary>
public sealed record DriveFreeSpaceSnapshot(string RootPath, long FreeBeforeBytes, long FreeAfterBytes)
{
    public long DeltaBytes => FreeAfterBytes - FreeBeforeBytes;

    public bool HasMeasurement => FreeBeforeBytes >= 0;
}

/// <summary>
/// Итоговый результат исполнения плана (FR-5.14): освобождено (факт) против плана (оценка),
/// сводка по фазам, пропущенные/заблокированные объекты с причинами, снимок до/после
/// и кандидаты второго эшелона (FR-5.19).
/// </summary>
public sealed class CleanPlanRunReport
{
    /// <summary>Объединённые записи всех фаз (локальные + elevated), в порядке исполнения.</summary>
    public IReadOnlyList<CleanEntry> Entries { get; init; } = Array.Empty<CleanEntry>();

    /// <summary>Фазы плана в порядке исполнения (FR-5.17).</summary>
    public IReadOnlyList<CleanPlanPhaseRunReport> Phases { get; init; } = Array.Empty<CleanPlanPhaseRunReport>();

    /// <summary>Оценка плана (сумма размеров выбранных листьев).</summary>
    public long PlannedBytes { get; init; }

    /// <summary>Факт: освобождено по записям исполнителя.</summary>
    public long FreedBytes { get; init; }

    /// <summary>Факт по диску: прирост свободного места (сумма положительных дельт снапшотов).</summary>
    public long DiskFreedBytes { get; init; }

    /// <summary>Снимок свободного места до/после (FR-5.15).</summary>
    public IReadOnlyList<DriveFreeSpaceSnapshot> DriveSnapshots { get; init; } = Array.Empty<DriveFreeSpaceSnapshot>();

    /// <summary>Шаги, помеченные «пропущено»/«заблокировано» с причинами (FR-5.14).</summary>
    public IReadOnlyList<CleanEntry> SkippedOrBlocked { get; init; } = Array.Empty<CleanEntry>();

    /// <summary>Кандидаты второго эшелона (FR-5.19), если факт меньше плана.</summary>
    public IReadOnlyList<CleanupItem> SecondEchelonCandidates { get; init; } = Array.Empty<CleanupItem>();

    public bool DryRun { get; init; }

    /// <summary>Прогон остановлен отменой между шагами (FR-5.18) до завершения всех фаз.</summary>
    public bool Canceled { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Расхождение план/факт (FR-5.15): положительное — факт меньше плана.</summary>
    public long GapBytes => Math.Max(0, PlannedBytes - FreedBytes);

    /// <summary>Факт «в пределах ожидаемого» (расхождение ≤ 10%, критерий приёмки PRD 05 §4).</summary>
    public bool WithinPlanTolerance => GapBytes <= PlannedBytes / 10;
}

/// <summary>Сводка одной фазы плана.</summary>
public sealed record CleanPlanPhaseRunReport(
    CleanPlanPhase Phase,
    int Items,
    long PlannedBytes,
    long FreedBytes,
    int Completed,
    int Skipped,
    int Failed)
{
    public string PhaseText => CleanPlanPhases.DisplayName(Phase);
}

/// <summary>Строит <see cref="CleanPlanRunReport"/> из фаз и записей исполнения.</summary>
public static class CleanPlanRunReportBuilder
{
    public static CleanPlanRunReport Build(
        IReadOnlyList<CleanPlanPhaseGroup> phases,
        IReadOnlyList<CleanEntry> entries,
        IReadOnlyList<DriveFreeSpaceSnapshot> snapshots,
        IReadOnlyList<CleanupItem> secondEchelon,
        long plannedBytes,
        long diskFreed,
        bool dryRun,
        bool canceled,
        TimeSpan elapsed)
    {
        var phaseReports = phases.Select(phase =>
        {
            var phaseEntries = entries
                .Where(e => phase.Items.Any(i => string.Equals(i.Key, e.Item.Key, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            return new CleanPlanPhaseRunReport(
                phase.Phase,
                phase.Items.Count,
                phase.EstimatedBytes,
                phaseEntries.Sum(e => Math.Max(0, e.FreedBytes)),
                phaseEntries.Count(e => IsSuccess(e.Outcome)),
                phaseEntries.Count(e => IsSkipped(e.Outcome)),
                phaseEntries.Count(e => e.Outcome == CleanOutcome.Error));
        }).ToList();

        return new CleanPlanRunReport
        {
            Entries = entries,
            Phases = phaseReports,
            PlannedBytes = plannedBytes,
            FreedBytes = entries.Sum(e => Math.Max(0, e.FreedBytes)),
            DiskFreedBytes = diskFreed,
            DriveSnapshots = snapshots,
            SkippedOrBlocked = entries.Where(e => IsSkippedOrBlocked(e.Outcome)).ToList(),
            SecondEchelonCandidates = secondEchelon,
            DryRun = dryRun,
            Canceled = canceled,
            Elapsed = elapsed
        };
    }

    private static bool IsSuccess(CleanOutcome outcome) => outcome switch
    {
        CleanOutcome.NativeCleaned or
        CleanOutcome.DirectDeleted or
        CleanOutcome.CommandOnlyCleaned or
        CleanOutcome.MovedToRecycleBin or
        CleanOutcome.Uninstalled or
        CleanOutcome.RegistryEntryDeleted or
        CleanOutcome.AlreadyUninstalled => true,
        _ => false
    };

    private static bool IsSkipped(CleanOutcome outcome) => outcome switch
    {
        CleanOutcome.InUseSkipped or
        CleanOutcome.Denied or
        CleanOutcome.ElevationDeclined or
        CleanOutcome.NotConfirmed or
        CleanOutcome.RequiresAdmin => true,
        _ => false
    };

    public static bool IsSkippedOrBlocked(CleanOutcome outcome) => outcome switch
    {
        CleanOutcome.InUseSkipped or
        CleanOutcome.Denied or
        CleanOutcome.ElevationDeclined or
        CleanOutcome.NotConfirmed or
        CleanOutcome.RequiresAdmin or
        CleanOutcome.Error or
        CleanOutcome.Partial or
        CleanOutcome.RebootRequired => true,
        _ => false
    };
}
