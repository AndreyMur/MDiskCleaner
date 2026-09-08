using System.ComponentModel;
using System.Diagnostics;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Tests;

public class ElevatedProcessE2eTests
{
    [Fact]
    public async Task ElevatedExe_ExecutesScenario_WritesJournal()
    {
        var exePath = FindElevatedExe();
        if (!File.Exists(exePath))
        {
            return;
        }

        using var root = new TempRoot();
        var dir = root.Combine("scenario-delete");
        root.CreateFile("scenario-delete\\file.bin", 700);

        var scenarioPath = Path.Combine(root.Path, "scenario.json");
        var journalPath = Path.Combine(root.Path, "journal.json");
        ElevatedJson.WriteScenario(scenarioPath, new ElevatedScenario
        {
            Steps =
            [
                new ElevatedStep
                {
                    Id = "delete1",
                    Kind = ElevatedStepKind.DeletePath,
                    Path = dir,
                    Target = CleanupTarget.Directory
                }
            ]
        });

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"\"--scenario\" \"{scenarioPath}\" \"--journal\" \"{journalPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = StartOrSkip(exePath, startInfo);
        if (process is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);

        Assert.True(File.Exists(journalPath), "Журнал не создан elevated-процессом.");
        var journal = ElevatedJson.ReadJournal(journalPath);

        Assert.False(Directory.Exists(dir));
        var result = Assert.Single(journal.Results);
        Assert.Equal("delete1", result.Id);
        Assert.True(result.Success);
        Assert.Equal(700, result.FreedBytes);
    }

    private static Process? StartOrSkip(string exePath, ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            // manifest requireAdministrator (FR-5.10/5.11): запуск без повышения невозможен —
            // интеграционный тест исполняется только из elevated-сессии (эталонная машина).
            return null;
        }
    }

    private static string? FindElevatedExe()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "Elevated", "DiskCleaner.Elevated.exe");
        if (File.Exists(local))
        {
            return local;
        }

        var root = FindRepoRoot(new DirectoryInfo(AppContext.BaseDirectory));
        if (root is null)
        {
            return null;
        }

        var config = Path.GetFileName(Path.GetDirectoryName(AppContext.BaseDirectory));
        var candidate = Path.Combine(root.FullName, "src", "DiskCleaner.Elevated", "bin", config ?? "Debug", "net8.0", "DiskCleaner.Elevated.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static DirectoryInfo? FindRepoRoot(DirectoryInfo start)
    {
        var current = start;
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "DiskCleaner.sln")))
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }
}
