using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

/// <summary>
/// Модуль 04, фаза 4 (milestone «Фаза 24») — перезагрузка (FR-4.12): система только запрашивает
/// перезагрузку по завершении, никогда не выполняет <c>msiexec /restart</c> принудительно;
/// пометка «требует перезагрузки» формируется по exit-коду 3010.
/// </summary>
public class UninstallRebootTests
{
    [Fact]
    public void ExecutionReport_FlagsRebootRequired_WhenMsiexecReturns3010()
    {
        var app = App("Windows SDK Component");
        var item = new UninstallExecutionItem(app, MsiexecCommand());

        var report = new UninstallExecutionReport
        {
            Entries =
            [
                Entry(item, UninstallExecutionOutcome.RebootRequired, UninstallExitCodes.RebootRequired),
                Entry(new UninstallExecutionItem(App("Other"), MsiexecCommand()), UninstallExecutionOutcome.Uninstalled, 0)
            ],
            StartedAt = DateTime.UtcNow,
            FinishedAt = DateTime.UtcNow
        };

        Assert.Equal(1, report.RebootRequiredCount);
        Assert.True(report.NeedsReboot);

        var request = UninstallRebootRequest.FromUninstallReport(report);
        Assert.True(request.Recommended);
        Assert.Contains("Windows SDK Component", request.RequiringItems);
        Assert.Contains("Перезагрузить", request.PromptMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionReport_WithoutRebootRequired_DoesNotRecommendReboot()
    {
        var report = new UninstallExecutionReport
        {
            Entries =
            [
                Entry(new UninstallExecutionItem(App("A"), MsiexecCommand()), UninstallExecutionOutcome.Uninstalled, 0)
            ],
            StartedAt = DateTime.UtcNow,
            FinishedAt = DateTime.UtcNow
        };

        Assert.False(report.NeedsReboot);
        var request = UninstallRebootRequest.FromUninstallReport(report);
        Assert.False(request.Recommended);
        Assert.Empty(request.RequiringItems);
    }

    [Fact]
    public void CleanReport_MarksRebootRequired_AndBuildsRequest()
    {
        var item = new CleanupItem
        {
            Key = "app:x:1",
            DisplayName = "Product A",
            Category = CleanupCategory.InstalledApp,
            CommandOnly = true
        };

        var report = new CleanReport
        {
            Entries =
            [
                new CleanEntry(item, CleanOutcome.RebootRequired, 0, "Требуется перезагрузка (код 3010).")
            ]
        };

        var request = UninstallRebootRequest.FromCleanReport(report);
        Assert.True(request.Recommended);
        Assert.Equal("Product A", Assert.Single(request.RequiringItems));
    }

    [Fact]
    public void SdkBulkReport_MarksNeedsReboot_WhenComponentReturns3010()
    {
        var report = new SdkBulkUninstallReport
        {
            DisplayVersion = "10.1.19041.5609",
            Components =
            [
                new SdkComponentResult(
                    "aaaa",
                    "Windows SDK Component",
                    "10.1.19041.5609",
                    1,
                    UninstallExecutionOutcome.RebootRequired,
                    UninstallExitCodes.RebootRequired,
                    "требуется перезагрузка")
            ]
        };

        Assert.True(report.NeedsReboot);
        var request = UninstallRebootRequest.FromSdkBulkReport(report);
        Assert.True(request.Recommended);
        Assert.Contains("Windows SDK Component", request.RequiringItems);
    }

    [Fact]
    public void Parser_NeverBuildsMsiexecRestart_AlwaysNorestart()
    {
        var parser = new UninstallStringParser();
        var command = parser.Parse("MsiExec.exe /X{11111111-1111-1111-1111-111111111111}");

        Assert.NotNull(command);
        Assert.Equal(UninstallerKind.Msi, command.Kind);
        Assert.Contains("/norestart", command.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/restart", command.Arguments, StringComparison.OrdinalIgnoreCase);
    }

    private static UninstallExecutionEntry Entry(
        UninstallExecutionItem item,
        UninstallExecutionOutcome outcome,
        int? exitCode) =>
        new(item, outcome, exitCode, "note", DateTime.UtcNow);

    private static UninstallCommand MsiexecCommand() =>
        new(UninstallerKind.Msi, "msiexec.exe", "/x {11111111-1111-1111-1111-111111111111} /qn /norestart", Silent: true);

    private static InstalledApp App(string name) => new()
    {
        ProductCode = "{11111111-1111-1111-1111-111111111111}",
        ScopeKey = nameof(InstalledAppScope.LocalMachine64),
        DisplayName = name,
        Publisher = "ACME",
        UninstallString = "MsiExec.exe /I{11111111-1111-1111-1111-111111111111}"
    };
}
