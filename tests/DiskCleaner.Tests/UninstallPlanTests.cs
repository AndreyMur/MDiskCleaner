using DiskCleaner.Core.Uninstall;
using Microsoft.Win32;

namespace DiskCleaner.Tests;

/// <summary>
/// Модуль 04, фаза 2 — рекомендации по выбору (FR-4.2/4.3/4.4, milestone «Фаза 22»):
/// эвристика дублей и «старых версий» без предвыбора, пометка аномалий с группой
/// «Проверить вручную» (в т.ч. InstallDate = дате первой загрузки Windows) и расчёт
/// «занимает на C:» отдельно от общего размера записи. Тесты на фикстурах реестра (#108).
/// </summary>
public class UninstallPlanTests
{
    [Fact]
    public void Analyze_FirstRunDateProvider_SameInstallDate_FlagsSuspiciousInstallDate()
    {
        var suspicious = InstalledApp("ToolB", "20240101", "ACME", "1.0");
        var ordinary = InstalledApp("ToolA", "20230501", "ACME", "2.0");
        var analyzer = new InstalledAppAnalyzer(() => new DateTime(2024, 1, 1));

        var analysis = analyzer.Analyze([suspicious, ordinary]);

        Assert.Contains(
            analysis,
            a => a.App.ProductCode == suspicious.ProductCode && a.NeedsReview &&
                 a.Anomalies.Contains(AppAnomalyKind.SuspiciousInstallDate) &&
                 a.Anomalies.Contains(AppAnomalyKind.ReviewManually));
        Assert.Contains(analysis, a => a.App.ProductCode == ordinary.ProductCode && !a.NeedsReview);
    }

    [Fact]
    public void Analyze_WithoutFirstRunProvider_DoesNotFlagInstallDate()
    {
        var app = InstalledApp("Tool", "20240101", "ACME", "1.0");
        var analysis = new InstalledAppAnalyzer().Analyze([app]);

        var item = Assert.Single(analysis);
        Assert.DoesNotContain(AppAnomalyKind.SuspiciousInstallDate, item.Anomalies);
        Assert.False(item.NeedsReview);
    }

    [Fact]
    public void Analyze_Duplicates_MarksOldVersionAndKeepsNewest_WithVersionAndDatesInNote()
    {
        var jdk11 = InstalledApp("Java(TM) SE Development Kit 11.0.19", "20230101", "Oracle", "11.0.19");
        var jdk17 = InstalledApp("Java(TM) SE Development Kit 17.0.9", "20240101", "Oracle", "17.0.9");
        var analyzer = new InstalledAppAnalyzer();

        var analysis = analyzer.Analyze([jdk11, jdk17]);

        var older = Assert.Single(analysis, a => a.App.ProductCode == jdk11.ProductCode);
        Assert.True(older.IsOldVersion);
        Assert.True(older.IsDuplicate);
        Assert.Equal(jdk17.ProductCode, older.KeepProductCode);
        Assert.Equal("17.0.9", older.KeepDisplayVersion);
        Assert.Equal("20240101", older.KeepInstallDate);
        Assert.Contains("17.0.9", older.Note);
        Assert.Contains("2024-01-01", older.Note);
        Assert.Contains("11.0.19", older.Note);
        Assert.Contains("2023-01-01", older.Note);

        var newest = Assert.Single(analysis, a => a.App.ProductCode == jdk17.ProductCode);
        Assert.True(newest.IsDuplicate);
        Assert.False(newest.IsOldVersion);
        Assert.Contains("оставить", newest.Note);
    }

    [Fact]
    public void BuildPlan_Duplicates_NeverPreselected_AndRecommendationOnlyForOld()
    {
        var jdk11 = InstalledApp("Java(TM) SE Development Kit 11.0.19", "20230101", "Oracle", "11.0.19");
        var jdk17 = InstalledApp("Java(TM) SE Development Kit 17.0.9", "20240101", "Oracle", "17.0.9");
        var service = new UninstallPlanService(
            registry: new UninstallRegistryService(branches: []),
            analyzer: new InstalledAppAnalyzer());

        var plan = service.BuildPlan([jdk11, jdk17]);

        var older = Assert.Single(plan.Items, i => i.Key.EndsWith(jdk11.ProductCode, StringComparison.Ordinal));
        var newest = Assert.Single(plan.Items, i => i.Key.EndsWith(jdk17.ProductCode, StringComparison.Ordinal));

        Assert.True(older.IsOldVersion);
        Assert.True(older.IsDuplicate);
        Assert.Contains("17.0.9", older.Note);
        Assert.False(older.IsEnabled, "рекомендации никогда не предвыбираются (FR-4.2)");

        Assert.True(newest.IsDuplicate);
        Assert.False(newest.IsOldVersion);
        Assert.False(newest.IsEnabled);

        Assert.Equal(1, plan.RecommendedCount);
        Assert.Equal("Oracle", older.GroupName);
        Assert.NotEqual(UninstallPlanItem.ReviewManuallyGroupName, older.GroupName);
    }

