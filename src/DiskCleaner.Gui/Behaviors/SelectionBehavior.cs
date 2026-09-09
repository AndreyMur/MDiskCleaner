using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DiskCleaner.Gui.Behaviors;

/// <summary>
/// Поведение: клик левой кнопкой по «пустому» месту (не по строке/элементу)
/// снимает текущее выделение. Вешается через attached-свойство на TreeView,
/// DataGrid и ListBox.
/// </summary>
public static class SelectionBehavior
{
    public static readonly DependencyProperty DeselectOnEmptyClickProperty =
        DependencyProperty.RegisterAttached(
            "DeselectOnEmptyClick",
            typeof(bool),
            typeof(SelectionBehavior),
            new PropertyMetadata(false, OnDeselectOnEmptyClickChanged));

    public static bool GetDeselectOnEmptyClick(DependencyObject obj) =>
        (bool)obj.GetValue(DeselectOnEmptyClickProperty);

    public static void SetDeselectOnEmptyClick(DependencyObject obj, bool value) =>
        obj.SetValue(DeselectOnEmptyClickProperty, value);

    private static void OnDeselectOnEmptyClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        if (Equals(e.NewValue, true))
            element.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        else
            element.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement owner)
            return;

        var start = e.OriginalSource as DependencyObject;
        if (start is null || IsInsideItem(start, owner))
            return;

        switch (owner)
        {
            case DataGrid grid:
                grid.SelectedItem = null;
                break;
            case ListBox listBox:
                listBox.SelectedIndex = -1;
                break;
            case TreeView tree:
                UnselectTree(tree);
                break;
        }
    }

    /// <summary>Есть ли среди визуальных предков кликнутого элемента строка/элемент-контейнер или служебная часть.</summary>
    private static bool IsInsideItem(DependencyObject? current, UIElement owner)
    {
        while (current is not null && !ReferenceEquals(current, owner))
        {
            if (current is ScrollBar || current is DataGridColumnHeader || current is DataGridRowHeader)
                return true;

            Type? containerType = owner switch
            {
                TreeView _ => typeof(TreeViewItem),
                DataGrid _ => typeof(DataGridRow),
                ListBox _ => typeof(ListBoxItem),
                _ => null
            };

            if (containerType is not null && containerType.IsInstanceOfType(current))
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static void UnselectTree(TreeView tree)
    {
        if (tree.SelectedItem is null)
            return;

        if (tree.ItemContainerGenerator.ContainerFromItem(tree.SelectedItem) is TreeViewItem item)
            item.IsSelected = false;
    }
}
