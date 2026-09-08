using System.Diagnostics;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Tests;

/// <summary>
/// Integration-тесты фазы 4 модуля 02 на временных каталогах: прямое удаление крупного каталога
/// параллельными партиями (замер времени, прогресс), dry-run ничего не удаляет, пропуск
/// заблокированных файлов записывается в журнал без прерывания плана (NFR, FR-2.6 fallback).
/// </summary>
public class CachePhase14IntegrationTests
{
    [Fact]
    public async Task DirectDelete_LargeCacheTree_DeletesByParallelBatches_InTime_WithProgress()
    {
        using var root = new TempRoot();
        var dir = root.Combine("big-cache");
        long expectedFiles = 0;
        long expectedBytes = 0;

        // Глубокое дерево, похожее на npm-кэш: партии каталогов и файлов на каждом уровне.
        for (var batch = 0; batch < 24; batch++)
        {
            for (var sub = 0; sub < 8; sub++)
            {
                for (var file = 0; file < 25; file++)
                {
                    root.CreateFile($"big-cache\\b{batch}\\m{sub}\\f{file}.bin", 128);
                    expectedFiles++;
                    expectedBytes += 128;
                }
            }
        }

        var snapshots = new List<DirectoryDeletionProgress>();
        var deleter = new DirectoryDeleter();
        var stopwatch = Stopwatch.StartNew();
        var outcome = await deleter.DeletePathAsync(
            dir,
            CleanupTarget.Directory,
            progress: new Progress<DirectoryDeletionProgress>(snapshots.Add),
            sizeHint: DirectoryDeleter.LargeDirectoryThresholdBytes + 1);
        stopwatch.Stop();

        Assert.False(Directory.Exists(dir));
        Assert.True(outcome.FullyDeleted);
        Assert.Equal(expectedFiles, outcome.DeletedFiles);
        Assert.Equal(expectedBytes, outcome.FreedBytes);

        // Прогресс отдавался партиями по мере удаления (NFR «показывать прогресс»).
        Assert.True(snapshots.Count > 1, "Прямое удаление должно публиковать несколько снимков прогресса.");
        Assert.Equal(snapshots[^1].DeletedFiles, outcome.DeletedFiles);
        Assert.Equal(snapshots[^1].DeletedBytes, outcome.FreedBytes);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(60),
            $"Удаление крупного каталога заняло {stopwatch.Elapsed.TotalSeconds:F1} с — дольше бюджета.");
    }

    [Fact]
    public async Task DryRun_CachePlan_DeletesNothing_AndPreviewsEveryObject()
    {
        using var root = new TempRoot();
        var npmCache = root.Combine("npm-cache");
        var pipCache = root.Combine("pip-cache");
        root.CreateFile("npm-cache\\a.bin", 1300);
        root.CreateFile("pip-cache\\b.bin", 500);

        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(),
            elevatedRunner: new ElevatedScenarioRunner(),
            processInspector: new FakeProcessInspector());

        var report = await executor.CleanAsync(
            [CacheItem(npmCache), CacheItem(pipCache)],
            new CleanOptions { DryRun = true });

        Assert.Equal(2, report.Entries.Count);
        Assert.All(report.Entries, e => Assert.Equal(CleanOutcome.DryRun, e.Outcome));
        Assert.True(Directory.Exists(npmCache));
        Assert.True(Directory.Exists(pipCache));
        Assert.True(File.Exists(Path.Combine(npmCache, "a.bin")));
        Assert.True(File.Exists(Path.Combine(pipCache, "b.bin")));

        var journal = CleanReportFormatter.ToMarkdown(report);
        Assert.Contains("предпросмотр (dry-run)", journal, StringComparison.OrdinalIgnoreCase);
        Assert.All(report.Entries, e => Assert.Contains(e.Item.DisplayName, journal));
    }

    [Fact]
    public async Task Executor_LockedFilesAreSkipped_JournalRecordsReason_AndPlanContinuesToNextItem()
    {
        using var root = new TempRoot();
        var lockedCache = root.Combine("locked-cache");
        var lockedFile = root.CreateFile("locked-cache\\locked.bin", 700);
        root.CreateFile("locked-cache\\free.bin", 300);
        var healthyCache = root.Combine("healthy-cache");
        root.CreateFile("healthy-cache\\data.bin", 900);

        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(),
            elevatedRunner: new ElevatedScenarioRunner(),
            processInspector: new FakeProcessInspector());

        using (new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var report = await executor.CleanAsync([CacheItem(lockedCache), CacheItem(healthyCache)]);

            Assert.Equal(2, report.Entries.Count);

            var partial = report.Entries.Single(e => e.Outcome == CleanOutcome.Partial);
            Assert.Equal(300, partial.FreedBytes);
            Assert.Contains("Пропущено", partial.Note, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(lockedFile, partial.Note, StringComparison.Ordinal);

            var healthy = report.Entries.Single(e => e.Outcome == CleanOutcome.DirectDeleted);
            Assert.Equal(900, healthy.FreedBytes);
            Assert.False(Directory.Exists(healthyCache));

            Assert.True(File.Exists(lockedFile), "Заблокированный файл не должен удаляться.");
            Assert.False(File.Exists(Path.Combine(lockedCache, "free.bin")));
            Assert.Equal(1, report.PartialItems);
            Assert.Equal(0, report.FailedItems);
            Assert.NotNull(healthy);
        }
    }

    private static CleanupItem CacheItem(string path) => new()
    {
        Key = "cache:" + path,
        Path = path,
        DisplayName = Path.GetFileName(path),
        Category = CleanupCategory.Cache,
        Risk = CleanupRisk.Low,
        Target = CleanupTarget.Directory
    };
}
