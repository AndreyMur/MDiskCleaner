using System.IO;
using DiskCleaner.Core.Leftovers;
using DiskCleaner.Core.Models;
using DiskCleaner.Gui.Services;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

/// <summary>Контекст теста экрана «Остатки»: подмены + ViewModel.</summary>
internal sealed record LeftoverScanHarness(
    FakeLeftoverPlanSource Source,
    FakeLeftoverCleanExecutor Executor,
    FakeLeftoverExclusionsService Exclusions,
    FakeLeftoverDialogs Dialogs,
    LeftoverScanViewModel ViewModel);

/// <summary>
/// Подмены и синтетические данные для тестов экрана «Остатки» (фаза 5 модуля 03, #94–#98):
/// синтетический план эталонной машины (апдейтеры, конфиг удалённой Android Studio, осиротевший
/// каталог Program Files, Windows.old), фейковые источник плана, исполнитель, исключения и диалоги.
/// </summary>
internal static class LeftoverTestSupport
{
    public static LeftoverScanHarness CreateHarness()
    {
        var exclusions = new FakeLeftoverExclusionsService();
        var source = new FakeLeftoverPlanSource
        {
            Handler = () => BuildTypicalPlan(exclusions.Load())
        };
        var executor = new FakeLeftoverCleanExecutor();
        var dialogs = new FakeLeftoverDialogs();

        var vm = new LeftoverScanViewModel(source, executor, exclusions, dialogs);
        return new LeftoverScanHarness(source, executor, exclusions, dialogs, vm);
    }

    /// <summary>План, похожий на план эталонной машины: апдейтеры, конфиг удалённой IDE, осиротевшие Program Files/ProgramData, Windows.old.</summary>
    public static LeftoverPlan BuildTypicalPlan(IReadOnlyCollection<string>? exclusions = null)
    {
        var excluded = exclusions ?? Array.Empty<string>();
        var items = new List<LeftoverPlanItem>();

        Add(items, Updater("lm-studio-updater", size: 1_200_000_000, lastWrite: new DateTime(2026, 2, 10, 9, 30, 0, DateTimeKind.Utc)));
        Add(items, Updater("qwen-updater", size: 250_000_000, lastWrite: new DateTime(2026, 1, 5, 18, 0, 0, DateTimeKind.Utc)));
        Add(items, Updater("kimi-desktop_updater", size: 180_000_000));
        Add(items, AndroidStudioConfig());
        Add(items, OrphanProgramFiles("OrphanTool"));
        Add(items, OrphanProgramData("Windows4DDiGFileRepair"));
        Add(items, WindowsOld());

        return new LeftoverPlan { Items = items };

        void Add(List<LeftoverPlanItem> list, LeftoverPlanItem item)
        {
            if (!ExclusionsStore.IsExcluded(excluded, item.Path, item.DisplayName))
            {
                list.Add(item);
            }
        }
    }

    /// <summary>План с группой, где один объект опасен (требует подтверждения), а другой — нет.</summary>
    public static LeftoverPlan BuildMixedGroupPlan()
    {
        return new LeftoverPlan
        {
            Items =
            [
                Updater("qwen-updater", size: 250_000_000),
                NewItem(
                    @"C:\Users\dev\AppData\Local\dangerous-updater",
                    "dangerous-updater",
                    "Остатки апдейтеров",
                    LeftoverReason.UpdaterFolder,
                    "Имя соответствует маске апдейтера.",
                    size: 10_000_000,
                    risk: CleanupRisk.Medium,
                    requiresAdmin: true,
                    requiresConfirmation: true)
            ]
        };
    }

    public static LeftoverPlanItem Updater(
        string name,
        long size = 100_000_000,
        DateTime? lastWrite = null) =>
        NewItem(
            @"C:\Users\dev\AppData\Local\" + name,
            name,
            LeftoverRuleEngine.UpdatersGroupName,
            LeftoverReason.UpdaterFolder,
            "Имя соответствует маске апдейтера (*-updater/updater/update/_updater): каталог остаётся после установки приложения (FR-3.2).",
            size: size,
            lastWrite: lastWrite);

    public static LeftoverPlanItem AndroidStudioConfig() =>
        NewItem(
            @"C:\Users\dev\AppData\Local\Google\AndroidStudio2025.3.2",
            "AndroidStudio2025.3.2",
            LeftoverRuleEngine.RemovedAppConfigsGroupName,
            LeftoverReason.ConfigOfRemovedApp,
            "Конфиг-каталог «AndroidStudio2025.3.2» продукта Android Studio: приложение отсутствует в реестре Uninstall, процессов из каталога не запущено (FR-3.3).",
            size: 1_100_000_000,
            risk: CleanupRisk.Medium,
            fileCount: 4200,
            lastWrite: new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));

    public static LeftoverPlanItem OrphanProgramFiles(string name = "OrphanTool") =>
        NewItem(
            @"C:\Program Files\" + name,
            name,
            "Осиротевшие папки в Program Files",
            LeftoverReason.OrphanProgramFiles,
            "Осиротевший каталог: отсутствует в реестре Uninstall, известный бренд удалённого продукта (FR-3.4, §5).",
            size: 4_500_000,
            risk: CleanupRisk.Medium,
            requiresAdmin: true,
            requiresConfirmation: true);

    public static LeftoverPlanItem OrphanProgramData(string name = "Windows4DDiGFileRepair") =>
        NewItem(
            @"C:\ProgramData\" + name,
            name,
            "Осиротевшие папки в ProgramData",
            LeftoverReason.OrphanProgramData,
            "Осиротевший каталог: отсутствует в реестре Uninstall, известный бренд удалённого продукта (FR-3.4, §5).",
            size: 8_000_000,
            risk: CleanupRisk.Medium,
            requiresAdmin: true,
            requiresConfirmation: true);

    public static LeftoverPlanItem WindowsOld() =>
        NewItem(
            @"C:\Windows.old",
            "Windows.old",
            LeftoverRuleEngine.WindowsOldGroupName,
            LeftoverReason.WindowsOld,
            "Каталог предыдущей версии Windows остался после обновления ОС (FR-3.5).",
            size: 40_000_000_000,
            risk: CleanupRisk.Medium,
            requiresAdmin: true,
            requiresConfirmation: true,
            recommended: "Storage Sense или Очистка диска (cleanmgr) → «Предыдущие установки Windows».");

    private static LeftoverPlanItem NewItem(
        string path,
        string displayName,
        string group,
        LeftoverReason reason,
        string reasonText,
        long? size,
        CleanupRisk risk = CleanupRisk.Low,
        long? fileCount = null,
        DateTime? lastWrite = null,
        bool requiresAdmin = false,
        bool requiresConfirmation = false,
        string? recommended = null)
    {
        var fullPath = Path.GetFullPath(path);
        return new LeftoverPlanItem
        {
            Key = fullPath,
            Path = fullPath,
            DisplayName = displayName,
            GroupName = group,
            Reason = reason,
            ReasonText = reasonText,
            Risk = risk,
            SizeBytes = size,
            FileCount = fileCount,
            LastWriteTimeUtc = lastWrite,
            RequiresAdmin = requiresAdmin,
            RequiresConfirmation = requiresConfirmation,
            RecommendedRemovalMethod = recommended,
            IsEnabled = false
        };
    }
}

