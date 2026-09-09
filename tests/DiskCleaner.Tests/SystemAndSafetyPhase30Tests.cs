using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Tests;

/// <summary>
/// Фаза 30 модуля 05 — «Исполнитель плана, снимок до/после и итоговый отчёт» (FR-5.14–5.19).
/// Backend:
/// — «Выполнить план» в порядке категорий: кэши → остатки → ПО → системные шаги (FR-5.17);
///   опасные шаги — только с подтверждением; повторный запуск безопасен (NFR);
/// — отмена между шагами через CancellationToken (FR-5.18);
/// — снимок свободного места до/после и сравнение «оценка плана vs факт» (FR-5.15);
/// — итоговый отчёт (освобождено, таблица по фазам, пропущенные с причинами) и экспорт
///   Markdown/JSON общим генератором ядра (FR-5.14/5.16);
/// — «кандидаты второго эшелона», когда факт меньше плана (FR-5.19).
/// </summary>
public class SystemAndSafetyPhase30Tests
{
    // ---------- FR-5.17 порядок категорий (unit) ----------

    [Fact]
    public void CleanPlanPhases_GroupByPhase_OrdersCachesLeftoversSoftwareSystem()
    {
        var system = Leaf("system:temp", "C:\\Windows\\Temp", CleanupCategory.Temp, CleanupRisk.Low, 500);
        var software = Leaf("app:x", "C:\\pf\\App", CleanupCategory.InstalledApp, CleanupRisk.Medium, 800, uninstall: true);
        var leftovers = Leaf("leftover:updater", "C:\\pf\\Updater", CleanupCategory.Leftover, CleanupRisk.Medium, 300);
        var cache = Leaf("cache:npm", "C:\\Users\\u\\npm", CleanupCategory.Cache, CleanupRisk.Low, 200);

        var groups = CleanPlanPhases.GroupByPhase([software, cache, system, leftovers]);

        Assert.Collection(
            groups,
            g => AssertPhase(g, CleanPlanPhase.Caches, "cache:npm"),
            g => AssertPhase(g, CleanPlanPhase.Leftovers, "leftover:updater"),
            g => AssertPhase(g, CleanPlanPhase.Software, "app:x"),
            g => AssertPhase(g, CleanPlanPhase.SystemSteps, "system:temp"));
    }

    [Fact]
    public void CleanPlanPhases_PhaseOf_MapsCategories()
    {
        Assert.Equal(CleanPlanPhase.Caches, CleanPlanPhases.PhaseOf(CleanupCategory.Cache));
        Assert.Equal(CleanPlanPhase.Caches, CleanPlanPhases.PhaseOf(CleanupCategory.DevToolchain));
        Assert.Equal(CleanPlanPhase.Leftovers, CleanPlanPhases.PhaseOf(CleanupCategory.Leftover));
        Assert.Equal(CleanPlanPhase.Software, CleanPlanPhases.PhaseOf(CleanupCategory.InstalledApp));
        Assert.Equal(CleanPlanPhase.SystemSteps, CleanPlanPhases.PhaseOf(CleanupCategory.SystemFile));
        Assert.Equal(CleanPlanPhase.SystemSteps, CleanPlanPhases.PhaseOf(CleanupCategory.Temp));
        Assert.Equal(CleanPlanPhase.SystemSteps, CleanPlanPhases.PhaseOf(CleanupCategory.RecycleBin));
        Assert.Equal(CleanPlanPhase.SystemSteps, CleanPlanPhases.PhaseOf(CleanupCategory.UserData));
    }