    [Fact]
    public void BuildPlan_Anomalies_GoToReviewManuallyGroup_WithEmptyPublisherAndSuspiciousTokens()
    {
        var emptyPublisher = InstalledApp("ToolEmpty", "20240101", "   ");
        var template = InstalledApp("ToolTemplate", "20240101", "${PRODUCT_PUBLISHER}");
        var domainName = InstalledApp("update.svc.host", "20240101", "ACME");
        var ordinary = InstalledApp("CleanApp", "20240101", "ACME");
        var service = new UninstallPlanService(
            registry: new UninstallRegistryService(branches: []),
            analyzer: new InstalledAppAnalyzer());

        var plan = service.BuildPlan([emptyPublisher, template, domainName, ordinary]);

        Assert.Equal(3, plan.ReviewManuallyCount);
        Assert.Equal(3, plan.RecommendedCount);
        Assert.Equal(4, plan.Items.Count);
        Assert.All(
            plan.ReviewManuallyItems,
            i => Assert.Equal(UninstallPlanItem.ReviewManuallyGroupName, i.GroupName));
        Assert.DoesNotContain(
            plan.Items.Where(i => !i.NeedsReview),
            i => i.GroupName == UninstallPlanItem.ReviewManuallyGroupName);
        Assert.All(plan.Items, i => Assert.False(i.IsEnabled));
    }

    [Fact]
    public void BuildPlan_FirstRunDate_FunnelsIntoReviewManuallyGroup()
    {
        var preinstalled = InstalledApp("VendorTool", "20240101", "Vendor");
        var other = InstalledApp("InstalledLater", "20240301", "Vendor");
        var analyzer = new InstalledAppAnalyzer(() => new DateTime(2024, 1, 1));
        var service = new UninstallPlanService(
            registry: new UninstallRegistryService(branches: []),
            analyzer: analyzer);

        var plan = service.BuildPlan([preinstalled, other]);

        var flagged = Assert.Single(plan.ReviewManuallyItems);
        Assert.EndsWith(preinstalled.ProductCode, flagged.Key);
        Assert.Equal(UninstallPlanItem.ReviewManuallyGroupName, flagged.GroupName);
    }

    [Fact]
    public void BuildPlan_ReportsSizeOnCDriveSeparatelyFromRegistryEstimate_WhenFolderOnC()
    {
        var fakeSize = 12345L;
        var app = InstalledApp(
            "BigApp",
            "20240101",
            "ACME",
            version: "1.0",
            location: @"C:\Program Files\BigApp");
        var service = new UninstallPlanService(
            registry: new UninstallRegistryService(branches: []),
            analyzer: new InstalledAppAnalyzer(),
            folderSize: new FakeFolderSizeProvider(fakeSize));

        var plan = service.BuildPlan(
            [app],
            new UninstallPlanOptions { PathExists = _ => true });

        var item = Assert.Single(plan.Items);
        Assert.Equal(@"C:\Program Files\BigApp", item.Path);
        Assert.Equal(4096, item.EstimatedSizeBytes);
        Assert.Equal(fakeSize, item.SizeOnCDriveBytes);
        Assert.True(item.IsOnCDrive);
    }

    [Fact]
    public void BuildPlan_DoesNotReportOnCDrive_WhenLocationOnOtherDrive()
    {
        var app = InstalledApp(
            "OtherApp",
            "20240101",
            "ACME",
            version: "1.0",
            location: @"D:\Apps\OtherApp");
        var service = new UninstallPlanService(
            registry: new UninstallRegistryService(branches: []),
            analyzer: new InstalledAppAnalyzer(),
            folderSize: new FakeFolderSizeProvider(999));

        var plan = service.BuildPlan(
            [app],
            new UninstallPlanOptions { PathExists = _ => true });

        var item = Assert.Single(plan.Items);
        Assert.Null(item.SizeOnCDriveBytes);
        Assert.False(item.IsOnCDrive);
    }

    [Fact]
    public void BuildPlan_SkipsSystemComponents_AndUnremovableWithoutFlag()
    {
        var systemComponent = InstalledApp("System Thing", "20240101", "Microsoft", isSystemComponent: true);

        var noUninstaller = new InstalledApp
        {
            ProductCode = "{nnnnnnnn-nnnn-nnnn-nnnn-nnnnnnnnnnnn}",
            ScopeKey = nameof(InstalledAppScope.LocalMachine64),
            DisplayName = "NoUninstaller",
            Publisher = "ACME",
            UninstallString = null
        };

        var service = new UninstallPlanService(
            registry: new UninstallRegistryService(branches: []),
            analyzer: new InstalledAppAnalyzer());

        var plan = service.BuildPlan([systemComponent, noUninstaller]);
        Assert.Empty(plan.Items);

        var withUnremovable = service.BuildPlan(
            [systemComponent, noUninstaller],
            new UninstallPlanOptions { IncludeUnremovable = true });
        var onlyNoUninstaller = Assert.Single(withUnremovable.Items);
        Assert.Equal("NoUninstaller", onlyNoUninstaller.DisplayName);
    }