/// <summary>Фейковый источник плана: возвращает синтетический план без IO.</summary>
internal sealed class FakeLeftoverPlanSource : ILeftoverPlanSource
{
    public Func<LeftoverPlan>? Handler { get; set; }

    public int Calls { get; private set; }

    public LeftoverPlan BuildPlan()
    {
        Calls++;
        return Handler?.Invoke() ?? new LeftoverPlan();
    }
}

/// <summary>Фейковый исполнитель: запоминает вход и возвращает синтетический отчёт.</summary>
internal sealed class FakeLeftoverCleanExecutor : ILeftoverCleanExecutor
{
    public Func<IReadOnlyList<LeftoverPlanItem>, LeftoverCleanOptions, LeftoverCleanReport>? Handler { get; set; }

    public int Calls { get; private set; }

    public IReadOnlyList<LeftoverPlanItem>? LastItems { get; private set; }

    public LeftoverCleanOptions? LastOptions { get; private set; }

    public Task<LeftoverCleanReport> CleanAsync(
        IReadOnlyList<LeftoverPlanItem> selectedItems,
        LeftoverCleanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastItems = selectedItems.ToList();
        LastOptions = options ?? new LeftoverCleanOptions();
        var report = Handler?.Invoke(LastItems, LastOptions) ?? DefaultReport(LastItems, LastOptions);
        return Task.FromResult(report);
    }

    private static LeftoverCleanReport DefaultReport(
        IReadOnlyList<LeftoverPlanItem> items,
        LeftoverCleanOptions options)
    {
        var entries = items.Select(item =>
        {
            var outcome = options.DryRun
                ? LeftoverCleanOutcome.DryRun
                : item.RequiresConfirmation && !options.ConfirmDangerous
                    ? LeftoverCleanOutcome.ConfirmationRequired
                    : LeftoverCleanOutcome.DirectDeleted;
            var note = options.DryRun
                ? $"Будет удалено. Основание: {item.ReasonText}"
                : $"Удалено. Основание: {item.ReasonText}";
            return new LeftoverDeletionEntry(item, outcome, item.SizeBytes ?? 0, note);
        }).ToList();

        return new LeftoverCleanReport
        {
            Entries = entries,
            DryRun = options.DryRun,
            Elapsed = TimeSpan.FromMilliseconds(50)
        };
    }
}

/// <summary>Фейковое хранилище исключений в памяти (FR-3.7).</summary>
internal sealed class FakeLeftoverExclusionsService : ILeftoverExclusionsService
{
    private readonly List<string> _entries = new();

    public string FilePath { get; } =
        Path.Combine(Path.GetTempPath(), "DiskCleaner.Gui.Tests", "exclusions.json");

    public IReadOnlyList<string> Load() => _entries.ToList();

    public void AddPath(string path) => AddEntry(Path.TrimEndingDirectorySeparator(path));

    public void AddBrand(string brandName) => AddEntry(brandName.Trim());

    public void AddEntry(string entry)
    {
        var normalized = entry.Trim();
        if (normalized.Length > 0 &&
            !_entries.Any(e => string.Equals(e, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            _entries.Add(normalized);
        }
    }

    public void Remove(string entry) => _entries.RemoveAll(e =>
        string.Equals(e, entry.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool IsPathEntry(string entry) => ExclusionsStore.IsPathEntry(entry);
}

/// <summary>Фейковые диалоги: запоминают текст подтверждения, выбранный способ исключения и отчёт.</summary>
internal sealed class FakeLeftoverDialogs : ILeftoverDialogService
{
    public bool ConfirmResult { get; set; } = true;

    public LeftoverExclusionChoice ExclusionResult { get; set; } = LeftoverExclusionChoice.Path;

    public string? LastConfirmTitle { get; private set; }

    public string? LastConfirmMessage { get; private set; }

    public LeftoverCleanReport? ShownReport { get; private set; }

    public bool Confirm(string title, string message)
    {
        LastConfirmTitle = title;
        LastConfirmMessage = message;
        return ConfirmResult;
    }

    public LeftoverExclusionChoice AskAddExclusion(string path, string brand) => ExclusionResult;

    public void ShowReport(LeftoverCleanReport report) => ShownReport = report;
}
