using System.Text.Json;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Tests;

public class ProcessAndReportTests
{
    [Fact]
    public void InUseDetector_DoesNotMarkUnrelatedProcess()
    {
        using var root = new TempRoot();
        var dir = root.Combine("sdk");
        var item = TestItems.Directory(dir);

        var processes = new[] { new RunningProcessInfo(@"C:\Windows\System32\notepad.exe", "notepad") };

        var marked = new InUseDetector().MarkInUse([item], processes);

        Assert.Equal(0, marked);
        Assert.False(item.InUse);
    }

    [Fact]
    public void ReportFormatter_MarkdownContainsSummaryAndRows()
    {
        using var root = new TempRoot();
        var dir = root.Combine("some-cache");
        root.CreateFile("some-cache\\a.bin", 3000);

        var item = TestItems.Directory(dir, "npm", CleanupCategory.Cache, CleanupRisk.Low);
        var report = new CleanReport
        {
            Entries = [new CleanEntry(item, CleanOutcome.DryRun, 3000, "Будет очищено")],
            DryRun = true,
            Elapsed = TimeSpan.FromSeconds(0.5)
        };

        var markdown = CleanReportFormatter.ToMarkdown(report);

        Assert.Contains("предпросмотр", markdown);
        Assert.Contains("3 КБ", markdown);
        Assert.Contains(item.DisplayName, markdown);
    }

    [Fact]
    public void ReportFormatter_JsonIsValid()
    {
        using var root = new TempRoot();
        var item = TestItems.Directory(root.Combine("cache"));
        var report = new CleanReport
        {
            Entries = [new CleanEntry(item, CleanOutcome.DirectDeleted, 42, null)],
            DryRun = false
        };

        var json = CleanReportFormatter.ToJson(report);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("clean", document.RootElement.GetProperty("mode").GetString());
        Assert.Equal(42, document.RootElement.GetProperty("summary").GetProperty("freedBytes").GetInt64());
        Assert.Equal(1, document.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(1, document.RootElement.GetProperty("removed").GetArrayLength());
        Assert.Empty(document.RootElement.GetProperty("blocked").EnumerateArray());
    }
}
