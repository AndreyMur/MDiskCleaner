using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Leftovers;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;

namespace DiskCleaner.Tests;

/// <summary>
/// Тесты исполнения подтверждённых удалений остатков (фаза 19, модуль 03, FR-3.8, FR-3.4,
/// FR-3.5, §5, NFR): dry-run ничего не удаляет; перед удалением повторяются проверки «живых»
/// объектов (процесс/%PATH%/служба); для Program Files/ProgramData формируется elevated-сценарий
/// одним UAC-подъёмом; журнал фиксирует основание удаления для каждого объекта (FR-3.6).
/// </summary>
public class LeftoverCleanServiceTests
{
    private static LeftoverCandidateScanner Scanner(FakeEnvironment environment) =>
        new(environment, new FakeProcessInspector(), new FakeServiceInspector());

    private static LeftoverCleanService CleanService(
        FakeEnvironment environment,
        IProcessInspector? processes = null,
        IServiceInspector? services = null,
        IElevatedRunner? elevatedRunner = null) =>
        new(
            environment,
            processes ?? new FakeProcessInspector(),
            services ?? new FakeServiceInspector(),
            elevatedRunner: elevatedRunner ?? new RecordingElevatedRunner());

    [Fact]
    public async Task DryRun_PreviewDeletesNothing_AndReportsExpectedReclaim()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var updaterPath = root.Combine("LocalAppData", "lm-studio-updater");
        root.CreateFile("LocalAppData\\lm-studio-updater\\Update.exe", 100);
        root.CreateFile("ProgramFiles\\OrphanTool\\tool.bin", 10);

        var plan = new LeftoverPlanService(Scanner(environment), new ExclusionsStore(root.Combine("exclusions")))
            .BuildPlan([]);
        var updater = Assert.Single(plan.Items, i => i.DisplayName == "lm-studio-updater");
        var orphan = Assert.Single(plan.Items, i => i.DisplayName == "OrphanTool");
        updater.IsEnabled = true;
        orphan.IsEnabled = true;

        var report = await CleanService(environment).CleanAsync(plan.EnabledItems, new LeftoverCleanOptions { DryRun = true });

        Assert.True(report.DryRun);
        Assert.All(report.Entries, e => Assert.Equal(LeftoverCleanOutcome.DryRun, e.Outcome));
        Assert.True(Directory.Exists(updaterPath));
        Assert.True(Directory.Exists(orphan.Path));

