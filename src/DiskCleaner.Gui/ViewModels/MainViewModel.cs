using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Reports;
using DiskCleaner.Core.Scanning;
using DiskCleaner.Core.Scheduling;

namespace DiskCleaner.Gui.ViewModels;

/// <summary>
/// ViewModel главного экрана «Анализ/План очистки» (FR-1.1, FR-1.11). Тонкий слой поверх ядра:
/// выбор источника анализа (полный скан диска или известные объекты профиля/реестра/системы),
/// запуск скана в фоне с прогрессом и отменой, дерево категорий и таблица плана. Вся бизнес-логика —
/// в <see cref="IAnalysisCoordinator"/> и сервисах ядра.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IAnalysisCoordinator _coordinator;
    private readonly PlanExecutor _plan;
    private readonly SchedulerService _scheduler;
    private CancellationTokenSource? _operationCts;
    private AnalysisRunOptions _lastOptions = new();

    public MainViewModel(IAnalysisCoordinator? coordinator = null)
    {
        _coordinator = coordinator ?? new AnalysisCoordinator();
        _plan = new PlanExecutor();
        _scheduler = new SchedulerService();
        Sources = BuildSources();
        SelectedSource = Sources.FirstOrDefault(s => s.IsDiskSource) ?? Sources.FirstOrDefault();
        RefreshFreeSpace();
        StatusText = "Готов к анализу. Выберите источник и нажмите «Анализ».";
    }

    /// <summary>Источники анализа: фиксированные диски + режим профиля/известных объектов.</summary>
    public IReadOnlyList<AnalysisSourceOption> Sources { get; }

    public ObservableCollection<TreeItemViewModel> RootNodes { get; } = new();

    public ObservableCollection<PlanRowViewModel> PlanRows { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(CanChangeSource))]
    [NotifyPropertyChangedFor(nameof(IsSystemDirectoriesToggleEnabled))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    private bool _dryRun;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _freeSpaceText = string.Empty;

    [ObservableProperty]
    private string _selectedSummary = "Ничего не выбрано";

    [ObservableProperty]
    private bool _isProgressVisible;

    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private TreeItemViewModel? _selectedNode;

    /// <summary>Выбранный источник анализа (диск или профиль/известные объекты).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemDirectoriesToggleEnabled))]
    private AnalysisSourceOption? _selectedSource;

    /// <summary>Включать системные каталоги в полный скан диска (FR-1.2).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemDirectoriesToggleEnabled))]
    private bool _includeSystemDirectories;

    public bool CanChangeSource => !IsBusy;

    /// <summary>Переключатель системных каталогов применим только к скану диска (FR-1.2).</summary>
    public bool IsSystemDirectoriesToggleEnabled =>
        !IsBusy && SelectedSource?.IsDiskSource == true;

    private bool CanStartOperation => !IsBusy;

    private bool CanCancelOperation => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task AnalyzeAsync()
    {
        await RunAnalysisAsync("Анализ", CreateCurrentOptions());
    }

    /// <summary>Анализ по расписанию: всегда режим известных объектов профиля/реестра/системы.</summary>
    public Task RunScheduledAnalysisAsync()
    {
        StatusText = "Запущен анализ по расписанию. Очистка выполняется только после подтверждения плана.";
        return RunAnalysisAsync("Анализ (по расписанию)", new AnalysisRunOptions());
    }

    private AnalysisRunOptions CreateCurrentOptions() => new()
    {
        DiskRootPath = SelectedSource?.RootPath,
        IncludeSystemDirectories = IncludeSystemDirectories && SelectedSource?.IsDiskSource == true,
        IncludeInstalledApps = true
    };

    private async Task RunAnalysisAsync(string operationLabel, AnalysisRunOptions options)
    {
        await RunOperationAsync(
            operationLabel,
            async (ct, text) =>
            {
                var scanProgress = new Progress<ScanProgress>(p =>
                {
                    var current = string.IsNullOrEmpty(p.CurrentPath)
                        ? string.Empty
                        : " · " + Path.GetFileName(p.CurrentPath);
                    text.Report($"каталогов: {p.DirectoriesCompleted} · {CleanReportFormatter.FormatBytes(p.BytesMeasured)}{current}");
                });

                var result = await _coordinator.RunAsync(options, scanProgress, ct);
                _lastOptions = options;
                ApplyAnalysis(result);
            });
    }

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private void OpenScheduler()
    {
        var owner = Application.Current.MainWindow;
        var scheduler = new SchedulerWindow(_scheduler, Environment.ProcessPath ?? string.Empty)
        {
            Owner = owner
        };

        if (scheduler.ShowDialog() == true)
        {
            StatusText = "Расписание обновлено. По расписанию выполняется только анализ — без удалений.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task CleanAsync()
    {
        var selectedLeaves = SelectedLeaves();
        if (selectedLeaves.Count == 0)
        {
            MessageBox.Show("Выберите объекты очистки в дереве.", "Очистка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var owner = Application.Current.MainWindow;
        if (!ConfirmPlan(owner, selectedLeaves))
        {
            return;
        }

        var dryRun = DryRun;
        await RunOperationAsync(
            dryRun ? "Предпросмотр (dry-run)" : "Очистка",
            async (ct, text) =>
            {
                var cleanProgress = new Progress<CleanProgress>(p =>
                    text.Report($"{p.CompletedItems}/{p.TotalItems} · {p.CurrentName} · освобождено {CleanReportFormatter.FormatBytes(p.BytesCleaned)}"));

                var options = new CleanOptions { DryRun = dryRun };
                var report = await _plan.CleanAsync(
                    selectedLeaves.Select(l => l.Item),
                    options,
                    cleanProgress,
                    ct);

                ReportWindow.Show(owner, report);

                if (!dryRun)
                {
                    await RefreshAfterCleanAsync(ct);
                }
            });
    }

    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private void Cancel() => _operationCts?.Cancel();

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
        IsProgressIndeterminate = true;
        ProgressText = operation + "...";

        var progress = new Progress<string>(t => ProgressText = operation + ": " + t);

        try
        {
            await work(cts.Token, progress);
            if (operation != "Анализ" && operation != "Анализ (по расписанию)" && operation != "Очистка")
            {
                StatusText = operation + " завершена.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = operation + " отменена.";
        }
        catch (Exception ex)
        {
            StatusText = $"Ошибка: {ex.Message}";
            Serilog.Log.Error(ex, "Operation '{Operation}' failed", operation);
        }
        finally
        {
            IsProgressVisible = false;
            IsBusy = false;
            ProgressText = string.Empty;
            _operationCts = null;
        }
    }

    private async Task RefreshAfterCleanAsync(CancellationToken ct)
    {
        var result = await _coordinator.RunAsync(_lastOptions, null, ct);
        ApplyAnalysis(result);
    }

    internal void ApplyAnalysis(AnalysisResult result)
    {
        RootNodes.Clear();
        PlanRows.Clear();

        foreach (var category in result.CategoryTree)
        {
            RootNodes.Add(new TreeItemViewModel(category, null, OnLeafSelectionChanged));
        }

        var summary = result.Items.Count == 0
            ? "Объекты для очистки не найдены."
            : $"Найдено объектов: {result.Items.Count} ({CleanReportFormatter.FormatBytes(result.TotalBytes)}), используется: {result.InUseItems}, пропущено (нет пути): {result.SkippedNonexistent}";
        StatusText = $"Анализ за {result.Elapsed.TotalSeconds:F1} с. " + summary;

        if (result.Errors.Count > 0)
        {
            StatusText += $" Ошибок доступа: {result.Errors.Count} (подробности в журнале).";
        }

        RefreshFreeSpace();
        UpdatePlan();
    }

    private void OnLeafSelectionChanged() => UpdatePlan();

    private void UpdatePlan()
    {
        PlanRows.Clear();

        var selected = SelectedLeaves();
        foreach (var leaf in selected)
        {
            PlanRows.Add(new PlanRowViewModel(leaf.Item));
        }

        var bytes = selected.Sum(l => l.Item.EffectiveSizeBytes);
        SelectedSummary = selected.Count == 0
            ? "Ничего не выбрано"
            : $"Выбрано: {selected.Count} объектов · освободится ≈ {CleanReportFormatter.FormatBytes(bytes)}";
    }

    private List<TreeItemViewModel> SelectedLeaves() =>
        RootNodes
            .SelectMany(node => node.GetLeaves())
            .Where(leaf => leaf.IsChecked == true)
            .ToList();

    private bool ConfirmPlan(Window owner, IReadOnlyList<TreeItemViewModel> selectedLeaves)
    {
        var dangerous = selectedLeaves
            .Where(l => l.Item.Risk != CleanupRisk.Low)
            .ToList();

        var message = DryRun
            ? $"Предпросмотр (dry-run): будет показано, что удалится для {selectedLeaves.Count} объектов (~{CleanReportFormatter.FormatBytes(selectedLeaves.Sum(l => l.Item.EffectiveSizeBytes))}). Ничего удалено не будет."
            : $"Выполнить очистку {selectedLeaves.Count} объектов? Освободится примерно {CleanReportFormatter.FormatBytes(selectedLeaves.Sum(l => l.Item.EffectiveSizeBytes))}.";

        if (dangerous.Count > 0 && !DryRun)
        {
            message += Environment.NewLine + Environment.NewLine +
                       "Требуют подтверждения (средний/высокий риск):" + Environment.NewLine +
                       string.Join(Environment.NewLine, dangerous.Select(l => $"• {l.Item.DisplayName} — {l.Item.Warning ?? "рискованная операция"}"));
        }

        if (DryRun)
        {
            MessageBox.Show(owner, message, "Предпросмотр", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }

        var result = MessageBox.Show(
            owner,
            message,
            "Подтверждение очистки",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        return result == MessageBoxResult.OK;
    }

    private static IReadOnlyList<AnalysisSourceOption> BuildSources()
    {
        var sources = new List<AnalysisSourceOption>();

        string? profileRoot = null;
        try
        {
            profileRoot = Path.GetPathRoot(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
        }

        var drives = new List<(string Root, bool IsSystem)>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed)
                {
                    continue;
                }

                var root = drive.RootDirectory.FullName;
                var isSystem = string.Equals(root, profileRoot, StringComparison.OrdinalIgnoreCase);
                drives.Add((root, isSystem));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        foreach (var systemDrive in drives.Where(d => d.IsSystem))
        {
            sources.Add(new AnalysisSourceOption
            {
                DisplayName = "Системный диск (" + FormatDrive(systemDrive.Root) + ") — полный скан",
                RootPath = systemDrive.Root
            });
        }

        foreach (var drive in drives.Where(d => !d.IsSystem))
        {
            sources.Add(new AnalysisSourceOption
            {
                DisplayName = "Диск " + FormatDrive(drive.Root) + " — полный скан",
                RootPath = drive.Root
            });
        }

        sources.Add(new AnalysisSourceOption
        {
            DisplayName = "Профиль и система: кэши, остатки, приложения (без полного скана диска)",
            RootPath = null
        });

        return sources;
    }

    private static string FormatDrive(string root) =>
        (root ?? string.Empty).TrimEnd('\\', '/');

    private void RefreshFreeSpace()
    {
        try
        {
            var root = Path.GetPathRoot(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var drive = new DriveInfo(root);
            var percent = drive.TotalSize == 0 ? 0.0 : drive.AvailableFreeSpace * 100.0 / drive.TotalSize;
            FreeSpaceText = $"{drive.Name} свободно: {CleanReportFormatter.FormatBytes(drive.AvailableFreeSpace)} из {CleanReportFormatter.FormatBytes(drive.TotalSize)} ({percent:0}%)";
        }
        catch (Exception ex)
        {
            FreeSpaceText = $"Свободное место: недоступно ({ex.Message})";
        }
    }
}
