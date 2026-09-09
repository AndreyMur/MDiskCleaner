using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Cleaning;

/// <summary>
/// Исполнитель плана (Фаза 30, FR-5.17–5.19): выполняет выбранные объекты по категориям
/// в порядке «кэши → остатки → ПО → системные шаги», каждый опасный шаг — с подтверждением,
/// отмена возможна между шагами через <see cref="CancellationToken"/>. До и после прогона
/// снимается свободное место диска (FR-5.15); результат сводится в отчёт «факт против плана»
/// с кандидатами второго эшелона (FR-5.14/5.19).
/// </summary>
public sealed class CleanPlanRunner
{
    private readonly PlanExecutor _executor;
    private readonly IDriveSpaceService _driveSpace;

    public CleanPlanRunner(
        PlanExecutor? executor = null,
        IDriveSpaceService? driveSpace = null)
    {
        _executor = executor ?? new PlanExecutor();
        _driveSpace = driveSpace ?? new DriveInfoDriveSpaceService();
    }

    public async Task<CleanPlanRunReport> RunAsync(
        IEnumerable<CleanupItem> planItems,
        CleanPlanRunOptions? options = null,
        IProgress<CleanPlanRunProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var runOptions = options ?? new CleanPlanRunOptions();
        var startedAt = DateTime.UtcNow;
        var phaseGroups = CleanPlanPhases.GroupByPhase(planItems);
        var allLeaves = phaseGroups.SelectMany(g => g.Items).ToList();
        var leavesByPhase = phaseGroups.ToDictionary(g => g.Phase);

        // Оценка плана — размер выбранных пользователем объектов (FR-5.15); фактически
        // освобождено может быть меньше (пропуски, отказ от опасных шагов, IN_USE).
        var plannedBytes = allLeaves.Sum(l => Math.Max(0, l.EffectiveSizeBytes));

        // Снимок «до» снимается до фаз, «после» — по завершении прогона (FR-5.15).
        var driveRoots = CollectDriveRoots(allLeaves);
        var before = CaptureFreeSpace(driveRoots, runOptions);
        var entries = new List<CleanEntry>();
        var totalSteps = allLeaves.Count;
        var completedSteps = 0;
        long bytesCleaned = 0;
        var canceled = false;

        foreach (var phase in CleanPlanPhases.Ordered)
        {
            if (!leavesByPhase.TryGetValue(phase, out var group))
            {
                continue;
            }

            // Отмена разрешена между шагами (FR-5.18): текущий шаг (фаза) доводится до безопасной
            // точки останова, затем исполнитель останавливается перед следующей фазой.
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            progress?.Report(new CleanPlanRunProgress(
                CleanPlanPhases.DisplayName(phase),
                group.Items.FirstOrDefault()?.DisplayName ?? string.Empty,
                completedSteps,
                totalSteps,
                bytesCleaned,
                "Подготовка фазы «" + CleanPlanPhases.DisplayName(phase) + "»"));

            try
            {
                var phaseEntries = await ExecutePhaseAsync(
                    group,
                    runOptions,
                    cancellationToken);

                entries.AddRange(phaseEntries);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Отмена пришла внутри фазы (между отдельными операциями фазы): фаза остановлена
                // в безопасной точке, выполненные ранее фазы сохраняются в отчёте (FR-5.18).
                canceled = true;
                break;
            }

            completedSteps += group.Items.Count;
            bytesCleaned += entries
                .Where(e => group.Items.Any(i => string.Equals(i.Key, e.Item.Key, StringComparison.OrdinalIgnoreCase)))
                .Sum(e => Math.Max(0, e.FreedBytes));

            progress?.Report(new CleanPlanRunProgress(
                CleanPlanPhases.DisplayName(phase),
                group.Items.LastOrDefault()?.DisplayName ?? string.Empty,
                completedSteps,
                totalSteps,
                bytesCleaned,
                "Фаза «" + CleanPlanPhases.DisplayName(phase) + "» завершена"));
        }

        var after = runOptions.DryRun
            ? new Dictionary<string, long>(before, StringComparer.OrdinalIgnoreCase)
            : CaptureFreeSpace(driveRoots, runOptions);

        var snapshots = driveRoots
            .Where(r => before.ContainsKey(r))
            .Select(root => new DriveFreeSpaceSnapshot(
                root,
                before[root],
                after.TryGetValue(root, out var free) ? free : before[root]))
            .ToList();

        var driveFreed = ComputeDriveFreed(snapshots);
        var entriesFreed = entries.Sum(e => Math.Max(0, e.FreedBytes));
        var secondEchelon = SelectSecondEchelon(runOptions, allLeaves, plannedBytes, entriesFreed);

        return CleanPlanRunReportBuilder.Build(
            phaseGroups,
            entries,
            snapshots,
            secondEchelon,
            plannedBytes,
            driveFreed,
            runOptions.DryRun,
            canceled,
            DateTime.UtcNow - startedAt);
    }

