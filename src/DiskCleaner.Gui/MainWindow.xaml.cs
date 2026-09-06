using System.Windows;
using System.Windows.Controls;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

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
