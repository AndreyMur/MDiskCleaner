using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Uninstall;
using DiskCleaner.Gui.Services;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

/// <summary>Контекст теста экрана «Деинсталляция и зачистка»: подмены + ViewModel.</summary>
internal sealed record UninstallerHarness(
    FakeUninstallerPlanSource Source,
    FakeUninstallerExecutor Executor,
    FakeUninstallerDialogs Dialogs,
    UninstallerViewModel ViewModel);

/// <summary>
/// Подмены и синтетические данные для тестов экрана «Деинсталляция и зачистка»
/// (задача #123): установленное ПО с пометками (дубль/старая версия/проверить вручную),
/// осиротевшие записи Uninstall, версия Windows SDK, фейковые источник, исполнитель и диалоги.
/// </summary>
internal static class UninstallerTestSupport
{
    public const string JdkOldCode = "{11111111-1111-1111-1111-111111111111}";
    public const string JdkNewCode = "{22222222-2222-2222-2222-222222222222}";
    public const string ReviewCode = "{33333333-3333-3333-3333-333333333333}";
    public const string GhostCode = "{44444444-4444-4444-4444-444444444444}";
    public const string SdkComponentCode = "{55555555-5555-5555-5555-555555555555}";
    public const string SdkVersion = "10.1.19041.5609";

    public static UninstallerHarness CreateHarness(Func<UninstallerSnapshot>? snapshotBuilder = null)
    {
        var source = new FakeUninstallerPlanSource
        {
            Handler = () => snapshotBuilder?.Invoke() ?? BuildTypicalSnapshot()
        };
        var executor = new FakeUninstallerExecutor();
        var dialogs = new FakeUninstallerDialogs();

        var vm = new UninstallerViewModel(source, executor, dialogs);
        return new UninstallerHarness(source, executor, dialogs, vm);
    }

    /// <summary>Снимок, похожий на эталонную машину: дубль JDK (старая версия), аномальная запись, осиротевшая запись и SDK-компонент.</summary>
    public static UninstallerSnapshot BuildTypicalSnapshot()
    {
        var apps = new List<InstalledApp>
        {
            InstalledApp(JdkOldCode, "Java SE Development Kit 11.0.19", "Oracle", version: "11.0.19"),
            InstalledApp(JdkNewCode, "Java SE Development Kit 17.0.9", "Oracle", version: "17.0.9"),
            InstalledApp(ReviewCode, "SomeTool", "   ", version: "1.0"),
            InstalledApp(GhostCode, "Ghost", "ACME", version: "1.0", location: @"C:\Program Files\GhostApp", uninstall: @"C:\Program Files\GhostApp\unins000.exe"),
            InstalledApp(SdkComponentCode, "Windows SDK Desktop Tools x64", "Microsoft Corporation", version: SdkVersion)
        };

        var jdkOld = PlanItem(JdkOldCode, "Java SE Development Kit 11.0.19", "Oracle", marks: [UninstallPlanMarkKind.Duplicate, UninstallPlanMarkKind.OldVersion], note: "Держать последнюю (17.0.9), удалить 11.0.19.");
        var jdkNew = PlanItem(JdkNewCode, "Java SE Development Kit 17.0.9", "Oracle");
        var review = PlanItem(ReviewCode, "SomeTool", "Проверить вручную", marks: [UninstallPlanMarkKind.ReviewManually], note: "Пустой издатель — проверьте вручную.");
        var sdk = PlanItem(SdkComponentCode, "Windows SDK Desktop Tools x64", "Microsoft Corporation");

        var plan = new UninstallPlan { Items = [jdkOld, jdkNew, review, sdk] };
        var orphans = new List<UninstallOrphanRegistryMatch>
        {
            new(apps[3], ["Каталог установки отсутствует: C:\\Program Files\\GhostApp"], false)
        };

        return new UninstallerSnapshot { Apps = apps, Plan = plan, Orphans = orphans };
    }

