using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Tests;

public class RecycleBinModeTests
{
    public sealed class FakeRecycleBin : IRecycleBin
    {
        public List<string> MovedPaths { get; } = new();

        public List<string> EmptiedDrives { get; } = new();

        public bool FailNext { get; set; }

        public bool FailEmpty { get; set; }

        public void MoveToRecycleBin(string path)
        {
            if (FailNext)
            {
                throw new IOException("Shell refused");
            }

            MovedPaths.Add(path);
        }

        public void Empty(string driveRoot)
        {
            if (FailEmpty)
            {
                throw new IOException("Shell empty refused");
            }

            EmptiedDrives.Add(driveRoot);
        }
    }

    private static CleanupItem UserDataItem(string path, bool moveToRecycleBin = true) => new()
    {
        Key = "user-data:test:" + path,
        Path = path,
        DisplayName = "Downloads",
        GroupName = "Корзина-режим (пользовательские данные)",
        Category = CleanupCategory.UserData,
        Risk = CleanupRisk.High,
        Target = CleanupTarget.Directory,
        MoveToRecycleBin = moveToRecycleBin
    };

    [Fact]
    public async Task Clean_UserDataItem_IsMovedToRecycleBin_NotPermanentlyDeleted()
    {
        using var root = new TempRoot();
        var folder = root.Combine("downloads");
        root.CreateFile("downloads\\file.bin", 1234);

        var recycleBin = new FakeRecycleBin();
        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(deleter: new DirectoryDeleter(recycleBin)),
            elevatedRunner: new ElevatedScenarioRunner());

        var report = await executor.CleanAsync([UserDataItem(folder)]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.MovedToRecycleBin, entry.Outcome);
        Assert.Contains(folder, recycleBin.MovedPaths);
        Assert.True(Directory.Exists(folder), "Корзина-режим не должен удалять данные безвозвратно.");
    }

    [Fact]
    public async Task Clean_UserDataWithoutRecycleMode_IsDenied()
    {
        using var root = new TempRoot();
        var folder = root.Combine("documents");
        root.CreateFile("documents\\doc.txt", 100);

        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(deleter: new DirectoryDeleter(new FakeRecycleBin())),
            elevatedRunner: new ElevatedScenarioRunner());

        var report = await executor.CleanAsync([UserDataItem(folder, moveToRecycleBin: false)]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CleanOutcome.Denied, entry.Outcome);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task DryRun_DoesNotTouchRecycleBin()
    {
        using var root = new TempRoot();
        var folder = root.Combine("downloads");
        root.CreateFile("downloads\\a.bin", 100);

        var recycleBin = new FakeRecycleBin();
        var executor = new PlanExecutor(
            localCleaner: new CacheCleanerService(deleter: new DirectoryDeleter(recycleBin)),
            elevatedRunner: new ElevatedScenarioRunner());

        var report = await executor.CleanAsync(
            [UserDataItem(folder)],
            new CleanOptions { DryRun = true });

        Assert.Empty(recycleBin.MovedPaths);
        Assert.Equal(CleanOutcome.DryRun, Assert.Single(report.Entries).Outcome);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task DirectoryDeleter_RecycleBinFailure_ReportsError_KeepsFiles()
    {
        using var root = new TempRoot();
        var folder = root.Combine("downloads");
        root.CreateFile("downloads\\a.bin", 100);

        var recycleBin = new FakeRecycleBin { FailNext = true };
        var outcome = await new DirectoryDeleter(recycleBin).DeleteAsync(UserDataItem(folder));

        Assert.False(outcome.FullyDeleted);
        Assert.NotEmpty(outcome.Errors);
        Assert.True(Directory.Exists(folder));
    }
}
