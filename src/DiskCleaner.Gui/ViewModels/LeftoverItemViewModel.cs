using CommunityToolkit.Mvvm.ComponentModel;
using DiskCleaner.Core.Leftovers;
using DiskCleaner.Core.Localization;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Gui.ViewModels;

/// <summary>
/// Строка экрана «Остатки»: один кандидат плана остатков (<see cref="LeftoverPlanItem"/>,
/// FR-3.2–3.6) с чекбоксом явного включения (по умолчанию всё выключено, FR-3.8).
/// Показывает размер, дату изменения, основание («почему это остаток»), признаки
/// «требует админ-прав» и обязательного пообъектного подтверждения для опасных объектов
/// (Program Files/ProgramData/Windows.old, FR-3.4, §5). Опасный объект включается только
/// после явного подтверждения пользователем.
/// </summary>
public sealed partial class LeftoverItemViewModel : ObservableObject
{
    private readonly Func<LeftoverPlanItem, bool> _confirmDangerous;
    private readonly Action _selectionChanged;
    private bool _isSelected;
    private bool _dangerousConfirmed;

    public LeftoverItemViewModel(
        LeftoverPlanItem item,
        Func<LeftoverPlanItem, bool> confirmDangerous,
        Action selectionChanged)
    {
        Item = item;
        _confirmDangerous = confirmDangerous;
        _selectionChanged = selectionChanged;
    }

    /// <summary>«Карточка» кандидата плана ядра (основание, риск, флаги).</summary>
    public LeftoverPlanItem Item { get; }

    public string Title => Item.DisplayName;

    public string PathText => Item.Path;

    public string ReasonText => Item.ReasonText;

    public CleanupRisk Risk => Item.Risk;

    public string RiskText => LocalizedNames.Risk(Item.Risk);

    public long? SizeBytes => Item.SizeBytes;

    /// <summary>Удаление требует прав администратора (Program Files/ProgramData/Windows.old, §5).</summary>
    public bool RequiresAdmin => Item.RequiresAdmin;

    /// <summary>Обязательное пообъектное подтверждение перед удалением (FR-3.4, §5).</summary>
    public bool RequiresConfirmation => Item.RequiresConfirmation;

    /// <summary>Рекомендуемый способ удаления (Windows.old: Storage Sense / cleanmgr / DISM, FR-3.5).</summary>
    public string? RecommendedRemovalMethod => Item.RecommendedRemovalMethod;

    public bool HasRecommendedRemovalMethod => !string.IsNullOrWhiteSpace(RecommendedRemovalMethod);

    public string? FileCountText =>
        Item.FileCount is long count ? count.ToString() : null;

    public bool HasFileCount => FileCountText is not null;

    public string? LastWriteText =>
        Item.LastWriteTimeUtc is DateTime value ? value.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : null;

    public bool HasLastWrite => LastWriteText is not null;

    /// <summary>Опасный объект уже подтверждён пользователем в текущем плане (FR-3.8, §5).</summary>
    public bool IsDangerousConfirmed => _dangerousConfirmed;

    /// <summary>Метки строки (админ-права и пообъектное подтверждение).</summary>
    public string LabelsText
    {
        get
        {
            var labels = new List<string>(2);
            if (RequiresAdmin)
            {
                labels.Add("требует админ-прав (UAC)");
            }

            if (RequiresConfirmation)
            {
                labels.Add("пообъектное подтверждение");
            }

            return string.Join(" · ", labels);
        }
    }

    public bool HasLabels => LabelsText.Length > 0;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !_isSelected && RequiresConfirmation && !_dangerousConfirmed)
            {
                if (!_confirmDangerous(Item))
                {
                    return;
                }

                _dangerousConfirmed = true;
                OnPropertyChanged(nameof(IsDangerousConfirmed));
            }

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

    /// <summary>Размер для колонки справа (null — размер не измерен).</summary>
    public string SizeText =>
        Item.SizeBytes is long bytes ? CleanReportFormatter.FormatBytes(bytes) : "—";
}
