using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Scheduling;

namespace DiskCleaner.Tests;

public class SchedulerTests
{
    private const string SampleTaskXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>DiskCleaner</Description></RegistrationInfo>
          <Triggers>
            <CalendarTrigger>
              <StartBoundary>2020-01-06T09:30:00</StartBoundary>
              <Enabled>true</Enabled>
              <ScheduleByWeek>
                <WeeksInterval>1</WeeksInterval>
                <DaysOfWeek><Tuesday /></DaysOfWeek>
              </ScheduleByWeek>
            </CalendarTrigger>
          </Triggers>
          <Actions Context="Author"><Exec><Command>C:\app\DiskCleaner.exe</Command><Arguments>--scan-scheduled</Arguments></Exec></Actions>
        </Task>
        """;

    [Fact]
    public async Task SaveAsync_RunsSchtasksCreate_WithXmlDefinition()
    {
        var runner = new FakeCommandRunner((command, _) =>
            Task.FromResult(new CommandResult(0, string.Empty, false)));

        var scheduler = new SchedulerService(runner: runner, taskName: "DiskCleanerAutoScanTest");

        var error = await scheduler.SaveAsync(new CleanSchedule
        {
            Enabled = true,
            Day = DayOfWeek.Tuesday,
            Time = new TimeSpan(9, 30, 0)
        }, typeof(SchedulerTests).Assembly.Location);

        Assert.Null(error);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Contains("/Create", invocation.Arguments);
        Assert.Contains("/TN \"DiskCleanerAutoScanTest\"", invocation.Arguments);
        Assert.Contains("/XML", invocation.Arguments);
        Assert.Contains("/F", invocation.Arguments);
    }

    [Fact]
    public async Task SaveAsync_DisabledSchedule_DeletesTask()
    {
        var runner = new FakeCommandRunner((command, _) =>
            Task.FromResult(new CommandResult(0, string.Empty, false)));

        var scheduler = new SchedulerService(runner: runner, taskName: "DiskCleanerAutoScanTest");

        var error = await scheduler.SaveAsync(new CleanSchedule { Enabled = false }, @"C:\app\DiskCleaner.exe");

        Assert.Null(error);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Contains("/Delete", invocation.Arguments);
    }

    [Fact]
    public async Task QueryAsync_ParsesDayAndTimeFromXml()
    {
        var runner = new FakeCommandRunner((command, _) =>
            Task.FromResult(new CommandResult(0, SampleTaskXml, false)));

        var scheduler = new SchedulerService(runner: runner, taskName: "DiskCleanerAutoScanTest");

        var state = await scheduler.QueryAsync();

        Assert.True(state.Exists);
        Assert.NotNull(state.Schedule);
        Assert.Equal(DayOfWeek.Tuesday, state.Schedule!.Day);
        Assert.Equal(new TimeSpan(9, 30, 0), state.Schedule.Time);
    }

    [Fact]
    public async Task QueryAsync_MissingTask_ReturnsNotExists()
    {
        var runner = new FakeCommandRunner((command, _) =>
            Task.FromResult(new CommandResult(1, "ERROR: The system cannot find the file specified.", false)));

        var scheduler = new SchedulerService(runner: runner, taskName: "DiskCleanerAutoScanTest");

        var state = await scheduler.QueryAsync();

        Assert.False(state.Exists);
        Assert.Null(state.Schedule);
    }

    [Fact]
    public async Task ScheduledTask_RealLifecycle_CreateQueryDelete()
    {
        var schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        if (!File.Exists(schtasks))
        {
            return;
        }

        var scheduler = new SchedulerService(
            taskName: "DiskCleanerTests_" + Guid.NewGuid().ToString("N"));

        try
        {
            var app = typeof(SchedulerTests).Assembly.Location;
            var error = await scheduler.SaveAsync(new CleanSchedule
            {
                Enabled = true,
                Day = DayOfWeek.Friday,
                Time = new TimeSpan(23, 45, 0)
            }, app);

            Assert.Null(error);

            var state = await scheduler.QueryAsync();
            Assert.True(state.Exists, "Задача планировщика должна существовать после создания.");
            Assert.NotNull(state.Schedule);
        }
        finally
        {
            await scheduler.DeleteAsync();
        }
    }
}
