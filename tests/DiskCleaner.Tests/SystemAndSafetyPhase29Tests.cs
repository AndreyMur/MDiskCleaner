using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Tests;

/// <summary>
/// Фаза 29 модуля 05 — «Системные операции: Корзина, SoftwareDistribution, гибернация,
/// Windows.old» (FR-5.1–5.6). Backend:
/// — Корзина очищается по выбранному диску штатным API оболочки (SHEmptyRecycleBin, FR-5.2),
///   а не прямым удалением содержимого $Recycle.Bin; C:\Windows\Temp уже маршрутизируется
///   в админ-пачку (FR-5.1);
/// — SoftwareDistribution\Download очищается в elevated-процессе со штатной остановкой службы
///   wuauserv; при недоступной/неостанавливаемой службе выдаётся инструкция (FR-5.3);
/// — гибернация: powercfg /h off с проверкой exit-кода 0 И исчезновения hiberfil.sys,
///   обратимость через powercfg /h on (FR-5.4);
/// — Windows.old* (включая суффиксы .000): оценка размера, удаление целиком только с админ-правами
///   и с инструкцией о штатных средствах (FR-5.5);
/// — deny-список защищён на уровне исполнителя: объект не исполняется локально и не тратит
///   UAC-подъём (FR-5.6).
/// </summary>
public class SystemAndSafetyPhase29Tests
{
    // ---------- FR-5.6 deny-список на уровне исполнителя ----------

    [Fact]
    public async Task PlanExecutor_DenyListedLeaf_IsDenied_NoLocalDelete_NoElevatedBatch()
    {
        using var root = new TempRoot();
        var protectedDir = root.Combine("Windows", "System32");
        var protectedFile = root.CreateFile("Windows\\System32\\ntdll.bin", 200);
        var healthy = root.Combine("healthy");
        root.CreateFile("healthy\\ok.bin", 300);

        var elevated = new RecordingElevatedRunner();
        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(),
            elevatedRunner: elevated);

        var report = await executor.CleanAsync(
        [
            DirectoryClean(protectedDir),
            DirectoryClean(healthy)
        ]);

        // FR-5.6: deny-объект помечается Denied и не удаляется.
        var denied = Assert.Single(report.Entries, e => e.Outcome == CleanOutcome.Denied);
        Assert.Equal(Path.GetFullPath(protectedDir), denied.Item.Path);
        Assert.Contains("никогда не удалять вручную", denied.Note, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(protectedFile), "Объект deny-списка не должен удаляться.");

