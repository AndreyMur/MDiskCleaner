using DiskCleaner.Core.Leftovers;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

/// <summary>
/// Тесты хранилища исключений (фаза 19, модуль 03, FR-3.7): <c>exclusions.json</c> хранит
/// исключения по пути и по бренду; кандидаты из исключений не попадают в план очистки;
/// по умолчанию все кандидаты плана «выключены» (FR-3.8).
/// </summary>
public class LeftoverExclusionsTests
{
    private static InstalledApp App(string name) => new()
    {
        ProductCode = "{" + Guid.NewGuid() + "}",
        ScopeKey = nameof(InstalledAppScope.LocalMachine64),
        DisplayName = name
    };

    private static LeftoverCandidateScanner Scanner(FakeEnvironment environment) =>
        new(environment, new FakeProcessInspector(), new FakeServiceInspector());

    [Fact]
    public void Store_PersistsPathAndBrandExclusions_AndRemoves()
    {
        using var root = new TempRoot();
        var storeDir = root.Combine("exclusions-state");
        var store = new ExclusionsStore(storeDir);

        var candidatePath = root.Combine("LocalAppData", "pachca-updater");
        store.AddPath(candidatePath);
        store.AddBrand("Wondershare");

        var reloaded = new ExclusionsStore(storeDir);
        Assert.Equal(2, reloaded.Load().Count);
        Assert.True(reloaded.Contains(candidatePath));
        Assert.True(reloaded.Contains("Wondershare"));

        Assert.True(ExclusionsStore.IsExcluded(
            reloaded.Load(),
            candidatePath + "\\Update.exe",
            "pachca-updater"));
        Assert.True(ExclusionsStore.IsExcluded(
            reloaded.Load(),
            root.Combine("ProgramData", "Wondershare"),
            "Wondershare"));

        reloaded.Remove(candidatePath);
        var afterRemove = new ExclusionsStore(storeDir);
        Assert.False(afterRemove.Contains(candidatePath));
        Assert.Single(afterRemove.Load());
        Assert.True(afterRemove.Contains("Wondershare"));
    }

    [Fact]
    public void Store_DistinguishesPathEntryFromBrandEntry()
    {
        using var root = new TempRoot();
        var localPath = root.Combine("LocalAppData", "qwen-updater");

        Assert.True(ExclusionsStore.IsPathEntry(localPath));
        Assert.True(ExclusionsStore.IsPathEntry(@"%LOCALAPPDATA%\qwen-updater"));
        Assert.False(ExclusionsStore.IsPathEntry("qwen-updater"));
        Assert.False(ExclusionsStore.IsPathEntry("Wondershare"));
    }

    [Fact]
    public void Plan_CandidatesFromPathAndBrandExclusions_NotIncluded()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var updaterPath = root.Combine("LocalAppData", "lm-studio-updater");
        root.CreateFile("LocalAppData\\lm-studio-updater\\Update.exe", 100);
        root.CreateFile("ProgramFiles\\OrphanTool\\tool.bin", 10);
        root.CreateFile("ProgramFilesX86\\OldTool\\old.bin", 20);

        var store = new ExclusionsStore(root.Combine("exclusions"));
        store.AddPath(updaterPath);
        store.AddBrand("OldTool");

        var plan = new LeftoverPlanService(Scanner(environment), store).BuildPlan([]);

        Assert.DoesNotContain(plan.Items, i => i.DisplayName == "lm-studio-updater");
        Assert.DoesNotContain(plan.Items, i => i.DisplayName == "OldTool");
        Assert.Contains(plan.Items, i => i.DisplayName == "OrphanTool");
    }

    [Fact]
    public void Plan_AllCandidatesDisabledByDefault()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        root.CreateFile("LocalAppData\\lm-studio-updater\\Update.exe", 100);
        root.CreateFile("ProgramFiles\\OrphanTool\\tool.bin", 10);
        root.CreateFile("Windows.old\\Windows\\System32\\config\\SYSTEM", 50);

        var plan = new LeftoverPlanService(Scanner(environment), new ExclusionsStore(root.Combine("exclusions"))).BuildPlan([]);

        Assert.NotEmpty(plan.Items);
        Assert.All(plan.Items, i => Assert.False(i.IsEnabled));
        Assert.Equal(0, plan.EnabledCount);
        Assert.Empty(plan.EnabledItems);
    }

    [Fact]
    public void Plan_WhitelistedInstalledApp_NotOffered()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        root.CreateFile("ProgramFiles\\InstalledApp\\app.exe", 100);
        root.CreateFile("ProgramFiles\\OrphanTool\\tool.bin", 10);

        var plan = new LeftoverPlanService(Scanner(environment), new ExclusionsStore(root.Combine("exclusions")))
            .BuildPlan([App("InstalledApp")]);

        Assert.DoesNotContain(plan.Items, i => i.DisplayName == "InstalledApp");
        Assert.Contains(plan.Items, i => i.DisplayName == "OrphanTool");
    }
}
