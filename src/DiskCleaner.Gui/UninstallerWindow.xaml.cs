using System.Windows;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui;

public partial class UninstallerWindow : Window
{
    private readonly UninstallerViewModel _viewModel;

    public UninstallerWindow(UninstallerViewModel? viewModel = null)
    {
        InitializeComponent();
        _viewModel = viewModel ?? new UninstallerViewModel();
        DataContext = _viewModel;
    }

    internal UninstallerViewModel ViewModel => _viewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasPlan && !_viewModel.IsBusy)
        {
            await _viewModel.ScanCommand.ExecuteAsync(null);
        }
    }
}
