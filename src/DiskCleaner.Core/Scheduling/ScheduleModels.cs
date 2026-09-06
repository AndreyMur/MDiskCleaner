namespace DiskCleaner.Core.Scheduling;

/// <summary>
/// Расписание «Анализ по расписанию». Запуск анализа не выполняет удалений:
/// принцип подтверждения сохраняется — очистка происходит только после явного выбора пользователя.
/// </summary>
public sealed class CleanSchedule
{
    public bool Enabled { get; set; } = true;

    public DayOfWeek Day { get; set; } = DayOfWeek.Sunday;

    public TimeSpan Time { get; set; } = new(10, 0, 0);
}

public sealed class ScheduleState
{
    public bool Exists { get; init; }

    public CleanSchedule? Schedule { get; init; }

    public string? Error { get; init; }
}
