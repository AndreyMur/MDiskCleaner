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

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeItemViewModel node)
        {
            _viewModel.SelectedNode = node;
        }
    }
}
