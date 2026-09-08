using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Поиск осиротевших записей Uninstall (фаза 24, модуль 04, FR-4.13). Осиротевшей считается
/// запись, которая ссылается на несуществующие пути: отсутствует каталог <c>InstallLocation</c>
/// либо отсутствует файл штатного деинсталлятора (для MSI проверка не выполняется — <c>msiexec</c>
/// является системным и всегда доступен).
/// <list type="bullet">
/// <item>учитываются только записи под <c>Software\Microsoft\Windows\CurrentVersion\Uninstall</c>
/// (LocalMachine) и <c>WOW6432Node</c> — пользовательские записи HKCU в группу не попадают;</item>
/// <item>системные компоненты (<c>SystemComponent=1</c>) не предлагаются без явного согласия —
/// <see cref="UninstallOrphanRegistryOptions.IncludeSystemComponents"/>;</item>
/// <item>удаляются только записи реестра (файлы/каталоги не затрагиваются), по подтверждению.</item>
/// </list>
/// </summary>
public sealed class UninstallOrphanRegistryScanner
{
    public const string GroupName = "Осиротевшие записи реестра";

    private readonly UninstallStringParser _parser;

    public UninstallOrphanRegistryScanner(UninstallStringParser? parser = null)
    {
        _parser = parser ?? new UninstallStringParser();
    }

    /// <summary>Проверка, что запись находится в одной из двух машинных веток Uninstall (FR-4.13).</summary>
    public static bool IsMachineScope(string scopeKey) =>
        scopeKey is nameof(InstalledAppScope.LocalMachine64) or nameof(InstalledAppScope.LocalMachine32);

    /// <summary>
    /// Осиротевшие записи Uninstall среди переданных записей установленного ПО.
    /// Пользовательские записи (HKCU) и системные компоненты (без явного согласия) пропускаются.
    /// </summary>
    public IReadOnlyList<UninstallOrphanRegistryMatch> Find(
        IEnumerable<InstalledApp> apps,
        UninstallOrphanRegistryOptions? options = null)
    {
        var opts = options ?? new UninstallOrphanRegistryOptions();
        var result = new List<UninstallOrphanRegistryMatch>();

        foreach (var app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.DisplayName) || !IsMachineScope(app.ScopeKey))
            {
                continue;
            }

            if (app.IsSystemComponent && !opts.IncludeSystemComponents)
            {
                continue;
            }

            var missing = MissingPaths(app, opts.PathExists);
            if (missing.Count == 0)
            {
                continue;
            }

            result.Add(new UninstallOrphanRegistryMatch(app, missing, app.IsSystemComponent));
        }

        return result;
    }

    /// <summary>
    /// Шаги плана очистки (CleanupItem) для осиротевших записей — отдельная группа
    /// <see cref="GroupName"/>. Каждый шаг удаляет только ветку реестра
    /// (<see cref="CleanupItem.RegistryDeletePath"/>), требует прав администратора для машинных
    /// веток и никогда не предвыбирается (подтверждение — при включении шага).
    /// </summary>
    public IReadOnlyList<CleanupItem> BuildCleanupItems(
        IEnumerable<InstalledApp> apps,
        UninstallOrphanRegistryOptions? options = null)
    {
        var matches = Find(apps, options);
        var items = new List<CleanupItem>(matches.Count);

        foreach (var match in matches)
        {
            var app = match.App;
            var systemNote = app.IsSystemComponent
                ? " Системный компонент (SystemComponent=1): удаление записи выполняется только по вашему явному согласию."
                : string.Empty;

            items.Add(new CleanupItem
            {
                Key = $"orphan-reg:{app.ScopeKey}:{app.ProductCode}",
                Path = null,
                DisplayName = app.DisplayName,
                GroupName = GroupName,
                Category = CleanupCategory.Leftover,
                Risk = app.IsSystemComponent ? CleanupRisk.High : CleanupRisk.Medium,
                Description = "Запись Uninstall ссылается на отсутствующие файлы/каталоги." +
                              (match.MissingPaths.Count == 0
                                  ? string.Empty
                                  : " " + string.Join("; ", match.MissingPaths)),
                Warning = "Будет удалена только запись реестра. Файлы не затрагиваются." + systemNote,
                RequiresAdmin = app.RequiresAdmin,
                RegistryDeletePath = RegistryDeletePathBuilder.Build(app),
                CommandOnly = false,
                AllowDirectDelete = false,
                ReviewReason = app.IsSystemComponent
                    ? "SystemComponent=1 — требует явного согласия пользователя."
                    : null
            });
        }

        return items;
    }

    private IReadOnlyList<string> MissingPaths(InstalledApp app, Func<string, bool> exists)
    {
        var missing = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(app.InstallLocation) && !exists(app.InstallLocation))
        {
            missing.Add($"Каталог установки отсутствует: {app.InstallLocation}");
        }

        var uninstallerPath = ExistingUninstallerPath(app);
        if (uninstallerPath is not null && !exists(uninstallerPath))
        {
            missing.Add($"Деинсталлятор отсутствует: {uninstallerPath}");
        }

        return missing;
    }

    /// <summary>
    /// Путь файла деинсталлятора, который должна проверить эвристика. Для MSI возвращается null
    /// (msiexec системный). Если задан хотя бы один из <c>UninstallString</c>/<c>QuietUninstallString</c>
    /// с распознаваемым exe-деинсталлятором — возвращается его путь.
    /// </summary>
    private string? ExistingUninstallerPath(InstalledApp app)
    {
        foreach (var text in new[] { app.UninstallString, app.QuietUninstallString })
        {
            var command = _parser.Parse(text, app.ProductCode);
            if (command is null)
            {
                continue;
            }

            if (command.Kind == UninstallerKind.Msi)
            {
                return null;
            }

            return command.FileName;
        }

        return null;
    }
}
