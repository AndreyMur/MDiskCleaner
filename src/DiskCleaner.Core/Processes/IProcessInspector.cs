namespace DiskCleaner.Core.Processes;

public interface IProcessInspector
{
    IReadOnlyList<RunningProcessInfo> GetRunningProcesses();
}
