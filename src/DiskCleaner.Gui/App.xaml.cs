using System.Windows;

namespace DiskCleaner.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DiskCleaner.Core.Logging.DiskCleanerLog.Initialize();

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