    [Fact]
    public void BuildPlan_FromRegistryFixture_DetectsDuplicatesAndAnomalies()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var guid = Guid.NewGuid().ToString("N");
        var branchRelative = $@"Software\DiskCleaner.Tests\{guid}\Uninstall";

        using var uninstall = Registry.CurrentUser.CreateSubKey(branchRelative);
        using (var product = uninstall.CreateSubKey("{11111111-1111-1111-1111-111111111111}"))
        {
            product.SetValue("DisplayName", "Java(TM) SE Development Kit 11.0.19");
            product.SetValue("DisplayVersion", "11.0.19");
            product.SetValue("Publisher", "Oracle");
            product.SetValue("InstallDate", "20230101");
            product.SetValue("UninstallString", "\"C:\\Tools\\unins000.exe\"");
        }

        using (var product = uninstall.CreateSubKey("{22222222-2222-2222-2222-222222222222}"))
        {
            product.SetValue("DisplayName", "Java(TM) SE Development Kit 17.0.9");
            product.SetValue("DisplayVersion", "17.0.9");
            product.SetValue("Publisher", "Oracle");
            product.SetValue("InstallDate", "20240101");
            product.SetValue("UninstallString", "\"C:\\Tools\\unins000.exe\"");
        }

        using (var product = uninstall.CreateSubKey("{33333333-3333-3333-3333-333333333333}"))
        {
            product.SetValue("DisplayName", "SuspiciousTool");
            product.SetValue("Publisher", "${PRODUCT_PUBLISHER}");
            product.SetValue("InstallDate", "20240115");
            product.SetValue("UninstallString", "\"C:\\Tools\\suspicious\\unins000.exe\"");
        }

        try
        {
            var registry = new UninstallRegistryService(
                new FakeEnvironment(new TempRoot()),
                branches: [new RegistryBranchSpec(RegistryHiveKind.CurrentUser, branchRelative)]);
            var service = new UninstallPlanService(
                registry,
                analyzer: new InstalledAppAnalyzer());

            var plan = service.BuildPlan();

            var oldJdk = Assert.Single(plan.Items, i => i.DisplayName.Contains("11.0.19", StringComparison.Ordinal));
            var newJdk = Assert.Single(plan.Items, i => i.DisplayName.Contains("17.0.9", StringComparison.Ordinal));
            var suspicious = Assert.Single(plan.Items, i => i.DisplayName == "SuspiciousTool");

            Assert.True(oldJdk.IsOldVersion && oldJdk.IsDuplicate);
            Assert.False(oldJdk.IsEnabled);
            Assert.True(newJdk.IsDuplicate && !newJdk.IsOldVersion);
            Assert.Contains("17.0.9", oldJdk.Note);
            Assert.True(suspicious.NeedsReview);
            Assert.Equal(UninstallPlanItem.ReviewManuallyGroupName, suspicious.GroupName);
            Assert.Equal(1, plan.ReviewManuallyCount);
            Assert.Equal(2, plan.RecommendedCount);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\DiskCleaner.Tests\{guid}", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void DirectoryScannerFolderSizeProvider_MeasuresRealFolderBytes()
    {
        using var root = new TempRoot();
        var installDir = root.Combine("Java 17");
        root.CreateFile("Java 17\\bin\\java.exe", 3000);
        root.CreateFile("Java 17\\lib\\modules", 7000);

        var provider = new DirectoryScannerFolderSizeProvider();
        var size = provider.MeasureFolderBytes(installDir);

        Assert.Equal(10000, size);
    }

    private static InstalledApp InstalledApp(
        string name,
        string installDate,
        string publisher,
        string? version = null,
        string? location = null,
        string? uninstall = null,
        bool isSystemComponent = false) => new()
    {
        ProductCode = "{" + Guid.NewGuid() + "}",
        ScopeKey = nameof(InstalledAppScope.LocalMachine64),
        DisplayName = name,
        InstallDate = installDate,
        Publisher = publisher,
        DisplayVersion = version,
        InstallLocation = location,
        EstimatedSizeBytes = 4096,
        IsSystemComponent = isSystemComponent,
        UninstallString = uninstall ?? "\"C:\\Tools\\unins000.exe\""
    };

    private sealed class FakeFolderSizeProvider(long size) : IUninstallFolderSizeProvider
    {
        public long? MeasureFolderBytes(string path) => size;
    }
}
