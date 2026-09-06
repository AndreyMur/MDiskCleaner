using System.Diagnostics;

namespace DiskCleaner.Core.Processes;

public sealed class ProcessInspector : IProcessInspector
{
    public IReadOnlyList<RunningProcessInfo> GetRunningProcesses()
    {
        var processes = new List<RunningProcessInfo>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var fileName = process.MainModule?.FileName;
                if (string.IsNullOrEmpty(fileName))
                {
                    continue;
                }

                processes.Add(new RunningProcessInfo(fileName, process.ProcessName));
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return processes;
    }
}
