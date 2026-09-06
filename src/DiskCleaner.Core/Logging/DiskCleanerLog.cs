using System.Text;
using Serilog;

namespace DiskCleaner.Core.Logging;

public static class DiskCleanerLog
{
    public static string DefaultLogDirectory =>
        Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "DiskCleaner",
            "logs");

    public static void Initialize(string? directory = null)
    {
        var logDirectory = directory ?? DefaultLogDirectory;
        Directory.CreateDirectory(logDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logDirectory, "diskcleaner-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                encoding: Encoding.UTF8,
                shared: true,
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
