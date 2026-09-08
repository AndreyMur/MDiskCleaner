using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Scanning;

namespace DiskCleaner.Tests;

/// <summary>
/// Фаза 28 модуля 05 — «UAC-группировка и повышенный процесс»
/// (FR-5.10 планировщик делит шаги на user/admin и формирует JSON-сценарий; FR-5.11 elevated
/// исполняет только сценарий и пишет журнал; FR-5.12 детект кода 5 / Access Denied → шаг
/// переносится в «требует админа», остальные продолжаются).
/// Unit: группировка шагов и сериализация сценария; integration: elevated-сценарий на
/// временных объектах (in-proc; на эталонной машине — real exe), обработка Access Denied.
/// </summary>
public class SystemAndSafetyPhase28Tests
{
    [Fact]
    public async Task PlanExecutor_MixedPlan_AdminStepsGoIntoSingleElevatedScenario_UserStepsStayLocal()
    {
        using var root = new TempRoot();
        var userTemp = root.Combine("user-temp");
        root.CreateFile("user-temp\\u.bin", 500);
        var windowsTemp = root.Combine("Win", "Temp");
        root.CreateFile("Win\\Temp\\w.bin", 700);
        var protectedLike = root.Combine("Admin", "Orphan");
        root.CreateFile("Admin\\Orphan\\o.bin", 300);

        var elevated = new RecordingElevatedRunner();
        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(),
            elevatedRunner: elevated);

        var report = await executor.CleanAsync(
        [
            ContentsOnlyTemp(userTemp, requiresAdmin: false),
            ContentsOnlyTemp(windowsTemp, requiresAdmin: true),
            DirectoryClean(protectedLike, requiresAdmin: true)
        ]);

        Assert.Equal(3, report.Entries.Count);
        Assert.Equal(0, report.FailedItems);
        Assert.All(report.Entries, e => Assert.Equal(CleanOutcome.DirectDeleted, e.Outcome));

        // Один вызов elevated-исполнителя (одна UAC-пачка) на всю админ-часть плана (FR-5.10).
        Assert.Equal(1, elevated.Calls);
        Assert.NotNull(elevated.Scenario);
        Assert.Equal(2, elevated.Scenario!.Steps.Count);
        Assert.DoesNotContain(userTemp, elevated.Scenario.Steps.Select(s => s.Path ?? string.Empty));

        Assert.True(Directory.Exists(userTemp), "%TEMP% пользователя чистится локально и не удаляется.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(userTemp));
        Assert.False(Directory.Exists(protectedLike));
    }

    [Fact]
    public async Task ElevatedScenarioAndJournal_JsonRoundTrip_PreservesStepsAndResults()
    {
        using var root = new TempRoot();
        var scenarioPath = Path.Combine(root.Path, "scenario.json");
        var journalPath = Path.Combine(root.Path, "journal.json");

        var scenario = new ElevatedScenario
        {
            Steps =
            [
                new ElevatedStep
                {
                    Id = "windows-temp",
                    Kind = ElevatedStepKind.DeletePath,
                    Path = root.Combine("Win", "Temp"),
                    DeleteContentsOnly = true
                },
                new ElevatedStep
                {
                    Id = "msi",
                    Kind = ElevatedStepKind.RunProcess,
                    FileName = "msiexec.exe",
                    Arguments = "/x {00000000-0000-0000-0000-000000000000} /qn /norestart",
                    TimeoutSec = 600,
                    ExitCodes = ExitCodePolicy.Msiexec
                }
            ]
        };

        var journal = new ElevatedJournal
        {
            StartedAt = new DateTime(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc),
            FinishedAt = new DateTime(2026, 9, 8, 10, 0, 5, DateTimeKind.Utc),
            Results =
            [
                new ElevatedStepResult
                {
                    Id = "windows-temp",
                    Success = true,
                    FreedBytes = 1024,
                    Note = "Содержимое очищено."
                },
                new ElevatedStepResult
                {
                    Id = "msi",
                    Success = true,
                    ExitCode = 0,
                    Note = "Деинсталляция завершена."
                }
            ]
        };

        ElevatedJson.WriteScenario(scenarioPath, scenario);
        ElevatedJson.WriteJournal(journalPath, journal);

        var readScenario = ElevatedJson.ReadScenario(scenarioPath);
        Assert.Equal(2, readScenario.Steps.Count);
        Assert.Equal(ElevatedStepKind.DeletePath, readScenario.Steps[0].Kind);
        Assert.True(readScenario.Steps[0].DeleteContentsOnly);
        Assert.Equal("msi", readScenario.Steps[1].Id);
        Assert.Equal(600, readScenario.Steps[1].TimeoutSec);
        Assert.Equal(ExitCodePolicy.Msiexec, readScenario.Steps[1].ExitCodes);

        var readJournal = ElevatedJson.ReadJournal(journalPath);
        Assert.Equal(2, readJournal.Results.Count);
        Assert.Equal(1024, readJournal.Results[0].FreedBytes);
        Assert.Equal(0, readJournal.Results[1].ExitCode);
        Assert.True(readJournal.AllSucceeded);
    }

