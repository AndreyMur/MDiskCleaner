using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

public class ElevatedScenarioRunnerTests
{
    [Fact]
    public async Task DeletePath_DeletesDirectory_AndReportsFreedBytes()
    {
        using var root = new TempRoot();
        var dir = root.Combine("elevated-delete");
        root.CreateFile("elevated-delete\\a.bin", 1500);

        var runner = new ElevatedScenarioRunner();
        var journal = await runner.RunAsync(new ElevatedScenario
        {
            Steps =
            [
                new ElevatedStep
                {
                    Id = "step1",
                    Kind = ElevatedStepKind.DeletePath,
                    Path = dir,
                    Target = CleanupTarget.Directory
                }
            ]
        });

        Assert.False(Directory.Exists(dir));
        var result = Assert.Single(journal.Results);
        Assert.True(result.Success);
        Assert.Equal(1500, result.FreedBytes);
    }

    [Fact]
    public async Task DeletePath_NonexistentPath_IsError()
    {
        var runner = new ElevatedScenarioRunner();
        var missing = Path.Combine(Path.GetTempPath(), "DiskCleaner.Tests", "missing-" + Guid.NewGuid().ToString("N"));

        var journal = await runner.RunAsync(new ElevatedScenario
        {
            Steps =
            [
                new ElevatedStep { Id = "s", Kind = ElevatedStepKind.DeletePath, Path = missing, Target = CleanupTarget.Directory }
            ]
        });

        var result = Assert.Single(journal.Results);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task RunProcess_SuccessfulCommand_RecordsZeroExitCode()
    {
        var runner = new ElevatedScenarioRunner();
        var journal = await runner.RunAsync(new ElevatedScenario
        {
            Steps =
            [
                new ElevatedStep
                {
                    Id = "cmd",
                    Kind = ElevatedStepKind.RunProcess,
                    FileName = "cmd.exe",
                    Arguments = "/c exit 0"
                }
            ]
        });

        var result = Assert.Single(journal.Results);
        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunProcess_MsiexecNotInstalledCode_IsIdempotentSuccess()
    {
        var runner = new ElevatedScenarioRunner();
        var journal = await runner.RunAsync(new ElevatedScenario
        {
            Steps =
            [
                new ElevatedStep
                {
                    Id = "cmd",
                    Kind = ElevatedStepKind.RunProcess,
                    FileName = "cmd.exe",
                    Arguments = "/c exit 1605",
                    ExitCodes = ExitCodePolicy.Msiexec
                }
            ]
        });

        var result = Assert.Single(journal.Results);
        Assert.True(result.Success);
        Assert.Equal(1605, result.ExitCode);
        Assert.Contains("не установлен", result.Note);
    }

    [Fact]
    public async Task RunProcess_RebootRequiredCode_FlagsReboot()
    {
        var runner = new ElevatedScenarioRunner();
        var journal = await runner.RunAsync(new ElevatedScenario
        {
            Steps =
            [
                new ElevatedStep
                {
                    Id = "cmd",
                    Kind = ElevatedStepKind.RunProcess,
                    FileName = "cmd.exe",
                    Arguments = "/c exit 3010",
                    ExitCodes = ExitCodePolicy.Msiexec
                }
            ]
        });

        var result = Assert.Single(journal.Results);
        Assert.True(result.Success);
        Assert.True(result.RebootRequired);
    }

    [Fact]
    public async Task DeleteRegistryKey_DeletesHkcuTempBranch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var guid = Guid.NewGuid().ToString("N");
        var basePath = $@"Software\DiskCleaner.Tests\{guid}";
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(basePath + @"\Sub"))
        {
            key.SetValue("DisplayName", "x");
        }

        try
        {
            var runner = new ElevatedScenarioRunner();
            var journal = await runner.RunAsync(new ElevatedScenario
            {
                Steps =
                [
                    new ElevatedStep
                    {
                        Id = "reg",
                        Kind = ElevatedStepKind.DeleteRegistryKey,
                        RegistryHive = RegistryHiveKind.CurrentUser,
                        RegistrySubKeyPath = basePath
                    }
                ]
            });

            var result = Assert.Single(journal.Results);
            Assert.True(result.Success);
            Assert.Null(Microsoft.Win32.Registry.CurrentUser.OpenSubKey(basePath));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(basePath, throwOnMissingSubKey: false);
        }
    }
}
