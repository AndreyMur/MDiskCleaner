using System.Diagnostics;
using System.Management;

namespace DiskCleaner.Core.Processes;

public sealed class ProcessInspector : IProcessInspector
{
    /// <summary>
    /// Собирает запущенные процессы с путями исполняемых файлов. Основной источник — WMI
    /// (Win32_Process.ExecutablePath), который видит процессы любых прав в отличие от MainModule;
    /// при недоступности WMI выполняется деградация на System.Diagnostics.Process.
    /// </summary>
    public IReadOnlyList<RunningProcessInfo> GetRunningProcesses()
    {
        try
        {
            var viaWmi = TryQueryWmi();
            if (viaWmi.Count > 0)
            {
                return viaWmi;
            }
        }
        catch
        {
            // Деградация ниже.
        }

        return EnumerateViaProcessApi();
    }

    private static List<RunningProcessInfo> TryQueryWmi()
    {
        var result = new List<RunningProcessInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ExecutablePath, Name FROM Win32_Process");
        foreach (var obj in searcher.Get())
        {
            using var instance = (ManagementObject)obj;
            var executablePath = instance["ExecutablePath"] as string;
            var name = instance["Name"] as string;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                continue;
            }

            result.Add(new RunningProcessInfo(
                executablePath,
                string.IsNullOrWhiteSpace(name)
                    ? Path.GetFileNameWithoutExtension(executablePath)
                    : name));
        }

        return result;
    }

    private static List<RunningProcessInfo> EnumerateViaProcessApi()
    {
        var result = new List<RunningProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var fileName = process.MainModule?.FileName;
                if (string.IsNullOrEmpty(fileName))
                {
                    continue;
                }

                result.Add(new RunningProcessInfo(fileName, process.ProcessName));
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }
}
