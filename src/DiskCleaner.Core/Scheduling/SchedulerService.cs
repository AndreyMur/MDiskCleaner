using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DiskCleaner.Core.Commanding;

namespace DiskCleaner.Core.Scheduling;

/// <summary>
/// Управление расписанием через штатный планировщик Windows (schtasks.exe).
/// Создаваемая задача запускает приложение с аргументом --scan-scheduled,
/// который выполняет только анализ — никаких «молчаливых» удалений.
/// </summary>
public sealed partial class SchedulerService
{
    public const string DefaultTaskName = "DiskCleanerAutoScan";

    public const string ScanScheduledArgument = "--scan-scheduled";

    private readonly ICommandRunner _runner;
    private readonly string _taskName;
    private readonly string _schtasksPath;
    private readonly string _workDirectory;

    public SchedulerService(
        ICommandRunner? runner = null,
        string? taskName = null,
        string? schtasksPath = null,
        string? workDirectory = null)
    {
        _runner = runner ?? new ProcessCommandRunner();
        _taskName = taskName ?? DefaultTaskName;
        _schtasksPath = schtasksPath ?? ResolveSchtasksPath();
        _workDirectory = workDirectory ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "DiskCleaner",
            "scheduler");
    }

    public string TaskName => _taskName;

    public async Task<string?> SaveAsync(
        CleanSchedule schedule,
        string applicationPath,
        CancellationToken cancellationToken = default)
    {
        if (!schedule.Enabled)
        {
            return await DeleteAsync(cancellationToken);
        }

        if (!File.Exists(applicationPath))
        {
            return $"Исполняемый файл не найден: {applicationPath}";
        }

        var xmlPath = await WriteTaskXmlAsync(schedule, applicationPath);
        var result = await _runner.RunAsync(new CommandDefinition
        {
            FileName = _schtasksPath,
            Arguments = $"/Create /TN \"{_taskName}\" /XML \"{xmlPath}\" /F",
            TimeoutSec = 30
        }, cancellationToken);

        TryDelete(xmlPath);

        if (result.TimedOut)
        {
            return "Планировщик не ответил вовремя.";
        }

        if (result.ExitCode != 0)
        {
            return $"schtasks завершился с кодом {result.ExitCode}: {Truncate(result.Output, 300)}";
        }

        return null;
    }

    public async Task<string?> DeleteAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(new CommandDefinition
        {
            FileName = _schtasksPath,
            Arguments = $"/Delete /TN \"{_taskName}\" /F",
            TimeoutSec = 30
        }, cancellationToken);

        if (result.ExitCode is 0 or 1)
        {
            return null;
        }

        return $"schtasks /Delete завершился с кодом {result.ExitCode}: {Truncate(result.Output, 300)}";
    }

    public async Task<ScheduleState> QueryAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(new CommandDefinition
        {
            FileName = _schtasksPath,
            Arguments = $"/Query /TN \"{_taskName}\" /XML",
            TimeoutSec = 30
        }, cancellationToken);

        if (result.TimedOut || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            return new ScheduleState { Exists = false };
        }

        var schedule = TryParseTaskXml(result.Output);
        return schedule is null
            ? new ScheduleState { Exists = false, Error = "Не удалось разобрать задачу планировщика." }
            : new ScheduleState { Exists = true, Schedule = schedule };
    }

    private async Task<string> WriteTaskXmlAsync(CleanSchedule schedule, string applicationPath)
    {
        Directory.CreateDirectory(_workDirectory);
        var path = Path.Combine(_workDirectory, "scheduled-scan.xml");

        var xml = BuildTaskXml(schedule, applicationPath);
        await File.WriteAllTextAsync(path, xml, Encoding.Unicode);
        return path;
    }

    private string BuildTaskXml(CleanSchedule schedule, string applicationPath)
    {
        var start = $"2020-01-06T{schedule.Time.Hours:00}:{schedule.Time.Minutes:00}:00";
        var day = DayCode(schedule.Day);
        var command = Path.GetFullPath(applicationPath);

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>DiskCleaner: запуск анализа по расписанию (удаление — только по подтверждению).</Description>
              </RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>{start}</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByWeek>
                    <WeeksInterval>1</WeeksInterval>
                    <DaysOfWeek>
                      <{day} />
                    </DaysOfWeek>
                  </ScheduleByWeek>
                </CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{EscapeXml(command)}</Command>
                  <Arguments>{ScanScheduledArgument}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private CleanSchedule? TryParseTaskXml(string output)
    {
        var xml = output.Trim();
        if (!xml.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) &&
            !xml.Contains("<Task", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var timeMatch = StartBoundaryRegex().Match(xml);
        var dayMatches = DayOfWeekRegex().Matches(xml);

        var schedule = new CleanSchedule();
        if (timeMatch.Success &&
            TimeSpan.TryParseExact(timeMatch.Groups[1].Value, @"hh\:mm", CultureInfo.InvariantCulture, out var time))
        {
            schedule.Time = time;
        }

        foreach (Match match in dayMatches)
        {
            var day = ParseDayCode(match.Groups[1].Value);
            if (day is not null)
            {
                schedule.Day = day.Value;
                break;
            }
        }

        return schedule;
    }

    private static DayOfWeek? ParseDayCode(string code) => code.ToLowerInvariant() switch
    {
        "monday" => DayOfWeek.Monday,
        "tuesday" => DayOfWeek.Tuesday,
        "wednesday" => DayOfWeek.Wednesday,
        "thursday" => DayOfWeek.Thursday,
        "friday" => DayOfWeek.Friday,
        "saturday" => DayOfWeek.Saturday,
        "sunday" => DayOfWeek.Sunday,
        _ => null
    };

    private static string DayCode(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "Monday",
        DayOfWeek.Tuesday => "Tuesday",
        DayOfWeek.Wednesday => "Wednesday",
        DayOfWeek.Thursday => "Thursday",
        DayOfWeek.Friday => "Friday",
        DayOfWeek.Saturday => "Saturday",
        _ => "Sunday"
    };

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
             .Replace("\"", "&quot;").Replace("'", "&apos;");

    private static string ResolveSchtasksPath()
    {
        var candidate = Path.Combine(System.Environment.SystemDirectory, "schtasks.exe");
        return File.Exists(candidate) ? candidate : "schtasks.exe";
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

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";

    [GeneratedRegex(@"<StartBoundary>\d{4}-\d{2}-\d{2}T(\d{2}:\d{2}):\d{2}</StartBoundary>")]
    private static partial Regex StartBoundaryRegex();

    [GeneratedRegex(@"<(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)\s*/>", RegexOptions.IgnoreCase)]
    private static partial Regex DayOfWeekRegex();
}