    [Fact]
    public async Task CleanPlanRunner_DryRun_DeletesNothing_PreviewPerCategory()
    {
        using var root = new TempRoot();
        var cacheDir = root.Combine("cache-npm");
        var leftDir = root.Combine("leftover-updater");

        var cache = DirectoryLeaf("cache:npm", cacheDir, CleanupCategory.Cache, CleanupRisk.Low, 200);
        var leftover = DirectoryLeaf("leftover:updater", leftDir, CleanupCategory.Leftover, CleanupRisk.Low, 300);

        var runner = CreateRunner();
        var report = await runner.RunAsync([cache, leftover], new CleanPlanRunOptions { DryRun = true });

        Assert.True(report.DryRun);
        Assert.False(report.Canceled);
        Assert.Empty(report.SkippedOrBlocked);
        Assert.True(Directory.Exists(cacheDir));
        Assert.True(Directory.Exists(leftDir));
        Assert.True(report.FreedBytes > 0, "dry-run прогнозирует освобождение");
    }

    // ---------- FR-5.17 подтверждение опасных шагов ----------

    [Fact]
    public async Task CleanPlanRunner_DangerousStep_NotConfirmed_IsSkipped_SafeStepRuns()
    {
        using var root = new TempRoot();
        var dangerous = root.Combine("dangerous");
        var safe = root.Combine("safe");

        var dangerousItem = DirectoryLeaf("cache:risky", dangerous, CleanupCategory.Cache, CleanupRisk.High, 1000);
        var safeItem = DirectoryLeaf("cache:safe", safe, CleanupCategory.Cache, CleanupRisk.Low, 300);

        var runner = CreateRunner();
        var report = await runner.RunAsync(
            [dangerousItem, safeItem],
            new CleanPlanRunOptions { ConfirmDangerousStep = _ => Task.FromResult(false) });

        var skipped = Assert.Single(report.SkippedOrBlocked, e => e.Item.Key == dangerousItem.Key);
        Assert.Equal(CleanOutcome.NotConfirmed, skipped.Outcome);
        Assert.True(Directory.Exists(dangerous), "Не подтверждённый опасный шаг не исполняется.");

        var cleaned = Assert.Single(report.Entries, e => e.Item.Key == safeItem.Key);
        Assert.Equal(CleanOutcome.DirectDeleted, cleaned.Outcome);
        Assert.False(Directory.Exists(safe));
    }

    // ---------- FR-5.18 отмена между шагами ----------

    [Fact]
    public async Task CleanPlanRunner_CancelBetweenPhases_StopsBeforeNext_ReRunIsSafe()
    {
        using var root = new TempRoot();
        var cacheDir = root.Combine("cache");
        var leftoverDir = root.Combine("leftover");

        var cache = DirectoryLeaf("cache:x", cacheDir, CleanupCategory.Cache, CleanupRisk.Low, 200);
        var leftover = DirectoryLeaf("leftover:y", leftoverDir, CleanupCategory.Leftover, CleanupRisk.Low, 300);

        using var cts = new CancellationTokenSource();
        var runner = CreateRunner();
        IProgress<CleanPlanRunProgress>? progress = new Progress<CleanPlanRunProgress>(p =>
        {
            if (p.Detail is not null && p.Detail.Contains("«Кэши» завершена", StringComparison.Ordinal))
            {
                cts.Cancel();
            }
        });

        var first = await runner.RunAsync([cache, leftover], progress: progress, cancellationToken: cts.Token);

        Assert.True(first.Canceled, "Отмена между шагами останавливает план.");
        Assert.False(Directory.Exists(cacheDir), "Первая фаза успела выполниться.");
        Assert.True(Directory.Exists(leftoverDir), "Следующая фаза не началась.");

        // Повторный запуск безопасен и идемпотентен (NFR): доделывает оставшееся.
        var second = await runner.RunAsync([cache, leftover], new CleanPlanRunOptions { ConfirmDangerousStep = _ => Task.FromResult(true) });

        Assert.False(second.Canceled);
        Assert.Equal(0, second.Phases.Sum(p => p.Failed));
        Assert.False(Directory.Exists(cacheDir));
        Assert.False(Directory.Exists(leftoverDir));
    }

