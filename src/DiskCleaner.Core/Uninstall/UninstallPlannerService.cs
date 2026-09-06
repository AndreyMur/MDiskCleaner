using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Uninstall;

public sealed class UninstallPlannerOptions
{
    public bool IncludeAllApps { get; set; }

    public bool IncludeOrphanedRegistryEntries { get; set; } = true;

    public Func<string, bool> PathExists { get; set; } = static path =>
        Directory.Exists(path) || File.Exists(path);
}

public sealed class UninstallPlannerService
{
    private readonly UninstallRegistryService _registry;
    private readonly UninstallStringParser _parser;
    private readonly BundleFallbackResolver _bundleResolver;
    private readonly string? _programDataRoot;

    public UninstallPlannerService(
        UninstallRegistryService? registry = null,
        UninstallStringParser? parser = null,
        BundleFallbackResolver? bundleResolver = null,
        string? programDataRoot = null)
    {
        _registry = registry ?? new UninstallRegistryService();
        _parser = parser ?? new UninstallStringParser();
        _bundleResolver = bundleResolver ?? new BundleFallbackResolver();
        _programDataRoot = programDataRoot;
    }

    public IReadOnlyList<CleanupItem> BuildSeeds(UninstallPlannerOptions? options = null) =>
        BuildSeedsFrom(_registry.ReadInstalledApps(), options);

    public IReadOnlyList<CleanupItem> BuildSeedsFrom(
        IReadOnlyList<InstalledApp> apps,
        UninstallPlannerOptions? options = null)
    {
        var opts = options ?? new UninstallPlannerOptions();
        var analysis = new InstalledAppAnalyzer().Analyze(apps);
        var seeds = new List<CleanupItem>();

        foreach (var item in analysis)
        {
            var app = item.App;
            if (app.IsSystemComponent || string.IsNullOrWhiteSpace(app.DisplayName))
            {
                continue;
            }

            var recommended = item.IsOldVersion || item.NeedsReview || item.IsDuplicate;
            if (!opts.IncludeAllApps && !recommended)
            {
                continue;
            }

            var command = _parser.Parse(app.UninstallString ?? app.QuietUninstallString, app.ProductCode);
            if (command is null)
            {
                continue;
            }

            var effective = ResolveBundleFallback(app, command);

            seeds.Add(new CleanupItem
            {
                Key = $"app:{app.ScopeKey}:{app.ProductCode}",
                Path = null,
                DisplayName = app.DisplayName,
                GroupName = string.IsNullOrWhiteSpace(app.Publisher) ? "Без издателя" : app.Publisher,
                Category = CleanupCategory.InstalledApp,
                Risk = app.RequiresAdmin ? CleanupRisk.Medium : CleanupRisk.Medium,
                Description = BuildDescription(app),
                Warning = BuildWarning(item, effective.Command),
                CleanCommand = effective.Command.DisplayText,
                CleanCommandFile = effective.Command.FileName,
                CleanCommandArgs = effective.Command.Arguments,
                RequiresAdmin = app.RequiresAdmin,
                CommandOnly = true,
                AllowDirectDelete = false,
                UninstallMode = true,
                SizeBytes = app.EstimatedSizeBytes,
                OwnerProcessNames = Array.Empty<string>()
            });
        }

        if (opts.IncludeOrphanedRegistryEntries)
        {
            AddOrphanedEntries(apps, opts, seeds);
        }

        return seeds;
    }

    private void AddOrphanedEntries(
        IReadOnlyList<InstalledApp> apps,
        UninstallPlannerOptions opts,
        List<CleanupItem> seeds)
    {
        foreach (var app in apps)
        {
            if (app.IsSystemComponent || string.IsNullOrWhiteSpace(app.DisplayName))
            {
                continue;
            }

            var locationMissing = !string.IsNullOrWhiteSpace(app.InstallLocation) &&
                                  !opts.PathExists(app.InstallLocation);
            var uninstallerMissing = !string.IsNullOrWhiteSpace(app.UninstallString) &&
                                     !InstalledAppPathExists(app.UninstallString, opts.PathExists);
            if (!locationMissing && !uninstallerMissing)
            {
                continue;
            }

            seeds.Add(new CleanupItem
            {
                Key = $"orphan-reg:{app.ScopeKey}:{app.ProductCode}",
                Path = null,
                DisplayName = app.DisplayName,
                GroupName = "Осиротевшие записи реестра",
                Category = CleanupCategory.Leftover,
                Risk = app.RequiresAdmin ? CleanupRisk.Medium : CleanupRisk.Low,
                Description = "Запись Uninstall ссылается на отсутствующие файлы/каталоги.",
                Warning = "Будет удалена только запись реестра. Файлы не затрагиваются.",
                RequiresAdmin = app.RequiresAdmin,
                RegistryDeletePath = RegistryDeletePathBuilder.Build(app),
                CommandOnly = false,
                AllowDirectDelete = false
            });
        }
    }

    private (UninstallCommand Command, bool IsBundle) ResolveBundleFallback(
        InstalledApp app,
        UninstallCommand command)
    {
        if (command.Kind != UninstallerKind.Msi)
        {
            return (command, false);
        }

        var bundleExe = _bundleResolver.TryResolveUninstallerExe(app, _programDataRoot);
        if (bundleExe is null)
        {
            return (command, false);
        }

        return (
            new UninstallCommand(
                UninstallerKind.Generic,
                bundleExe,
                "/uninstall",
                Silent: false),
            true);
    }

    private static bool InstalledAppPathExists(string uninstallString, Func<string, bool> exists)
    {
        var parsed = new UninstallStringParser().Parse(uninstallString);
        if (parsed is null || parsed.Kind == UninstallerKind.Msi)
        {
            return true;
        }

        return exists(parsed.FileName);
    }

    private static string BuildDescription(InstalledApp app)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(app.DisplayVersion))
        {
            parts.Add($"Версия: {app.DisplayVersion}");
        }

        if (!string.IsNullOrWhiteSpace(app.InstallDate))
        {
            parts.Add($"Дата установки: {FormatInstallDate(app.InstallDate)}");
        }

        if (!string.IsNullOrWhiteSpace(app.InstallLocation))
        {
            parts.Add($"Каталог: {app.InstallLocation}");
        }

        if (app.IsWindowsInstaller)
        {
            parts.Add("Тип: MSI (Windows Installer)");
        }

        return parts.Count == 0 ? "Установленное приложение." : string.Join(System.Environment.NewLine, parts);
    }

    private static string BuildWarning(InstalledAppAnalysis item, UninstallCommand command)
    {
        var warnings = new List<string> { $"Будет запущен деинсталлятор: {command.DisplayText}" };

        if (item.IsOldVersion)
        {
            warnings.Add("Это более старая версия (дубль). Убедитесь, что свежая версия работает корректно.");
        }

        if (item.NeedsReview)
        {
            warnings.Add("Запись помечена как требующая ручной проверки.");
        }

        if (!command.Silent)
        {
            warnings.Add("Деинсталлятор может открыть мастер — завершите его вручную.");
        }

        if (item.App.RequiresAdmin)
        {
            warnings.Add("Требуются права администратора (UAC).");
        }

        return string.Join(" ", warnings);
    }

    private static string FormatInstallDate(string installDate)
    {
        if (installDate.Length == 8 &&
            DateTime.TryParseExact(installDate, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date))
        {
            return date.ToString("yyyy-MM-dd");
        }

        return installDate;
    }
}