        // Соседний разрешённый шаг исполняется локально; UAC-подъём не тратится зря.
        Assert.Equal(1, report.SkippedItems);
        Assert.Equal(0, report.FailedItems);
        Assert.Equal(0, elevated.Calls);
        var cleaned = Assert.Single(report.Entries, e => e.Outcome == CleanOutcome.DirectDeleted);
        Assert.Equal(Path.GetFullPath(healthy), cleaned.Item.Path);
        Assert.False(Directory.Exists(healthy));
    }

    [Fact]
    public async Task ElevatedScenario_DeniedPathInBatch_MapsBackToDenied_NotError()
    {
        using var root = new TempRoot();
        var protectedDir = root.Combine("Windows", "System32");
        root.CreateFile("Windows\\System32\\ntdll.bin", 200);

        var item = DirectoryClean(protectedDir, requiresAdmin: true);
        var scenario = new ElevatedScenarioBuilder().Build([item]);

        var runner = new ElevatedScenarioRunner();
        var journal = await runner.RunAsync(scenario);

        // DirectoryDeleter отклоняет путь и внутри elevated-сценария (последняя линия защиты).
        var mapped = new ElevatedScenarioBuilder().MapResults([item], journal);
        var entry = Assert.Single(mapped);
        Assert.Equal(CleanOutcome.Denied, entry.Outcome);
        Assert.Contains("никогда не удалять вручную", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(protectedDir, "ntdll.bin")));
    }

    // ---------- FR-5.2 Корзина ----------

    [Fact]
    public async Task PlanExecutor_RecycleBinItem_EmptiesDriveViaShellApi_NotMoveOrDirectDelete()
    {
        using var root = new TempRoot();
        var binDir = root.Combine("bin");
        root.CreateFile("bin\\$R123.bin", 1000);
        var pseudoDriveRoot = root.Path.TrimEnd('\\') + "\\";

        var recycleBin = new FakeRecycleBin();
        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(deleter: new DirectoryDeleter(recycleBin)),
            elevatedRunner: new ElevatedScenarioRunner());

        var report = await executor.CleanAsync(
        [
            RecycleBinItem(binDir, pseudoDriveRoot)
        ]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.DirectDeleted, entry.Outcome);
        Assert.Contains("SHEmptyRecycleBin", entry.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Single(recycleBin.AttemptedDrives);
        Assert.Equal(pseudoDriveRoot, recycleBin.AttemptedDrives[0]);
        Assert.Empty(recycleBin.MovedPaths);
        Assert.True(Directory.Exists(binDir), "Корзина-каталог не удаляется — очищается содержимое.");
    }

    [Fact]
    public async Task PlanExecutor_RecycleBinEmptyFailure_IsError_AndBinIsNotEmptied()
    {
        using var root = new TempRoot();
        var binDir = root.Combine("bin");
        root.CreateFile("bin\\$R123.bin", 1000);
        var pseudoDriveRoot = root.Path.TrimEnd('\\') + "\\";

        var recycleBin = new FakeRecycleBin { FailEmpty = true };
        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(deleter: new DirectoryDeleter(recycleBin)),
            elevatedRunner: new ElevatedScenarioRunner());

        var report = await executor.CleanAsync(
        [
            RecycleBinItem(binDir, pseudoDriveRoot)
        ]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.Error, entry.Outcome);
        Assert.Contains("Shell empty refused", entry.Note);
        Assert.Single(recycleBin.AttemptedDrives);
        Assert.True(File.Exists(Path.Combine(binDir, "$R123.bin")));
    }

    // ---------- FR-5.4 Гибернация ----------

    [Fact]
    public void ElevatedScenarioBuilder_HibernationOff_ProducesRunProcessWithVerifyPathAbsent()
    {
        var hiberfil = @"C:\hiberfil.sys";
        var item = new CleanupItem
        {
            Key = "system:hibernation:off",
            Path = null,
            DisplayName = "Отключить гибернацию",
            Category = CleanupCategory.SystemFile,
            Risk = CleanupRisk.Medium,
            Target = CleanupTarget.File,
            RequiresAdmin = true,
            CommandOnly = true,
            AllowDirectDelete = false,
            CleanCommand = "powercfg /h off",
            CleanCommandFile = @"C:\Windows\System32\powercfg.exe",
            CleanCommandArgs = "/h off",
            VerifyPathAbsent = hiberfil
        };

        var scenario = new ElevatedScenarioBuilder().Build([item]);

        var step = Assert.Single(scenario.Steps);
        Assert.Equal(ElevatedStepKind.RunProcess, step.Kind);
        Assert.Equal("/h off", step.Arguments);
        Assert.Equal(hiberfil, step.VerifyPathAbsent);
    }

    [Fact]
    public async Task ElevatedScenarioRunner_RunProcess_Exit0_ButFileRemains_IsNotSuccess()
    {
        using var root = new TempRoot();
        var file = root.CreateFile("hiberfil.sys", 200);

        var runner = new ElevatedScenarioRunner();
        var journal = await runner.RunAsync(new ElevatedScenario
        {
            Steps =
            [
                new ElevatedStep
                {
                    Id = "hiber",
                    Kind = ElevatedStepKind.RunProcess,
                    FileName = "cmd.exe",
                    Arguments = "/c exit 0",
                    VerifyPathAbsent = file
                }
            ]
        });

        var result = Assert.Single(journal.Results);
        Assert.False(result.Success, "exit-код 0 без исчезновения файла — шаг не успешен (FR-5.4).");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("остался на месте", result.Error);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task ElevatedScenarioRunner_RunProcess_FileRemovedAfterExit0_IsSuccess()
    {
        using var root = new TempRoot();
        var file = root.CreateFile("hiberfil.sys", 200);

        var runner = new ElevatedScenarioRunner();
        var journal = await runner.RunAsync(new ElevatedScenario
        {
            Steps =
            [
                new ElevatedStep
                {
                    Id = "hiber",
                    Kind = ElevatedStepKind.RunProcess,
                    FileName = "cmd.exe",
                    Arguments = $"/c del /f /q \"{file}\"",
                    VerifyPathAbsent = file
                }
            ]
        });

        var result = Assert.Single(journal.Results);
        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void SystemScanSeedsProvider_HiberfilPresent_EmitsOffSeedWithVerify_AndNoOnSeed()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var hiberfil = root.CreateFile("hiberfil.sys", 100);

        var provider = new SystemScanSeedsProvider(
            environment: environment,
            windowsDirectory: root.Combine("Win"),
            systemRoot: root.Path,
            recycleBinDirectories: []);

        var seeds = provider.BuildSeeds();

        var off = Assert.Single(seeds, s => s.Key == "system:hibernation:off");
        Assert.True(off.RequiresAdmin);
        Assert.Equal("/h off", off.CleanCommandArgs);
        Assert.Equal(hiberfil, off.VerifyPathAbsent);
        Assert.Equal(100, off.SizeBytes);
        Assert.DoesNotContain(seeds, s => s.Key == "system:hibernation:on");
    }

    // ---------- FR-5.3 SoftwareDistribution ----------

    [Fact]
    public async Task ElevatedScenarioRunner_ServiceClean_WhenServiceUnavailable_AddsInstruction_StillCleans()
    {
        using var root = new TempRoot();
        var dir = root.Combine("sd-download");
        root.CreateFile("sd-download\\update.cab", 5000);

        var runner = new ElevatedScenarioRunner();
        var journal = await runner.RunAsync(new ElevatedScenario
        {
            Steps =
            [
                new ElevatedStep
                {
                    Id = "wu",
                    Kind = ElevatedStepKind.ServiceCleanDirectory,
                    Path = dir,
                    ServiceName = "DiskCleaner.NoSuchService." + Guid.NewGuid().ToString("N"),
                    DeleteContentsOnly = true
                }
            ]
        });

        var result = Assert.Single(journal.Results);
        Assert.True(result.Success, "Каталог очищается, даже если служба не найдена.");
        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        Assert.Equal(5000, result.FreedBytes);
        Assert.Contains("не найдена или недоступна", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("без остановки службы", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- FR-5.5 Windows.old* ----------

    [Fact]
    public void SystemScanSeedsProvider_WindowsOldVariants_SeedWithSizeAdminWholeRemoval()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        root.CreateFile("Windows.old\\Windows\\System32\\config\\SYSTEM", 300);
        root.CreateFile("Windows.old.000\\Windows\\System32\\config\\SYSTEM", 200);

        var provider = new SystemScanSeedsProvider(
            environment: environment,
            windowsDirectory: root.Combine("Win"),
            systemRoot: root.Path,
            recycleBinDirectories: []);

        var seeds = provider.BuildSeeds();

        var exact = Assert.Single(seeds, s => s.Key == "system:windows-old");
        AssertWindowsOldSeed(exact);

        var variant = Assert.Single(seeds, s => s.Key == "system:windows-old:Windows.old.000");
        AssertWindowsOldSeed(variant);
    }

    private static void AssertWindowsOldSeed(CleanupItem seed)
    {
        Assert.Equal(CleanupCategory.SystemFile, seed.Category);
        Assert.Equal(CleanupRisk.High, seed.Risk);
        Assert.True(seed.RequiresAdmin, "Windows.old удаляется только с админ-правами (FR-5.5).");
        Assert.False(seed.DeleteContentsOnly, "Windows.old удаляется целиком, а не только содержимое.");
        Assert.True(seed.SizeBytes > 0, "Seed должен нести оценку размера (FR-5.5).");
        Assert.Contains("Storage Sense", seed.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UAC", seed.Warning, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- helpers ----------

    private static CleanupItem RecycleBinItem(string path, string driveRoot) => new()
    {
        Key = "system:recycle-bin:" + path.TrimEnd('\\'),
        Path = Path.GetFullPath(path),
        DisplayName = "Корзина (" + driveRoot + ")",
        GroupName = "Системная очистка",
        Category = CleanupCategory.RecycleBin,
        Risk = CleanupRisk.Medium,
        Target = CleanupTarget.Directory,
        EmptyRecycleBinDrive = driveRoot
    };

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

    private sealed class FakeRecycleBin : IRecycleBin
    {
        public List<string> AttemptedDrives { get; } = new();

        public List<string> MovedPaths { get; } = new();

        public bool FailEmpty { get; set; }

        public void MoveToRecycleBin(string path) => MovedPaths.Add(path);

        public void Empty(string driveRoot)
        {
            AttemptedDrives.Add(driveRoot);
            if (FailEmpty)
            {
                throw new IOException("Shell empty refused");
            }
        }
    }

    private sealed class RecordingElevatedRunner : IElevatedRunner
    {
        private readonly IElevatedRunner _inner = new ElevatedScenarioRunner();

        public int Calls { get; private set; }

        public Task<ElevatedJournal> RunAsync(ElevatedScenario scenario, CancellationToken cancellationToken = default)
        {
            Calls++;
            return _inner.RunAsync(scenario, cancellationToken);
        }
    }
}
