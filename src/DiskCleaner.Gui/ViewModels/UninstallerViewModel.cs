using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Reports;
using DiskCleaner.Core.Uninstall;
using DiskCleaner.Gui.Services;

namespace DiskCleaner.Gui.ViewModels;

/// <summary>Режим отображения списка установленного ПО (FR-4.2/4.3).</summary>
public enum UninstallFilterKind
{
    /// <summary>Все записи установленного ПО.</summary>
    All,

    /// <summary>Только рекомендации к удалению: дубли и старые версии (FR-4.2).</summary>
    Recommended,

    /// <summary>Только группа «Проверить вручную» (аномалии, FR-4.3).</summary>
    ReviewManually
}

/// <summary>Вариант фильтра для выпадающего списка.</summary>
public sealed class UninstallFilterOption
{
    public required UninstallFilterKind Kind { get; init; }

    public required string DisplayName { get; init; }
}

/// <summary>
/// ViewModel экрана «Деинсталляция и зачистка» (фаза 5 модуля 04, FR-4.1–4.13):
/// <list type="bullet">
/// <item>список установленного ПО из реестра с колонками «размер / «на C:» / дата / версия /
/// издатель / путь» и фильтрами «дубли и старые версии» и группы «Проверить вручную»
/// (FR-4.1–4.4);</item>
/// <item>выбор без предвыбора — рекомендации только помечаются (FR-4.2); каждый шаг
/// подтверждается отдельно с объёмом и названием (FR-4.11);</item>
/// <item>отдельная группа «Осиротевшие записи Uninstall» с подтверждением удаления записей
/// реестра (FR-4.13);</item>
/// <item>после операций — показ отчёта и запрос перезагрузки при необходимости (FR-4.12).</item>
/// </list>
/// </summary>
public sealed partial class UninstallerViewModel : ObservableObject
{
    private readonly IUninstallerPlanSource _source;
    private readonly IUninstallerExecutor _executor;
    private readonly IUninstallerDialogService _dialogs;
    private CancellationTokenSource? _operationCts;
    private UninstallerSnapshot? _snapshot;
    private IReadOnlyList<UninstallItemViewModel> _allRows = Array.Empty<UninstallItemViewModel>();
    private UninstallFilterKind _filter = UninstallFilterKind.All;

    public UninstallerViewModel(
        IUninstallerPlanSource? source = null,
        IUninstallerExecutor? executor = null,
        IUninstallerDialogService? dialogs = null)
    {
        _source = source ?? new UninstallerPlanSource();
        _executor = executor ?? new UninstallerExecutor();
        _dialogs = dialogs ?? new MessageBoxUninstallerDialogService();
        SelectedFilter = FilterOptions[0];
        StatusText = "Экран «Деинсталляция и зачистка». Нажмите «Найти установленные программы» — будет показан список ПО и осиротевших записей.";
    }

    public ObservableCollection<UninstallItemViewModel> Rows { get; } = new();

    public ObservableCollection<OrphanUninstallItemViewModel> Orphans { get; } = new();

    public ObservableCollection<SdkVersionItemViewModel> SdkVersions { get; } = new();

    /// <summary>Журнал прогресса массового удаления версии SDK (по компонентам, FR-4.7).</summary>
    public ObservableCollection<string> SdkProgressLines { get; } = new();

    /// <summary>
    /// Инструкция в UI для GUI-деинсталляторов (FR-4.11, §7): если при массовом удалении SDK
    /// появится мастер (например, winsdksetup.exe) — его нужно завершить вручную; приложение
    /// ожидает завершения с таймаутом.
    /// </summary>
    public string SdkInstructionText =>
        "Массовое удаление MSI-компонентов версии Windows SDK выполняется последовательно (FR-4.7). " +
        "Если для записи-пакета (bundle) откроется мастер удаления (например, winsdksetup.exe) — " +
        "завершите его вручную: приложение будет ожидать завершения мастера (с таймаутом). " +
        "После каждого прохода реестр перечитывается, «вернувшиеся» записи удаляются повторно (FR-4.8).";

