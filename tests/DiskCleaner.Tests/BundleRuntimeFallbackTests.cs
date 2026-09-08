using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

/// <summary>
/// Тесты обработки пакетных (bundle) установок на этапе исполнения (фаза 23, модуль 04, FR-4.6):
/// <c>msiexec /x</c> для родительской записи бандла возвращает 1605 («это не MSI») — тогда
/// в <c>%ProgramData%\Package Cache\{code}</c> ищется исполняемый файл мастера и запускается
/// с <c>/uninstall</c>. Для GUI-мастеров (<c>winsdksetup.exe</c>) ожидается завершение
/// пользователем в рамках таймаута. Если движка в Package Cache нет — код 1605 трактуется
/// как «не установлено» (идемпотентно, NFR).
/// </summary>
public class BundleRuntimeFallbackTests
{
    private const string ProductCode = "{33333333-3333-3333-3333-333333333333}";

    private static CommandResult Exit(int code) => new(code, string.Empty, false);

    private static ElevatedStep MsiexecStep(string? bundleProductCode = ProductCode) => new()
    {
        Id = "bundle-step",
        Kind = ElevatedStepKind.RunProcess,
        FileName = "msiexec.exe",
        Arguments = $"/x {ProductCode} /qn /norestart",
        TimeoutSec = 60,
        ExitCodes = ExitCodePolicy.Msiexec,
        BundleProductCode = bundleProductCode
    };

    private static string CacheDir(TempRoot root) =>
        Path.Combine(root.ProgramData, "Package Cache", ProductCode.Trim('{', '}'));

