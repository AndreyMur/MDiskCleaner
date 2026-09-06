using System.ComponentModel;
using System.Diagnostics;

namespace DiskCleaner.Core.Elevated;

public sealed class ElevatedProcessLauncher : IElevatedRunner
{
    private readonly string _elevatedExePath;
    private readonly string _workDirectory;

    public ElevatedProcessLauncher(string? elevatedExePath = null, string? workDirectory = null)
    {
        _elevatedExePath = elevatedExePath ?? ResolveElevatedExePath();
        _workDirectory = workDirectory ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "DiskCleaner",
            "elevated");
    }

    public static string ResolveElevatedExePath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "DiskCleaner.Elevated.exe"),
            Path.Combine(baseDirectory, "Elevated", "DiskCleaner.Elevated.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var config = Path.GetFileName(Path.GetDirectoryName(baseDirectory));
        var walk = new DirectoryInfo(baseDirectory);
        while (walk is not null)
        {
            var possible = Path.Combine(walk.FullName, "src", "DiskCleaner.Elevated", "bin", config ?? string.Empty);
            foreach (var tfm in new[] { "net8.0", "net8.0-windows" })
            {
                var full = Path.Combine(possible, tfm, "DiskCleaner.Elevated.exe");
                if (File.Exists(full))
                {
                    return full;
                }
            }

            walk = walk.Parent;
        }

        return candidates[0];
    }

    public async Task<ElevatedJournal> RunAsync(
        ElevatedScenario scenario,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_workDirectory);
        var requestId = Guid.NewGuid().ToString("N");
        var scenarioPath = Path.Combine(_workDirectory, requestId + ".scenario.json");
        var journalPath = Path.Combine(_workDirectory, requestId + ".journal.json");

        try
        {
            ElevatedJson.WriteScenario(scenarioPath, scenario);

            var startInfo = new ProcessStartInfo
            {
                FileName = _elevatedExePath,
                Arguments = $"\"--scenario\" \"{scenarioPath}\" \"--journal\" \"{journalPath}\"",
                Verb = "RunAs",
                UseShellExecute = true,
                WorkingDirectory = _workDirectory
            };

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new ElevationDeclinedException("Подтверждение UAC отклонено пользователем.", ex);
            }

            await process.WaitForExitAsync(cancellationToken);

            return File.Exists(journalPath)
                ? ElevatedJson.ReadJournal(journalPath)
                : new ElevatedJournal { Results = Array.Empty<ElevatedStepResult>() };
        }
        finally
        {
            TryDelete(scenarioPath);
            TryDelete(journalPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

public sealed class ElevationDeclinedException : Exception
{
    public ElevationDeclinedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
