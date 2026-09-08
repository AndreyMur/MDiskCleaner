using CommunityToolkit.Mvvm.ComponentModel;
using DiskCleaner.Core.Reports;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Gui.ViewModels;

/// <summary>
/// Строка экрана «Деинсталляция и зачистка»: одна запись установленного ПО плана
/// (<see cref="UninstallPlanItem"/>, FR-4.1–4.4). Показывает название, издателя, версию, дату,
/// общий размер записи, отдельно «занимает на C:» (FR-4.4), путь и пометки
/// «дубль/старая версия/проверить вручную» (FR-4.2, FR-4.3). Включение — только явное
/// (чекбокс), по умолчанию всё выключено; пометки-рекомендации не предвыбираются (FR-4.2).
/// </summary>
public sealed partial class UninstallItemViewModel : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _isSelected;

    public UninstallItemViewModel(UninstallPlanItem item, Action selectionChanged)
    {
        Item = item;
        _selectionChanged = selectionChanged;
    }

    /// <summary>Шаг плана деинсталляции ядра (название, объёмы, пометки, зависимости).</summary>
    public UninstallPlanItem Item { get; }

    public string Title => Item.DisplayName;

    public string PublisherText => string.IsNullOrWhiteSpace(Item.Publisher) ? "—" : Item.Publisher!;

    public string VersionText => string.IsNullOrWhiteSpace(Item.DisplayVersion) ? "—" : Item.DisplayVersion!;

    public string DateText => string.IsNullOrWhiteSpace(Item.InstallDateText) ? "—" : Item.InstallDateText!;

    public string PathText => string.IsNullOrWhiteSpace(Item.Path) ? "—" : Item.Path!;

    /// <summary>Общий размер записи реестра (FR-4.4).</summary>
    public string SizeText =>
        Item.EstimatedSizeBytes > 0 ? CleanReportFormatter.FormatBytes(Item.EstimatedSizeBytes) : "—";

    /// <summary>Фактический размер каталога установки на диске C: — отдельно от записи (FR-4.4).</summary>
    public string SizeOnCDriveText =>
        Item.SizeOnCDriveBytes is long bytes ? CleanReportFormatter.FormatBytes(bytes) : "—";

    public bool IsOnCDrive => Item.IsOnCDrive;

    public bool RequiresAdmin => Item.RequiresAdmin;

    /// <summary>Рекомендация к удалению (дубль-старая версия или «проверить вручную») — без предвыбора.</summary>
    public bool IsRecommended => Item.IsRecommended;

    public bool IsDuplicate => Item.IsDuplicate;

    public bool IsOldVersion => Item.IsOldVersion;

    public bool NeedsReview => Item.NeedsReview;

    /// <summary>Метки строки одной строкой, например «дубль · старая версия» (FR-4.2/4.3).</summary>
    public string MarksText
    {
        get
        {
            var marks = new List<string>(3);
            foreach (var kind in Item.Marks)
            {
                marks.Add(MarkText(kind));
            }

            return string.Join(" · ", marks.Distinct(StringComparer.Ordinal));
        }
    }

    public bool HasMarks => MarksText.Length > 0;

    /// <summary>Пояснение рекомендации (версии и даты установки для дублей, FR-4.2).</summary>
    public string? NoteText => Item.Note;

    public bool HasNote => !string.IsNullOrWhiteSpace(NoteText);

    /// <summary>Есть ли предупреждение «удаление может затронуть X» (§7, FR-4.11).</summary>
    public bool HasDependencyImpacts => Item.HasDependencyImpacts;

    public string DependencyImpactText => Item.DependencyImpactText ?? string.Empty;

    public string? GroupName => Item.GroupName;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            Item.IsEnabled = value;
            OnPropertyChanged();
            _selectionChanged();
        }
    }

    private static string MarkText(UninstallPlanMarkKind kind) => kind switch
    {
        UninstallPlanMarkKind.Duplicate => "дубль",
        UninstallPlanMarkKind.OldVersion => "старая версия",
        UninstallPlanMarkKind.ReviewManually => "проверить вручную",
        _ => kind.ToString()
    };
}
