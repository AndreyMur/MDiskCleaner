using DiskCleaner.Core.Localization;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Gui.ViewModels;

public sealed class PlanRowViewModel
{
    public PlanRowViewModel(CleanupItem item)
    {
        Item = item;
    }

    public CleanupItem Item { get; }

    public string Name =>
        string.IsNullOrEmpty(Item.GroupName)
            ? Item.DisplayName
            : $"{Item.GroupName} — {Item.DisplayName}";

    public string Category => LocalizedNames.Category(Item.Category);

    public string Risk => LocalizedNames.Risk(Item.Risk);

    public string Size => CleanReportFormatter.FormatBytes(Item.EffectiveSizeBytes);

    public string Command => Item.CleanCommand ?? "—";

    public string Warning => Item.Warning ?? string.Empty;

    public string Path => Item.Path ?? "(команда без пути)";

    public bool IsInUse => Item.InUse;
}
