using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Gui.ViewModels;

public sealed class TreeItemViewModel : ObservableObject
{
    private readonly TreeItemViewModel? _parent;
    private readonly Action _leafSelectionChanged;
    private bool? _isChecked;

    public TreeItemViewModel(
        CleanupItem item,
        TreeItemViewModel? parent,
        Action leafSelectionChanged)
    {
        Item = item;
        _parent = parent;
        _leafSelectionChanged = leafSelectionChanged;
        Children = new ObservableCollection<TreeItemViewModel>(
            item.Children.Select(child => new TreeItemViewModel(child, this, leafSelectionChanged)));
    }

    public CleanupItem Item { get; }

    public ObservableCollection<TreeItemViewModel> Children { get; }

    public string DisplayName => Item.DisplayName;

    /// <summary>Корневой узел дерева — группа категории (FR-1.11).</summary>
    public bool IsCategoryRoot => _parent is null && Children.Count > 0;

    /// <summary>
    /// Сводка «число объектов» для категории (FR-1.11): показывается на корневом узле
    /// категории рядом с суммарным размером. Для остальных узлов — пустая строка.
    /// </summary>
    public string CategoryObjectsText
    {
        get
        {
            if (!IsCategoryRoot)
            {
                return string.Empty;
            }

            var count = GetLeaves().Count();
            return $"{count} {PluralObjects(count)}";
        }
    }

    public bool IsExpanded { get; set; } = true;

    public bool IsSelectable => Children.Count == 0;

    public bool CanCheck => IsSelectable ? !Item.InUse : true;

    public bool? IsChecked
    {
        get => IsSelectable ? _isChecked : ComputeAggregate(Children);
        set
        {
            if (value is not true and not false)
            {
                return;
            }

            ApplyToLeaves(value.Value);
            NotifyAncestorsOfAggregateChange();
            _leafSelectionChanged();
        }
    }

    public void ApplyToLeaves(bool value)
    {
        if (IsSelectable)
        {
            if (!CanCheck)
            {
                return;
            }

            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            OnPropertyChanged();
        }
        else
        {
            foreach (var child in Children)
            {
                child.ApplyToLeaves(value);
            }
        }
    }

    public IEnumerable<TreeItemViewModel> GetLeaves()
    {
        if (IsSelectable)
        {
            yield return this;
            yield break;
        }

        foreach (var child in Children)
        {
            foreach (var leaf in child.GetLeaves())
            {
                yield return leaf;
            }
        }
    }

    private void NotifyAncestorsOfAggregateChange()
    {
        var parent = _parent;
        while (parent is not null)
        {
            parent.OnPropertyChanged(nameof(IsChecked));
            parent = parent._parent;
        }
    }

    private static string PluralObjects(int count)
    {
        var mod10 = count % 10;
        var mod100 = count % 100;
        if (mod10 == 1 && mod100 != 11)
        {
            return "объект";
        }

        if (mod10 is >= 2 and <= 4 && mod100 is not (>= 12 and <= 14))
        {
            return "объекта";
        }

        return "объектов";
    }

    private static bool? ComputeAggregate(IEnumerable<TreeItemViewModel> children)
    {
        var values = children.Select(c => c.IsChecked).ToList();
        if (values.All(v => v == true))
        {
            return true;
        }

        return values.Any(v => v == true) ? null : false;
    }
}
