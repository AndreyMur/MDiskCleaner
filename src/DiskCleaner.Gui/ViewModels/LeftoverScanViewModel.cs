using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskCleaner.Core.Leftovers;
using DiskCleaner.Core.Reports;
using DiskCleaner.Gui.Services;

namespace DiskCleaner.Gui.ViewModels;

/// <summary>
/// ViewModel экрана «Остатки» (фаза 5 модуля 03, задачи #94–#98). Тонкий слой поверх ядра:
/// <list type="bullet">
/// <item>поиск остатков и группировка кандидатов в группы («Остатки апдейтеров», «Конфиги
/// удалённых программ», «Осиротевшие папки Program Files/ProgramData», «Предыдущая версия
/// Windows») с размером, датой и основанием «почему это остаток» (FR-3.2–3.6);</item>
/// <item>все кандидаты выключены по умолчанию, включение чекбоксом (FR-3.8); опасные объекты
/// (Program Files/ProgramData/Windows.old) требуют обязательного пообъектного подтверждения
/// (FR-3.4, §5);</item>
/// <item>dry-run-предпросмотр плана (FR-3.8) и выполнение подтверждённых удалений с
/// отображением админ-шагов и одного UAC-подъёма, журнал с основанием (FR-3.5/3.6, §5);</item>
/// <item>«Добавить в исключения» по пути или бренду с перестроением плана (FR-3.7).</item>
/// </list>
/// </summary>
public sealed partial class LeftoverScanViewModel : ObservableObject
{
    private readonly ILeftoverPlanSource _source;
    private readonly ILeftoverCleanExecutor _executor;
    private readonly ILeftoverExclusionsService _exclusions;
    private readonly ILeftoverDialogService _dialogs;
    private CancellationTokenSource? _operationCts;
    private LeftoverPlan? _plan;

    public LeftoverScanViewModel(
        ILeftoverPlanSource? source = null,
        ILeftoverCleanExecutor? executor = null,
        ILeftoverExclusionsService? exclusions = null,
        ILeftoverDialogService? dialogs = null)
    {
        _source = source ?? new LeftoverPlanSource();
        _executor = executor ?? new LeftoverCleanExecutor();
        _exclusions = exclusions ?? new LeftoverExclusionsService();
        _dialogs = dialogs ?? new MessageBoxLeftoverDialogService();
        StatusText = "Экран остатков. Нажмите «Найти остатки» — будут показаны остатки удалённых программ по группам с основаниями.";
    }

    public ObservableCollection<LeftoverGroupViewModel> Groups { get; } = new();

