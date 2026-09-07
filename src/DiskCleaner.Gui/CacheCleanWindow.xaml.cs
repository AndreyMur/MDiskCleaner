using System.Windows;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui;

public partial class CacheCleanWindow : Window
{
    private readonly CacheCleanViewModel _viewModel;

    public CacheCleanWindow(CacheCleanViewModel? viewModel = null)
    {
        InitializeComponent();
        _viewModel = viewModel ?? new CacheCleanViewModel();
        DataContext = _viewModel;
    }

    internal CacheCleanViewModel ViewModel => _viewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Groups.Count == 0 && !_viewModel.IsBusy)
        {
            await _viewModel.AnalyzeCommand.ExecuteAsync(null);
        }
    }
}
