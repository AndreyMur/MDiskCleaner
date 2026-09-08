using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

/// <summary>
/// Тесты сквозного удаления (фаза 21, модуль 04, FR-4.5/4.15/4.16, NFR PRD 04):
/// маршрутизация HKCU без повышения, одна UAC-проверка на пачку для LocalMachine,
/// обработка exit-кодов (0/3010/1605/1612), журнал с кодами и идемпотентность повторного
/// запуска на уже удалённых объектах. Фиктивный деинсталлятор — реальный процесс
/// <c>cmd.exe /c exit N</c>, а elevated-сценарий выполняется in-proc
/// (<see cref="ElevatedScenarioRunner"/>) на временных объектах без UAC.
/// </summary>
public class UninstallExecutionTests
{
    private static InstalledApp App(
        string displayName,
        InstalledAppScope scope,
        string? uninstall = null,
        string? quiet = null,
        string? location = null) => new()
    {
        ProductCode = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}",
        ScopeKey = scope.ToString(),
        DisplayName = displayName,
        UninstallString = uninstall,
        QuietUninstallString = quiet,
        InstallLocation = location
    };

    /// <summary>«MSI»-шаг, исполняемый фиктивным деинсталлятором <c>cmd.exe /c exit N</c>.</summary>
    private static UninstallExecutionItem MsiItem(InstalledAppScope scope, int exitCode, string name = "Пример MSI") =>
        new(App(name, scope), new UninstallCommand(
            UninstallerKind.Msi,
            "cmd.exe",
            $"/c exit {exitCode}",
            Silent: true));

    private static UninstallExecutionService UserService(
        IElevatedRunner? elevated = null,
        ICommandRunner? commandRunner = null) =>
        new(
            elevatedRunner: elevated ?? new NeverElevatedRunner(),
            commandRunner: commandRunner ?? new ProcessCommandRunner());

    [Fact]
    public async Task MsiexecInUserContext_ExitZero_Uninstalled_AndCodeRecorded()
    {
        var report = await UserService().UninstallAsync([MsiItem(InstalledAppScope.CurrentUser, 0)]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(UninstallExecutionOutcome.Uninstalled, entry.Outcome);
        Assert.Equal(0, entry.ExitCode);
        Assert.True(entry.IsOk);
        Assert.Equal(1, report.OkCount);
        Assert.Equal(0, report.ErrorCount);
    }

    [Fact]
    public async Task MsiexecInUserContext_Exit3010_RebootRequired()
    {
        var report = await UserService().UninstallAsync([MsiItem(InstalledAppScope.CurrentUser, 3010)]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(UninstallExecutionOutcome.RebootRequired, entry.Outcome);
        Assert.Equal(3010, entry.ExitCode);
        Assert.True(entry.IsOk);
    }

    [Theory]
    [InlineData(1605)]
    [InlineData(1612)]
    public async Task MsiexecNotInstalled_NotAnError_OnRepeatedRun(int exitCode)
    {
        var service = UserService();
        var item = MsiItem(InstalledAppScope.CurrentUser, exitCode);

        var first = await service.UninstallAsync([item]);
        var second = await service.UninstallAsync([item]);

        foreach (var report in new[] { first, second })
        {
            var entry = Assert.Single(report.Entries);
            Assert.Equal(UninstallExecutionOutcome.NotInstalled, entry.Outcome);
            Assert.Equal(exitCode, entry.ExitCode);
            Assert.True(entry.IsOk);
            Assert.Contains("не установлен", entry.Note, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("идемпотентно", entry.Note, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task GenericUninstaller_ExitNonZero_Failed()
    {
        var item = new UninstallExecutionItem(
            App("Generic", InstalledAppScope.CurrentUser, uninstall: "\"C:\\Windows\\System32\\cmd.exe\" /c exit 7"),
            new UninstallCommand(UninstallerKind.Generic, "cmd.exe", "/c exit 7", Silent: false));

        var report = await UserService().UninstallAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(UninstallExecutionOutcome.Failed, entry.Outcome);
        Assert.Equal(7, entry.ExitCode);
        Assert.False(entry.IsOk);
    }

    [Fact]
    public async Task UserUninstaller_Timeout_ReportsTimedOut()
    {
        var item = new UninstallExecutionItem(
            App("Slow", InstalledAppScope.CurrentUser),
            new UninstallCommand(UninstallerKind.Generic, "cmd.exe", "/c ping -n 60 127.0.0.1 >nul", Silent: false));

        var report = await UserService().UninstallAsync(
            [item],
            new UninstallExecutionOptions { CommandTimeoutSec = 1 });

        var entry = Assert.Single(report.Entries);
        Assert.Equal(UninstallExecutionOutcome.TimedOut, entry.Outcome);
        Assert.Null(entry.ExitCode);
        Assert.False(entry.IsOk);
    }

    [Fact]
    public async Task UserContext_UninstallerFileMissing_AlreadyUninstalled_WithoutInvocation()
    {
        using var root = new TempRoot();
        var missing = Path.Combine(root.Path, "gone", "unins000.exe");
        var runner = new FakeCommandRunner().Result(new CommandResult(0, string.Empty, false));
        var item = new UninstallExecutionItem(
            App("Gone", InstalledAppScope.CurrentUser),
            new UninstallCommand(UninstallerKind.Inno, missing, "/VERYSILENT", Silent: true));

        var report = await UserService(commandRunner: runner).UninstallAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(UninstallExecutionOutcome.AlreadyUninstalled, entry.Outcome);
        Assert.Null(entry.ExitCode);
        Assert.True(entry.IsOk);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task HkcuApp_RunsInUserContext_NotThroughElevatedRunner()
    {
        var elevated = new ScriptedElevatedRunner(0);
        var report = await UserService(elevated).UninstallAsync([MsiItem(InstalledAppScope.CurrentUser, 0)]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(UninstallExecutionOutcome.Uninstalled, entry.Outcome);
        Assert.Equal(0, elevated.Calls);
        Assert.Null(elevated.Scenario);
    }

    [Fact]
    public async Task LocalMachineApps_BatchedIntoOneElevatedScenario_SingleUac()
    {
        var elevated = new ScriptedElevatedRunner(0, 3010);

        var report = await UserService(elevated).UninstallAsync(
        [
            MsiItem(InstalledAppScope.LocalMachine64, 0, "first"),
            MsiItem(InstalledAppScope.LocalMachine32, 3010, "second")
        ]);

        Assert.Equal(1, elevated.Calls);
        Assert.NotNull(elevated.Scenario);
        Assert.Equal(2, elevated.Scenario.Steps.Count);
        Assert.All(elevated.Scenario.Steps, s =>
        {
            Assert.Equal(ElevatedStepKind.RunProcess, s.Kind);
            Assert.Equal(ExitCodePolicy.Msiexec, s.ExitCodes);
            Assert.Equal("cmd.exe", s.FileName);
        });

        var byName = report.Entries.ToDictionary(e => e.DisplayName);
        Assert.Equal(UninstallExecutionOutcome.Uninstalled, byName["first"].Outcome);
        Assert.Equal(0, byName["first"].ExitCode);
        Assert.Equal(UninstallExecutionOutcome.RebootRequired, byName["second"].Outcome);
        Assert.Equal(3010, byName["second"].ExitCode);
    }

    [Fact]
    public async Task Elevated_Declined_MarksEveryItem_WithoutExecuting()
    {
        var report = await UserService(new ScriptedElevatedRunner(decline: true)).UninstallAsync(
        [
            MsiItem(InstalledAppScope.LocalMachine64, 0),
            MsiItem(InstalledAppScope.LocalMachine32, 0)
        ]);

        Assert.Equal(2, report.Entries.Count);
        Assert.All(report.Entries, e =>
        {
            Assert.Equal(UninstallExecutionOutcome.ElevationDeclined, e.Outcome);
            Assert.Null(e.ExitCode);
            Assert.Contains("UAC", e.Note, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(2, report.DeclinedCount);
    }

    [Fact]
    public async Task Elevated_NotInstalled_IsOk_OnRepeatedPlanRun()
    {
        var service = UserService(new ScriptedElevatedRunner(1605));
        var item = MsiItem(InstalledAppScope.LocalMachine64, 1605);

        var first = await service.UninstallAsync([item]);
        var second = await service.UninstallAsync([item]);

        foreach (var report in new[] { first, second })
        {
            var entry = Assert.Single(report.Entries);
            Assert.Equal(UninstallExecutionOutcome.NotInstalled, entry.Outcome);
            Assert.Equal(1605, entry.ExitCode);
            Assert.True(entry.IsOk);
            Assert.Equal(0, report.ErrorCount);
        }
    }

    [Fact]
    public async Task Integration_ElevatedScenario_ExecutedInProc_OnTempObjects()
    {
        var elevated = new ElevatedScenarioRunner();
        var items = new[]
        {
            MsiItem(InstalledAppScope.LocalMachine64, 0, "ok"),
            MsiItem(InstalledAppScope.LocalMachine32, 1605, "gone")
        };

        var report = await UserService(elevated).UninstallAsync(items);

        var byName = report.Entries.ToDictionary(e => e.DisplayName);
        Assert.Equal(UninstallExecutionOutcome.Uninstalled, byName["ok"].Outcome);
        Assert.Equal(0, byName["ok"].ExitCode);
        Assert.Equal(UninstallExecutionOutcome.NotInstalled, byName["gone"].Outcome);
        Assert.Equal(1605, byName["gone"].ExitCode);
        Assert.Equal(2, report.OkCount);
    }

    [Fact]
    public async Task BuildItem_NoUninstallString_ProducesNoUninstallerEntry()
    {
        var service = UserService();
        var report = await service.UninstallAsync(
        [
            new UninstallExecutionItem(
                App("Без деинсталлятора", InstalledAppScope.CurrentUser, uninstall: null, quiet: null),
                null)
        ]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(UninstallExecutionOutcome.NoUninstaller, entry.Outcome);
        Assert.False(entry.IsOk);
        Assert.Equal(1, report.ErrorCount);
    }

    [Fact]
    public async Task FromInstalledApps_GenericQuietString_RunsCommand()
    {
        var service = UserService();
        var app = App(
            "Из реестра",
            InstalledAppScope.CurrentUser,
            uninstall: null,
            quiet: "\"C:\\Windows\\System32\\cmd.exe\" /c exit 0");

        var report = await service.UninstallAsync([app]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(UninstallExecutionOutcome.Uninstalled, entry.Outcome);
        Assert.Equal(0, entry.ExitCode);
    }

    private sealed class NeverElevatedRunner : IElevatedRunner
    {
        public Task<ElevatedJournal> RunAsync(ElevatedScenario scenario, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Пользовательский (HKCU) шаг не должен исполняться через elevated-процесс.");
    }

    private sealed class ScriptedElevatedRunner : IElevatedRunner
    {
        private readonly IReadOnlyList<int> _exitCodes;
        private readonly bool _decline;

        public ScriptedElevatedRunner(params int[] exitCodes)
        {
            _exitCodes = exitCodes;
        }

        public ScriptedElevatedRunner(bool decline)
        {
            _decline = decline;
        }

        public int Calls { get; private set; }

        public ElevatedScenario? Scenario { get; private set; }

        public Task<ElevatedJournal> RunAsync(ElevatedScenario scenario, CancellationToken cancellationToken = default)
        {
            Calls++;
            Scenario = scenario;

            if (_decline)
            {
                throw new ElevationDeclinedException("Повышение прав отклонено пользователем.", new InvalidOperationException());
            }

            var results = new List<ElevatedStepResult>(scenario.Steps.Count);
            for (var index = 0; index < scenario.Steps.Count; index++)
            {
                var step = scenario.Steps[index];
                var code = _exitCodes.Count == 0 ? 0 : _exitCodes[Math.Min(index, _exitCodes.Count - 1)];
                var meaning = step.ExitCodes == ExitCodePolicy.Msiexec
                    ? UninstallExitCodes.Classify(code)
                    : code == 0 ? ProcessExitMeaning.Success : ProcessExitMeaning.Failed;

                results.Add(new ElevatedStepResult
                {
                    Id = step.Id,
                    Success = meaning is ProcessExitMeaning.Success or ProcessExitMeaning.RebootRequired or ProcessExitMeaning.NotInstalled,
                    ExitCode = code,
                    RebootRequired = meaning == ProcessExitMeaning.RebootRequired,
                    Note = meaning == ProcessExitMeaning.NotInstalled
                        ? $"Продукт уже не установлен (код {code})."
                        : meaning == ProcessExitMeaning.RebootRequired
                            ? "Требуется перезагрузка (код 3010)."
                            : null
                });
            }

            return Task.FromResult(new ElevatedJournal
            {
                StartedAt = DateTime.UtcNow,
                FinishedAt = DateTime.UtcNow,
                Results = results
            });
        }
    }
}
