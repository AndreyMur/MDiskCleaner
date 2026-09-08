using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

/// <summary>
/// Модуль 04, фаза 4 (milestone «Фаза 24») — пообъектное подтверждение шагов с предупреждением
/// «удаление может затронуть X» по статическому списку известных зависимостей
/// (VS Build Tools 2019 → Windows SDK 19041, §7, FR-4.11).
/// </summary>
public class UninstallDependencyTests
{
    private const string Sdk19041Version = "10.1.19041.5609";
    private const string Sdk26100Version = "10.1.26100.1";

    [Fact]
    public void BuildPlan_WarnsThatRemovingSdk19041_MayAffectInstalledBuildTools2019()
    {
        var sdk19041 = SdkComponent(Sdk19041Version, "sdk19041");
        var sdk26100 = SdkComponent(Sdk26100Version, "sdk26100");
        var buildTools2019 = BuildTools("2019", "bt2019");
        var service = new UninstallPlanService(analyzer: new InstalledAppAnalyzer());

        var plan = service.BuildPlan([sdk19041, sdk26100, buildTools2019]);

        var sdkItem = Assert.Single(plan.Items, i => i.ProductCode == sdk19041.ProductCode);
        Assert.True(sdkItem.HasDependencyImpacts);
        Assert.Contains(buildTools2019.DisplayName, sdkItem.DependencyImpactNames);
        Assert.Contains("может затронуть", sdkItem.DependencyImpactText, StringComparison.Ordinal);

        var currentSdkItem = Assert.Single(plan.Items, i => i.ProductCode == sdk26100.ProductCode);
        Assert.False(currentSdkItem.HasDependencyImpacts);
    }

    [Fact]
    public void BuildPlan_DoesNotWarn_WhenNoAffectedProductInstalled()
    {
        var sdk19041 = SdkComponent(Sdk19041Version, "sdk19041");
        var service = new UninstallPlanService(analyzer: new InstalledAppAnalyzer());

        var plan = service.BuildPlan([sdk19041]);

        var sdkItem = Assert.Single(plan.Items);
        Assert.False(sdkItem.HasDependencyImpacts);
        Assert.Empty(sdkItem.DependencyImpactNames);
    }

    [Fact]
    public void BuildPlan_DoesNotWarn_ForNewerSdk_OrNewerBuildTools()
    {
        var sdk26100 = SdkComponent(Sdk26100Version, "sdk26100");
        var buildTools2022 = BuildTools("2022", "bt2022");
        var service = new UninstallPlanService(analyzer: new InstalledAppAnalyzer());

        var plan = service.BuildPlan([sdk26100, buildTools2022]);

        Assert.All(plan.Items, item => Assert.False(item.HasDependencyImpacts));
    }

    [Fact]
    public void PlannerSeeds_WarningText_ContainsAffectedProductNames()
    {
        var sdk19041 = SdkComponent(Sdk19041Version, "sdk19041");
        var buildTools2019 = BuildTools("2019", "bt2019");
        var planner = new UninstallPlannerService(registry: new UninstallRegistryService(branches: []));

        var seeds = planner.BuildSeedsFrom(
            [sdk19041, buildTools2019],
            new UninstallPlannerOptions { IncludeAllApps = true });

        var seed = Assert.Single(seeds, s => s.Key.EndsWith(sdk19041.ProductCode, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Удаление может затронуть", seed.Warning, StringComparison.Ordinal);
        Assert.Contains(buildTools2019.DisplayName, seed.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_AffectedExcludedTokens_ExcludeNewerVisualStudioInstallations()
    {
        var catalog = new UninstallDependencyCatalog();
        var sdk19041 = SdkComponent(Sdk19041Version, "sdk19041");
        var buildTools2019 = BuildTools("2019", "bt2019");
        var buildTools2022 = BuildTools("2022", "bt2022");

        var affected = catalog.FindAffectedProducts([buildTools2019, buildTools2022], sdk19041);

        Assert.Contains(buildTools2019.DisplayName, affected);
        Assert.DoesNotContain(buildTools2022.DisplayName, affected);
    }

    private static InstalledApp SdkComponent(string version, string seed) => new()
    {
        ProductCode = "{11111111-1111-1111-1111-" + FixedSuffix(seed) + "}",
        ScopeKey = nameof(InstalledAppScope.LocalMachine64),
        DisplayName = "Windows SDK Desktop Tools x64",
        DisplayVersion = version,
        Publisher = "Microsoft Corporation",
        UninstallString = "MsiExec.exe /X{11111111-1111-1111-1111-" + FixedSuffix(seed) + "}"
    };

    private static InstalledApp BuildTools(string year, string seed) => new()
    {
        ProductCode = "{22222222-2222-2222-2222-" + FixedSuffix(seed) + "}",
        ScopeKey = nameof(InstalledAppScope.LocalMachine64),
        DisplayName = $"Microsoft Visual Studio Build Tools {year}",
        DisplayVersion = year == "2019" ? "16.11.9" : "17.9.0",
        Publisher = "Microsoft Corporation",
        UninstallString = $@"C:\Program Files (x86)\Microsoft Visual Studio\{year}\BuildTools\unins000.exe"
    };

    /// <summary>Стабильный 12-символьный hex-суффикс GUID по «seed»-имени (для читаемых ключей в тестах).</summary>
    private static string FixedSuffix(string seed)
    {
        var hex = string.Concat(seed.Select(ch => ((int)ch).ToString("x2")));
        return hex[..Math.Min(12, hex.Length)].PadRight(12, '0');
    }
}
