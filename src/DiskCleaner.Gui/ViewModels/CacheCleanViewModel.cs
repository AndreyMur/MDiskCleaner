using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Localization;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;
using DiskCleaner.Core.Reports;
using DiskCleaner.Core.Scanning;
using DiskCleaner.Gui.Services;

namespace DiskCleaner.Gui.ViewModels;

/// <summary>
/// ViewModel экрана «Очистка кэшей» (фаза 5 модуля 02). Тонкий слой поверх ядра:
/// <list type="bullet">
/// <item>быстрый анализ известных кэшей (профильные объекты) с измерением размеров и
/// проверкой занятости процессов — источник для <see cref="CacheCleanPlanner"/>;</item>
/// <item>группировка «карточек» по менеджеру/приложению с размером, штатной командой и
/// последствиями (FR-2.1, FR-2.5); кэш вне сканируемого диска показывается отдельно (FR-2.3);</item>
/// <item>чекбокс на каждый объект — очистка только при явной отметке (NFR G3), согласие
/// «Ask» отмечается индивидуально (FR-2.11);</item>
/// <item>dry-run-предпросмотр и подтверждение перед выполнением (NFR G3), выполнение с
/// прогрессом, предупреждения о запущенных приложениях (VS Code) и IN_USE, отчёт.</item>
/// </list>
/// Без «тихой» очистки: план — отметки — предпросмотр/подтверждение — отчёт.
/// </summary>
public sealed partial class CacheCleanViewModel : ObservableObject
{
    private readonly IAnalysisCoordinator _coordinator;
    private readonly CacheCleanPlanner _planner;
    private readonly ICacheCleanExecutor _executor;
    private readonly ICacheCleanDialogService _dialogs;
    private readonly string? _scannedDiskRoot;
    private CancellationTokenSource? _operationCts;
    private CacheCleanPlan? _plan;

    public CacheCleanViewModel(
        IAnalysisCoordinator? coordinator = null,
        CacheCleanPlanner? planner = null,
        ICacheCleanExecutor? executor = null,
        ICacheCleanDialogService? dialogs = null,
        string? scannedDiskRoot = null)
    {
        _coordinator = coordinator ?? new AnalysisCoordinator();
        _planner = planner ?? new CacheCleanPlanner();
        _executor = executor ?? new PlanExecutorCacheCleanExecutor();
        _dialogs = dialogs ?? new MessageBoxCacheCleanDialogService();
        _scannedDiskRoot = NormalizeRoot(scannedDiskRoot);
        HasScannedDisk = _scannedDiskRoot is not null;
        StatusText = "Экран очистки кэшей. Нажмите «Сформировать план» — будет выполнен быстрый анализ кэшей и составлен план очистки.";
    }

    public ObservableCollection<CacheGroupViewModel> Groups { get; } = new();

    /// <summary>Был ли задан корень сканирования основного окна (для группы FR-2.3).</summary>
    public bool HasScannedDisk { get; }

    /// <summary>Пояснение о контексте сканирования и кэшах вне сканируемого диска (FR-2.3).</summary>
    public string ContextText => HasScannedDisk
        ? $"В основном окне выполнен скан диска {_scannedDiskRoot}. Кэши, фактический путь которых находится на другом диске, показаны отдельно ниже и предлагаются к очистке (FR-2.3)."
        : "Кэши обнаруживаются по справочнику менеджеров; фактический путь определяется командами менеджера (например, «npm config get cache»), поэтому кэш на другом диске не пропускается.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewDryRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isProgressVisible;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string _statusText = string.Empty;

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    [ObservableProperty]
    private string _selectedSummary = "Ничего не выбрано";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string _warningsText = string.Empty;

    public bool HasWarnings => !string.IsNullOrWhiteSpace(WarningsText);

    /// <summary>План сформирован и показан на экране.</summary>
    [ObservableProperty]
    private bool _hasPlan;

    private bool CanStartOperation => !IsBusy;

    private bool CanCancelOperation => IsBusy;

    private bool CanRunCleaning => !IsBusy && SelectedActions().Count > 0;

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private Task AnalyzeAsync() =>
        RunOperationAsync("Кэш-анализ", AnalyzeCoreAsync);

    [RelayCommand(CanExecute = nameof(CanRunCleaning))]
    private Task PreviewDryRunAsync() =>
        RunOperationAsync("Предпросмотр (dry-run)", PreviewCoreAsync);

