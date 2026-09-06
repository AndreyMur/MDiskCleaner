using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Tests;

public class ReportJsonSchemaTests
{
    [Fact]
    public void CleanReport_BlockedItems_AreCollectedWithReasons()
    {
        using var root = new TempRoot();
        var removed = TestItems.Directory(root.Combine("cache-a"), "npm", CleanupCategory.Cache, CleanupRisk.Low);
        var blocked = TestItems.Directory(root.Combine("cache-b"), "npm", CleanupCategory.Cache, CleanupRisk.Low);
        var deferred = TestItems.Directory(root.Combine("cache-c"), "npm", CleanupCategory.Cache, CleanupRisk.Low);

        var report = new CleanReport
        {
            DryRun = false,
            Entries =
            [
                new CleanEntry(removed, CleanOutcome.DirectDeleted, 100, null),
                new CleanEntry(blocked, CleanOutcome.Denied, 0, "Путь в deny-списке"),
                new CleanEntry(deferred, CleanOutcome.InUseSkipped, 0, "Используется процессом")
            ]
        };

        var doc = ReportDocumentBuilder.FromCleanReport(report);

        Assert.Equal("clean", doc.Mode);
        Assert.Equal(3, doc.Summary.TotalItems);
        Assert.Equal(1, doc.Summary.DeletedItems);
        Assert.Equal(1, doc.Summary.DeferredItems);
        Assert.Equal(2, doc.Summary.BlockedItems);
        Assert.Single(doc.Removed);
        Assert.Equal(2, doc.Blocked.Count);
        Assert.Contains(doc.Blocked, b => b.Reason == "запрещено (deny-список)");
        Assert.Contains(doc.Blocked, b => b.Reason == "отложено (используется)");
    }

    [Fact]
    public void DryRun_Report_DocumentUsesDryRunMode()
    {
        using var root = new TempRoot();
        var item = TestItems.Directory(root.Combine("cache"), "npm", CleanupCategory.Cache, CleanupRisk.Low);

        var report = new CleanReport
        {
            DryRun = true,
            Entries = [new CleanEntry(item, CleanOutcome.DryRun, 200, "Будет очищено")]
        };

        var doc = ReportDocumentBuilder.FromCleanReport(report);

        Assert.Equal("dry-run", doc.Mode);
        Assert.Equal(200, doc.Summary.FreedBytes);
        Assert.Single(doc.Items);
        Assert.Empty(doc.Removed);
        Assert.Empty(doc.Blocked);
    }
}