    [Fact]
    public async Task CleanPlanRunner_FullRepeatRun_IsSafe_AndIdempotent()
    {
        using var root = new TempRoot();
        var cacheDir = root.Combine("cache");
        root.CreateFile("cache\\a.bin", 100);
        var leftoverDir = root.Combine("leftover");
        root.CreateFile("leftover\\b.bin", 100);

        var cache = DirectoryLeaf("cache:x", cacheDir, CleanupCategory.Cache, CleanupRisk.Low, 100);
        var leftover = DirectoryLeaf("leftover:y", leftoverDir, CleanupCategory.Leftover, CleanupRisk.Low, 100);

        var runner = CreateRunner();
        var first = await runner.RunAsync([cache, leftover]);
        Assert.False(first.Canceled);
        Assert.Equal(200, first.FreedBytes);

        // Повторный запуск того же плана безопасен (NFR): объекты уже отсутствуют,
        // повторно ничего не «освобождается», ошибок нет.
        var second = await runner.RunAsync([cache, leftover]);
        Assert.False(second.Canceled);
        Assert.Equal(0, second.Phases.Sum(p => p.Failed));
        Assert.Equal(0, second.FreedBytes);
        Assert.False(Directory.Exists(cacheDir));
        Assert.False(Directory.Exists(leftoverDir));
    }

    [Fact]
    public async Task CleanPlanRunner_GapWhenFreedBelowPlan_IsNotWithinTolerance()
    {
        using var root = new TempRoot();
        var runnable = root.Combine("runnable");
        root.CreateFile("runnable\\a.bin", 300);
        var inUse = root.Combine("in-use");
        root.CreateFile("in-use\\b.bin", 700);

        var runnableItem = DirectoryLeaf("cache:runnable", runnable, CleanupCategory.Cache, CleanupRisk.Low, 300);
        var blockedItem = DirectoryLeaf("cache:blocked", inUse, CleanupCategory.Cache, CleanupRisk.High, 700);

        var runner = CreateRunner();
        var report = await runner.RunAsync(
            [runnableItem, blockedItem],
            new CleanPlanRunOptions { ConfirmDangerousStep = _ => Task.FromResult(false) });

        Assert.Equal(1000, report.PlannedBytes);
        Assert.Equal(300, report.FreedBytes);
        Assert.Equal(700, report.GapBytes);
        Assert.False(report.WithinPlanTolerance);
        Assert.Contains(report.SkippedOrBlocked, e => e.Item.Key == blockedItem.Key);
    }

    // ---------- FR-5.15 снимок до/после и план против факта ----------

    [Fact]
    public async Task CleanPlanRunner_DriveSnapshot_BeforeAfterAndPlanVsFact()
    {
        using var root = new TempRoot();
        var cacheDir = root.Combine("cache");
        root.CreateFile("cache\\data.bin", 200);
        var cache = DirectoryLeaf("cache:x", cacheDir, CleanupCategory.Cache, CleanupRisk.Low, 200);

        var driveSpace = new ScriptedDriveSpace();
        var runner = CreateRunner(driveSpace);
        var report = await runner.RunAsync([cache], new CleanPlanRunOptions { DriveSpace = driveSpace });

        var snapshot = Assert.Single(report.DriveSnapshots);
        Assert.Equal(10_000_000, snapshot.FreeBeforeBytes);
        Assert.Equal(10_001_000, snapshot.FreeAfterBytes);
        Assert.Equal(1000, snapshot.DeltaBytes);

        Assert.Equal(200, report.PlannedBytes);
        Assert.Equal(200, report.FreedBytes);
        Assert.Equal(0, report.GapBytes);
        Assert.True(report.WithinPlanTolerance);
    }

    // ---------- FR-5.14/5.16 итоговый отчёт и экспорт ----------

    [Fact]
    public void CleanPlanRunFormatter_Markdown_IncludesFreedPhasesAndReasons()
    {
        var report = BuildSampleReport(includeBlocked: true);

        var markdown = CleanPlanRunFormatter.ToMarkdown(report);

        Assert.Contains("Освобождено", markdown);
        Assert.Contains("План против факта", markdown);
        Assert.Contains("Сводка по фазам", markdown);
        Assert.Contains("Пропущенные / заблокированные объекты", markdown);
        Assert.Contains("не подтверждено пользователем", markdown);
    }

