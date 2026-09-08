using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

/// <summary>
/// Модуль 04, фаза 4 (milestone «Фаза 24») — приложения без записи в реестре, кейс 4DDiG-типа
/// (FR-4.10): поиск каталогов Program Files/ProgramData, не упомянутых записями Uninstall,
/// удаление с проверкой процессов и только через повышенный процесс (FR-4.15).
/// </summary>
public class UnrecordedAppTests
{
    [Fact]
    public void Scanner_FindsUnrecordedFolderWithExecutable_AndKeepsReferencedFolderAway()
    {
        using var root = new TempRoot();
        var programFiles = root.ProgramFiles;

        var orphanFolder = Path.Combine(programFiles, "4DDiG File Repair");
        Directory.CreateDirectory(orphanFolder);
        File.WriteAllText(Path.Combine(orphanFolder, "4DDiGFileRepair.exe"), "exe");

        var referencedFolder = Path.Combine(programFiles, "Installed Tool");
        Directory.CreateDirectory(referencedFolder);
        File.WriteAllText(Path.Combine(referencedFolder, "app.exe"), "exe");

        var installedApp = new InstalledApp
        {
            ProductCode = "{11111111-1111-1111-1111-111111111111}",
            ScopeKey = nameof(InstalledAppScope.LocalMachine64),
            DisplayName = "Installed Tool",
            InstallLocation = referencedFolder
        };

        var scanner = new UnrecordedAppScanner();
        var candidates = scanner.Find([programFiles], [installedApp]);

        var candidate = Assert.Single(candidates);
        Assert.Equal(orphanFolder, candidate.Path);
        Assert.Equal("4DDiG File Repair", candidate.Name);
        Assert.True(candidate.RequiresAdmin);
        Assert.Contains("4DDiGFileRepair", candidate.OwnerProcessNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scanner_ExcludesSystemAndSharedFolders()
    {
        using var root = new TempRoot();
        var programFiles = root.ProgramFiles;

        foreach (var folder in new[] { "Common Files", "Microsoft", "Windows NT", "Package Cache" })
        {
            var dir = Path.Combine(programFiles, folder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "core.exe"), "exe");
        }

        var scanner = new UnrecordedAppScanner();

        var candidates = scanner.Find([programFiles], []);

        Assert.Empty(candidates);
    }

    [Fact]
    public void BuildCleanupItems_ProducesGroupedAdminDirectDeleteItems()
    {
        using var root = new TempRoot();
        var programFiles = root.ProgramFiles;
        var folder = Path.Combine(programFiles, "4DDiG File Repair");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "4DDiGFileRepair.exe"), "exe");

        var items = new UnrecordedAppScanner().BuildCleanupItems([programFiles], []);

        var item = Assert.Single(items);
        Assert.Equal(UnrecordedAppScanner.GroupName, item.GroupName);
        Assert.Equal(CleanupCategory.Leftover, item.Category);
        Assert.Equal(CleanupTarget.Directory, item.Target);
        Assert.True(item.AllowDirectDelete);
        Assert.True(item.RequiresAdmin);
        Assert.Null(item.RegistryDeletePath);
        Assert.False(item.CommandOnly);
        Assert.Contains("записи", item.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanExecutor_DeletesUnrecordedFolderOnlyWhenNoProcessHoldsIt()
    {
        using var root = new TempRoot();
        var folder = Path.Combine(root.ProgramFiles, "GhostTool");
        Directory.CreateDirectory(folder);
        var exePath = Path.Combine(folder, "GhostTool.exe");
        File.WriteAllText(exePath, "exe");

        var items = new UnrecordedAppScanner().BuildCleanupItems([root.ProgramFiles], []);
        var item = Assert.Single(items);

        var inUse = new RunningProcessInfo(exePath, "GhostTool");
        var executor = new PlanExecutor(
            elevatedRunner: new ElevatedScenarioRunner(),
            processInspector: new FakeProcessInspector(inUse));

        var inUseReport = await executor.CleanAsync(
            [item],
            new CleanOptions { DryRun = false },
            cancellationToken: CancellationToken.None);

        var skipped = Assert.Single(inUseReport.Entries);
        Assert.Equal(CleanOutcome.InUseSkipped, skipped.Outcome);
        Assert.True(Directory.Exists(folder), "Каталог не должен удаляться, пока процесс из него запущен.");

        var freeExecutor = new PlanExecutor(
            elevatedRunner: new ElevatedScenarioRunner(),
            processInspector: new FakeProcessInspector());
        item.InUse = false;

        var freeReport = await freeExecutor.CleanAsync(
            [item],
            new CleanOptions { DryRun = false },
            cancellationToken: CancellationToken.None);

        var deleted = Assert.Single(freeReport.Entries);
        Assert.Equal(CleanOutcome.DirectDeleted, deleted.Outcome);
        Assert.False(Directory.Exists(folder), "Каталог должен быть удалён после завершения процесса.");
    }
}