    [Fact]
    public async Task ElevatedScenarioRunner_MultipleSteps_ExecutesAllInOrder_AndWritesJournalPerStep()
    {
        using var root = new TempRoot();
        var dirA = root.Combine("scenario-a");
        root.CreateFile("scenario-a\\a.bin", 400);
        var dirB = root.Combine("scenario-b");
        root.CreateFile("scenario-b\\b.bin", 600);

        var runner = new ElevatedScenarioRunner();
        var journal = await runner.RunAsync(new ElevatedScenario
        {
            Steps =
            [
                new ElevatedStep
                {
                    Id = "step-a",
                    Kind = ElevatedStepKind.DeletePath,
                    Path = dirA,
                    Target = CleanupTarget.Directory
                },
                new ElevatedStep
                {
                    Id = "step-b",
                    Kind = ElevatedStepKind.DeletePath,
                    Path = dirB,
                    Target = CleanupTarget.Directory
                }
            ]
        });

        Assert.False(Directory.Exists(dirA));
        Assert.False(Directory.Exists(dirB));
        Assert.Equal(2, journal.Results.Count);
        Assert.Equal(new[] { "step-a", "step-b" }, journal.Results.Select(r => r.Id));
        Assert.All(journal.Results, r => Assert.True(r.Success));
        Assert.Equal(1000, journal.Results.Sum(r => r.FreedBytes));
    }

    [Fact]
    public async Task DirectoryDeleter_AccessDeniedRoot_SetsAccessDeniedFlag_AndNothingDeleted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TempRoot();
        var locked = root.Combine("admin-protected");
        Directory.CreateDirectory(locked);
        File.WriteAllBytes(Path.Combine(locked, "hidden.bin"), new byte[700]);

