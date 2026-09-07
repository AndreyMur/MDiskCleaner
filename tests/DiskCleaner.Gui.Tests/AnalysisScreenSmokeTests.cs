using System.Windows;
using System.Windows.Threading;
using DiskCleaner.Gui;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

/// <summary>Выполняет тест на STA-потоке с работающим Dispatcher (для WPF-объектов).</summary>
internal static class StaRunner
{
    public static void Run(Func<Task> action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            var frame = new DispatcherFrame();
            var task = action();
            task.ContinueWith(
                completed =>
                {
                    if (completed.IsFaulted)
                    {
                        error = completed.Exception?.GetBaseException();
                    }

                    frame.Continue = false;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (error is not null)
        {
            throw error;
        }
    }
}

public class AnalysisScreenSmokeTests
{
    [Fact]
    public void MainWindow_RendersSyntheticPlan_Smoke()
    {
        StaRunner.Run(async () =>
        {
            var fake = new FakeAnalysisCoordinator
            {
                Handler = _ => SamplePlan.Build()
            };
            var vm = MainViewModelFactory.Create(fake);

            await vm.AnalyzeCommand.ExecuteAsync(null);
            foreach (var leaf in vm.RootNodes.SelectMany(n => n.GetLeaves()).Take(2))
            {
                leaf.IsChecked = true;
            }

            var window = new MainWindow(vm)
            {
                Width = 1280,
                Height = 760
            };

            window.Show();
            await Task.Delay(50);
            window.UpdateLayout();

            Assert.Same(vm, window.DataContext);
            Assert.Same(vm, window.ViewModel);
            Assert.NotEmpty(window.ViewModel.RootNodes);
            Assert.NotEmpty(window.ViewModel.PlanRows);

            window.Close();
        });
    }
}