    /// <summary>
    /// Выполняет одну фазу: опасные шаги подтверждаются пользователем (FR-5.17) и без
    /// подтверждения пропускаются с исходом <see cref="CleanOutcome.NotConfirmed"/>;
    /// подтверждённые шаги исполняются через общего исполнителя <see cref="PlanExecutor"/>.
    /// </summary>
    private async Task<IReadOnlyList<CleanEntry>> ExecutePhaseAsync(
        CleanPlanPhaseGroup group,
        CleanPlanRunOptions options,
        CancellationToken cancellationToken)
    {
        var confirmed = new List<CleanupItem>();
        var entries = new List<CleanEntry>();

        foreach (var item in group.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (options.DryRun || !DangerousPlanStep.IsDangerous(item))
            {
                confirmed.Add(item);
                continue;
            }

            // FR-5.17: опасный шаг исполняется только с подтверждением.
            var confirm = options.ConfirmDangerousStep is null
                ? false
                : await options.ConfirmDangerousStep(item);

            if (confirm)
            {
                confirmed.Add(item);
            }
            else
            {
                entries.Add(new CleanEntry(
                    item,
                    CleanOutcome.NotConfirmed,
                    0,
                    "Опасный шаг не подтверждён пользователем — пропущен (FR-5.17)."));
            }
        }

        if (confirmed.Count == 0)
        {
            return entries;
        }

        var report = await _executor.CleanAsync(
            confirmed,
            new CleanOptions { DryRun = options.DryRun },
            null,
            cancellationToken);

        entries.AddRange(report.Entries);
        return entries;
    }

    /// <summary>Корни затронутых дисков (для снимка свободного места, FR-5.15).</summary>
    private static IReadOnlyList<string> CollectDriveRoots(IReadOnlyList<CleanupItem> leaves) =>
        leaves
            .Select(LeafDriveRoot)
            .Where(root => root is not null)
            .Select(root => root!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Свободное место по затронутым дискам в текущий момент (FR-5.15).</summary>
    private Dictionary<string, long> CaptureFreeSpace(
        IReadOnlyList<string> roots,
        CleanPlanRunOptions options)
    {
        var service = options.DriveSpace ?? _driveSpace;
        var current = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var space = service.GetDriveSpace(root);
            if (space is not null)
            {
                current[root] = space.FreeBytes;
            }
        }

        return current;
    }

    private static long ComputeDriveFreed(IEnumerable<DriveFreeSpaceSnapshot> snapshots) =>
        snapshots.Sum(s => Math.Max(0, s.DeltaBytes));

    /// <summary>
    /// Корень диска объекта: для Корзины — корень диска (EmptyRecycleBinDrive), для остальных —
    /// корень пути. Command-only шаги без пути (гибернация и т.п.) диск не трогают локально.
    /// </summary>
    private static string? LeafDriveRoot(CleanupItem item)
    {
        var probe = item.EmptyRecycleBinDrive ?? item.Path;
        if (string.IsNullOrWhiteSpace(probe))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(probe);
            var root = Path.GetPathRoot(full);
            return string.IsNullOrEmpty(root) ? null : root;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Кандидаты второго эшелона (FR-5.19): крупные приложения, которые пользователь решил
    /// НЕ удалять, показываются, если фактически освобождено меньше плана (оценка vs факт,
    /// FR-5.15): часть шагов пропущена/заблокирована, и места не хватает.
    /// </summary>
    private static IReadOnlyList<CleanupItem> SelectSecondEchelon(
        CleanPlanRunOptions options,
        IReadOnlyList<CleanupItem> leaves,
        long plannedBytes,
        long entriesFreed)
    {
        if (options.DryRun || options.SecondEchelonCandidates is null || options.SecondEchelonCandidates.Count == 0)
        {
            return Array.Empty<CleanupItem>();
        }

        if (entriesFreed >= plannedBytes)
        {
            return Array.Empty<CleanupItem>();
        }

        var plannedKeys = leaves.Select(l => l.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return options.SecondEchelonCandidates
            .Where(c => c.Category == CleanupCategory.InstalledApp)
            .Where(c => !plannedKeys.Contains(c.Key))
            .Where(c => c.EffectiveSizeBytes >= options.SecondEchelonMinBytes)
            .OrderByDescending(c => c.EffectiveSizeBytes)
            .ThenBy(c => c.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }
}
