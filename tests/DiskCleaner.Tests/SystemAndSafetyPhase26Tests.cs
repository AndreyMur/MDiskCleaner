using System.Text;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Logging;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;

namespace DiskCleaner.Tests;

/// <summary>
/// Фаза 26 модуля 05 — «Безопасная очистка Temp и журналирование (Tracer Bullet)»
/// (FR-5.1, FR-5.3, FR-5.9, FR-5.12, FR-5.13). Integration-тесты на временных каталогах:
/// один файл открыт процессом → шаг завершается «частично», прогон продолжается,
/// журнал корректен, Windows\Temp уходит в админ-пачку.
/// </summary>
public class SystemAndSafetyPhase26Tests
{
    [Fact]
    public async Task UserTemp_OneLockedFile_StepPartial_PlanContinues_AndListsBlockedFile()
    {
        using var root = new TempRoot();
        var userTemp = root.Combine("user-temp");
        var lockedFile = root.CreateFile("user-temp\\locked.bin", 700);
        root.CreateFile("user-temp\\free.bin", 300);
        var healthyTemp = root.Combine("healthy-temp");
        root.CreateFile("healthy-temp\\h.bin", 900);

        var executor = CreateExecutor();

        using (new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var report = await executor.CleanAsync(
                [ContentsOnlyTemp(userTemp), ContentsOnlyTemp(healthyTemp)]);

            Assert.Equal(2, report.Entries.Count);

            var partial = report.Entries.Single(e => e.Outcome == CleanOutcome.Partial);
            Assert.Equal(300, partial.FreedBytes);
            Assert.Contains(lockedFile, partial.Note, StringComparison.Ordinal);
            Assert.Contains("пропущено", partial.Note, StringComparison.OrdinalIgnoreCase);

            var healthy = report.Entries.Single(e => e.Outcome == CleanOutcome.DirectDeleted);
            Assert.Equal(900, healthy.FreedBytes);

            // FR-5.9: «частично» не считается ошибкой плана.
            Assert.Equal(0, report.FailedItems);
            Assert.Equal(1, report.PartialItems);

            // Сам каталог %TEMP% сохраняется; свободные файлы удалены, заблокированный — нет.
            Assert.True(Directory.Exists(userTemp));
            Assert.True(File.Exists(lockedFile));
            Assert.False(File.Exists(Path.Combine(userTemp, "free.bin")));
            Assert.True(Directory.Exists(healthyTemp), "Очистка содержимого сохраняет сам каталог.");
            Assert.Empty(Directory.EnumerateFileSystemEntries(healthyTemp));
        }
    }

    [Fact]
    public async Task WindowsTemp_RequiresAdmin_IsMovedToElevatedBatch_UserTempStaysLocal()
    {
        using var root = new TempRoot();
        var userTemp = root.Combine("user-temp");
        root.CreateFile("user-temp\\u.bin", 500);
        var windowsTemp = root.Combine("Win", "Temp");
        root.CreateFile("Win\\Temp\\w.bin", 700);

        var elevated = new RecordingElevatedRunner();
        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(),
            elevatedRunner: elevated,
            processInspector: new FakeProcessInspector());

        var report = await executor.CleanAsync(
        [
            ContentsOnlyTemp(userTemp, requiresAdmin: false),
            ContentsOnlyTemp(windowsTemp, requiresAdmin: true)
        ]);

        // Оба шага очищены (Windows\Temp — через elevated-исполнитель в админ-пачке).
        Assert.Equal(2, report.Entries.Count);
        Assert.All(report.Entries, e => Assert.Equal(CleanOutcome.DirectDeleted, e.Outcome));
        Assert.Equal(0, report.FailedItems);

        // В админ-пачку попадает только Windows\Temp; %TEMP% пользователя исполняется локально.
        var step = Assert.Single(elevated.Scenario!.Steps);
        Assert.Equal(ElevatedStepKind.DeletePath, step.Kind);
        Assert.Equal(Path.GetFullPath(windowsTemp), step.Path);
        Assert.True(step.DeleteContentsOnly);
        Assert.DoesNotContain(userTemp, elevated.Scenario!.Steps.Select(s => s.Path ?? string.Empty));

