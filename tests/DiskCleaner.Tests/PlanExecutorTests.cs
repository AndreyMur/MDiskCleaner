using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Tests;

public class PlanExecutorTests
{
    private sealed class FakeElevatedRunner : IElevatedRunner
    {
        private readonly int _exitCode;

        public FakeElevatedRunner(int exitCode)
        {
            _exitCode = exitCode;
        }

        public ElevatedScenario? Scenario { get; private set; }

        public Task<ElevatedJournal> RunAsync(ElevatedScenario scenario, CancellationToken cancellationToken = default)
        {
            Scenario = scenario;
            var results = scenario.Steps.Select(step => new ElevatedStepResult
            {
                Id = step.Id,
                Success = _exitCode is 0 or 1605 or 3010,
                ExitCode = _exitCode,
                Note = _exitCode == 1605 ? "Продукт уже не установлен (код 1605)." : null,
                RebootRequired = _exitCode == 3010
            }).ToList();

            return Task.FromResult(new ElevatedJournal { Results = results });
        }
    }

    private static CleanupItem Dir(string path, CleanupCategory category, bool requiresAdmin = false) => new()
    {
        Key = "test:" + path,
        Path = path,
        DisplayName = Path.GetFileName(path),
        Category = category,
        Target = CleanupTarget.Directory,
        RequiresAdmin = requiresAdmin
    };

    private static PlanExecutor CreateExecutor() => new(
        localCleaner: new CacheCleanerService(),
        elevatedRunner: new ElevatedScenarioRunner());

