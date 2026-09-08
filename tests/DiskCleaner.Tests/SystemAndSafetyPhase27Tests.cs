using System.Diagnostics;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;

namespace DiskCleaner.Tests;

/// <summary>
/// Фаза 27 модуля 05 — «Детектор занятости IN_USE и стратегии для "долгоживущих" приложений»
/// (FR-5.7, FR-5.8). Integration: запущенный временный exe внутри удаляемого пути → объект
/// IN_USE, список блокирующих процессов передан исполнителю, удаление не выполняется;
/// unit: кэширование списка процессов на время плана и стратегии рекомендаций.
/// </summary>
public class SystemAndSafetyPhase27Tests
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
    public async Task RunningExecutableInsideTarget_IsMarkedInUse_BlockersReturned_NotDeleted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TempRoot();
        var dir = root.Combine("Android", "Sdk");
        var source = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!File.Exists(source))
        {
            return;
        }

        var copiedExe = Path.Combine(dir, "adb.exe");
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

            // FR-5.7: объект помечен IN_USE, список блокирующих процессов передан, удаление не выполнено.
            Assert.True(entry.Item.InUse);
            Assert.Contains(entry.Item.BlockingProcesses, p =>
                p.ExecutablePath?.StartsWith(dir, StringComparison.OrdinalIgnoreCase) == true);
            Assert.True(File.Exists(copiedExe), "Объект с запущенным процессом не должен удаляться.");
            Assert.True(Directory.Exists(dir));

            Assert.Equal(1, report.DeferredItems);
            Assert.Equal(0, report.FailedItems);
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
    public async Task AdbLikeProcessInsideSdkPath_SkipWithDeferStepAdvice_AndBlockerNamedInNote()
    {
        using var root = new TempRoot();
        var dir = root.Combine("Android", "Sdk");
        root.CreateFile("Android\\Sdk\\platform-tools\\tools.bin", 200);

        var fakeProcesses = new FakeProcessInspector(
            new RunningProcessInfo(Path.Combine(dir, "platform-tools", "adb.exe"), "adb.exe"),
            new RunningProcessInfo(@"C:\Windows\System32\notepad.exe", "notepad"));
        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(),
            elevatedRunner: new ElevatedScenarioRunner(),
            processInspector: fakeProcesses);

        var report = await executor.CleanAsync([Item(dir)]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.InUseSkipped, entry.Outcome);

        var blocker = Assert.Single(entry.Item.BlockingProcesses);
        Assert.Equal("adb.exe", blocker.Name);
        Assert.Equal(InUseAdvice.DeferStep, entry.Item.InUseAdvice);

        Assert.Contains("adb.exe", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Блокирующие процессы", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("отложен", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(dir, "platform-tools", "tools.bin")), "Объект не удаляется при занятости.");
    }

    [Fact]
    public async Task VSCodeCacheBlockedByCode_CloseAndRetryStrategy_InNoteAndAdvice()
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

        // FR-5.8: для «долгоживущего» приложения (VS Code) — статус «закройте приложение и повторите».
        Assert.Equal(InUseAdvice.CloseAndRetry, entry.Item.InUseAdvice);
        Assert.Contains("VS Code", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Закройте VS Code и повторите попытку", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("отложите шаг", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(dir, "a.bin")));
    }

    [Fact]
    public void InUseMessages_AdviceFor_LongLivedBrowser_IsCloseAndRetry_GenericNode_IsDeferStep()
    {
        using var root = new TempRoot();

        var edgeItem = new CleanupItem
        {
            Key = "edge-cache",
            Path = root.Combine("edge", "Cache"),
            DisplayName = "Edge Cache",
            Category = CleanupCategory.Cache,
            Target = CleanupTarget.Directory,
            OwnerProcessNames = new[] { "msedge" }
        };
        var edgeBlocking = new[] { new RunningProcessInfo(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "msedge") };
        Assert.Equal(InUseAdvice.CloseAndRetry, InUseMessages.AdviceFor(edgeItem, edgeBlocking));

        var nodeItem = new CleanupItem
        {
            Key = "node-cache",
            Path = root.Combine("node", "Cache"),
            DisplayName = "node",
            Category = CleanupCategory.Cache,
            Target = CleanupTarget.Directory
        };
        var nodeBlocking = new[] { new RunningProcessInfo(@"C:\Program Files\nodejs\node.exe", "node") };
        Assert.Equal(InUseAdvice.DeferStep, InUseMessages.AdviceFor(nodeItem, nodeBlocking));
    }

    [Fact]
    public void InUseDetector_MarkInUse_RecordsBlockingProcesses_AndDoesNotTouchUnrelated()
    {
        using var root = new TempRoot();
        var sdkDir = root.Combine("Android", "Sdk");
        var item = TestItems.Directory(sdkDir);

        var processes = new[]
        {
            new RunningProcessInfo(Path.Combine(sdkDir, "platform-tools", "adb.exe"), "adb.exe"),
            new RunningProcessInfo(@"C:\Windows\System32\notepad.exe", "notepad")
        };

        var marked = new InUseDetector().MarkInUse([item], processes);

        Assert.Equal(1, marked);
        Assert.True(item.InUse);
        var blocker = Assert.Single(item.BlockingProcesses);
        Assert.Equal("adb.exe", blocker.Name);
        Assert.Equal(InUseAdvice.DeferStep, item.InUseAdvice);
    }

    [Fact]
    public void ProcessSnapshotCache_QueriesInspectorOnce_AndInvalidateRefreshes()
    {
        var inspector = new CountingProcessInspector(
            new RunningProcessInfo(@"C:\Tools\app.exe", "app"));
        var cache = new ProcessSnapshotCache(inspector);

        var first = cache.Processes;
        var second = cache.Processes;

        Assert.Same(first, second);
        Assert.Equal(1, inspector.QueryCount);

        cache.Invalidate();

        var refreshed = cache.Processes;
        Assert.NotSame(first, refreshed);
        Assert.Equal(2, inspector.QueryCount);
    }

    [Fact]
    public async Task PlanExecutor_SharedSnapshot_IsUsedAcrossDryRunAndClean_WithoutRequery()
    {
        using var root = new TempRoot();
        var dir = root.Combine("shared-plan-cache");
        root.CreateFile("shared-plan-cache\\a.bin", 500);

        var inspector = new CountingProcessInspector(
            new RunningProcessInfo(Path.Combine(dir, "app.exe"), "app"));
        var sharedCache = new ProcessSnapshotCache(inspector);

        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(),
            elevatedRunner: new ElevatedScenarioRunner(),
            processSnapshotCache: sharedCache);

        // Предпросмотр и исполнение — один план: обе проверки используют один кэшированный снимок.
        var dry = await executor.CleanAsync([Item(dir)], new CleanOptions { DryRun = true });
        var clean = await executor.CleanAsync([Item(dir)]);

        Assert.Equal(1, inspector.QueryCount);

        Assert.Equal(CleanOutcome.DryRun, Assert.Single(dry.Entries).Outcome);
        Assert.Contains("Будет пропущено", Assert.Single(dry.Entries).Note, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(CleanOutcome.InUseSkipped, Assert.Single(clean.Entries).Outcome);
        Assert.True(File.Exists(Path.Combine(dir, "a.bin")), "Занятый процессом объект не удаляется.");
    }

    private sealed class CountingProcessInspector : IProcessInspector
    {
        private readonly IReadOnlyList<RunningProcessInfo> _processes;

        public CountingProcessInspector(params RunningProcessInfo[] processes)
        {
            _processes = processes;
        }

        public int QueryCount { get; private set; }

        public IReadOnlyList<RunningProcessInfo> GetRunningProcesses()
        {
            QueryCount++;
            // Новый экземпляр списка на каждый опрос (как реальный WMI-запрос).
            return _processes.ToList();
        }
    }
}
