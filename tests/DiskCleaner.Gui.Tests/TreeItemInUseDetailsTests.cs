using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

/// <summary>
/// Тесты отображения блокировок (задача #150, FR-5.7/5.8): для объекта IN_USE пользователь
/// видит список блокирующих процессов и рекомендацию «закройте приложение и повторите» либо
/// «отложите шаг»; объект не выбирается в план.
/// </summary>
public class TreeItemInUseDetailsTests
{
    [Fact]
    public async Task InUseLeaf_ShowsProcessesAndCloseAndRetryAdvice_NotSelectable()
    {
        var vscode = new CleanupItem
        {
            Key = "cache:vscode-logs",
            Path = @"C:\fake\Code\logs",
            DisplayName = "VS Code logs",
            GroupName = "VS Code",
            Category = CleanupCategory.Cache,
            Risk = CleanupRisk.Low,
            SizeBytes = 40_000_000,
            InUse = true,
            OwnerProcessNames = ["Code", "Code - Insiders"],
            BlockingProcesses =
            [
                new RunningProcessInfo(@"C:\Users\dev\AppData\Local\Programs\Microsoft VS Code\Code.exe", "Code.exe")
            ],
            InUseAdvice = InUseAdvice.CloseAndRetry
        };

        var (_, _, _, vm) = PlanRunTestSupport.Create(PlanRunTestSupport.TreeOf(new[] { vscode }));
        await vm.AnalyzeCommand.ExecuteAsync(null);

        var leaf = vm.RootNodes.SelectMany(n => n.GetLeaves()).Single(l => l.Item.Key == "cache:vscode-logs");

        Assert.True(leaf.IsInUse);
        Assert.False(leaf.CanCheck);
        Assert.True(leaf.HasBlockingProcesses);
        Assert.Contains("Code.exe", leaf.BlockingText, StringComparison.Ordinal);
        Assert.Contains("Microsoft VS Code\\Code.exe", leaf.BlockingText, StringComparison.Ordinal);
        Assert.Contains("VS Code", leaf.InUseOwnerText, StringComparison.Ordinal);
        Assert.Contains("закройте", leaf.InUseAdviceText, StringComparison.Ordinal);
        Assert.DoesNotContain(vm.PlanRows, r => r.Item.Key == "cache:vscode-logs");
    }

    [Fact]
    public async Task InUseLeaf_WithUnknownBlocker_SuggestsDeferringStep()
    {
        var adb = new CleanupItem
        {
            Key = "dev:android-adb",
            Path = @"C:\fake\Android\Sdk\platform-tools\adb.exe",
            DisplayName = "adb.exe",
            GroupName = "Android SDK",
            Category = CleanupCategory.Cache,
            Risk = CleanupRisk.Low,
            Target = CleanupTarget.File,
            SizeBytes = 1_000_000,
            InUse = true,
            BlockingProcesses =
            [
                new RunningProcessInfo(@"C:\fake\Android\Sdk\platform-tools\adb.exe", "adb.exe")
            ],
            InUseAdvice = InUseAdvice.DeferStep
        };

        var (_, _, _, vm) = PlanRunTestSupport.Create(PlanRunTestSupport.TreeOf(new[] { adb }));
        await vm.AnalyzeCommand.ExecuteAsync(null);

        var leaf = vm.RootNodes.SelectMany(n => n.GetLeaves()).Single(l => l.Item.Key == "dev:android-adb");

        Assert.True(leaf.IsInUse);
        Assert.True(leaf.HasBlockingProcesses);
        Assert.Contains("adb.exe", leaf.BlockingText, StringComparison.Ordinal);
        Assert.Contains("отложите", leaf.InUseAdviceText, StringComparison.Ordinal);
    }
}