    [RelayCommand(CanExecute = nameof(CanRunCleaning))]
    private async Task ExecuteAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var actions = SelectedActions();
        if (actions.Count == 0)
        {
            return;
        }

        var message = BuildConfirmationMessage(actions);
        if (!_dialogs.Confirm("Подтверждение очистки кэшей", message))
        {
            StatusText = "Очистка кэшей отменена пользователем.";
            return;
        }

        await RunOperationAsync("Очистка кэшей", (ct, text) => ExecuteCoreAsync(actions, ct, text));
    }

    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private void Cancel() => _operationCts?.Cancel();

    /// <summary>Выбранные пользователем «карточки» (явная отметка, NFR G3).</summary>
    internal IReadOnlyList<CacheCleanAction> SelectedActions() =>
        Groups
            .SelectMany(g => g.Actions)
            .Where(a => a.IsSelected)
            .Select(a => a.Action)
            .ToList();

    private async Task AnalyzeCoreAsync(CancellationToken ct, IProgress<string> text)
    {
        var scanProgress = new Progress<ScanProgress>(p =>
        {
            var current = string.IsNullOrEmpty(p.CurrentPath)
                ? string.Empty
                : " · " + System.IO.Path.GetFileName(p.CurrentPath);
            text.Report($"каталогов: {p.DirectoriesCompleted} · {CleanReportFormatter.FormatBytes(p.BytesMeasured)}{current}");
        });

        var result = await _coordinator.RunAsync(new AnalysisRunOptions(), scanProgress, ct);
        var plan = _planner.BuildPlan(result.Items);
        ApplyPlan(plan);

        var deferred = plan.Actions.Count(a => a.Method == CacheCleanMethod.DeferredInUse);
        var askCount = plan.CleanableActions.Count(a => a.Consent == CacheConsent.Ask);
        var summary = $"Кэш-анализ за {result.Elapsed.TotalSeconds:F1} с: кэшей найдено {plan.Actions.Count}; " +
                      $"к очистке предложено {plan.CleanableActions.Count} (≈ {CleanReportFormatter.FormatBytes(plan.TotalEstimatedSavingsBytes)}); " +
                      $"требует явного согласия: {askCount}; отложено (используется): {deferred}.";
        if (plan.NotAllowedCount > 0)
        {
            summary += $" Заблокировано правилами: {plan.NotAllowedCount}.";
        }

        StatusText = summary;
    }

    private async Task PreviewCoreAsync(CancellationToken ct, IProgress<string> text)
    {
        var actions = SelectedActions();
        if (actions.Count == 0)
        {
            StatusText = "Ничего не выбрано — отметьте кэши чекбоксами.";
            return;
        }

        var progress = new Progress<CleanProgress>(p =>
            text.Report(ProgressTextFor(p, dryRun: true)));

        var report = await _executor.CleanAsync(
            actions.Select(a => a.Item),
            new CleanOptions { DryRun = true },
            progress,
            ct);

        _dialogs.ShowReport(report);

        StatusText = $"Предпросмотр (dry-run) завершён: выбрано {actions.Count} объектов; ничего не удалено. См. отчёт.";
    }

    private async Task ExecuteCoreAsync(
        IReadOnlyList<CacheCleanAction> actions,
        CancellationToken ct,
        IProgress<string> text)
    {
        var progress = new Progress<CleanProgress>(p =>
            text.Report(ProgressTextFor(p, dryRun: false)));

        var report = await _executor.CleanAsync(
            actions.Select(a => a.Item),
            new CleanOptions { DryRun = false },
            progress,
            ct);

        _dialogs.ShowReport(report);

        var removed = report.Entries.Count(e => IsRemovedOutcome(e.Outcome));
        var skipped = report.Entries.Count(e => IsSkippedOutcome(e.Outcome));
        StatusText = $"Очистка завершена: освобождено {CleanReportFormatter.FormatBytes(report.TotalFreedBytes)}; " +
                     $"удалено объектов: {removed}; пропущено/отложено: {skipped} (причины — в отчёте).";

        if (report.Entries.Count > 0)
        {
            text.Report("обновление плана после очистки…");
            await RefreshPlanAsync(ct);
        }
    }

    private static string ProgressTextFor(CleanProgress p, bool dryRun) =>
        $"{p.CompletedItems}/{p.TotalItems} · {p.CurrentName} · " +
        (dryRun
            ? $"освободит ≈ {CleanReportFormatter.FormatBytes(p.BytesCleaned)}"
            : $"освобождено {CleanReportFormatter.FormatBytes(p.BytesCleaned)}");

    private static bool IsRemovedOutcome(CleanOutcome outcome) =>
        outcome is CleanOutcome.NativeCleaned
            or CleanOutcome.DirectDeleted
            or CleanOutcome.CommandOnlyCleaned
            or CleanOutcome.MovedToRecycleBin
            or CleanOutcome.Uninstalled
            or CleanOutcome.RegistryEntryDeleted
            or CleanOutcome.AlreadyUninstalled;

    private static bool IsSkippedOutcome(CleanOutcome outcome) =>
        outcome is CleanOutcome.InUseSkipped
            or CleanOutcome.Error
            or CleanOutcome.Partial
            or CleanOutcome.Denied
            or CleanOutcome.ElevationDeclined;

    private async Task RefreshPlanAsync(CancellationToken ct)
    {
        try
        {
            var result = await _coordinator.RunAsync(new AnalysisRunOptions(), null, ct);
            ApplyPlan(_planner.BuildPlan(result.Items));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Serilog.Log.Warning(ex, "Cache plan refresh after cleaning failed");
        }
    }

    /// <summary>
    /// Применяет план действий: очищает группы, распределяет «карточки» по менеджеру/приложению,
    /// выносит кэши вне сканируемого диска в отдельную группу (FR-2.3) и обновляет предупреждения.
    /// </summary>
    private void ApplyPlan(CacheCleanPlan plan)
    {
        _plan = plan;
        Groups.Clear();

        var normalSlots = new List<GroupSlot>();
        var offDiskActions = new List<CacheCleanAction>();

        foreach (var action in plan.Actions)
        {
            if (IsOutsideScannedDisk(action.Item))
            {
                offDiskActions.Add(action);
                continue;
            }

            var key = GroupKeyOf(action);
            var slot = normalSlots.FirstOrDefault(s => s.Key == key);
            if (slot is null)
            {
                slot = new GroupSlot(key, title: key, isOffDisk: false);
                normalSlots.Add(slot);
            }

            slot.Actions.Add(action);
        }

        foreach (var slot in normalSlots)
        {
            AddGroup(new CacheGroupViewModel(
                slot.Title,
                slot.Actions.Select(a => new CacheActionViewModel(a, isOffDisk: false, OnSelectionChanged)),
                OnSelectionChanged));
        }

        if (offDiskActions.Count > 0)
        {
            var drives = offDiskActions
                .Select(a => SafeDriveRoot(a.Item.Path))
                .Where(root => root is not null)
                .Select(root => root!.TrimEnd('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var subtitle = $"Фактический путь этих кэшей лежит на диске(ах) {string.Join(", ", drives)} — вне сканируемого диска {_scannedDiskRoot}. Они не пропущены и предлагаются к очистке (FR-2.3).";

            AddGroup(new CacheGroupViewModel(
                "Вне сканируемого диска",
                offDiskActions.Select(a => new CacheActionViewModel(a, isOffDisk: true, OnSelectionChanged)),
                OnSelectionChanged,
                subtitle: subtitle,
                isOffDiskGroup: true));
        }

        WarningsText = ComposeWarnings(plan);
        HasPlan = Groups.Count > 0;
        OnSelectionChanged();
    }

    private void AddGroup(CacheGroupViewModel group)
    {
        if (group.Actions.Count > 0)
        {
            Groups.Add(group);
        }
    }

    /// <summary>Пересчёт выбора: тройные состояния групп, сводка выбранного, доступность команд.</summary>
    private void OnSelectionChanged()
    {
        foreach (var group in Groups)
        {
            group.Refresh();
        }

        var selected = SelectedActions();
        SelectedSummary = selected.Count == 0
            ? "Ничего не выбрано"
            : $"Выбрано: {selected.Count} · освободится ≈ {CleanReportFormatter.FormatBytes(selected.Sum(a => Math.Max(0, a.EstimatedSavingsBytes)))}";

        PreviewDryRunCommand.NotifyCanExecuteChanged();
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Предупреждения: запущенные приложения (VS Code) с отложенными кэшами (FR-2.8/2.10), защитный режим (FR-2.12).</summary>
    private string ComposeWarnings(CacheCleanPlan plan)
    {
        var parts = new List<string>(3);
        var deferred = plan.Actions.Where(a => a.Method == CacheCleanMethod.DeferredInUse).ToList();
        if (deferred.Count > 0)
        {
            var owners = deferred
                .Select(a => InUseMessages.OwnerLabel(a.Item))
                .Where(label => label is not null)
                .Select(label => label!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var text = owners.Count > 0
                ? $"Запущенные приложения ({string.Join(", ", owners)}) используют {deferred.Count} кэшей — они отложены и не будут очищены, пока приложение запущено (FR-2.8, FR-2.10)."
                : $"{deferred.Count} кэшей используются запущенными процессами и отложены (FR-2.8).";
            parts.Add(text);
        }

        if (plan.GuardNote is not null)
        {
            parts.Add(plan.GuardNote);
        }

        if (plan.NotAllowedCount > 0)
        {
            parts.Add($"Заблокировано правилами очистки кэшей: {plan.NotAllowedCount} объект(ов) (вне whitelist VS Code, категория Leftover и т.п.).");
        }

        return string.Join(Environment.NewLine, parts);
    }

    /// <summary>Текст подтверждения перед выполнением: объём, объекты с явным согласием и риском.</summary>
    internal string BuildConfirmationMessage(IReadOnlyList<CacheCleanAction> actions)
    {
        var builder = new StringBuilder();
        builder.Append($"Выполнить очистку {actions.Count} объектов кэшей? Освободится примерно {CleanReportFormatter.FormatBytes(actions.Sum(a => Math.Max(0, a.EstimatedSavingsBytes)))}.");

        var ask = actions.Where(a => a.Consent == CacheConsent.Ask).ToList();
        if (ask.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine($"Объекты, требующие явного согласия ({ask.Count}):");
            foreach (var action in ask)
            {
                builder.AppendLine("• " + ActionTitle(action));
            }
        }

        var risky = actions
            .Where(a => a.Item.Risk != CleanupRisk.Low && a.Consent != CacheConsent.Ask)
            .ToList();
        if (risky.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine($"Средний/высокий риск ({risky.Count}):");
            foreach (var action in risky)
            {
                builder.AppendLine("• " + ActionTitle(action));
            }
        }

        if (ask.Count == 0 && risky.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.Append("Отмеченные объекты имеют низкий риск и очищаются автоматически (после этой отметки).");
        }

        return builder.ToString();
    }

    private static string ActionTitle(CacheCleanAction action) =>
        string.IsNullOrEmpty(action.Item.GroupName)
            ? action.Item.DisplayName
            : $"{action.Item.GroupName}: {action.Item.DisplayName}";

    private static string GroupKeyOf(CacheCleanAction action)
    {
        var group = action.Item.GroupName;
        return string.IsNullOrWhiteSpace(group)
            ? LocalizedNames.Category(action.Item.Category)
            : group!;
    }

    private bool IsOutsideScannedDisk(CleanupItem item)
    {
        if (_scannedDiskRoot is null)
        {
            return false;
        }

        var root = SafeDriveRoot(item.Path);
        return root is not null &&
               !string.Equals(root, _scannedDiskRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string? SafeDriveRoot(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            return System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return null;
        }
    }

    private static string? NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        try
        {
            var full = System.IO.Path.GetFullPath(root);
            return System.IO.Path.GetPathRoot(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return null;
        }
    }

    private async Task RunOperationAsync(string operation, Func<CancellationToken, IProgress<string>, Task> work)
    {
        if (IsBusy)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _operationCts = cts;
        IsBusy = true;
        IsProgressVisible = true;
        ProgressText = operation + "...";

        var progress = new Progress<string>(t => ProgressText = operation + ": " + t);

        try
        {
            await work(cts.Token, progress);
        }
        catch (OperationCanceledException)
        {
            StatusText = operation + " отменена.";
        }
        catch (Exception ex)
        {
            StatusText = $"Ошибка: {ex.Message}";
            Serilog.Log.Error(ex, "Cache clean operation '{Operation}' failed", operation);
        }
        finally
        {
            IsProgressVisible = false;
            IsBusy = false;
            ProgressText = string.Empty;
            _operationCts = null;
        }
    }

    private sealed class GroupSlot
    {
        public GroupSlot(string key, string title, bool isOffDisk)
        {
            Key = key;
            Title = title;
            IsOffDisk = isOffDisk;
        }

        public string Key { get; }

        public string Title { get; }

        public bool IsOffDisk { get; }

        public List<CacheCleanAction> Actions { get; } = new();
    }
}
