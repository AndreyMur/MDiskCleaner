using CommunityToolkit.Mvvm.ComponentModel;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Gui.ViewModels;

/// <summary>
/// Строка группы «Осиротевшие записи Uninstall» (FR-4.13): запись реестра Uninstall,
/// ссылающаяся на отсутствующие пути. Удаляется только ветка реестра (файлы не затрагиваются),
/// по явному подтверждению; системные компоненты не предвыбираются.
/// </summary>
public sealed partial class OrphanUninstallItemViewModel : ObservableObject
{
    private readonly Func<UninstallOrphanRegistryMatch, bool> _confirmDelete;
    private readonly Action _selectionChanged;
    private bool _isSelected;
    private bool _confirmed;

    public OrphanUninstallItemViewModel(
        UninstallOrphanRegistryMatch match,
        Func<UninstallOrphanRegistryMatch, bool> confirmDelete,
        Action selectionChanged)
    {
        Match = match;
        _confirmDelete = confirmDelete;
        _selectionChanged = selectionChanged;
    }

    public UninstallOrphanRegistryMatch Match { get; }

    public string Title => Match.DisplayName;

    public string ScopeText => Match.App.ScopeKey;

    public string MissingPathsText => string.Join(Environment.NewLine, Match.MissingPaths);

    public bool HasMissingPaths => Match.MissingPaths.Count > 0;

    public bool IsSystemComponent => Match.IsSystemComponent;

    public bool RequiresAdmin => Match.RequiresAdmin;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !_isSelected && !_confirmed)
            {
                if (!_confirmDelete(Match))
                {
                    return;
                }

                _confirmed = true;
                OnPropertyChanged(nameof(IsConfirmed));
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

    public bool IsConfirmed => _confirmed;
}
