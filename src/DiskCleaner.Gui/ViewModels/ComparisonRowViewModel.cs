using DiskCleaner.Core.Reports;

namespace DiskCleaner.Gui.ViewModels;

/// <summary>
/// Строка раздела «Было/стало» (FR-1.13): объект плана с размерами предыдущего и текущего
/// скана и дельтой. Только отображение результата <see cref="PlanComparer"/> без логики.
/// </summary>
public sealed class ComparisonRowViewModel
{
    public ComparisonRowViewModel(PlanObjectComparison comparison)
    {
        Comparison = comparison;
    }

    public PlanObjectComparison Comparison { get; }

    public string Name =>
        string.IsNullOrEmpty(Comparison.Group)
            ? Comparison.Name
            : $"{Comparison.Group} — {Comparison.Name}";

    public string Category => Comparison.CategoryText;

    public string Was => CleanReportFormatter.FormatBytes(Comparison.PreviousBytes);

    public string Now => CleanReportFormatter.FormatBytes(Comparison.CurrentBytes);

    /// <summary>Дельта со знаком: «+» — размер вырос, «−» — освободилось, «—» — без изменений.</summary>
    public string Delta => Comparison.DeltaBytes == 0
        ? "—"
        : (Comparison.DeltaBytes > 0 ? "+" : "-") + CleanReportFormatter.FormatBytes(Math.Abs(Comparison.DeltaBytes));

    public PlanObjectChangeKind ChangeKind => Comparison.ChangeKind;

    public string ChangeText => Comparison.ChangeKind switch
    {
        PlanObjectChangeKind.Added => "Добавлен",
        PlanObjectChangeKind.Removed => "Удалён",
        PlanObjectChangeKind.Changed => "Изменён",
        _ => "Без изменений"
    };
}