    public static InstalledApp InstalledApp(
        string productCode,
        string name,
        string publisher,
        string version,
        string? location = null,
        string? uninstall = null) => new()
    {
        ProductCode = productCode,
        ScopeKey = nameof(InstalledAppScope.LocalMachine64),
        DisplayName = name,
        Publisher = publisher,
        DisplayVersion = version,
        InstallLocation = location,
        UninstallString = uninstall ?? $"\"C:\\Program Files\\{name.Replace(" ", "")}\\unins000.exe\"",
        EstimatedSizeBytes = 100_000_000
    };

    public static UninstallPlanItem PlanItem(
        string productCode,
        string name,
        string group,
        IReadOnlyList<UninstallPlanMarkKind>? marks = null,
        string? note = null,
        long estimatedBytes = 100_000_000,
        long? sizeOnCDriveBytes = 100_000_000,
        string? path = @"C:\Program Files\App") => new()
    {
        Key = $"app:{nameof(InstalledAppScope.LocalMachine64)}:{productCode}",
        ProductCode = productCode,
        ScopeKey = nameof(InstalledAppScope.LocalMachine64),
        DisplayName = name,
        GroupName = group,
        Path = path,
        EstimatedSizeBytes = estimatedBytes,
        SizeOnCDriveBytes = sizeOnCDriveBytes,
        RequiresAdmin = true,
        Marks = marks ?? Array.Empty<UninstallPlanMarkKind>(),
        Note = note,
        IsEnabled = false
    };