        Assert.True(Directory.Exists(userTemp), "Сам каталог %TEMP% не удаляется.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(userTemp));
        Assert.True(Directory.Exists(windowsTemp), "Сам каталог Windows\\Temp не удаляется.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(windowsTemp));
    }

    [Fact]
    public async Task ElevatedScenarioRunner_LockedFile_CompletesPartial_AndRunContinuesToNextStep()
    {
        using var root = new TempRoot();
        var lockedDir = root.Combine("win-temp-locked");
        var lockedFile = root.CreateFile("win-temp-locked\\locked.bin", 800);
        root.CreateFile("win-temp-locked\\ok.bin", 200);
        var healthyDir = root.Combine("win-temp-healthy");
        root.CreateFile("win-temp-healthy\\h.bin", 400);

        var runner = new ElevatedScenarioRunner();
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var journal = await runner.RunAsync(new ElevatedScenario
            {
                Steps =
                [
                    new ElevatedStep
                    {
                        Id = "windows-temp",
                        Kind = ElevatedStepKind.DeletePath,
                        Path = lockedDir,
                        Target = CleanupTarget.Directory,
                        DeleteContentsOnly = true
                    },
                    new ElevatedStep
                    {
                        Id = "healthy",
                        Kind = ElevatedStepKind.DeletePath,
                        Path = healthyDir,
                        Target = CleanupTarget.Directory,
                        DeleteContentsOnly = true
                    }
                ]
            });

            Assert.Equal(2, journal.Results.Count);
            Assert.False(journal.AllSucceeded);

            var partial = journal.Results.Single(r => r.Id == "windows-temp");
            Assert.False(partial.Success);
            Assert.Equal(200, partial.FreedBytes);
            Assert.Contains(lockedFile, partial.Error, StringComparison.Ordinal);

            var healthy = journal.Results.Single(r => r.Id == "healthy");
            Assert.True(healthy.Success);
            Assert.Equal(400, healthy.FreedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(healthyDir));

            Assert.True(Directory.Exists(lockedDir));
            Assert.True(File.Exists(lockedFile));
            Assert.False(File.Exists(Path.Combine(lockedDir, "ok.bin")));
        }
    }

    [Fact]
    public void PartialStep_IsNotCountedAsPlanError()
    {
        var item = ContentsOnlyTemp("C:\\temp\\fake");
        var report = new CleanReport
        {
            Entries =
            [
                new CleanEntry(item, CleanOutcome.DirectDeleted, 100, null),
                new CleanEntry(item, CleanOutcome.Partial, 50, "Пропущено: один файл заблокирован"),
                new CleanEntry(item, CleanOutcome.Error, 0, "Не удалось очистить объект"),
                new CleanEntry(item, CleanOutcome.InUseSkipped, 0, "Используется процессом")
            ]
        };

        Assert.Equal(1, report.FailedItems);
        Assert.Equal(1, report.PartialItems);
        Assert.Equal(1, report.SkippedItems);
    }

    [Fact]
    public async Task Journal_PerAction_RecordsFieldsAndBlockedFiles_ToLocalAppDataLog()
    {
        using var root = new TempRoot();
        var logDirectory = root.Combine("logs");
        DiskCleanerLog.Initialize(logDirectory);

        try
        {
            var userTemp = root.Combine("journal-temp");
            var lockedFile = root.CreateFile("journal-temp\\locked.bin", 700);
            root.CreateFile("journal-temp\\free.bin", 300);
            var healthyTemp = root.Combine("journal-healthy");
            root.CreateFile("journal-healthy\\h.bin", 900);

            var executor = new PlanExecutor(
                localCleaner: new CacheCleanerService(),
                elevatedRunner: new ElevatedScenarioRunner(),
                processInspector: new FakeProcessInspector());

            using (new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await executor.CleanAsync([ContentsOnlyTemp(userTemp), ContentsOnlyTemp(healthyTemp)]);
            }

            Serilog.Log.CloseAndFlush();

            var logFile = Directory.GetFiles(logDirectory, "diskcleaner-*.log")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
            Assert.NotNull(logFile);

            var bytes = File.ReadAllBytes(logFile!);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.Contains("пропущено", text, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("Clean action:", text);
            Assert.Contains(Path.GetFullPath(userTemp), text);
            Assert.Contains("op=clear-directory", text);
            Assert.Contains("result=Partial", text);
            Assert.Contains(lockedFile, text);
            Assert.Contains("пропущено", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("result=DirectDeleted", text);
            Assert.Contains("sizeBytes=", text);
            Assert.Contains("exitCode=", text);
            Assert.Contains("error=", text);
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
        }
    }

    private static PlanExecutor CreateExecutor() => new(
        localCleaner: new CacheCleanerService(),
        elevatedRunner: new ElevatedScenarioRunner(),
        processInspector: new FakeProcessInspector());

    private static CleanupItem ContentsOnlyTemp(string path, bool requiresAdmin = false)
    {
        var full = Path.GetFullPath(path);
        return new CleanupItem
        {
            Key = requiresAdmin ? "system:temp:windows" : "temp:" + full,
            Path = full,
            DisplayName = requiresAdmin ? "Системные временные файлы (Windows\\Temp)" : "Временные файлы пользователя (%TEMP%)",
            GroupName = "Системная очистка",
            Category = CleanupCategory.Temp,
            Risk = CleanupRisk.Low,
            Target = CleanupTarget.Directory,
            DeleteContentsOnly = true,
            RequiresAdmin = requiresAdmin
        };
    }

    /// <summary>
    /// Записывает elevated-сценарий (для проверки группировки админ-пачки), а исполнение
    /// делегирует реальному <see cref="ElevatedScenarioRunner"/> (in-proc, на временных объектах).
    /// </summary>
    private sealed class RecordingElevatedRunner : IElevatedRunner
    {
        private readonly IElevatedRunner _inner = new ElevatedScenarioRunner();

        public ElevatedScenario? Scenario { get; private set; }

        public async Task<ElevatedJournal> RunAsync(ElevatedScenario scenario, CancellationToken cancellationToken = default)
        {
            Scenario = scenario;
            return await _inner.RunAsync(scenario, cancellationToken);
        }
    }
}