    /// <summary>Пояснение о работе экрана и правилах безопасности (FR-3.8, §5).</summary>
    public string ContextText =>
        "Остатки удалённых программ и следов установок: папки-апдейтеры, конфиги удалённых приложений, " +
        "осиротевшие каталоги Program Files/ProgramData и Windows.old. Все кандидаты по умолчанию выключены — " +
        "включите чекбоксом только то, что готовы удалить. Опасные объекты требуют обязательного пообъектного " +
        "подтверждения; удаление из Program Files/ProgramData и Windows.old выполняется с админ-правами (UAC).";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewDryRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddExclusionCommand))]
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExclusions))]
    private string _exclusionsSummary = string.Empty;

    public bool HasExclusions => !string.IsNullOrWhiteSpace(ExclusionsSummary);

    /// <summary>План сформирован и показан на экране.</summary>
    [ObservableProperty]
    private bool _hasPlan;

    /// <summary>Сервис исключений для окна управления исключениями (FR-3.7).</summary>
    internal ILeftoverExclusionsService ExclusionsService => _exclusions;

    private bool CanStartOperation => !IsBusy;

    private bool CanCancelOperation => IsBusy;

    private bool CanRunCleaning => !IsBusy && SelectedItems().Count > 0;

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private Task ScanAsync() =>
        RunOperationAsync("Поиск остатков", ScanCoreAsync);

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

        var items = SelectedItems();
        if (items.Count == 0)
        {
            return;
        }

        var message = BuildExecutionMessage(items);
        if (!_dialogs.Confirm("Подтверждение удаления остатков", message))
        {
            StatusText = "Удаление остатков отменено пользователем.";
            return;
        }

        await RunOperationAsync("Удаление остатков", (ct, text) => ExecuteCoreAsync(items, ct, text));
    }

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private Task AddExclusionAsync(LeftoverItemViewModel? item) =>
        RunOperationAsync("Добавление в исключения", (ct, text) => AddExclusionCoreAsync(item, ct, text));

    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private void Cancel() => _operationCts?.Cancel();

    /// <summary>Выбранные пользователем кандидаты (явное включение чекбоксом, FR-3.8).</summary>
    internal IReadOnlyList<LeftoverPlanItem> SelectedItems() =>
        Groups
            .SelectMany(g => g.Items)
            .Where(i => i.IsSelected)
            .Select(i => i.Item)
            .ToList();

    /// <summary>Перестроение плана после изменений исключений (FR-3.7) или очистки.</summary>
    internal Task RefreshPlanAsync() => RunOperationAsync("Обновление плана остатков", ScanCoreAsync);

    private async Task ScanCoreAsync(CancellationToken ct, IProgress<string> text)
    {
        ct.ThrowIfCancellationRequested();
        text.Report("чтение реестра Uninstall и обход верхних уровней каталогов…");
        var startedAt = Stopwatch.StartNew();
        var plan = await Task.Run(() => _source.BuildPlan(), ct);
        startedAt.Stop();
        ct.ThrowIfCancellationRequested();

        ApplyPlan(plan);
        StatusText = BuildScanSummary(startedAt.Elapsed, plan);
    }

    private async Task PreviewCoreAsync(CancellationToken ct, IProgress<string> text)
    {
        var items = SelectedItems();
        if (items.Count == 0)
        {
            StatusText = "Ничего не выбрано — включите чекбоксами остатки для предпросмотра.";
            return;
        }

        var report = await _executor.CleanAsync(
            items,
            new LeftoverCleanOptions { DryRun = true },
            ct);

        _dialogs.ShowReport(report);

        var skipped = report.Entries.Count(e => e.Outcome == LeftoverCleanOutcome.LiveObjectSkipped);
        StatusText = $"Предпросмотр (dry-run) завершён: выбрано {items.Count} остатков; ничего не удалено. " +
                     $"Будет пропущено (используется): {skipped}. Подробности — в отчёте.";
    }

    private async Task ExecuteCoreAsync(
        IReadOnlyList<LeftoverPlanItem> items,
        CancellationToken ct,
        IProgress<string> text)
    {
        var options = new LeftoverCleanOptions
        {
            DryRun = false,
            ConfirmDangerous = items.Any(i => i.RequiresConfirmation)
        };

        var report = await _executor.CleanAsync(items, options, ct);
        _dialogs.ShowReport(report);

        var completedText = $"Удаление завершено: удалено {report.DeletedCount}, пропущено {report.SkippedCount}; " +
                            $"освобождено {CleanReportFormatter.FormatBytes(report.TotalFreedBytes)}. Журнал с основаниями — в отчёте.";
        StatusText = completedText;

        if (report.Entries.Count > 0)
        {
            text.Report("обновление плана после удаления…");
            await RefreshPlanAfterCleanAsync(ct, completedText);
        }
    }

    private async Task AddExclusionCoreAsync(
        LeftoverItemViewModel? item,
        CancellationToken ct,
        IProgress<string> text)
    {
        if (item is null)
        {
            StatusText = "Выберите остаток, который нужно добавить в исключения.";
            return;
        }

        var planItem = item.Item;
        var choice = _dialogs.AskAddExclusion(planItem.Path, planItem.DisplayName);
        if (choice == LeftoverExclusionChoice.None)
        {
            StatusText = "В исключения не добавлено.";
            return;
        }

        var entry = choice == LeftoverExclusionChoice.Path ? planItem.Path : planItem.DisplayName;
        if (choice == LeftoverExclusionChoice.Path)
        {
            _exclusions.AddPath(planItem.Path);
        }
        else
        {
            _exclusions.AddBrand(planItem.DisplayName);
        }

        StatusText = $"Добавлено в исключения «{entry}». Обновляю план…";
        await RefreshPlanAfterCleanAsync(ct);
    }

    /// <summary>Повторное построение плана без отдельного заголовка операции (после изменений).</summary>
    private async Task RefreshPlanAfterCleanAsync(CancellationToken ct, string? prefix = null)
    {
        var plan = await Task.Run(() => _source.BuildPlan(), ct);
        ct.ThrowIfCancellationRequested();
        ApplyPlan(plan);
        var summary = BuildScanSummary(TimeSpan.Zero, plan, suffix: " План обновлён.");
        StatusText = prefix is null ? summary : prefix + " " + summary;
    }

    /// <summary>
    /// Применяет план: группирует кандидатов по <see cref="LeftoverPlanItem.GroupName"/> в группы
    /// (FR-3.2–3.6) и обновляет предупреждения о пообъектном подтверждении опасных объектов.
    /// </summary>
    private void ApplyPlan(LeftoverPlan plan)
    {
        _plan = plan;
        Groups.Clear();

        var slots = new List<GroupSlot>();
        foreach (var item in plan.Items)
        {
            var slot = slots.FirstOrDefault(s => string.Equals(s.Title, item.GroupName, StringComparison.Ordinal));
            if (slot is null)
            {
                slot = new GroupSlot(item.GroupName);
                slots.Add(slot);
            }

            slot.Items.Add(item);
        }

        foreach (var slot in slots)
        {
            var items = slot.Items.Select(item => new LeftoverItemViewModel(
                item,
                ConfirmDangerous,
                OnSelectionChanged)).ToList();
            Groups.Add(new LeftoverGroupViewModel(slot.Title, items, OnSelectionChanged));
        }

        WarningsText = ComposeWarnings(plan);
        ExclusionsSummary = ComposeExclusionsSummary(plan);
        HasPlan = plan.Items.Count > 0;
        OnSelectionChanged();
    }

    /// <summary>Пообъектное подтверждение опасного остатка (Program Files/ProgramData/Windows.old, FR-3.8, §5).</summary>
    private bool ConfirmDangerous(LeftoverPlanItem item)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Удалить «{item.DisplayName}»?");
        builder.AppendLine();
        builder.AppendLine($"Путь: {item.Path}");
        builder.AppendLine();
        builder.AppendLine("Это опасный объект (Program Files/ProgramData/Windows.old) — удаление требует обязательного пообъектного подтверждения (FR-3.4, §5).");
        if (item.RequiresAdmin)
        {
            builder.AppendLine("Удаление будет выполнено через elevated-процесс с одним запросом UAC.");
        }

        builder.AppendLine();
        builder.AppendLine($"Почему это остаток: {item.ReasonText}");
        return _dialogs.Confirm("Подтверждение удаления остатка", builder.ToString());
    }

    private string BuildScanSummary(TimeSpan elapsed, LeftoverPlan plan, string? suffix = null)
    {
        if (plan.Items.Count == 0)
        {
            return "Остатки не найдены: каталоги сопоставлены с установленным ПО, исключения учтены." + (suffix ?? string.Empty);
        }

        var requiresConfirmation = plan.Items.Count(i => i.RequiresConfirmation);
        var admin = plan.Items.Count(i => i.RequiresAdmin);
        var time = elapsed.TotalSeconds > 0 ? $" за {elapsed.TotalSeconds:F1} с" : string.Empty;
        return $"Остатки{time}: найдено {plan.Items.Count} ({CleanReportFormatter.FormatBytes(plan.TotalBytes)}); " +
               $"требуют пообъектного подтверждения: {requiresConfirmation}; удаление только с админ-правами: {admin}." +
               (suffix ?? string.Empty);
    }

    private string ComposeExclusionsSummary(LeftoverPlan plan)
    {
        var exclusions = _exclusions.Load();
        return exclusions.Count == 0
            ? string.Empty
            : $"Исключений пользователя: {exclusions.Count} (файл {_exclusions.FilePath}). Кандидаты из исключений не предлагаются.";
    }

    /// <summary>Предупреждение об опасных объектах, требующих пообъектного подтверждения (FR-3.4, §5).</summary>
    private string ComposeWarnings(LeftoverPlan plan)
    {
        var dangerous = plan.Items.Count(i => i.RequiresConfirmation);
        if (dangerous == 0)
        {
            return string.Empty;
        }

        return $"{dangerous} опасных объектов (осиротевшие каталоги Program Files/ProgramData, Windows.old) требуют " +
               "обязательного пообъектного подтверждения — они включаются только по одному, с подтверждением. " +
               "Перед удалением повторно проверяются процессы/%PATH%/службы (FR-3.4, §5).";
    }

    private void OnSelectionChanged()
    {
        foreach (var group in Groups)
        {
            group.Refresh();
        }

        var selected = SelectedItems();
        SelectedSummary = selected.Count == 0
            ? "Ничего не выбрано"
            : $"Выбрано: {selected.Count} · освободится ≈ {CleanReportFormatter.FormatBytes(selected.Sum(i => Math.Max(0, i.SizeBytes ?? 0)))}";

        PreviewDryRunCommand.NotifyCanExecuteChanged();
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Текст подтверждения перед выполнением: объём, опасные объекты и админ-шаги с UAC (FR-3.4, §5).</summary>
    internal string BuildExecutionMessage(IReadOnlyList<LeftoverPlanItem> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Выполнить удаление {items.Count} остатков? Освободится примерно {CleanReportFormatter.FormatBytes(items.Sum(i => Math.Max(0, i.SizeBytes ?? 0)))}.");
        builder.AppendLine();

        var dangerous = items.Where(i => i.RequiresConfirmation).ToList();
        if (dangerous.Count > 0)
        {
            builder.AppendLine($"Опасные объекты, подтверждённые пообъектно ({dangerous.Count}):");
            foreach (var item in dangerous)
            {
                builder.AppendLine("• " + Describe(item));
            }

            builder.AppendLine();
        }

        var admin = items.Where(i => i.RequiresAdmin).ToList();
        if (admin.Count > 0)
        {
            builder.AppendLine($"Админ-шаги ({admin.Count}): удаление Program Files/ProgramData/Windows.old выполняется через elevated-процесс одним запросом UAC.");
            foreach (var item in admin)
            {
                builder.AppendLine("• " + Describe(item));
            }

            builder.AppendLine();
        }

        builder.AppendLine("Перед каждым удалением повторяется проверка «живых» объектов (запущенные процессы/%PATH%/службы) — используемые объекты будут пропущены.");
        return builder.ToString();
    }

    private static string Describe(LeftoverPlanItem item) =>
        string.IsNullOrEmpty(item.DisplayName)
            ? item.Path
            : $"{item.DisplayName} — {item.Path}";

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
            Serilog.Log.Error(ex, "Leftover operation '{Operation}' failed", operation);
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
        public GroupSlot(string title)
        {
            Title = title;
        }

        public string Title { get; }

        public List<LeftoverPlanItem> Items { get; } = new();
    }
}