    [Fact]
    public void CleanPlanRunFormatter_Json_SerializesPlanVsFactAndBlocked()
    {
        var report = BuildSampleReport(includeBlocked: true);

        var json = CleanPlanRunFormatter.ToJson(report);

        var compact = json.Replace(" ", string.Empty);
        Assert.Contains("\"schema\":\"diskcleaner.plan-run\"", compact);
        Assert.Contains("\"planVsFact\"", compact);
        Assert.Contains("\"gapBytes\":800", compact);
        Assert.Contains("cache:blocked", json);
    }

    [Fact]
    public void CleanPlanRunJson_NoBlocked_OmitsReasonsSectionInMarkdown()
    {
        var report = BuildSampleReport(includeBlocked: false);

        var markdown = CleanPlanRunFormatter.ToMarkdown(report);
        Assert.DoesNotContain("Пропущенные / заблокированные объекты", markdown);
    }

    // ---------- FR-5.19 кандидаты второго эшелона ----------

    [Fact]
    public async Task CleanPlanRunner_SecondEchelon_WhenFreedBelowPlan_ListsKeptLargeApps()
    {
        using var root = new TempRoot();
        var blocked = root.Combine("blocked-cache");
        var runnable = root.Combine("runnable-cache");

        var runnableItem = DirectoryLeaf("cache:runnable", runnable, CleanupCategory.Cache, CleanupRisk.Low, 300);
        var blockedItem = DirectoryLeaf("cache:blocked", blocked, CleanupCategory.Cache, CleanupRisk.High, 500);
        var keptBig = InstalledAppLeaf("app:vs", "Visual Studio", 2_500_000_000);
        var keptSmall = InstalledAppLeaf("app:notes", "Заметки", 500_000_000);

        var runner = CreateRunner();
        var report = await runner.RunAsync(
            [runnableItem, blockedItem],
            new CleanPlanRunOptions
            {
                ConfirmDangerousStep = _ => Task.FromResult(false),
                SecondEchelonCandidates = [keptBig, keptSmall],
                SecondEchelonMinBytes = 1_000_000_000
            });

        // Факт (300) меньше плана (800): показываются крупные приложения, которые НЕ удаляли.
        Assert.True(report.GapBytes > 0);
        var candidate = Assert.Single(report.SecondEchelonCandidates);
        Assert.Equal("app:vs", candidate.Key);

        var markdown = CleanPlanRunFormatter.ToMarkdown(report);
        Assert.Contains("второго эшелона", markdown);
        Assert.Contains("Visual Studio", markdown);
    }

    [Fact]
    public async Task CleanPlanRunner_SecondEchelon_NotShown_WhenPlanFullyFreed()
    {
        using var root = new TempRoot();
        var dir = root.Combine("cache");
        var item = DirectoryLeaf("cache:x", dir, CleanupCategory.Cache, CleanupRisk.Low, 300);
        var keptBig = InstalledAppLeaf("app:vs", "Visual Studio", 2_500_000_000);

        var runner = CreateRunner();
        var report = await runner.RunAsync(
            [item],
            new CleanPlanRunOptions
            {
                SecondEchelonCandidates = [keptBig],
                SecondEchelonMinBytes = 1_000_000_000
            });

        Assert.Equal(0, report.GapBytes);
        Assert.Empty(report.SecondEchelonCandidates);
    }

    // ---------- helpers ----------

    /// <summary>
    /// Исполнитель с фиктивным инспектором процессов: детерминированный прогон локальных
    /// удалений без WMI-опроса (пустой список процессов — ни один объект не IN_USE).
    /// </summary>
    private static CleanPlanRunner CreateRunner(IDriveSpaceService? driveSpace = null) =>
        new(
            executor: new PlanExecutor(
                localCleaner: new CacheCleanerService(),
                elevatedRunner: new ElevatedScenarioRunner(),
                processInspector: new FakeProcessInspector()),
            driveSpace: driveSpace);