        var rule = DenyReadForCurrentUser(locked);
        try
        {
            var outcome = await new DirectoryDeleter().DeletePathAsync(
                locked,
                CleanupTarget.Directory,
                deleteContentsOnly: false);

            Assert.True(outcome.AccessDenied);
            Assert.False(outcome.FullyDeleted);
            Assert.Equal(0, outcome.FreedBytes);
            Assert.True(Directory.Exists(locked), "Каталог не должен быть удалён при Access Denied.");
            Assert.True(
                outcome.Errors.Any(e => e.Contains(locked, StringComparison.OrdinalIgnoreCase)),
                "Ожидалась ошибка с путём. Errors: " + string.Join(" | ", outcome.Errors));
        }
        finally
        {
            RestoreAccess(locked, rule);
        }
    }

    [Fact]
    public async Task CacheCleaner_AccessDeniedRoot_StepBecomesRequiresAdmin_AndPlanContinues()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TempRoot();
        var locked = root.Combine("admin-protected");
        Directory.CreateDirectory(locked);
        File.WriteAllBytes(Path.Combine(locked, "hidden.bin"), new byte[700]);
        var healthy = root.Combine("healthy");
        root.CreateFile("healthy\\ok.bin", 300);

        var rule = DenyReadForCurrentUser(locked);
        try
        {
            var cleaner = new CacheCleanerService();
            var report = await cleaner.CleanAsync(
            [
                DirectoryClean(locked, requiresAdmin: false),
                DirectoryClean(healthy, requiresAdmin: false)
            ]);

            // FR-5.12: шаг с Access Denied → «требует админа», но не ошибка плана;
            // соседний доступный шаг продолжается и выполняется.
            Assert.Equal(1, report.RequiresAdminItems);
            Assert.Equal(0, report.FailedItems);

            var requiresAdmin = Assert.Single(report.Entries, e => e.Outcome == CleanOutcome.RequiresAdmin);
            Assert.Equal(Path.GetFullPath(locked), requiresAdmin.Item.Path);
            Assert.Contains("требует админа", requiresAdmin.Note, StringComparison.OrdinalIgnoreCase);

            var cleaned = Assert.Single(report.Entries, e => e.Outcome == CleanOutcome.DirectDeleted);
            Assert.Equal(Path.GetFullPath(healthy), cleaned.Item.Path);
            Assert.False(Directory.Exists(healthy));
        }
        finally
        {
            RestoreAccess(locked, rule);
        }
    }

    [Fact]
    public async Task PlanExecutor_AccessDeniedStep_IsMovedToAdminBatch_AndOtherUserStepsContinue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TempRoot();
        var locked = root.Combine("admin-protected");
        Directory.CreateDirectory(locked);
        File.WriteAllBytes(Path.Combine(locked, "hidden.bin"), new byte[700]);
        var healthy = root.Combine("healthy");
        root.CreateFile("healthy\\ok.bin", 300);

        var rule = DenyReadForCurrentUser(locked);
        try
        {
            var elevated = new RecordingElevatedRunner();
            var executor = new PlanExecutor(
                localCleaner: new CacheCleanerService(),
                elevatedRunner: elevated);

            var report = await executor.CleanAsync(
            [
                DirectoryClean(locked, requiresAdmin: false),
                DirectoryClean(healthy, requiresAdmin: false)
            ]);

            // Healthy-шаг выполнен локально; залоченный перенесён в админ-сценарий.
            var cleaned = Assert.Single(report.Entries, e => e.Outcome == CleanOutcome.DirectDeleted);
            Assert.Equal(Path.GetFullPath(healthy), cleaned.Item.Path);
            Assert.False(Directory.Exists(healthy));

            Assert.Equal(1, elevated.Calls);
            Assert.NotNull(elevated.Scenario);
            var step = Assert.Single(elevated.Scenario!.Steps);
            Assert.Equal(ElevatedStepKind.DeletePath, step.Kind);
            Assert.Equal(Path.GetFullPath(locked), step.Path);
        }
        finally
        {
            RestoreAccess(locked, rule);
        }
    }

    [Fact]
    public void Manifests_ElevatedRequiresAdministrator_MainAppAsInvoker()
    {
        var repoRoot = FindRepoRoot(new DirectoryInfo(AppContext.BaseDirectory));
        if (repoRoot is null)
        {
            return;
        }

        var elevatedManifest = Path.Combine(repoRoot.FullName, "src", "DiskCleaner.Elevated", "app.manifest");
        var guiManifest = Path.Combine(repoRoot.FullName, "src", "DiskCleaner.Gui", "app.manifest");

        Assert.True(File.Exists(elevatedManifest), $"Манифест elevated-процесса не найден: {elevatedManifest}");
        Assert.True(File.Exists(guiManifest), $"Манифест GUI не найден: {guiManifest}");

        // FR-5.10/5.11: основной процесс (asInvoker) прав не получает; права берёт только
        // отдельный elevated-процесс (requireAdministrator).
        Assert.Contains("requireAdministrator", File.ReadAllText(elevatedManifest));
        Assert.DoesNotContain("asInvoker", File.ReadAllText(elevatedManifest));
        Assert.Contains("asInvoker", File.ReadAllText(guiManifest));
    }

    private static DirectoryInfo? FindRepoRoot(DirectoryInfo start)
    {
        var current = start;
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "DiskCleaner.sln")))
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    private static CleanupItem ContentsOnlyTemp(string path, bool requiresAdmin = false)
    {
        var full = Path.GetFullPath(path);
        return new CleanupItem
        {
            Key = requiresAdmin ? "system:temp:windows" : "temp:" + full,
            Path = full,
            DisplayName = requiresAdmin ? "Системные временные файлы (Windows\\Temp)" : "Временные файлы пользователя (%TEMP%)",
            Category = CleanupCategory.Temp,
            Risk = CleanupRisk.Low,
            Target = CleanupTarget.Directory,
            DeleteContentsOnly = true,
            RequiresAdmin = requiresAdmin
        };
    }

    private static CleanupItem DirectoryClean(string path, bool requiresAdmin = false)
    {
        var full = Path.GetFullPath(path);
        return new CleanupItem
        {
            Key = "dir:" + full,
            Path = full,
            DisplayName = Path.GetFileName(full),
            Category = CleanupCategory.Cache,
            Risk = CleanupRisk.Low,
            Target = CleanupTarget.Directory,
            RequiresAdmin = requiresAdmin
        };
    }

    private static FileSystemAccessRule DenyReadForCurrentUser(string directory)
    {
        var directoryInfo = new DirectoryInfo(directory);
        var security = directoryInfo.GetAccessControl();
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Не удалось получить SID текущего пользователя.");
        var rule = new FileSystemAccessRule(
            currentUser,
            FileSystemRights.Read | FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Deny);
        security.AddAccessRule(rule);
        directoryInfo.SetAccessControl(security);
        return rule;
    }

    private static void RestoreAccess(string directory, FileSystemAccessRule rule)
    {
        var directoryInfo = new DirectoryInfo(directory);
        var security = directoryInfo.GetAccessControl();
        security.RemoveAccessRule(rule);
        directoryInfo.SetAccessControl(security);
    }

    private sealed class RecordingElevatedRunner : IElevatedRunner
    {
        private readonly IElevatedRunner _inner = new ElevatedScenarioRunner();

        public ElevatedScenario? Scenario { get; private set; }

        public int Calls { get; private set; }

        public async Task<ElevatedJournal> RunAsync(ElevatedScenario scenario, CancellationToken cancellationToken = default)
        {
            Calls++;
            Scenario = scenario;
            return await _inner.RunAsync(scenario, cancellationToken);
        }
    }
}
