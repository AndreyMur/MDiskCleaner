using DiskCleaner.Core.Models;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

public class PlanRowViewModelTests
{
    [Fact]
    public void DefaultActionText_ReflectsRisk()
    {
        Assert.Equal("Очистить", RowFor(Risk: CleanupRisk.Low).DefaultActionText);
        Assert.Equal("Спросить", RowFor(Risk: CleanupRisk.Medium).DefaultActionText);
        Assert.Equal("Не трогать", RowFor(Risk: CleanupRisk.High).DefaultActionText);
    }

    [Fact]
    public void DefaultAction_KeepsForInUseItem_EvenWhenRiskIsLow()
    {
        var row = RowFor(Risk: CleanupRisk.Low, inUse: true);

        Assert.Equal(CleanupDefaultAction.Keep, row.DefaultAction);
        Assert.Equal("Не трогать", row.DefaultActionText);
        Assert.True(row.IsInUse);
        Assert.Contains("IN_USE", row.FlagsText);
    }

    [Fact]
    public void ReviewManually_IsShownAsFlag()
    {
        var row = RowFor(
            Risk: CleanupRisk.Medium,
            reviewReason: "Review manually: странный издатель");

        Assert.True(row.NeedsReview);
        Assert.Equal("Review manually", row.ReviewText);
        Assert.Contains("Review manually", row.FlagsText);
    }

    [Fact]
    public void FlagsText_CombinesInUseAndReviewManually()
    {
        var row = RowFor(
            Risk: CleanupRisk.Low,
            inUse: true,
            reviewReason: "Review manually: странный издатель");

        Assert.Equal("IN_USE, Review manually", row.FlagsText);
    }

    [Fact]
    public void AdminText_ReflectsRequiresAdmin()
    {
        Assert.Equal("да", RowFor(Risk: CleanupRisk.Medium, requiresAdmin: true).AdminText);
        Assert.Equal("нет", RowFor(Risk: CleanupRisk.Low).AdminText);
    }

    [Fact]
    public void SizeAndName_AreFormattedForDisplay()
    {
        var row = RowFor(Risk: CleanupRisk.Low, sizeBytes: 1024, groupName: "Менеджеры пакетов");

        Assert.Equal("Менеджеры пакетов — npm cache", row.Name);
        Assert.Equal("1 КБ", row.Size);
        Assert.Equal("Кэши", row.Category);
    }

    private static PlanRowViewModel RowFor(
        CleanupRisk Risk,
        bool inUse = false,
        string? reviewReason = null,
        bool requiresAdmin = false,
        long sizeBytes = 1024,
        string? groupName = null) =>
        new(new CleanupItem
        {
            Key = "test:row",
            DisplayName = "npm cache",
            GroupName = groupName,
            Category = CleanupCategory.Cache,
            Risk = Risk,
            SizeBytes = sizeBytes,
            InUse = inUse,
            ReviewReason = reviewReason,
            RequiresAdmin = requiresAdmin
        });
}
