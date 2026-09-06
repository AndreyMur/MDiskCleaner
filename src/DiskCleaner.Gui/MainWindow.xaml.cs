using System.Windows;
using System.Windows.Controls;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel? viewModel = null)
    {
        InitializeComponent();
        _viewModel = viewModel ?? new MainViewModel();
        DataContext = _viewModel;
    }

    internal MainViewModel ViewModel => _viewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.ScheduledScanRequested)
        {
            await _viewModel.RunScheduledAnalysisAsync();
        }
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeItemViewModel node)
        {
            _viewModel.SelectedNode = node;
        }
    }
}