    [Fact]
    public async Task Clean_AdminItem_GoesThroughElevatedScenario()
    {
        using var root = new TempRoot();
        var adminDir = root.Combine("AdminLeftover");
        root.CreateFile("AdminLeftover\\file.bin", 900);
        var item = Dir(adminDir, CleanupCategory.Leftover, requiresAdmin: true);

        var executor = CreateExecutor();
        var report = await executor.CleanAsync([item]);

        Assert.False(Directory.Exists(adminDir));
        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.DirectDeleted, entry.Outcome);
        Assert.Equal(900, entry.FreedBytes);
    }

    [Fact]
    public async Task Clean_LocalCacheItem_IsDeletedLocally()
    {
        using var root = new TempRoot();
        var dir = root.Combine("npm-cache");
        root.CreateFile("npm-cache\\a.bin", 400);
        var item = Dir(dir, CleanupCategory.Cache);

        var executor = CreateExecutor();
        var report = await executor.CleanAsync([item]);

        Assert.False(Directory.Exists(dir));
        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.DirectDeleted, entry.Outcome);
    }

    [Fact]
    public async Task DryRun_DeletesNothing_ForAdminAndLocalItems()
    {
        using var root = new TempRoot();
        var local = root.Combine("cache-local");
        var admin = root.Combine("AdminLeftover");
        root.CreateFile("cache-local\\a.bin", 100);
        root.CreateFile("AdminLeftover\\a.bin", 200);

        var executor = CreateExecutor();
        var report = await executor.CleanAsync(
            [Dir(local, CleanupCategory.Cache), Dir(admin, CleanupCategory.Leftover, requiresAdmin: true)],
            new CleanOptions { DryRun = true });

        Assert.True(Directory.Exists(local));
        Assert.True(Directory.Exists(admin));
        Assert.Equal(2, report.Entries.Count);
        Assert.All(report.Entries, e => Assert.Equal(CleanOutcome.DryRun, e.Outcome));
    }

    [Fact]
    public async Task Clean_InUseItem_IsSkipped_NotDeleted()
    {
        using var root = new TempRoot();
        var dir = root.Combine("InUseCache");
        root.CreateFile("InUseCache\\a.bin", 100);
        var item = Dir(dir, CleanupCategory.Cache);
        item.InUse = true;

        var executor = CreateExecutor();
        var report = await executor.CleanAsync([item]);

        Assert.True(Directory.Exists(dir));
        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.InUseSkipped, entry.Outcome);
    }

    [Fact]
    public async Task Clean_UninstallModeItem_RunsSingleElevatedBatch_AndMapsOutcome()
    {
        var item = new CleanupItem
        {
            Key = "app:LocalMachine64:{44444444-4444-4444-4444-444444444444}",
            DisplayName = "Пример SDK",
            Category = CleanupCategory.InstalledApp,
            UninstallMode = true,
            RequiresAdmin = true,
            CommandOnly = true,
            AllowDirectDelete = false,
            CleanCommandFile = "msiexec.exe",
            CleanCommandArgs = "/x {44444444-4444-4444-4444-444444444444} /qn /norestart"
        };

        var elevated = new FakeElevatedRunner(0);
        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(),
            elevatedRunner: elevated);

        var report = await executor.CleanAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.Uninstalled, entry.Outcome);
        Assert.Single(elevated.Scenario!.Steps);
        Assert.Equal(ElevatedStepKind.RunProcess, elevated.Scenario.Steps[0].Kind);
    }

    [Fact]
    public async Task Clean_UninstallAlreadyAbsent_MapsToAlreadyUninstalled()
    {
        var item = new CleanupItem
        {
            Key = "app:LocalMachine64:{55555555-5555-5555-5555-555555555555}",
            DisplayName = "Уже удалён",
            Category = CleanupCategory.InstalledApp,
            UninstallMode = true,
            RequiresAdmin = true,
            CommandOnly = true,
            CleanCommandFile = "msiexec.exe",
            CleanCommandArgs = "/x {55555555-5555-5555-5555-555555555555} /qn /norestart"
        };

        var elevated = new FakeElevatedRunner(1605);
        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(),
            elevatedRunner: elevated);

        var report = await executor.CleanAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.AlreadyUninstalled, entry.Outcome);
    }

    // ---- Фаза 3 модуля 02: глобальные менеджеры (Program Files) исполняются через
    // elevated-инфраструктуру (UAC RunAs); журнал действий фиксирует каждый объект. ----

    [Fact]
    public async Task Clean_CacheCommandRequiringAdmin_RunsElevatedRunProcess_WithCatalogTimeout()
    {
        var item = new CleanupItem
        {
            Key = "rustup-toolchains:(command)",
            DisplayName = "rustup toolchains",
            Category = CleanupCategory.DevToolchain,
            Risk = CleanupRisk.Medium,
            CommandOnly = true,
            AllowDirectDelete = false,
            RequiresAdmin = true,
            CleanCommandFile = "cmd.exe",
            CleanCommandArgs = "/c rustup self uninstall -y",
            CleanCommandTimeoutSec = 600
        };

        var elevated = new FakeElevatedRunner(0);
        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(),
            elevatedRunner: elevated,
            processInspector: new FakeProcessInspector());

        var report = await executor.CleanAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.CommandOnlyCleaned, entry.Outcome);

        var step = Assert.Single(elevated.Scenario!.Steps);
        Assert.Equal(ElevatedStepKind.RunProcess, step.Kind);
        Assert.Equal("cmd.exe", step.FileName);
        Assert.Equal(600, step.TimeoutSec);
        Assert.Contains("rustup self uninstall", step.Arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clean_CacheCommandRequiringAdmin_WhenElevationDeclined_MapsElevationDeclined()
    {
        var item = new CleanupItem
        {
            Key = "global-npm:(command)",
            DisplayName = "global npm cache",
            Category = CleanupCategory.Cache,
            CommandOnly = true,
            AllowDirectDelete = false,
            RequiresAdmin = true,
            CleanCommandFile = "cmd.exe",
            CleanCommandArgs = "/c npm cache clean --force"
        };

        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(),
            elevatedRunner: new DecliningElevatedRunner(),
            processInspector: new FakeProcessInspector());

        var report = await executor.CleanAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.ElevationDeclined, entry.Outcome);
        Assert.Contains("Повышение прав отменено", entry.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clean_MixedCachePlan_JournalCapturesDeletedAndSkippedWithReasons()
    {
        using var root = new TempRoot();
        var vscodeCache = Path.Combine(root.AppData, "Code", "Cache");
        root.CreateFile("AppData\\Code\\Cache\\a.bin", 700);
        var npmCache = root.Combine("npm-cache");
        root.CreateFile("npm-cache\\a.bin", 1300);

        var vscodeItem = new CleanupItem
        {
            Key = "vscode-cache:" + vscodeCache,
            Path = vscodeCache,
            DisplayName = "VS Code Cache",
            Category = CleanupCategory.Cache,
            Risk = CleanupRisk.Low,
            Target = CleanupTarget.Directory,
            OwnerProcessNames = new[] { "Code", "Code - Insiders", "VSCodium" }
        };
        var npmItem = Dir(npmCache, CleanupCategory.Cache);

        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(),
            elevatedRunner: new ElevatedScenarioRunner(),
            processInspector: new FakeProcessInspector(
                new RunningProcessInfo(@"C:\Program Files\Microsoft VS Code\Code.exe", "Code")));

        var report = await executor.CleanAsync([vscodeItem, npmItem]);

        Assert.Equal(2, report.Entries.Count);

        var deleted = report.Entries.Single(e => e.Outcome == CleanOutcome.DirectDeleted);
        Assert.Equal(npmItem.Key, deleted.Item.Key);
        Assert.Equal(1300, deleted.FreedBytes);

        var skipped = report.Entries.Single(e => e.Outcome == CleanOutcome.InUseSkipped);
        Assert.Equal(vscodeItem.Key, skipped.Item.Key);
        Assert.Equal(0, skipped.FreedBytes);
        Assert.Contains("Используется запущенным процессом", skipped.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, report.DeferredItems);
        Assert.True(Directory.Exists(vscodeCache));
        Assert.False(Directory.Exists(npmCache));

        var journal = CleanReportFormatter.ToMarkdown(report);
        Assert.Contains(npmItem.DisplayName, journal);
        Assert.Contains("удалено", journal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("отложено (используется)", journal, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DecliningElevatedRunner : IElevatedRunner
    {
        public Task<ElevatedJournal> RunAsync(ElevatedScenario scenario, CancellationToken cancellationToken = default) =>
            throw new ElevationDeclinedException("Подтверждение UAC отклонено пользователем.", new InvalidOperationException());
    }
}
