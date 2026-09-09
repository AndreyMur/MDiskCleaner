using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Models;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

/// <summary>
/// Тесты «системные шаги в плане» (задача #149, FR-5.3/5.4, NFR): по умолчанию после анализа
/// отмечаются только безопасные объекты; ни один системный шаг (Temp, Корзина, гибернация,
/// Windows.old, пользовательские данные) не выполняется «по умолчанию» — его нужно включить
/// явно. Системный шаг несёт описание последствий и пометку «требует админа».
/// </summary>
public class MainViewModelPlanDefaultsTests
{
    [Fact]
    public async Task AfterAnalysis_SafeObjectsChecked_SystemStepsOffByDefault()
    {
        var (_, _, _, vm) = PlanRunTestSupport.Create(MixedPlan());
        await vm.AnalyzeCommand.ExecuteAsync(null);

        var leaves = vm.RootNodes.SelectMany(n => n.GetLeaves()).ToList();

        Assert.True(Check(leaves, "cache:npm").IsChecked);
        Assert.Single(vm.PlanRows);
        Assert.StartsWith("Выбрано: 1 объектов", vm.SelectedSummary);

        Assert.False(Check(leaves, "system:temp:user").IsChecked);
        Assert.False(Check(leaves, "system:recycle:c").IsChecked);
        Assert.False(Check(leaves, "system:hibernation:off").IsChecked);
        Assert.False(Check(leaves, "dev:android-sdk").IsChecked);
        Assert.False(Check(leaves, "app:big").IsChecked);
    }

    [Fact]
    public async Task SystemStep_ExplicitlyEnabled_JoinsPlanWithAdminInstructionAndConsequences()
    {
        var (_, _, _, vm) = PlanRunTestSupport.Create(MixedPlan());
        await vm.AnalyzeCommand.ExecuteAsync(null);

        var leaves = vm.RootNodes.SelectMany(n => n.GetLeaves()).ToList();
        Check(leaves, "system:recycle:c").IsChecked = true;
        Check(leaves, "system:hibernation:off").IsChecked = true;

        Assert.Equal(3, vm.PlanRows.Count);

        var hiberRow = vm.PlanRows.Single(r => r.Item.Key == "system:hibernation:off");
        Assert.True(hiberRow.RequiresAdmin);
        Assert.Equal("да", hiberRow.AdminText);
        Assert.Contains("Отключает гибернацию; быстрый запуск сохраняется", hiberRow.Warning, StringComparison.Ordinal);

        Assert.StartsWith("Выбрано: 3 объектов", vm.SelectedSummary);
    }

    [Fact]
    public async Task StatusMentionsThatSystemStepsAreOffByDefault()
    {
        var (_, _, _, vm) = PlanRunTestSupport.Create(MixedPlan());

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.Contains("Системные шаги по умолчанию выключены", vm.StatusText, StringComparison.Ordinal);
    }

    private static TreeItemViewModel Check(IEnumerable<TreeItemViewModel> leaves, string key) =>
        leaves.Single(l => l.Item.Key == key);

    private static AnalysisResult MixedPlan() => PlanRunTestSupport.TreeOf(new[]
    {
        PlanRunTestSupport.Leaf("cache:npm", "npm cache", CleanupCategory.Cache, CleanupRisk.Low, 2_000_000, warning: "Пакеты будут загружены заново."),
        PlanRunTestSupport.Leaf("system:temp:user", "Временные файлы пользователя", CleanupCategory.Temp, CleanupRisk.Low, 100_000_000, group: "Системная очистка", warning: "Файлы, занятые процессами, будут пропущены."),
        PlanRunTestSupport.Leaf("system:recycle:c", "Корзина (C:)", CleanupCategory.RecycleBin, CleanupRisk.Medium, 7_000, group: "Системная очистка", warning: "Содержимое Корзины будет удалено безвозвратно."),
        PlanRunTestSupport.Leaf("system:hibernation:off", "Отключить гибернацию (hiberfil.sys)", CleanupCategory.SystemFile, CleanupRisk.Medium, 2_800_000_000, group: "Системная очистка", requiresAdmin: true, warning: "Отключает гибернацию; быстрый запуск сохраняется. Требуются права администратора (UAC)."),
        PlanRunTestSupport.Leaf("dev:android-sdk", "Android SDK", CleanupCategory.DevToolchain, CleanupRisk.Medium, 3_000_000, group: "Android SDK", warning: "Будет переустановлено."),
        PlanRunTestSupport.Leaf("app:big", "Большое приложение", CleanupCategory.InstalledApp, CleanupRisk.High, 5_000_000_000, group: "Vendor", warning: "Удаление программы.")
    });
}
