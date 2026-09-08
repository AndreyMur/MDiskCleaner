using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Logging;

namespace DiskCleaner.Elevated;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        DiskCleanerLog.Initialize();
        Serilog.Log.Information("DiskCleaner.Elevated started: {Arguments}", string.Join(' ', args));

        if (!TryParseArgs(args, out var scenarioPath, out var journalPath))
        {
            Console.Error.WriteLine("Использование: DiskCleaner.Elevated.exe --scenario <файл> --journal <файл>");
            return 2;
        }

        try
        {
            var scenario = ElevatedJson.ReadScenario(scenarioPath);
            var runner = new ElevatedScenarioRunner();
            var journal = await runner.RunAsync(scenario);
            ElevatedJson.WriteJournal(journalPath, journal);

            AuditSteps(scenario, journal);
            Serilog.Log.Information(
                "DiskCleaner.Elevated finished: {Ok}/{Total} steps succeeded",
                journal.Results.Count(r => r.Success),
                journal.Results.Count);

            return journal.AllSucceeded ? 0 : 1;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Elevated scenario failed");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool TryParseArgs(string[] args, out string scenario, out string journal)
    {
        scenario = string.Empty;
        journal = string.Empty;

        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--scenario":
                    scenario = args[++i];
                    break;
                case "--journal":
                    journal = args[++i];
                    break;
            }
        }

        return File.Exists(scenario) && journal.Length > 0;
    }

    /// <summary>
    /// Журнал каждого шага elevated-сценария (FR-5.13): время, объект, размер, тип операции,
    /// результат, exit-код, ошибка — пишется в общий журнал %LOCALAPPDATA%\DiskCleaner\logs.
    /// </summary>
    private static void AuditSteps(ElevatedScenario scenario, ElevatedJournal journal)
    {
        for (var i = 0; i < scenario.Steps.Count && i < journal.Results.Count; i++)
        {
            var step = scenario.Steps[i];
            var result = journal.Results[i];
            var objectName = step.Path
                ?? step.FileName
                ?? step.RegistrySubKeyPath
                ?? step.Id;

            Serilog.Log.Information(
                "Elevated action: time={Time:yyyy-MM-dd HH:mm:ss} object={Object} sizeBytes={Size} op={Operation} result={Outcome} exitCode={ExitCode} freedBytes={Freed} error={Error}",
                DateTime.Now,
                objectName,
                result.FreedBytes,
                step.Kind,
                result.Success ? "success" : "failed",
                result.ExitCode,
                result.FreedBytes,
                result.Error ?? result.Note);
        }
    }
}
