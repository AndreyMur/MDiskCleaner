using System.Windows;
using DiskCleaner.Core.Scheduling;

namespace DiskCleaner.Gui;

public partial class App : Application
{
    public static bool ScheduledScanRequested { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ScheduledScanRequested = e.Args.Contains(
            SchedulerService.ScanScheduledArgument,
            StringComparer.OrdinalIgnoreCase);

        DiskCleaner.Core.Logging.DiskCleanerLog.Initialize();

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