    [Fact]
    public async Task Elevated_MsiexecExit1605_WithEngineInPackageCache_RunsEngineUninstall()
    {
        using var root = new TempRoot();
        var engine = Path.Combine(CacheDir(root), "winsdksetup.exe");
        root.CreateFile(
            $"ProgramData\\Package Cache\\{ProductCode.Trim('{', '}')}\\winsdksetup.exe",
            100);
        var runner = new SequenceCommandRunner(Exit(1605), Exit(0));
        var elevated = new ElevatedScenarioRunner(
            commandRunner: runner,
            packageCacheRoot: root.ProgramData);

        var journal = await elevated.RunAsync(new ElevatedScenario { Steps = [MsiexecStep()] });

        var result = Assert.Single(journal.Results);
        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("bundle", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, runner.Invocations.Count);

        var engineCall = runner.Invocations[1];
        Assert.Equal(engine, engineCall.FileName);
        Assert.Contains("/uninstall", engineCall.Arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Elevated_WinsdksetupEngine_NotePointsToGuiMaster()
    {
        using var root = new TempRoot();
        root.CreateFile(
            $"ProgramData\\Package Cache\\{ProductCode.Trim('{', '}')}\\winsdksetup.exe",
            100);
        var runner = new SequenceCommandRunner(Exit(1605), Exit(0));
        var elevated = new ElevatedScenarioRunner(
            commandRunner: runner,
            packageCacheRoot: root.ProgramData);

        var journal = await elevated.RunAsync(new ElevatedScenario { Steps = [MsiexecStep()] });

        var result = Assert.Single(journal.Results);
        Assert.True(result.Success);
        Assert.Contains("winsdksetup", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("мастер", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Elevated_MsiexecExit1605_WithoutEngine_IsIdempotentNotInstalled()
    {
        using var root = new TempRoot();
        var runner = new SequenceCommandRunner(Exit(1605));
        var elevated = new ElevatedScenarioRunner(
            commandRunner: runner,
            packageCacheRoot: root.ProgramData);

        var journal = await elevated.RunAsync(new ElevatedScenario { Steps = [MsiexecStep()] });

        var result = Assert.Single(journal.Results);
        Assert.True(result.Success);
        Assert.Equal(1605, result.ExitCode);
        Assert.Contains("не установлен", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task Elevated_MsiexecSuccess_EngineNotInvoked()
    {
        using var root = new TempRoot();
        root.CreateFile(
            $"ProgramData\\Package Cache\\{ProductCode.Trim('{', '}')}\\winsdksetup.exe",
            100);
        var runner = new SequenceCommandRunner(Exit(0));
        var elevated = new ElevatedScenarioRunner(
            commandRunner: runner,
            packageCacheRoot: root.ProgramData);

        var journal = await elevated.RunAsync(new ElevatedScenario { Steps = [MsiexecStep()] });

        var result = Assert.Single(journal.Results);
        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task Elevated_EngineTimeout_ReportsTimedOutWithInstruction()
    {
        using var root = new TempRoot();
        root.CreateFile(
            $"ProgramData\\Package Cache\\{ProductCode.Trim('{', '}')}\\winsdksetup.exe",
            100);
        var runner = new SequenceCommandRunner(Exit(1605), new CommandResult(0, string.Empty, TimedOut: true));
        var elevated = new ElevatedScenarioRunner(
            commandRunner: runner,
            packageCacheRoot: root.ProgramData);

        var journal = await elevated.RunAsync(new ElevatedScenario { Steps = [MsiexecStep()] });

        var result = Assert.Single(journal.Results);
        Assert.False(result.Success);
        Assert.Null(result.ExitCode);
        Assert.Contains("завершите его", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ElevatedScenarioBuilder_SetsBundleProductCode_ForMsiexecStepsOnly()
    {
        var bundle = MsiItem("{11111111-1111-1111-1111-111111111111}");
        var generic = new UninstallExecutionItem(
            new InstalledApp
            {
                ProductCode = "{22222222-2222-2222-2222-222222222222}",
                ScopeKey = nameof(InstalledAppScope.LocalMachine64),
                DisplayName = "Generic"
            },
            new UninstallCommand(UninstallerKind.Generic, "setup.exe", "/uninstall", Silent: false));

        var scenario = UninstallElevatedScenarioBuilder.Build([bundle, generic], timeoutSec: 30);

        var msiStep = Assert.Single(scenario.Steps, s => s.ExitCodes == ExitCodePolicy.Msiexec);
        Assert.Equal("{11111111-1111-1111-1111-111111111111}", msiStep.BundleProductCode);
        var genericStep = Assert.Single(scenario.Steps, s => s.ExitCodes == ExitCodePolicy.Generic);
        Assert.Null(genericStep.BundleProductCode);
    }

    [Fact]
    public async Task UserContext_MsiexecExit1605_WithEngineInPackageCache_RunsEngine()
    {
        using var root = new TempRoot();
        var engine = Path.Combine(CacheDir(root), "winsdksetup.exe");
        root.CreateFile(
            $"ProgramData\\Package Cache\\{ProductCode.Trim('{', '}')}\\winsdksetup.exe",
            100);
        var runner = new SequenceCommandRunner(Exit(1605), Exit(0));
        var service = new UninstallExecutionService(
            elevatedRunner: new NeverElevatedRunner(),
            commandRunner: runner,
            programDataRoot: root.ProgramData);

        var app = new InstalledApp
        {
            ProductCode = ProductCode,
            ScopeKey = nameof(InstalledAppScope.CurrentUser),
            DisplayName = "Windows Software Development Kit",
            UninstallString = $"MsiExec.exe /X{ProductCode}"
        };

        var report = await service.UninstallAsync([service.BuildItem(app)]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(UninstallExecutionOutcome.Uninstalled, entry.Outcome);
        Assert.Equal(0, entry.ExitCode);
        Assert.True(entry.IsOk);
        Assert.Contains("bundle", entry.Note, StringComparison.OrdinalIgnoreCase);

        var engineCall = runner.Invocations[1];
        Assert.Equal(engine, engineCall.FileName);
        Assert.Contains("/uninstall", engineCall.Arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UserContext_MsiexecExit1605_WithoutEngine_IsNotInstalled()
    {
        using var root = new TempRoot();
        var runner = new SequenceCommandRunner(Exit(1605));
        var service = new UninstallExecutionService(
            elevatedRunner: new NeverElevatedRunner(),
            commandRunner: runner,
            programDataRoot: root.ProgramData);

        var app = new InstalledApp
        {
            ProductCode = ProductCode,
            ScopeKey = nameof(InstalledAppScope.CurrentUser),
            DisplayName = "Windows Software Development Kit",
            UninstallString = $"MsiExec.exe /X{ProductCode}"
        };

        var report = await service.UninstallAsync([service.BuildItem(app)]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(UninstallExecutionOutcome.NotInstalled, entry.Outcome);
        Assert.True(entry.IsOk);
        Assert.Single(runner.Invocations);
    }

    private static UninstallExecutionItem MsiItem(string productCode) => new(
        new InstalledApp
        {
            ProductCode = productCode,
            ScopeKey = nameof(InstalledAppScope.LocalMachine64),
            DisplayName = "Windows SDK bundle",
            UninstallString = $"MsiExec.exe /X{productCode}"
        },
        new UninstallCommand(UninstallerKind.Msi, "msiexec.exe", $"/x {productCode} /qn /norestart", Silent: true));

    private sealed class NeverElevatedRunner : IElevatedRunner
    {
        public Task<ElevatedJournal> RunAsync(ElevatedScenario scenario, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Пользовательский шаг не должен исполняться через elevated-процесс.");
    }
}

internal sealed class SequenceCommandRunner : ICommandRunner
{
    private readonly Queue<CommandResult> _results;

    public SequenceCommandRunner(params CommandResult[] results)
    {
        _results = new Queue<CommandResult>(results);
    }

    public IReadOnlyList<CommandDefinition> Invocations { get; } = new List<CommandDefinition>();

    public Task<CommandResult> RunAsync(CommandDefinition command, CancellationToken cancellationToken = default)
    {
        ((List<CommandDefinition>)Invocations).Add(command);
        return Task.FromResult(_results.Count == 0 ? new CommandResult(0, string.Empty, false) : _results.Dequeue());
    }
}