        var orphanEntry = Assert.Single(report.Entries, e => e.Item.DisplayName == "OrphanTool");
        Assert.Contains("elevated-процесс", orphanEntry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, orphanEntry.FreedBytes);
    }

    [Fact]
    public async Task Execute_UserPathDeletedInUserContext_JournalKeepsBasis()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var updaterPath = root.Combine("LocalAppData", "lm-studio-updater");
        root.CreateFile("LocalAppData\\lm-studio-updater\\Update.exe", 100);

        var plan = new LeftoverPlanService(Scanner(environment), new ExclusionsStore(root.Combine("exclusions")))
            .BuildPlan([]);
        var updater = Assert.Single(plan.Items, i => i.DisplayName == "lm-studio-updater");
        updater.IsEnabled = true;

        var report = await CleanService(environment).CleanAsync(plan.EnabledItems);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(LeftoverCleanOutcome.DirectDeleted, entry.Outcome);
        Assert.Equal(100, entry.FreedBytes);
        Assert.Equal(updater.ReasonText, entry.Basis);
        Assert.False(Directory.Exists(updaterPath));
        Assert.Contains("Основание:", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, report.DeletedCount);
    }

    [Fact]
    public async Task Execute_AdminPathsGoThroughElevatedScenario_SingleLift_UserPathNotElevated()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var adminPath = root.Combine("ProgramFiles", "OrphanTool");
        var updaterPath = root.Combine("LocalAppData", "qwen-updater");
        root.CreateFile("ProgramFiles\\OrphanTool\\tool.bin", 10);
        root.CreateFile("LocalAppData\\qwen-updater\\Setup.exe", 80);

        var plan = new LeftoverPlanService(Scanner(environment), new ExclusionsStore(root.Combine("exclusions")))
            .BuildPlan([]);
        var orphan = Assert.Single(plan.Items, i => i.DisplayName == "OrphanTool");
        var updater = Assert.Single(plan.Items, i => i.DisplayName == "qwen-updater");
        orphan.IsEnabled = true;
        updater.IsEnabled = true;

        var elevated = new RecordingElevatedRunner();
        var report = await CleanService(environment, elevatedRunner: elevated).CleanAsync(
            plan.EnabledItems,
            new LeftoverCleanOptions { ConfirmDangerous = true });

        var elevatedScenario = Assert.Single(elevated.Scenarios);
        var step = Assert.Single(elevatedScenario.Steps);
        Assert.Equal(ElevatedStepKind.DeletePath, step.Kind);
        Assert.Equal(adminPath, step.Path);
        Assert.Equal(CleanupTarget.Directory, step.Target);
        Assert.Equal(adminPath, step.Id);
        Assert.DoesNotContain(elevatedScenario.Steps, s => s.Path == updaterPath);

        var orphanEntry = Assert.Single(report.Entries, e => e.Item.DisplayName == "OrphanTool");
        Assert.Equal(LeftoverCleanOutcome.ElevatedDeleted, orphanEntry.Outcome);
        Assert.Equal(orphan.ReasonText, orphanEntry.Basis);

        var updaterEntry = Assert.Single(report.Entries, e => e.Item.DisplayName == "qwen-updater");
        Assert.Equal(LeftoverCleanOutcome.DirectDeleted, updaterEntry.Outcome);
        Assert.False(Directory.Exists(updaterPath));
        // Каталог Program Files «удалён» поддельным elevated-исполнителем — записи журнала хватает для проверки сценария.
    }

    [Fact]
    public void ElevatedScenarioBuilder_FormsDeletePathStep_ForProgramFilesCandidate()
    {
        using var root = new TempRoot();
        var adminPath = root.Combine("ProgramFiles", "OrphanTool");
        var item = new LeftoverPlanItem
        {
            Key = adminPath,
            Path = adminPath,
            DisplayName = "OrphanTool",
            GroupName = "Осиротевшие папки в Program Files",
            Reason = LeftoverReason.OrphanProgramFiles,
            ReasonText = "Осиротевший каталог",
            RequiresAdmin = true,
            RequiresConfirmation = true
        };

        var scenario = LeftoverElevatedScenarioBuilder.Build([item]);

        var step = Assert.Single(scenario.Steps);
        Assert.Equal(adminPath, step.Id);
        Assert.Equal(ElevatedStepKind.DeletePath, step.Kind);
        Assert.Equal(adminPath, step.Path);
        Assert.Equal(CleanupTarget.Directory, step.Target);
    }

    [Fact]
    public async Task Execute_DangerousCandidateWithoutConfirmation_NotDeleted()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var adminPath = root.Combine("ProgramFiles", "OrphanTool");
        root.CreateFile("ProgramFiles\\OrphanTool\\tool.bin", 10);

        var plan = new LeftoverPlanService(Scanner(environment), new ExclusionsStore(root.Combine("exclusions")))
            .BuildPlan([]);
        var orphan = Assert.Single(plan.Items, i => i.DisplayName == "OrphanTool");
        Assert.True(orphan.RequiresConfirmation);
        orphan.IsEnabled = true;

        var elevated = new RecordingElevatedRunner();
        var report = await CleanService(environment, elevatedRunner: elevated).CleanAsync(plan.EnabledItems);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(LeftoverCleanOutcome.ConfirmationRequired, entry.Outcome);
        Assert.True(Directory.Exists(adminPath));
        Assert.Empty(elevated.Scenarios);
    }

    [Fact]
    public async Task Execute_RecheckFindsRunningProcess_ObjectSkipped_NotDeleted()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var updaterPath = root.Combine("LocalAppData", "lm-studio-updater");
        root.CreateFile("LocalAppData\\lm-studio-updater\\Update.exe", 100);

        // При сканировании процесс не запущен — кандидат есть в плане.
        var plan = new LeftoverPlanService(Scanner(environment), new ExclusionsStore(root.Combine("exclusions")))
            .BuildPlan([]);
        var updater = Assert.Single(plan.Items, i => i.DisplayName == "lm-studio-updater");
        updater.IsEnabled = true;

        // К моменту удаления (повторная проверка Фазы 3) из каталога запущен процесс.
        var liveProcess = new RunningProcessInfo(updaterPath + "\\Update.exe", "lm-studio-updater");
        var report = await CleanService(
                environment,
                processes: new FakeProcessInspector(liveProcess),
                elevatedRunner: new ElevatedScenarioRunner())
            .CleanAsync(plan.EnabledItems);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(LeftoverCleanOutcome.LiveObjectSkipped, entry.Outcome);
        Assert.True(Directory.Exists(updaterPath));
        Assert.Contains("запущен процесс", entry.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_RecheckFindsPathEntry_ObjectSkipped_NotDeleted()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var updaterPath = root.Combine("LocalAppData", "pt-updater");
        root.CreateFile("LocalAppData\\pt-updater\\Update.exe", 100);

        var plan = new LeftoverPlanService(Scanner(environment), new ExclusionsStore(root.Combine("exclusions")))
            .BuildPlan([]);
        var updater = Assert.Single(plan.Items, i => i.DisplayName == "pt-updater");
        updater.IsEnabled = true;

        var withPath = new FakeEnvironment(root, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = updaterPath
        });
        var report = await CleanService(withPath).CleanAsync(plan.EnabledItems);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(LeftoverCleanOutcome.LiveObjectSkipped, entry.Outcome);
        Assert.True(Directory.Exists(updaterPath));
        Assert.Contains("%PATH%", entry.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_RecheckFindsRegisteredService_ObjectSkipped()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var serviceDir = root.Combine("ProgramData", "SvcUpdater");
        root.CreateFile("ProgramData\\SvcUpdater\\svc.exe", 100);

        // Имя -updater в ProgramData предлагается как апдейтер.
        var plan = new LeftoverPlanService(Scanner(environment), new ExclusionsStore(root.Combine("exclusions")))
            .BuildPlan([]);
        var candidate = Assert.Single(plan.Items, i => i.DisplayName == "SvcUpdater");
        candidate.IsEnabled = true;

        var registered = new RegisteredServiceInfo("FakeSvc", serviceDir + "\\svc.exe");
        var report = await CleanService(
                environment,
                services: new FakeServiceInspector(registered),
                elevatedRunner: new ElevatedScenarioRunner())
            .CleanAsync(plan.EnabledItems, new LeftoverCleanOptions { ConfirmDangerous = true });

        var entry = Assert.Single(report.Entries);
        Assert.Equal(LeftoverCleanOutcome.LiveObjectSkipped, entry.Outcome);
        Assert.True(Directory.Exists(serviceDir));
        Assert.Contains("службы", entry.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_RealElevatedRunner_DeletesProgramFilesCandidate_WithBasisInJournal()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var adminPath = root.Combine("ProgramFiles", "OrphanTool");
        root.CreateFile("ProgramFiles\\OrphanTool\\tool.bin", 10);
        root.CreateFile("ProgramFiles\\OrphanTool\\sub\\deep.bin", 20);

        var plan = new LeftoverPlanService(Scanner(environment), new ExclusionsStore(root.Combine("exclusions")))
            .BuildPlan([]);
        var orphan = Assert.Single(plan.Items, i => i.DisplayName == "OrphanTool");
        orphan.IsEnabled = true;

        var report = await CleanService(
                environment,
                elevatedRunner: new ElevatedScenarioRunner())
            .CleanAsync(plan.EnabledItems, new LeftoverCleanOptions { ConfirmDangerous = true });

        var entry = Assert.Single(report.Entries);
        Assert.Equal(LeftoverCleanOutcome.ElevatedDeleted, entry.Outcome);
        Assert.Equal(30, entry.FreedBytes);
        Assert.Equal(orphan.ReasonText, entry.Basis);
        Assert.False(Directory.Exists(adminPath));
        Assert.Contains("Основание:", entry.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_AlreadyMissingPath_IsIdempotentSuccess_WithBasis()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var updaterPath = root.Combine("LocalAppData", "kimi-desktop_updater");
        root.CreateFile("LocalAppData\\kimi-desktop_updater\\update.exe", 100);

        var plan = new LeftoverPlanService(Scanner(environment), new ExclusionsStore(root.Combine("exclusions")))
            .BuildPlan([]);
        var updater = Assert.Single(plan.Items, i => i.DisplayName == "kimi-desktop_updater");
        updater.IsEnabled = true;

        Directory.Delete(updaterPath, recursive: true);

        var report = await CleanService(environment).CleanAsync(plan.EnabledItems);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(LeftoverCleanOutcome.AlreadyAbsent, entry.Outcome);
        Assert.Contains("Основание:", entry.Note, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingElevatedRunner : IElevatedRunner
    {
        public IReadOnlyList<ElevatedScenario> Scenarios { get; } = new List<ElevatedScenario>();

        public Task<ElevatedJournal> RunAsync(
            ElevatedScenario scenario,
            CancellationToken cancellationToken = default)
        {
            ((List<ElevatedScenario>)Scenarios).Add(scenario);
            var results = scenario.Steps
                .Select(step => new ElevatedStepResult
                {
                    Id = step.Id,
                    Success = true,
                    FreedBytes = 0,
                    Note = "Каталог удалён."
                })
                .ToList();
            return Task.FromResult(new ElevatedJournal { Results = results });
        }
    }
}
