using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Gui.ViewModels;

/// <summary>
/// Группа «карточек» очистки кэшей по менеджеру/приложению (FR-2.1) либо отдельная группа
/// кэшей вне сканируемого диска (FR-2.3). Заголовок показывает число объектов и суммарный
/// размер; чекбокс группы отмечает/снимает только объекты с автоматическим согласием
/// (FR-2.11) — объекты, требующие явного согласия, отмечаются пользователем по одному.
/// </summary>
public sealed class CacheGroupViewModel : ObservableObject
{
    private readonly IReadOnlyList<CacheActionViewModel> _bulkSelectable;
    private readonly Action _selectionChanged;

    public CacheGroupViewModel(
        string title,
        IEnumerable<CacheActionViewModel> actions,
        Action selectionChanged,
        string? subtitle = null,
        bool isOffDiskGroup = false)
    {
        Title = title;
        Subtitle = subtitle;
        IsOffDiskGroup = isOffDiskGroup;
        _selectionChanged = selectionChanged;
        Actions = new ObservableCollection<CacheActionViewModel>(actions);
        _bulkSelectable = Actions.Where(a => a.IsBulkSelectable).ToList();
    }

    public string Title { get; }

    public string? Subtitle { get; }

    /// <summary>Есть ли пояснение под заголовком группы (для группы кэшей вне сканируемого диска).</summary>
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);

    /// <summary>Отдельная группа кэшей, чьи пути лежат вне сканируемого диска (FR-2.3).</summary>
    public bool IsOffDiskGroup { get; }

    public ObservableCollection<CacheActionViewModel> Actions { get; }

    public bool HasActions => Actions.Count > 0;

    /// <summary>Чекбокс группы активен, если есть хотя бы один объект с авто-согласием.</summary>
    public bool CanBulkCheck => _bulkSelectable.Count > 0;

    /// <summary>Число объектов и суммарный размер группы (заголовок, FR-2.1).</summary>
    public string SummaryText
    {
        get
        {
            var count = Actions.Count;
            var bytes = Actions.Sum(a => Math.Max(0, a.SavingsBytes));
            return $"{count} {Plural(count)} · {CleanReportFormatter.FormatBytes(bytes)}";
        }
    }

    /// <summary>Тройное состояние отметки группы по объектам с авто-согласием.</summary>
    public bool? IsChecked
    {
        get
        {
            if (_bulkSelectable.Count == 0)
            {
                return false;
            }

            var selected = _bulkSelectable.Count(a => a.IsSelected);
            if (selected == _bulkSelectable.Count)
            {
                return true;
            }

            return selected == 0 ? false : null;
        }
        set
        {
            if (value is not true and not false)
            {
                return;
            }

            foreach (var action in _bulkSelectable)
            {
                if (action.IsSelected != value)
                {
                    action.IsSelected = value.Value;
                }
            }

            _selectionChanged();
        }
    }

    /// <summary>Текстовое состояние выбора группы для доступности (accessible name).</summary>
    public string SelectedText
    {
        get
        {
            var selected = Actions.Count(a => a.IsSelected);
            return selected == 0
                ? string.Empty
                : $"выбрано {selected} из {Actions.Count}";
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(SummaryText));
    }

    private static string Plural(int count)
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
}
