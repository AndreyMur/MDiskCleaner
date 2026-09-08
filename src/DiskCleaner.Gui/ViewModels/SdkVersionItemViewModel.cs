using CommunityToolkit.Mvvm.ComponentModel;
using DiskCleaner.Core.Reports;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Gui.ViewModels;

/// <summary>
/// Строка раздела «Версии Windows SDK» экрана «Деинсталляция и зачистка»: одна установленная
/// версия Windows SDK (общий <see cref="InstalledApp.DisplayVersion"/>, FR-4.7) с числом
/// MSI-компонентов и суммарным размером записей. Массовое удаление версии выполняется по
/// отдельному подтверждению и с прогрессом по компонентам.
/// </summary>
public sealed partial class SdkVersionItemViewModel : ObservableObject
{
    private readonly IReadOnlyList<InstalledApp> _components;

    public SdkVersionItemViewModel(string displayVersion, IReadOnlyList<InstalledApp> components)
    {
        DisplayVersion = displayVersion;
        _components = components;
    }

    public string DisplayVersion { get; }

    public int ComponentCount => _components.Count;

    public string ComponentCountText => ComponentCount == 1 ? "1 компонент" : $"{ComponentCount} компонента(ов)";

    public long EstimatedTotalBytes => _components.Sum(c => Math.Max(0, c.EstimatedSizeBytes));

    public string EstimatedTotalText => CleanReportFormatter.FormatBytes(EstimatedTotalBytes);

    /// <summary>Названия компонентов для подтверждения и журнала.</summary>
    public string ComponentNamesText =>
        string.Join(Environment.NewLine, _components.Select(c => "• " + c.DisplayName));
}
