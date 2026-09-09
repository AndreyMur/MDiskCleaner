using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Gui.ViewModels;

/// <summary>
/// Группа остатков экрана «Остатки» (FR-3.2–3.6): кандидаты одной группы («Остатки апдейтеров»,
/// «Конфиги удалённых программ», «Осиротевшие папки Program Files/ProgramData», «Предыдущая версия
/// Windows»). Заголовок показывает число объектов и суммарный размер; чекбокс группы отмечает/снимает
/// только объекты без обязательного пообъектного подтверждения — опасные объекты (Program
/// Files/ProgramData/Windows.old) пользователь отмечает по одному (FR-3.8, §5).
/// </summary>
public sealed partial class LeftoverGroupViewModel : ObservableObject
{
    private readonly IReadOnlyList<LeftoverItemViewModel> _bulkItems;
    private readonly Action _selectionChanged;

    public LeftoverGroupViewModel(
        string title,
        IEnumerable<LeftoverItemViewModel> items,
        Action selectionChanged)
    {
        Title = title;
        _selectionChanged = selectionChanged;
        Items = new ObservableCollection<LeftoverItemViewModel>(items);
        _bulkItems = Items.Where(i => !i.RequiresConfirmation).ToList();
    }

    public string Title { get; }

    public ObservableCollection<LeftoverItemViewModel> Items { get; }

    public bool HasItems => Items.Count > 0;

    /// <summary>Чекбокс группы активен, если есть хотя бы один объект без обязательного подтверждения.</summary>
    public bool CanBulkCheck => _bulkItems.Count > 0;

    /// <summary>Число объектов и суммарный размер группы (заголовок, FR-3.2–3.6).</summary>
    public string SummaryText
    {
        get
        {
            var count = Items.Count;
            var bytes = Items.Sum(i => Math.Max(0, i.SizeBytes ?? 0));
            return $"{count} {Plural(count)} · {CleanReportFormatter.FormatBytes(bytes)}";
        }
    }

    /// <summary>Тройное состояние отметки группы по объектам без обязательного подтверждения.</summary>
    public bool? IsChecked
    {
        get
        {
            if (_bulkItems.Count == 0)
            {
                return false;
            }

            var selected = _bulkItems.Count(i => i.IsSelected);
            if (selected == _bulkItems.Count)
            {
                return true;
            }

            return selected == 0 ? false : null;
        }
        set
        {
            // WPF-чекбокс группы (IsThreeState) при клике по полностью отмеченной группе
            // передаёт null (цикл true → null → false), а не false. Null трактуем как «снять»,
            // иначе снять всю группу одним кликом невозможно.
            var target = value == true;

            foreach (var item in _bulkItems)
            {
                if (item.IsSelected != target)
                {
                    item.IsSelected = target;
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
            var selected = Items.Count(i => i.IsSelected);
            return selected == 0 ? string.Empty : $"выбрано {selected} из {Items.Count}";
        }
    }

    /// <summary>Есть ли объекты, требующие пообъектного подтверждения (пояснение группы).</summary>
    public bool HasSubtitle => Items.Count(i => i.RequiresConfirmation) > 0;

    public string Subtitle
    {
        get
        {
            var dangerous = Items.Count(i => i.RequiresConfirmation);
            if (dangerous == 0)
            {
                return string.Empty;
            }

            return dangerous == Items.Count
                ? "Все объекты группы требуют пообъектного подтверждения — отмечаются по одному (FR-3.4, §5)."
                : $"{dangerous} объект(ов) группы требуют пообъектного подтверждения — отмечаются по одному (FR-3.4, §5).";
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