    /// <summary>Варианты фильтра «Все / Дубли и старые версии / Проверить вручную» (FR-4.2/4.3).</summary>
    public IReadOnlyList<UninstallFilterOption> FilterOptions { get; } =
    [
        new UninstallFilterOption { Kind = UninstallFilterKind.All, DisplayName = "Все установленные программы" },
        new UninstallFilterOption { Kind = UninstallFilterKind.Recommended, DisplayName = "Дубли и старые версии (рекомендации)" },
        new UninstallFilterOption { Kind = UninstallFilterKind.ReviewManually, DisplayName = "Проверить вручную" }
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentFilterDisplayName))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private UninstallFilterOption? _selectedFilter;

    public string CurrentFilterDisplayName => SelectedFilter?.DisplayName ?? FilterOptions[0].DisplayName;

    /// <summary>Пояснение о работе экрана и правилах безопасности (FR-4.2, FR-4.11).</summary>
    public string ContextText =>
        "Установленные программы из реестра Uninstall (три ветки). Ничего не предвыбирается: " +
        "дубли и старые версии помечаются как рекомендации, но включаются только по вашему выбору. " +
        "Каждый шаг деинсталляции подтверждается отдельно — с названием и объёмом (FR-4.2, FR-4.11). " +
        "Удаление выполняется штатным деинсталлятором; для машинных веток — с одним запросом UAC на пачку.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteOrphansCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSdkVersionCommand))]
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

    /// <summary>Список ПО сформирован и показан на экране.</summary>
    [ObservableProperty]
    private bool _hasPlan;

    /// <summary>Найдены осиротевшие записи Uninstall (FR-4.13).</summary>
    [ObservableProperty]
    private bool _hasOrphans;

    /// <summary>Найдены установленные версии Windows SDK (FR-4.7).</summary>
    [ObservableProperty]
    private bool _hasSdkVersions;

    /// <summary>Выполняется массовое удаление версии SDK (для блокировки перезапуска).</summary>
    [ObservableProperty]
    private bool _isSdkBusy;

    private bool CanStartOperation => !IsBusy;

    private bool CanCancelOperation => IsBusy;

    private bool CanExecute => !IsBusy && SelectedItems().Count > 0;

    private bool CanExecuteOrphans => !IsBusy && SelectedOrphans().Count > 0;

    private bool CanRemoveSdkVersion => !IsBusy && !IsSdkBusy && SelectedSdkVersion is not null;

    /// <summary>Выбранная версия Windows SDK для массового удаления (FR-4.7).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSdkVersionCommand))]
    private SdkVersionItemViewModel? _selectedSdkVersion;

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private Task ScanAsync() =>
        RunOperationAsync("Поиск установленных программ", ScanCoreAsync);

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var selected = SelectedItems();
        if (selected.Count == 0)
        {
            return;
        }

        // FR-4.11: каждый шаг подтверждается отдельно — с объёмом и названием.
        var confirmed = new List<UninstallItemViewModel>(selected.Count);
        foreach (var item in selected)
        {
            if (_dialogs.ConfirmStep(item.Item))
            {
                confirmed.Add(item);
            }
        }

        if (confirmed.Count == 0)
        {
            StatusText = "Деинсталляция отменена пользователем (шаги не подтверждены).";
            return;
        }

        var apps = ResolveApps(confirmed);
        if (apps.Count == 0)
        {
            StatusText = "Не удалось сопоставить выбранные шаги с записями реестра.";
            return;
        }

        var message = BuildExecutionMessage(confirmed, apps);
        if (!_dialogs.Confirm("Подтверждение деинсталляции", message))
        {
            StatusText = "Деинсталляция отменена пользователем.";
            return;
        }

        await RunOperationAsync("Деинсталляция", (ct, text) => ExecuteCoreAsync(apps, ct, text));
    }

    /// <summary>Массовое удаление выбранной версии Windows SDK с прогрессом по компонентам (FR-4.7).</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSdkVersion))]
    private async Task RemoveSdkVersionAsync()
    {
        var version = SelectedSdkVersion;
        if (IsBusy || IsSdkBusy || version is null)
        {
            return;
        }

        var message =
            $"Удалить все компоненты Windows SDK версии {version.DisplayVersion}?\n\n" +
            version.ComponentNamesText + "\n\n" +
            $"Компонентов: {version.ComponentCount} · суммарно ~{version.EstimatedTotalText}\n\n" +
            "Удаление выполняется последовательно по GUID (FR-4.7); после каждого прохода реестр " +
            "перечитывается и «вернувшиеся» записи удаляются повторно (FR-4.8). Компоненты других " +
            "версий SDK не затрагиваются.\n\n" +
            SdkInstructionText;
        if (!_dialogs.Confirm("Массовое удаление Windows SDK", message))
        {
            StatusText = "Массовое удаление SDK отменено пользователем.";
            return;
        }

        await RunOperationAsync(
            "Массовое удаление SDK",
            (ct, text) => RemoveSdkVersionCoreAsync(version, ct, text));
    }

    [RelayCommand(CanExecute = nameof(CanExecuteOrphans))]
    private async Task ExecuteOrphansAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var selected = SelectedOrphans();
        if (selected.Count == 0)
        {
            return;
        }

        var matches = selected.Select(o => o.Match).ToList();
        var names = string.Join(Environment.NewLine, matches.Select(m => "• " + m.DisplayName));
        var message =
            $"Удалить {matches.Count} осиротевш(их/ие) запис(ей/и) Uninstall из реестра?\n\n{names}\n\n" +
            "Будет удалена только ветка реестра Uninstall. Файлы и каталоги не затрагиваются (FR-4.13).";
        if (!_dialogs.Confirm("Подтверждение удаления записей реестра", message))
        {
            StatusText = "Удаление осиротевших записей отменено пользователем.";
            return;
        }

        await RunOperationAsync("Удаление осиротевших записей", (ct, text) => RemoveOrphansCoreAsync(matches, ct, text));
    }

    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private void Cancel() => _operationCts?.Cancel();

    /// <summary>Выбранные пользователем записи ПО (явное включение чекбоксом, FR-4.2).</summary>
    internal IReadOnlyList<UninstallItemViewModel> SelectedItems() =>
        _allRows.Where(i => i.IsSelected).ToList();

    /// <summary>Выбранные осиротевшие записи Uninstall (с пообъектным подтверждением).</summary>
    internal IReadOnlyList<OrphanUninstallItemViewModel> SelectedOrphans() =>
        Orphans.Where(o => o.IsSelected).ToList();

    internal IReadOnlyList<InstalledApp> ResolveApps(IReadOnlyList<UninstallItemViewModel> items)
    {
        if (_snapshot is null)
        {
            return Array.Empty<InstalledApp>();
        }

        var byKey = _snapshot.Apps.ToDictionary(
            a => $"app:{a.ScopeKey}:{a.ProductCode}",
            StringComparer.OrdinalIgnoreCase);

        return items
            .Select(i => byKey.TryGetValue(i.Item.Key, out var app) ? app : null)
            .Where(app => app is not null)
            .Select(app => app!)
            .ToList();
    }

    private async Task ScanCoreAsync(CancellationToken ct, IProgress<string> text)
    {
        ct.ThrowIfCancellationRequested();
        text.Report("чтение реестра Uninstall и измерение размеров…");
        var startedAt = Stopwatch.StartNew();
        var snapshot = await Task.Run(() => _source.Build(), ct);
        startedAt.Stop();
        ct.ThrowIfCancellationRequested();

        SdkProgressLines.Clear();
        ApplySnapshot(snapshot);
        StatusText = BuildScanSummary(startedAt.Elapsed, snapshot);
    }

    private async Task ExecuteCoreAsync(
        IReadOnlyList<InstalledApp> apps,
        CancellationToken ct,
        IProgress<string> text)
    {
        var report = await _executor.UninstallAppsAsync(apps, cancellationToken: ct);
        _dialogs.ShowReport(report);

        StatusText = $"Деинсталляция завершена: успешно (в т.ч. идемпотентно) — {report.OkCount}; " +
                     $"требует перезагрузки — {report.RebootRequiredCount}; ошибок — {report.ErrorCount}.";
        text.Report("обновление списка после деинсталляции…");

        if (report.NeedsReboot)
        {
            AskReboot(UninstallRebootRequest.FromUninstallReport(report));
        }

        if (report.Entries.Count > 0)
        {
            await RefreshAfterOperationAsync(ct);
        }
    }

    private async Task RemoveOrphansCoreAsync(
        IReadOnlyList<UninstallOrphanRegistryMatch> matches,
        CancellationToken ct,
        IProgress<string> text)
    {
        var report = await _executor.RemoveOrphanRecordsAsync(matches, ct);
        _dialogs.ShowCleanReport(report);

        var removed = report.Entries.Count(e =>
            e.Outcome is CleanOutcome.RegistryEntryDeleted or CleanOutcome.AlreadyUninstalled);
        StatusText = $"Удаление осиротевших записей завершено: удалено/уже отсутствует — {removed}; ошибок — {report.FailedItems}.";

        if (UninstallRebootRequest.FromCleanReport(report).Recommended)
        {
            AskReboot(UninstallRebootRequest.FromCleanReport(report));
        }

        if (report.Entries.Count > 0)
        {
            await RefreshAfterOperationAsync(ct);
        }
    }

    /// <summary>Повторное построение снимка после операций (удалённые объекты исчезают из списка).</summary>
    private async Task RefreshAfterOperationAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = await Task.Run(() => _source.Build(), ct);
            ct.ThrowIfCancellationRequested();
            ApplySnapshot(snapshot);
            StatusText += " Список обновлён.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Serilog.Log.Warning(ex, "Uninstall plan refresh after operation failed");
            StatusText += " Список обновить не удалось: " + ex.Message;
        }
    }

    /// <summary>Применяет снимок: строки ПО (с учётом фильтра) и осиротевшие записи (FR-4.1, FR-4.13).</summary>
    private void ApplySnapshot(UninstallerSnapshot snapshot)
    {
        _snapshot = snapshot;
        _allRows = snapshot.Plan.Items
            .Select(item => new UninstallItemViewModel(item, OnSelectionChanged))
            .ToList();
        ApplyFilter();

        Orphans.Clear();
        foreach (var match in snapshot.Orphans)
        {
            Orphans.Add(new OrphanUninstallItemViewModel(match, ConfirmOrphanDelete, OnSelectionChanged));
        }

        WarningsText = ComposeWarnings(snapshot);
        HasPlan = snapshot.Plan.Items.Count > 0 || snapshot.Orphans.Count > 0;
        HasOrphans = snapshot.Orphans.Count > 0;
        HasSdkVersions = PopulateSdkVersions(snapshot.Apps);
        OnSelectionChanged();
    }

    /// <summary>Версии Windows SDK, найденные в установленном ПО (FR-4.7).</summary>
    private bool PopulateSdkVersions(IReadOnlyList<InstalledApp> apps)
    {
        var previous = SelectedSdkVersion;
        SdkVersions.Clear();

        var versions = WindowsSdkFamily.FindVersions(apps);
        foreach (var version in versions)
        {
            SdkVersions.Add(new SdkVersionItemViewModel(
                version,
                WindowsSdkFamily.ComponentsOfVersion(apps, version)));
        }

        SelectedSdkVersion = previous is not null &&
                             SdkVersions.Any(v => v.DisplayVersion == previous.DisplayVersion)
            ? SdkVersions.First(v => v.DisplayVersion == previous.DisplayVersion)
            : SdkVersions.FirstOrDefault();

        return SdkVersions.Count > 0;
    }

    private async Task RemoveSdkVersionCoreAsync(
        SdkVersionItemViewModel version,
        CancellationToken ct,
        IProgress<string> text)
    {
        IsSdkBusy = true;
        SdkProgressLines.Clear();
        try
        {
            var progress = new Progress<SdkBulkUninstallProgress>(p =>
            {
                var line = FormatSdkProgress(p);
                SdkProgressLines.Add(line);
                text.Report(line);
            });

            var report = await _executor.UninstallSdkVersionAsync(
                version.DisplayVersion,
                progress: progress,
                cancellationToken: ct);

            _dialogs.ShowSdkReport(report);

            StatusText = report.FullyRemoved
                ? $"Версия Windows SDK {report.DisplayVersion} полностью удалена: компонентов {report.Components.Count}, " +
                  $"проходов {report.PassesUsed}; каталогов Include/Lib/bin удалено {report.FolderCleanups.Count(c => c.Removed)}, " +
                  $"Package Cache очищено {report.PackageCacheCleanups.Count(c => c.Removed)}."
                : $"Версия Windows SDK {report.DisplayVersion}: удалено не полностью — осталось записей " +
                  $"{report.RemainingProductCodes.Count} ({string.Join("; ", report.RemainingDisplayNames)}).";

            if (report.NeedsReboot)
            {
                AskReboot(UninstallRebootRequest.FromSdkBulkReport(report));
            }

            await RefreshAfterOperationAsync(ct);
        }
        finally
        {
            IsSdkBusy = false;
        }
    }

    private static string FormatSdkProgress(SdkBulkUninstallProgress p)
    {
        var component = string.IsNullOrWhiteSpace(p.DisplayName)
            ? string.Empty
            : $" · {p.DisplayName}";
        return p.ComponentTotal > 0
            ? $"Проход {p.Pass}, компонент {p.ComponentIndex}/{p.ComponentTotal}: {p.Message}{component}"
            : p.Message;
    }

    /// <summary>Фильтр списка ПО: «дубли и старые версии» (FR-4.2) и группа «Проверить вручную» (FR-4.3).</summary>
    private void ApplyFilter()
    {
        Rows.Clear();
        IEnumerable<UninstallItemViewModel> items = _allRows;
        switch (SelectedFilter?.Kind ?? _filter)
        {
            case UninstallFilterKind.Recommended:
                items = _allRows.Where(i => i.IsDuplicate || i.IsOldVersion);
                break;
            case UninstallFilterKind.ReviewManually:
                items = _allRows.Where(i => i.NeedsReview);
                break;
            default:
                break;
        }

        foreach (var item in items)
        {
            Rows.Add(item);
        }
    }

    partial void OnSelectedFilterChanged(UninstallFilterOption? value)
    {
        if (value is null)
        {
            return;
        }

        _filter = value.Kind;
        ApplyFilter();
        OnSelectionChanged();
    }

    private bool ConfirmOrphanDelete(UninstallOrphanRegistryMatch match)
    {
        var message =
            $"Удалить запись реестра «{match.DisplayName}»?\n\n" +
            string.Join("\n", match.MissingPaths) + "\n\n" +
            "Будет удалена только запись Uninstall (ветка реестра). Файлы не затрагиваются (FR-4.13)." +
            (match.IsSystemComponent ? "\nСистемный компонент (SystemComponent=1): удаление только по вашему явному согласию." : string.Empty);
        return _dialogs.Confirm("Подтверждение удаления записи реестра", message);
    }

    private string BuildExecutionMessage(
        IReadOnlyList<UninstallItemViewModel> items,
        IReadOnlyList<InstalledApp> apps)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Выполнить деинсталляцию {items.Count} программ(ы)?");
        builder.AppendLine();

        foreach (var item in items)
        {
            builder.Append("• ").Append(item.Title);
            if (!string.IsNullOrWhiteSpace(item.PublisherText))
            {
                builder.Append(" — ").Append(item.PublisherText);
            }

            builder.AppendLine();
            var details = new List<string> { "объём записи " + item.SizeText };
            if (item.IsOnCDrive)
            {
                details.Add("на C: " + item.SizeOnCDriveText);
            }

            if (!string.IsNullOrWhiteSpace(item.VersionText))
            {
                details.Add("версия " + item.VersionText);
            }

            builder.AppendLine("    " + string.Join(" · ", details));
            if (item.HasMarks)
            {
                builder.AppendLine("    Пометки: " + item.MarksText);
            }

            if (item.HasDependencyImpacts)
            {
                builder.AppendLine("    " + item.DependencyImpactText);
            }
        }

        var admin = apps.Count(a => a.RequiresAdmin);
        builder.AppendLine();
        builder.AppendLine(admin > 0
            ? $"Записей с админ-правами: {admin} — будут удалены одним elevated-процессом (один запрос UAC, FR-4.15)."
            : "Все выбранные приложения удаляются в контексте пользователя без повышения прав (FR-4.16).");
        builder.AppendLine();
        builder.AppendLine("Команда строится из UninstallString/QuietUninstallString по типу деинсталлятора (FR-4.5); "
                           + "коды 1605/1612 и отсутствующий деинсталлятор считаются «уже удалено» (идемпотентно).");
        return builder.ToString();
    }

    private string BuildScanSummary(TimeSpan elapsed, UninstallerSnapshot snapshot)
    {
        var plan = snapshot.Plan;
        if (plan.Items.Count == 0 && snapshot.Orphans.Count == 0)
        {
            return "Установленные программы и осиротевшие записи не найдены.";
        }

        var recommended = plan.Items.Count(i => i.IsRecommended);
        var admin = plan.Items.Count(i => i.RequiresAdmin);
        var time = elapsed.TotalSeconds > 0 ? $" за {elapsed.TotalSeconds:F1} с" : string.Empty;
        return $"Установлено ПО{time}: {plan.Items.Count} ({CleanReportFormatter.FormatBytes(plan.TotalEstimatedBytes)}); " +
               $"рекомендаций (дубли/старые версии/проверить вручную): {recommended}; с админ-правами: {admin}; " +
               $"осиротевших записей: {snapshot.Orphans.Count}.";
    }

    private string ComposeWarnings(UninstallerSnapshot snapshot)
    {
        var recommended = snapshot.Plan.Items.Count(i => i.IsRecommended);
        var parts = new List<string>(2);
        if (recommended > 0)
        {
            parts.Add($"{recommended} рекомендаций (дубли, старые версии, «проверить вручную») помечены, но не предвыбраны — " +
                      "включите их чекбоксами только осознанно (FR-4.2).");
        }

        if (snapshot.Orphans.Count > 0)
        {
            parts.Add($"Осиротевшие записи Uninstall ({snapshot.Orphans.Count}) удаляются только по подтверждению и только из реестра — " +
                      "файлы не затрагиваются (FR-4.13).");
        }

        return string.Join(Environment.NewLine, parts);
    }

    private void OnSelectionChanged()
    {
        var selected = SelectedItems();
        SelectedSummary = selected.Count == 0
            ? "Ничего не выбрано"
            : $"Выбрано: {selected.Count} · записи ≈ {CleanReportFormatter.FormatBytes(selected.Sum(i => Math.Max(0, i.Item.EstimatedSizeBytes)))}";

        ExecuteCommand.NotifyCanExecuteChanged();
        ExecuteOrphansCommand.NotifyCanExecuteChanged();
    }

    private void AskReboot(UninstallRebootRequest request)
    {
        var choice = _dialogs.AskReboot(request);
        StatusText += choice == UninstallerRebootChoice.Now
            ? " Пользователь подтвердил перезагрузку (перезагрузите компьютер вручную)."
            : choice == UninstallerRebootChoice.Later
                ? " Перезагрузка отложена пользователем."
                : string.Empty;
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
            Serilog.Log.Error(ex, "Uninstall operation '{Operation}' failed", operation);
        }
        finally
        {
            IsProgressVisible = false;
            IsBusy = false;
            ProgressText = string.Empty;
            _operationCts = null;
        }
    }
}
