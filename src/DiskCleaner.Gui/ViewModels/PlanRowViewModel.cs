using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Localization;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Gui.ViewModels;

/// <summary>
/// Строка таблицы плана (FR-1.11): имя, категория, риск, размер, «требует админа?»,
/// действие по умолчанию и метки <c>IN_USE</c>/<c>Review manually</c>. Только отображение
/// данных ядра — без бизнес-логики.
/// </summary>
public sealed class PlanRowViewModel
{
    public PlanRowViewModel(CleanupItem item)
    {
        Item = item;
        DefaultAction = new CategorizationService().DefaultActionFor(item);
    }

    public CleanupItem Item { get; }

    /// <summary>Действие по умолчанию, вычисленное ядром из риска и признака IN_USE (FR-1.6/FR-1.7).</summary>
    public CleanupDefaultAction DefaultAction { get; }

    public string Name =>
        string.IsNullOrEmpty(Item.GroupName)
            ? Item.DisplayName
            : $"{Item.GroupName} — {Item.DisplayName}";

    public string Category => LocalizedNames.Category(Item.Category);

    public string Risk => LocalizedNames.Risk(Item.Risk);

    public string DefaultActionText => LocalizedNames.DefaultAction(DefaultAction);

    public string Size => CleanReportFormatter.FormatBytes(Item.EffectiveSizeBytes);

    public string Command => Item.CleanCommand ?? "—";

    public string Warning => Item.Warning ?? string.Empty;

    public string Path => Item.Path ?? "(команда без пути)";

    public bool IsInUse => Item.InUse;

    public string InUseText => Item.InUse ? "IN_USE" : string.Empty;

    public bool NeedsReview => Item.ReviewManually;

    public string ReviewText => Item.ReviewManually ? "Review manually" : string.Empty;

    /// <summary>Метки объекта одной строкой, например «IN_USE, Review manually».</summary>
    public string FlagsText
    {
        get
        {
            var flags = new List<string>(2);
            if (Item.InUse)
            {
                flags.Add("IN_USE");
            }

            if (Item.ReviewManually)
            {
                flags.Add("Review manually");
            }

            return string.Join(", ", flags);
        }
    }

    public bool RequiresAdmin => Item.RequiresAdmin;

    public string AdminText => Item.RequiresAdmin ? "да" : "нет";

    public string RecycleText => Item.MoveToRecycleBin ? "Корзина" : "—";

    public string RegistryText => Item.RegistryDeletePath is null ? string.Empty : "запись реестра";
}
