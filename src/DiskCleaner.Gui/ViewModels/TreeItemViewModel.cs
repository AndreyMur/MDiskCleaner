using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;

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

    /// <summary>
    /// Дефолтный выбор плана (FR-5.3/5.4, NFR): помечает каждый доступный лист значением
    /// предиката («безопасный объект» — отмечен, системный шаг/IN_USE — выключен), затем
    /// обновляет агрегатные состояния родительских узлов. Сам по себе выбор пользователя
    /// (<see cref="IsChecked"/>) не изменяется и события выделения не вызывает.
    /// </summary>
    public void ApplyDefaults(Func<CleanupItem, bool> autoSelected)
    {
        if (IsSelectable)
        {
            if (!CanCheck)
            {
                return;
            }

            var value = autoSelected(Item);
            if (_isChecked != value)
            {
                _isChecked = value;
                OnPropertyChanged();
            }

            NotifyAncestorsOfAggregateChange();
            return;
        }

        foreach (var child in Children)
        {
            child.ApplyDefaults(autoSelected);
        }
    }

    /// <summary>Блокирующие процессы объекта IN_USE (FR-5.7): список для показа пользователю.</summary>
    public IReadOnlyList<RunningProcessInfo> BlockingProcesses => Item.BlockingProcesses;

    /// <summary>Есть ли у выбранного объекта заблокировавшие его процессы (FR-5.7).</summary>
    public bool HasBlockingProcesses => Item.InUse && Item.BlockingProcesses.Count > 0;

    /// <summary>Блокирующие процессы одной многострочной строкой («имя — путь», FR-5.7).</summary>
    public string BlockingText
    {
        get
        {
            if (Item.BlockingProcesses.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(
                Environment.NewLine,
                Item.BlockingProcesses.Select(p =>
                    string.IsNullOrWhiteSpace(p.Name) ? p.ExecutablePath : $"{p.Name} — {p.ExecutablePath}"));
        }
    }

    /// <summary>Объект занят запущенным процессом (IN_USE, FR-5.7) — не выполняется и не выбирается.</summary>
    public bool IsInUse => Item.InUse;

    /// <summary>
    /// Текст «объект используется»: для известного «владельца» (VS Code, браузер) называется
    /// приложение и даётся совет закрыть его (FR-5.8); иначе — общая формулировка.
    /// </summary>
    public string InUseOwnerText
    {
        get
        {
            if (!Item.InUse)
            {
                return string.Empty;
            }

            var label = InUseMessages.OwnerLabel(Item);
            return label is null
                ? "Объект используется запущенным процессом: он не будет очищен и пропущен при выполнении плана."
                : $"{label} запущен — объект не будет очищен, пока {label} не закрыт (FR-5.8).";
        }
    }

    /// <summary>Рекомендация для занятого объекта: «закройте приложение и повторите» либо «отложите шаг» (FR-5.8).</summary>
    public string InUseAdviceText => Item.InUseAdvice switch
    {
        InUseAdvice.CloseAndRetry => "Рекомендация: закройте приложение и повторно выполните «Анализ», затем отметьте объект в плане (FR-5.8).",
        InUseAdvice.DeferStep => "Рекомендация: отложите шаг — повторный запуск плана безопасен и идемпотентен (FR-5.8).",
        _ => string.Empty
    };

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
