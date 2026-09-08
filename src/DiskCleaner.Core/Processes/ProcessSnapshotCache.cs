namespace DiskCleaner.Core.Processes;

/// <summary>
/// Кэшируемый на время плана список запущенных процессов (FR-5.7): WMI-опрос
/// (<c>Win32_Process.ExecutablePath</c>) выполняется один раз, все последующие обращения
/// возвращают тот же снимок. Один экземпляр можно разделить между исполнителями,
/// работающими над одним планом (кэширование — это и есть гарантия согласованной
/// проверки занятости на весь прогон).
/// </summary>
public sealed class ProcessSnapshotCache
{
    private readonly IProcessInspector _inspector;
    private IReadOnlyList<RunningProcessInfo>? _processes;

    public ProcessSnapshotCache(IProcessInspector? inspector = null)
    {
        _inspector = inspector ?? new ProcessInspector();
    }

    /// <summary>Снимок процессов плана; первый доступ выполняет опрос, повторные — из кэша.</summary>
    public IReadOnlyList<RunningProcessInfo> Processes => _processes ??= _inspector.GetRunningProcesses();

    /// <summary>Сброс кэша: следующий доступ выполнит новый опрос процессов (новый план).</summary>
    public void Invalidate()
    {
        _processes = null;
    }
}
