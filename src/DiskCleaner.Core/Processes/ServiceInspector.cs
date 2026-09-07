using System.Management;

namespace DiskCleaner.Core.Processes;

/// <summary>
/// Собирает зарегистрированные Windows-службы с путями исполняемых файлов. Основной
/// источник — WMI (Win32_Service.PathName); при недоступности WMI выполняется деградация
/// на <see cref="System.ServiceProcess.ServiceController"/> без путей (тогда проверка
/// «используется службой» для каталога кандидата вернёт «нет совпадений»).
/// </summary>
public sealed class ServiceInspector : IServiceInspector
{
    public IReadOnlyList<RegisteredServiceInfo> GetServices()
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

        return EnumerateViaServiceController();
    }

    private static List<RegisteredServiceInfo> TryQueryWmi()
    {
        var result = new List<RegisteredServiceInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, PathName FROM Win32_Service");
        foreach (var obj in searcher.Get())
        {
            using var instance = (ManagementObject)obj;
            var name = instance["Name"] as string;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            result.Add(new RegisteredServiceInfo(
                name,
                ParseExecutablePath(instance["PathName"] as string)));
        }

        return result;
    }

    private static List<RegisteredServiceInfo> EnumerateViaServiceController()
    {
        var result = new List<RegisteredServiceInfo>();
        System.ServiceProcess.ServiceController[] services;
        try
        {
            services = System.ServiceProcess.ServiceController.GetServices();
        }
        catch
        {
            return result;
        }

        foreach (var controller in services)
        {
            try
            {
                result.Add(new RegisteredServiceInfo(controller.ServiceName, null));
            }
            catch
            {
            }
            finally
            {
                controller.Dispose();
            }
        }

        return result;
    }

    /// <summary>
    /// Извлекает путь к исполняемому файлу из командной строки службы (PathName):
    /// учитываются кавычки вокруг пути и хвостовые аргументы («"C:\…\svc.exe" -k run»).
    /// </summary>
    private static string? ParseExecutablePath(string? pathName)
    {
        if (string.IsNullOrWhiteSpace(pathName))
        {
            return null;
        }

        var value = pathName.Trim();
        if (value.StartsWith('"'))
        {
            var endQuote = value.IndexOf('"', 1);
            return endQuote > 1 ? value[1..endQuote] : null;
        }

        var spaceIndex = value.IndexOf(' ');
        return spaceIndex > 0 ? value[..spaceIndex] : value;
    }
}
