using System.Windows;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui;

public partial class LeftoverScanWindow : Window
{
    private readonly LeftoverScanViewModel _viewModel;

    public LeftoverScanWindow(LeftoverScanViewModel? viewModel = null)
    {
        InitializeComponent();
        _viewModel = viewModel ?? new LeftoverScanViewModel();
        DataContext = _viewModel;
    }

    internal LeftoverScanViewModel ViewModel => _viewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Groups.Count == 0 && !_viewModel.IsBusy)
        {
            await _viewModel.ScanCommand.ExecuteAsync(null);
        }
    }

    private async void ExclusionsButton_Click(object sender, RoutedEventArgs e)
    {
        var manager = new ExclusionsWindow(_viewModel.ExclusionsService)
        {
            Owner = this
        };

        if (manager.ShowDialog() == true)
        {
            await _viewModel.RefreshPlanAsync();
        }
    }
}
