using CommunityToolkit.Mvvm.ComponentModel;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Localization;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Gui.ViewModels;

/// <summary>
/// Строка экрана «Очистка кэшей»: одна «карточка действия» плана
/// (<see cref="Core.Caches.CacheCleanAction"/>, FR-2.1/FR-2.5) с чекбоксом явной отметки
/// пользователя (NFR G3). Очистка выполняется только для отмеченных объектов; объекты,
/// которые чистить нельзя (заняты процессом, вне правил), не выбираются.
/// </summary>
public sealed class CacheActionViewModel : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _isSelected;

    public CacheActionViewModel(CacheCleanAction action, bool isOffDisk, Action selectionChanged)
    {
        Action = action;
        IsOffDisk = isOffDisk;
        _selectionChanged = selectionChanged;
    }

    /// <summary>Карточка действия ядра (способ, согласие, последствия, восстановление).</summary>
    public CacheCleanAction Action { get; }

    public CleanupItem Item => Action.Item;

    /// <summary>Кэш находится вне диска, который сканировался в основном окне (FR-2.3).</summary>
    public bool IsOffDisk { get; }

    /// <summary>Заголовок строки: для «внешних» кэшей добавляется менеджер/приложение.</summary>
    public string Title =>
        IsOffDisk && !string.IsNullOrEmpty(Item.GroupName)
            ? $"{Item.GroupName}: {Item.DisplayName}"
            : Item.DisplayName;

    /// <summary>Объект можно отметить чекбоксом (метод очистки применим и объект не отложен).</summary>
    public bool IsSelectable => Action.Cleanable && !Action.IsHeld;

    /// <summary>
    /// Объект попадает в массовую отметку всей группы (FR-2.11): применим, не отложен
    /// и не требует отдельного явного согласия (<see cref="CacheConsent.Ask"/>).
    /// </summary>
    public bool IsBulkSelectable => IsSelectable && Action.Consent == CacheConsent.Auto;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !IsSelectable)
            {
                return;
            }

            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
            _selectionChanged();
        }
    }

    public CleanupRisk Risk => Item.Risk;

    public string RiskText => LocalizedNames.Risk(Item.Risk);

    public long SavingsBytes => Action.EstimatedSavingsBytes;

    public string SizeText => CleanReportFormatter.FormatBytes(Action.EstimatedSavingsBytes);

    public bool HasNativeCommand => !string.IsNullOrWhiteSpace(Action.NativeCommandText);

    public string NativeCommandText => Action.NativeCommandText ?? string.Empty;

    public string MethodText => Action.Method switch
    {
        CacheCleanMethod.NativeCommand => "Штатная команда менеджера",
        CacheCleanMethod.NativeWithDirectFallback => "Штатная команда менеджера; прямое удаление каталога — если размер не изменится",
        CacheCleanMethod.DirectDelete => "Прямое удаление каталога",
        CacheCleanMethod.DeferredInUse => "Отложено: объект используется запущенным процессом",
        _ => "Очистка запрещена правилами"
    };

    public bool RequiresExplicitConsent => Action.Consent == CacheConsent.Ask;

    public string ConsentText =>
        RequiresExplicitConsent ? "требует явного согласия" : "согласие автоматическое";

    public bool IsOrphan => Item.IsOrphan;

    public string OrphanText => "осиротевший кэш — удаляется напрямую";

    public bool IsHeld => Action.IsHeld;

    public bool IsNotAllowed => Action.Method == CacheCleanMethod.NotAllowed;

    /// <summary>Причина «не трогать» (занято процессом / защитный режим) или запрета.</summary>
    public string ReasonText =>
        Action.HoldReason ?? Action.NotAllowedReason ?? string.Empty;

    public bool HasReason => !string.IsNullOrWhiteSpace(ReasonText);

    /// <summary>Последствия очистки («что произойдёт», FR-2.5).</summary>
    public string ConsequencesText => Action.ConsequencesText;

    /// <summary>Оценка времени/трафика на восстановление (FR-2.5).</summary>
    public string RestoreText => Action.RestoreEstimateText;

    public bool HasPath => Item.Path is not null;

    public string PathText => Item.Path ?? "(команда без пути)";

    public string? Description => Item.Description;

    /// <summary>Метки строки одной строкой, например «требует явного согласия · осиротевший».</summary>
    public string LabelsText
    {
        get
        {
            var labels = new List<string>(3);
            if (RequiresExplicitConsent)
            {
                labels.Add("требует явного согласия");
            }

            if (Action.Method == CacheCleanMethod.DeferredInUse)
            {
                labels.Add("используется (отложен)");
            }

            if (Action.Method == CacheCleanMethod.NotAllowed)
            {
                labels.Add("нельзя чистить");
            }

            return string.Join(" · ", labels);
        }
    }
}
