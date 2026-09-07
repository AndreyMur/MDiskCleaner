using System.Diagnostics;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;

namespace DiskCleaner.Tests;

public class InUseSafetyTests
{
    private static CleanupItem Item(string path, bool requiresAdmin = false) => new()
    {
        Key = "inuse:" + path,
        Path = path,
        DisplayName = Path.GetFileName(path),
        Category = CleanupCategory.Cache,
        Risk = CleanupRisk.Low,
        Target = CleanupTarget.Directory,
        RequiresAdmin = requiresAdmin
    };

    [Fact]
    public async Task Clean_ItemInUseAtRunTime_IsSkipped_NotDeleted()
    {
        using var root = new TempRoot();
        var dir = root.Combine("InUseNow");
        root.CreateFile("InUseNow\\a.bin", 500);

        var fakeProcesses = new FakeProcessInspector(
            new RunningProcessInfo(Path.Combine(dir, "app.exe"), "app"));
        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(),
            elevatedRunner: new ElevatedScenarioRunner(),
            processInspector: fakeProcesses);

        var report = await executor.CleanAsync([Item(dir)]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.InUseSkipped, entry.Outcome);
        Assert.True(Directory.Exists(dir), "Объект, используемый процессом, не должен удаляться.");
    }

    [Fact]
    public async Task Clean_AdminItemInUse_IsNotSentToElevatedBatch()
    {
        using var root = new TempRoot();
        var dir = root.Combine("AdminInUse");
        root.CreateFile("AdminInUse\\a.bin", 200);

        var fakeProcesses = new FakeProcessInspector(
            new RunningProcessInfo(Path.Combine(dir, "code.exe"), "code"));
        var elevated = new RecordingElevatedRunner();
        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(),
            elevatedRunner: elevated,
            processInspector: fakeProcesses);

        var report = await executor.CleanAsync([Item(dir, requiresAdmin: true)]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.InUseSkipped, entry.Outcome);
        Assert.Null(elevated.InvokedScenario);
    }

    [Fact]
    public async Task Clean_RunningExecutableInsideTarget_IsDetected_AndSkipped()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TempRoot();
        var dir = root.Combine("running-app");
        var source = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!File.Exists(source))
        {
            return;
        }

        var copiedExe = Path.Combine(dir, "app.exe");
        File.Copy(source, copiedExe);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = copiedExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        try
        {
            process.Start();

            var executor = new PlanExecutor(
                localCleaner: new CacheCleanerService(),
                elevatedRunner: new ElevatedScenarioRunner());

            var report = await executor.CleanAsync([Item(dir)]);

            var entry = Assert.Single(report.Entries);
            Assert.Equal(CleanOutcome.InUseSkipped, entry.Outcome);
            Assert.True(File.Exists(copiedExe));
        }
        finally
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task Clean_VSCodeCacheWhileCodeRunning_IsSkipped_WithOwnerProcessReason()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.AppData, "Code", "Cache");
        root.CreateFile("AppData\\Code\\Cache\\a.bin", 700);

        var item = new CleanupItem
        {
            Key = "vscode-cache:" + dir,
            Path = dir,
            DisplayName = "VS Code Cache",
            Category = CleanupCategory.Cache,
            Risk = CleanupRisk.Low,
            Target = CleanupTarget.Directory,
            OwnerProcessNames = new[] { "Code", "Code - Insiders", "VSCodium" },
            AllowDirectDelete = true
        };

        var fakeProcesses = new FakeProcessInspector(
            new RunningProcessInfo(@"C:\Program Files\Microsoft VS Code\Code.exe", "Code"));
        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(),
            elevatedRunner: new ElevatedScenarioRunner(),
            processInspector: fakeProcesses);

        var report = await executor.CleanAsync([item]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.InUseSkipped, entry.Outcome);
        Assert.Equal(1, report.DeferredItems);
        Assert.Equal(0, report.FailedItems);
        Assert.True(Directory.Exists(dir));
        Assert.Contains("VS Code", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("отложен", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("закрытия VS Code", entry.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DryRun_VSCodeRunning_ShowsDeferralWarning_AndDeletesNothing()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.AppData, "Code", "Cache");
        root.CreateFile("AppData\\Code\\Cache\\a.bin", 700);

        var item = new CleanupItem
        {
            Key = "vscode-cache:" + dir,
            Path = dir,
            DisplayName = "VS Code Cache",
            Category = CleanupCategory.Cache,
            Risk = CleanupRisk.Low,
            Target = CleanupTarget.Directory,
            OwnerProcessNames = new[] { "Code", "Code - Insiders", "VSCodium" },
            AllowDirectDelete = true
        };

        var fakeProcesses = new FakeProcessInspector(
            new RunningProcessInfo(@"C:\Program Files\Microsoft VS Code\Code.exe", "Code"));
        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(),
            elevatedRunner: new ElevatedScenarioRunner(),
            processInspector: fakeProcesses);

        var report = await executor.CleanAsync([item], new CleanOptions { DryRun = true });

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.DryRun, entry.Outcome);
        Assert.Equal(0, entry.FreedBytes);
        Assert.Contains("Будет пропущено", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VS Code", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir, "a.bin")));
    }

    private sealed class RecordingElevatedRunner : IElevatedRunner
    {
        public ElevatedScenario? InvokedScenario { get; private set; }

        public Task<ElevatedJournal> RunAsync(ElevatedScenario scenario, CancellationToken cancellationToken = default)
        {
            InvokedScenario = scenario;
            return Task.FromResult(new ElevatedJournal { Results = scenario.Steps.Select(s => new ElevatedStepResult { Id = s.Id, Success = true }).ToList() });
        }
    }
}