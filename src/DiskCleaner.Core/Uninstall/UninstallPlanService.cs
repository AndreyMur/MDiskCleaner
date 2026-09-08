namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Параметры построения плана деинсталляции (модуль 04, фаза 2 — рекомендации по выбору).
/// </summary>
public sealed class UninstallPlanOptions
{
    /// <summary>Проверять существование каталога InstallLocation на диске.</summary>
    public Func<string, bool>? PathExists { get; set; }

    /// <summary>
    /// Включать в план записи без пригодной команды деинсталляции. По умолчанию такие записи
    /// пропускаются (их нельзя удалить штатным деинсталлятором, FR-4.5).
    /// </summary>
    public bool IncludeUnremovable { get; set; }
}

/// <summary>
/// Строит план деинсталляции (FR-4.1–4.4, FR-4.11): список установленного ПО из реестра
/// (три ветки Uninstall), каждый объект — шаг плана с названием, размером, путём и пометками
/// «дубль / старая версия / проверить вручную». Шаги по умолчанию выключены
/// (<see cref="UninstallPlanItem.IsEnabled"/> == false) — рекомендации никогда не предвыбираются
/// (FR-4.2). Аномальные записи (пустой/подозрительный Publisher, подозрительные домены,
/// InstallDate = дате первой загрузки Windows) собираются в группу «Проверить вручную» (FR-4.3).
/// Для InstallLocation на диске C: измеряется фактический размер каталога — отдельно от общего
/// размера записи реестра (FR-4.4).
/// </summary>
public sealed class UninstallPlanService
{
    private readonly UninstallRegistryService? _registry;
    private readonly UninstallStringParser _parser;
    private readonly InstalledAppAnalyzer _analyzer;
    private readonly IUninstallFolderSizeProvider? _folderSize;
    private readonly IWindowsFirstRunDateProvider _firstRunDate;
    private readonly UninstallDependencyCatalog _dependencyCatalog;

    public UninstallPlanService(
        UninstallRegistryService? registry = null,
        UninstallStringParser? parser = null,
        InstalledAppAnalyzer? analyzer = null,
        IUninstallFolderSizeProvider? folderSize = null,
        IWindowsFirstRunDateProvider? firstRunDate = null,
        UninstallDependencyCatalog? dependencyCatalog = null)
    {
        _registry = registry;
        _parser = parser ?? new UninstallStringParser();
        _firstRunDate = firstRunDate ?? new RegistryWindowsFirstRunDateProvider();
        _folderSize = folderSize ?? new DirectoryScannerFolderSizeProvider();
        _analyzer = analyzer ?? new InstalledAppAnalyzer(() => _firstRunDate.GetFirstRunDate());
        _dependencyCatalog = dependencyCatalog ?? new UninstallDependencyCatalog();
    }

    /// <summary>Строит план из реестра Uninstall (три ветки). Требует <see cref="UninstallRegistryService"/>.</summary>
    public UninstallPlan BuildPlan(UninstallPlanOptions? options = null)
    {
        var registry = _registry ?? new UninstallRegistryService();
        return BuildPlan(registry.ReadInstalledApps(), options);
    }

    /// <summary>Строит план из переданного списка установленного ПО (для тестов и повторного использования).</summary>
    public UninstallPlan BuildPlan(
        IReadOnlyList<InstalledApp> apps,
        UninstallPlanOptions? options = null)
    {
        var opts = options ?? new UninstallPlanOptions();
        var analysis = _analyzer.Analyze(apps);
        var items = new List<UninstallPlanItem>(analysis.Count);

        foreach (var item in analysis)
        {
            var app = item.App;
            if (app.IsSystemComponent || string.IsNullOrWhiteSpace(app.DisplayName))
            {
                continue;
            }

            var command = _parser.ParseFor(app);
            if (command is null && !opts.IncludeUnremovable)
            {
                continue;
            }

            var path = ResolvePath(app, opts);
            items.Add(new UninstallPlanItem
            {
                Key = $"app:{app.ScopeKey}:{app.ProductCode}",
                ProductCode = app.ProductCode,
                ScopeKey = app.ScopeKey,
                DisplayName = app.DisplayName,
                Publisher = app.Publisher,
                DisplayVersion = app.DisplayVersion,
                InstallDateText = FormatInstallDate(app.InstallDate),
                GroupName = item.NeedsReview
                    ? UninstallPlanItem.ReviewManuallyGroupName
                    : (string.IsNullOrWhiteSpace(app.Publisher) ? "Без издателя" : app.Publisher),
                Path = path,
                EstimatedSizeBytes = app.EstimatedSizeBytes,
                SizeOnCDriveBytes = MeasureOnCDriveBytes(app, path, opts),
                RequiresAdmin = app.RequiresAdmin,
                Marks = BuildMarks(item),
                Note = item.Note,
                DependencyImpactNames = _dependencyCatalog.FindAffectedProducts(apps, app),
                IsEnabled = false
            });
        }

        return new UninstallPlan
        {
            Items = items
                .OrderBy(i => i.GroupName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private string? ResolvePath(InstalledApp app, UninstallPlanOptions opts)
    {
        if (string.IsNullOrWhiteSpace(app.InstallLocation))
        {
            return null;
        }

        var exists = opts.PathExists ?? (path => Directory.Exists(path) || File.Exists(path));
        if (!exists(app.InstallLocation))
        {
            return null;
        }

        return app.InstallLocation;
    }

    private long? MeasureOnCDriveBytes(InstalledApp app, string? path, UninstallPlanOptions opts)
    {
        if (string.IsNullOrWhiteSpace(path) || _folderSize is null)
        {
            return null;
        }

        if (!IsOnCDrive(path))
        {
            return null;
        }

        return _folderSize.MeasureFolderBytes(path);
    }

    private static IReadOnlyList<UninstallPlanMarkKind> BuildMarks(InstalledAppAnalysis item)
    {
        var marks = new List<UninstallPlanMarkKind>(3);
        if (item.IsDuplicate)
        {
            marks.Add(UninstallPlanMarkKind.Duplicate);
        }

        if (item.IsOldVersion)
        {
            marks.Add(UninstallPlanMarkKind.OldVersion);
        }

        if (item.NeedsReview)
        {
            marks.Add(UninstallPlanMarkKind.ReviewManually);
        }

        return marks;
    }

    private static bool IsOnCDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is not null && root.StartsWith("C:", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string FormatInstallDate(string? installDate)
    {
        if (string.IsNullOrWhiteSpace(installDate))
        {
            return string.Empty;
        }

        if (installDate.Length == 8 &&
            DateTime.TryParseExact(
                installDate,
                "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var date))
        {
            return date.ToString("yyyy-MM-dd");
        }

        return installDate;
    }
}