    public static UninstallExecutionReport BuildExecutionReport(
        IReadOnlyList<InstalledApp> apps,
        bool rebootRequired = false) => new()
    {
        Entries = apps.Select(app => new UninstallExecutionEntry(
            new UninstallExecutionItem(app, new UninstallCommand(UninstallerKind.Inno, app.UninstallString ?? "unins000.exe", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", Silent: true)),
            rebootRequired ? UninstallExecutionOutcome.RebootRequired : UninstallExecutionOutcome.Uninstalled,
            rebootRequired ? 3010 : 0,
            rebootRequired ? "Требуется перезагрузка (код 3010)." : "Деинсталляция завершена успешно (код 0).",
            DateTime.UtcNow)).ToList()
    };

    public static SdkBulkUninstallReport BuildSdkReport(string displayVersion = SdkVersion, bool rebootRequired = false) => new()
    {
        DisplayVersion = displayVersion,
        PassesUsed = 1,
        Components =
        [
            new SdkComponentResult(
                SdkComponentCode,
                "Windows SDK Desktop Tools x64",
                displayVersion,
                1,
                rebootRequired ? UninstallExecutionOutcome.RebootRequired : UninstallExecutionOutcome.Uninstalled,
                rebootRequired ? 3010 : 0,
                rebootRequired ? "Требуется перезагрузка (код 3010)." : "Компонент удалён.")
        ],
        RemainingProductCodes = [],
        RemainingDisplayNames = []
    };

    public static CleanReport BuildOrphanCleanReport(bool removed = true)
    {
        var item = new CleanupItem
        {
            Key = "orphan-test",
            DisplayName = "Ghost",
            Category = DiskCleaner.Core.Models.CleanupCategory.Leftover,
            RegistryDeletePath = "HKEY_LOCAL_MACHINE\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Ghost"
        };
        return new CleanReport
        {
            Entries =
            [
                new CleanEntry(item, removed ? CleanOutcome.RegistryEntryDeleted : CleanOutcome.Error, 0,
                    removed ? "Запись реестра удалена." : "Не удалось удалить запись реестра.")
            ],
            DryRun = false,
            Elapsed = TimeSpan.FromMilliseconds(40)
        };
    }
}

/// <summary>Фейковый источник плана: возвращает синтетический снимок без IO.</summary>
internal sealed class FakeUninstallerPlanSource : IUninstallerPlanSource
{
    public Func<UninstallerSnapshot>? Handler { get; set; }

    public int Calls { get; private set; }

    public UninstallerSnapshot Build()
    {
        Calls++;
        return Handler?.Invoke() ?? new UninstallerSnapshot
        {
            Apps = [],
            Plan = new UninstallPlan(),
            Orphans = []
        };
    }
}

/// <summary>Фейковый исполнитель: запоминает вход и возвращает синтетические отчёты.</summary>
internal sealed class FakeUninstallerExecutor : IUninstallerExecutor
{
    public Func<IReadOnlyList<InstalledApp>, UninstallExecutionReport>? AppsHandler { get; set; }

    public Func<string, SdkBulkUninstallReport>? SdkHandler { get; set; }

    public Func<IReadOnlyList<UninstallOrphanRegistryMatch>, CleanReport>? OrphansHandler { get; set; }

    public int AppsCalls { get; private set; }

    public int SdkCalls { get; private set; }

    public int OrphansCalls { get; private set; }

    public IReadOnlyList<InstalledApp>? LastApps { get; private set; }

    public string? LastSdkVersion { get; private set; }

    public IReadOnlyList<UninstallOrphanRegistryMatch>? LastOrphans { get; private set; }

    public IProgress<SdkBulkUninstallProgress>? LastSdkProgress { get; private set; }

    public Task<UninstallExecutionReport> UninstallAppsAsync(
        IReadOnlyList<InstalledApp> apps,
        UninstallExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        AppsCalls++;
        LastApps = apps.ToList();
        var report = AppsHandler?.Invoke(LastApps) ?? UninstallerTestSupport.BuildExecutionReport(LastApps);
        return Task.FromResult(report);
    }

    public Task<SdkBulkUninstallReport> UninstallSdkVersionAsync(
        string displayVersion,
        SdkBulkUninstallOptions? options = null,
        IProgress<SdkBulkUninstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        SdkCalls++;
        LastSdkVersion = displayVersion;
        LastSdkProgress = progress;
        var report = SdkHandler?.Invoke(displayVersion) ?? UninstallerTestSupport.BuildSdkReport(displayVersion);
        return Task.FromResult(report);
    }

    public Task<CleanReport> RemoveOrphanRecordsAsync(
        IReadOnlyList<UninstallOrphanRegistryMatch> orphanRecords,
        CancellationToken cancellationToken = default)
    {
        OrphansCalls++;
        LastOrphans = orphanRecords.ToList();
        var report = OrphansHandler?.Invoke(LastOrphans) ?? UninstallerTestSupport.BuildOrphanCleanReport();
        return Task.FromResult(report);
    }
}

/// <summary>Фейковые диалоги: запоминают текст подтверждений, пошаговые подтверждения, отчёты и запрос перезагрузки.</summary>
internal sealed class FakeUninstallerDialogs : IUninstallerDialogService
{
    public bool ConfirmResult { get; set; } = true;

    public bool ConfirmStepResult { get; set; } = true;

    public UninstallerRebootChoice RebootChoice { get; set; } = UninstallerRebootChoice.Later;

    public string? LastConfirmTitle { get; private set; }

    public string? LastConfirmMessage { get; private set; }

    public int ConfirmStepCalls { get; private set; }

    public UninstallPlanItem? LastConfirmedStep { get; private set; }

    public UninstallRebootRequest? LastRebootRequest { get; private set; }

    public UninstallExecutionReport? ShownUninstallReport { get; private set; }

    public SdkBulkUninstallReport? ShownSdkReport { get; private set; }

    public CleanReport? ShownCleanReport { get; private set; }

    public bool Confirm(string title, string message)
    {
        LastConfirmTitle = title;
        LastConfirmMessage = message;
        return ConfirmResult;
    }

    public bool ConfirmStep(UninstallPlanItem item)
    {
        ConfirmStepCalls++;
        LastConfirmedStep = item;
        return ConfirmStepResult;
    }

    public UninstallerRebootChoice AskReboot(UninstallRebootRequest request)
    {
        LastRebootRequest = request;
        return RebootChoice;
    }

    public void ShowReport(UninstallExecutionReport report) => ShownUninstallReport = report;

    public void ShowSdkReport(SdkBulkUninstallReport report) => ShownSdkReport = report;

    public void ShowCleanReport(CleanReport report) => ShownCleanReport = report;
}