    private static void AssertPhase(CleanPlanPhaseGroup group, CleanPlanPhase expectedPhase, string expectedKey)
    {
        Assert.Equal(expectedPhase, group.Phase);
        Assert.Equal(expectedKey, Assert.Single(group.Items).Key);
    }

    private static CleanupItem DirectoryLeaf(
        string key,
        string path,
        CleanupCategory category,
        CleanupRisk risk,
        long sizeBytes)
    {
        var full = Path.GetFullPath(path);
        return new CleanupItem
        {
            Key = key,
            Path = full,
            DisplayName = Path.GetFileName(full),
            Category = category,
            Risk = risk,
            Target = CleanupTarget.Directory,
            SizeBytes = sizeBytes
        };
    }

    private static CleanupItem InstalledAppLeaf(string key, string displayName, long sizeBytes) => new()
    {
        Key = key,
        DisplayName = displayName,
        Category = CleanupCategory.InstalledApp,
        Risk = CleanupRisk.Medium,
        Target = CleanupTarget.Directory,
        SizeBytes = sizeBytes,
        UninstallMode = true,
        CommandOnly = true,
        AllowDirectDelete = false
    };

    private static CleanupItem Leaf(
        string key,
        string path,
        CleanupCategory category,
        CleanupRisk risk,
        long sizeBytes,
        bool uninstall = false) => new()
    {
        Key = key,
        Path = path,
        DisplayName = key,
        Category = category,
        Risk = risk,
        Target = uninstall ? CleanupTarget.Directory : CleanupTarget.Directory,
        SizeBytes = sizeBytes,
        UninstallMode = uninstall,
        CommandOnly = uninstall,
        AllowDirectDelete = !uninstall
    };

    private static CleanPlanRunReport BuildSampleReport(bool includeBlocked)
    {
        var planned = 2000L;
        var freed = includeBlocked ? 1500L : 2000L;

        var cache = Leaf("cache:x", "C:\\x", CleanupCategory.Cache, CleanupRisk.Low, 500);
        var leftover = Leaf("leftover:y", "C:\\y", CleanupCategory.Leftover, CleanupRisk.Low, 700);
        var blocked = Leaf("cache:blocked", "C:\\z", CleanupCategory.Cache, CleanupRisk.High, 800);

        var groups = CleanPlanPhases.GroupByPhase([cache, leftover, blocked]);
        var entries = new List<CleanEntry>
        {
            new(cache, CleanOutcome.DirectDeleted, 500, null),
            new(leftover, CleanOutcome.DirectDeleted, 700, null)
        };

        if (includeBlocked)
        {
            entries.Add(new CleanEntry(blocked, CleanOutcome.NotConfirmed, 0, "Опасный шаг не подтверждён пользователем — пропущен (FR-5.17)."));
        }

        return CleanPlanRunReportBuilder.Build(
            groups,
            entries,
            [new DriveFreeSpaceSnapshot("C:\\", 10_000_000, 10_001_000)],
            Array.Empty<CleanupItem>(),
            planned,
            1000,
            dryRun: false,
            canceled: false,
            elapsed: TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Фиктивный источник свободного места (FR-5.15): первый запрос диска возвращает значение
    /// «до» (10 000 000), повторный — «после» (10 001 000), моделируя освобождение 1000 байт
    /// без обращения к реальному <see cref="DriveInfo"/>.
    /// </summary>
    private sealed class ScriptedDriveSpace : IDriveSpaceService
    {
        private readonly HashSet<string> _measured = new(StringComparer.OrdinalIgnoreCase);

        public CacheDriveSpace? GetDriveSpace(string path)
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
            var free = _measured.Add(root) ? 10_000_000 : 10_001_000;
            return new CacheDriveSpace(root, free, 100_000_000);
        }
    }
}
