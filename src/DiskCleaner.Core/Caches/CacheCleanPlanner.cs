using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Environment;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Core.Caches;

/// <summary>
/// Планировщик действий очистки кэшей (фаза 2 модуля 02). Для каждого кэша решает
/// «чем и с каким согласием чистить» (FR-2.6/2.7), формирует «карточку действия»
/// (штатная команда, оценка экономии, последствия, восстановление — FR-2.1/2.5),
/// применяет правила исключений и согласий (Gradle split, whitelist VS Code,
/// категория Leftover — FR-2.9–2.11) и защитный режим «не трогать при &lt; 15%
/// свободного места» (FR-2.12).
/// </summary>
public sealed class CacheCleanPlanner
{
    /// <summary>Порог защитного режима FR-2.12: при доле свободного места ниже этого кэши «не трогать».</summary>
    public const double DefaultLowSpaceThreshold = 0.15;

    private const string VSCodeRootPathPattern = "%AppData%\\Code";

    /// <summary>Единственные кэш-подпапки VS Code, которые разрешено чистить (FR-2.10).</summary>
    private static readonly HashSet<string> VSCodeAllowedSubfolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "CachedData", "CachedExtensionVSIXs", "Crashpad", "GPUCache", "logs", "WebStorage"
    };

    private static readonly HashSet<string> RecreatePathSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "daemon", "logs", "Crashpad", "GPUCache"
    };

    private readonly Abstractions.IEnvironment _environment;
    private readonly IDriveSpaceService _driveSpace;
    private readonly IReadOnlyDictionary<string, CacheTargetDefinition> _definitionsById;

    public CacheCleanPlanner(
        Abstractions.IEnvironment? environment = null,
        IDriveSpaceService? driveSpace = null,
        CacheCatalogService? catalog = null)
    {
        _environment = environment ?? new EnvironmentProvider();
        _driveSpace = driveSpace ?? new DriveInfoDriveSpaceService();
        var document = (catalog ?? new CacheCatalogService()).LoadDocument();
        _definitionsById = document.Targets
            .Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .ToDictionary(t => t.Id!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Формирует план действий очистки по измеренным объектам кэшей.
    /// Объекты вне области очистки кэшей пропускаются; для заблокированных правилами
    /// возвращается действие <see cref="CacheCleanMethod.NotAllowed"/> с причиной.
    /// </summary>
    /// <param name="items">Объекты плана (листья дерева анализа модуля 01).</param>
    /// <param name="lowSpaceThresholdFraction">Порог FR-2.12 (по умолчанию 15%).</param>
    public CacheCleanPlan BuildPlan(
        IEnumerable<CleanupItem> items,
        double lowSpaceThresholdFraction = DefaultLowSpaceThreshold)
    {
        var threshold = lowSpaceThresholdFraction is <= 0 or >= 1
            ? DefaultLowSpaceThreshold
            : lowSpaceThresholdFraction;

        var leaves = AnalysisService.DescendantLeaves(items)
            .DistinctBy(i => i.Key)
            .ToList();

        var candidates = new List<Candidate>(leaves.Count);
        var outOfScope = 0;
        var notAllowed = 0;
        var vsCodeRoot = VSCodeRoot(_environment);

        foreach (var leaf in leaves)
        {
            if (leaf.Category is not (CleanupCategory.Cache or CleanupCategory.DevToolchain))
            {
                outOfScope++;
                continue;
            }

            var definition = FindDefinition(leaf);

            var leftoverReason = LeftoverReason(leaf);
            if (leftoverReason is not null)
            {
                notAllowed++;
                candidates.Add(new Candidate(leaf, definition, CacheCleanMethod.NotAllowed, leftoverReason, HoldReason: null));
                continue;
            }

            var whitelistReason = VSCodeWhitelistReason(leaf, vsCodeRoot);
            if (whitelistReason is not null)
            {
                notAllowed++;
                candidates.Add(new Candidate(leaf, definition, CacheCleanMethod.NotAllowed, whitelistReason, HoldReason: null));
                continue;
            }

            if (leaf.InUse)
            {
                candidates.Add(new Candidate(
                    leaf,
                    definition,
                    CacheCleanMethod.DeferredInUse,
                    NotAllowedReason: null,
                    "Используется запущенным процессом — объект отложен (FR-2.8)"));
                continue;
            }

            var method = DecideMethod(leaf);
            if (method == CacheCleanMethod.NotAllowed)
            {
                notAllowed++;
                candidates.Add(new Candidate(
                    leaf,
                    definition,
                    CacheCleanMethod.NotAllowed,
                    "Нет штатной команды, а прямое удаление запрещено конфигурацией",
                    HoldReason: null));
                continue;
            }

            candidates.Add(new Candidate(leaf, definition, method, NotAllowedReason: null, HoldReason: null));
        }

        var lowSpaceDrives = CollectLowSpaceDrives(candidates, threshold);
        var actions = new List<CacheCleanAction>(candidates.Count);

        foreach (var candidate in candidates)
        {
            actions.Add(CreateAction(candidate, lowSpaceDrives, threshold));
        }

        var guardNote = lowSpaceDrives.Count == 0
            ? null
            : GuardNote(lowSpaceDrives, threshold);

        return new CacheCleanPlan
        {
            Actions = actions,
            OutOfScopeCount = outOfScope,
            NotAllowedCount = notAllowed,
            LowSpaceDrives = lowSpaceDrives,
            GuardNote = guardNote
        };
    }

    /// <summary>
    /// Матрица решений «команда vs прямое удаление» (FR-2.6, FR-2.7): приоритет штатной
    /// команды менеджера; прямое удаление каталога — fallback. Осиротевший кэш (FR-2.4)
    /// менеджер не знает — только прямое удаление.
    /// </summary>
    private static CacheCleanMethod DecideMethod(CleanupItem item)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return string.IsNullOrEmpty(item.CleanCommandFile)
                ? CacheCleanMethod.NotAllowed
                : CacheCleanMethod.NativeCommand;
        }

        // Осиротевший кэш (FR-2.4): менеджер его больше не использует,
        // штатная команда его не знает — только прямое удаление.
        if (item.IsOrphan)
        {
            return CacheCleanMethod.DirectDelete;
        }

        if (!string.IsNullOrEmpty(item.CleanCommandFile))
        {
            // FR-2.6/2.7: приоритет штатной команды; прямое удаление — fallback,
            // если после команды размер не изменился/изменился незначительно.
            return item.AllowDirectDelete
                ? CacheCleanMethod.NativeWithDirectFallback
                : CacheCleanMethod.NativeCommand;
        }

        return item.AllowDirectDelete
            ? CacheCleanMethod.DirectDelete
            : CacheCleanMethod.NotAllowed;
    }

    private static CacheCleanAction CreateAction(
        Candidate candidate,
        IReadOnlyList<CacheDriveSpace> lowSpaceDrives,
        double threshold)
    {
        var item = candidate.Item;
        var method = candidate.Method;
        var isCleanable = method is CacheCleanMethod.NativeCommand
            or CacheCleanMethod.NativeWithDirectFallback
            or CacheCleanMethod.DirectDelete;

        string? holdReason = candidate.HoldReason;
        if (holdReason is null && isCleanable && item.Path is not null)
        {
            var space = FindDriveSpace(lowSpaceDrives, item.Path);
            if (space is not null)
            {
                holdReason = LowSpaceHoldReason(space, threshold);
            }
        }

        return new CacheCleanAction
        {
            Item = item,
            Method = method,
            Consent = ConsentFor(item, candidate.Definition),
            EstimatedSavingsBytes = item.EffectiveSizeBytes,
            SavingsText = SavingsText(item.EffectiveSizeBytes, method),
            NativeCommandText = string.IsNullOrWhiteSpace(item.CleanCommand) ? null : item.CleanCommand,
            ConsequencesText = ConsequencesTextFor(item, method),
            RestoreEstimateText = RestoreEstimateText(item.EffectiveSizeBytes, RestoreKindFor(item, candidate.Definition)),
            NotAllowedReason = candidate.NotAllowedReason,
            HoldReason = holdReason
        };
    }

    private static CacheDriveSpace? FindDriveSpace(
        IReadOnlyList<CacheDriveSpace> lowSpaceDrives,
        string path)
    {
        var driveRoot = DriveRootOf(path);
        return driveRoot is null
            ? null
            : lowSpaceDrives.FirstOrDefault(d => d.RootPath.Equals(driveRoot, StringComparison.OrdinalIgnoreCase));
    }

    private static CacheConsent ConsentFor(CleanupItem item, CacheTargetDefinition? definition)
    {
        if (definition is not null &&
            definition.ConsentLevel.Equals("Ask", StringComparison.OrdinalIgnoreCase))
        {
            return CacheConsent.Ask;
        }

        return item.Risk == CleanupRisk.Low ? CacheConsent.Auto : CacheConsent.Ask;
    }

    private static string RestoreKindFor(CleanupItem item, CacheTargetDefinition? definition)
    {
        if (definition is not null)
        {
            return definition.RestoreHint;
        }

        return PathSegments(item.Path).Any(segment => RecreatePathSegments.Contains(segment))
            ? "recreate"
            : "download";
    }

    private static string ConsequencesTextFor(CleanupItem item, CacheCleanMethod method)
    {
        if (!string.IsNullOrWhiteSpace(item.Warning))
        {
            return item.Warning!;
        }

        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            return item.Description!;
        }

        return method switch
        {
            CacheCleanMethod.NativeCommand => "Будет выполнена штатная команда менеджера.",
            CacheCleanMethod.NativeWithDirectFallback => "Очистка штатной командой менеджера; при необходимости — прямое удаление каталога.",
            CacheCleanMethod.DirectDelete => "Каталог кэша будет удалён полностью; содержимое пересоздаётся при следующем использовании.",
            CacheCleanMethod.DeferredInUse => "Объект используется процессом и будет очищен позже.",
            _ => "Очистка кэша; содержимое пересоздаётся при следующем использовании."
        };
    }

    private static string RestoreEstimateText(long bytes, string restoreKind)
    {
        var hasSize = bytes > 0;
        var formatted = Reports.CleanReportFormatter.FormatBytes(bytes);

        return restoreKind.ToLowerInvariant() switch
        {
            "reinstall" when hasSize =>
                $"Требуется повторная установка (≈ {formatted}); время зависит от скорости канала.",
            "reinstall" =>
                "Требуется повторная установка тулчейна/инструмента; объём и время зависят от удалённого.",
            "recreate" =>
                "Пересоздаётся автоматически локально; трафик на восстановление не требуется.",
            _ when hasSize =>
                $"Повторная загрузка ≈ {formatted}; время зависит от скорости канала.",
            _ =>
                "Повторная загрузка при следующем использовании; объём зависит от удалённого."
        };
    }

    private static string SavingsText(long bytes, CacheCleanMethod method)
    {
        if (method is CacheCleanMethod.DeferredInUse or CacheCleanMethod.NotAllowed)
        {
            return string.Empty;
        }

        return bytes > 0
            ? $"Освободит ≈ {Reports.CleanReportFormatter.FormatBytes(bytes)}"
            : "Экономия уточняется после анализа";
    }

    private IReadOnlyList<CacheDriveSpace> CollectLowSpaceDrives(
        IReadOnlyList<Candidate> candidates,
        double threshold)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lowSpace = new List<CacheDriveSpace>();

        foreach (var candidate in candidates.Where(IsCleanableMethod))
        {
            var path = candidate.Item.Path;
            if (path is null || !seenPaths.Add(path))
            {
                continue;
            }

            var space = _driveSpace.GetDriveSpace(path);
            if (space is not null &&
                space.FreeFraction < threshold &&
                seenRoots.Add(space.RootPath))
            {
                lowSpace.Add(space);
            }
        }

        return lowSpace;
    }

    private static bool IsCleanableMethod(Candidate candidate) =>
        candidate.Method is CacheCleanMethod.NativeCommand
            or CacheCleanMethod.NativeWithDirectFallback
            or CacheCleanMethod.DirectDelete;

    private static string? DriveRootOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string LowSpaceHoldReason(CacheDriveSpace space, double threshold) =>
        $"Защитный режим «не трогать»: на диске {space.RootPath} свободно {space.FreeFraction:P0} (< {threshold:P0}). Кэш помечен «не трогать» (FR-2.12).";

    private static string GuardNote(IReadOnlyList<CacheDriveSpace> lowSpaceDrives, double threshold)
    {
        var summary = string.Join(
            "; ",
            lowSpaceDrives.Select(d =>
                $"{d.RootPath}: свободно {d.FreeFraction:P0} ({Reports.CleanReportFormatter.FormatBytes(d.FreeBytes)})"));
        return $"Защитный режим «не трогать»: на диске {summary} свободно менее {threshold:P0} — кэши на этих дисках помечены «не трогать» (FR-2.12).";
    }

    private CacheTargetDefinition? FindDefinition(CleanupItem item)
    {
        var separator = item.Key.IndexOf(':');
        if (separator <= 0)
        {
            return null;
        }

        var id = item.Key[..separator];
        return _definitionsById.TryGetValue(id, out var definition) ? definition : null;
    }

    /// <summary>
    /// Каталоги <c>*-updater</c> и конфиги удалённых приложений — категория Leftover (FR-2.9),
    /// в плане очистки кэшей не показываются.
    /// </summary>
    private static string? LeftoverReason(CleanupItem item)
    {
        if (item.Category == CleanupCategory.Leftover ||
            PathSegments(item.Path).Any(segment => segment.EndsWith("-updater", StringComparison.OrdinalIgnoreCase)))
        {
            return "Каталоги *-updater и конфиги удалённых приложений не относятся к кэшам — категория Leftover (FR-2.9)";
        }

        return null;
    }

    /// <summary>
    /// VS Code: удалять только перечень кэш-подпапок (FR-2.10); никогда <c>User/</c>,
    /// <c>extensions/</c>, <c>settings.json</c> и сам каталог <c>Code</c>.
    /// </summary>
    private static string? VSCodeWhitelistReason(CleanupItem item, string? vsCodeRoot)
    {
        if (string.IsNullOrEmpty(vsCodeRoot) || !PathStartsWith(item.Path, vsCodeRoot))
        {
            return null;
        }

        var relative = RelativeSegments(item.Path!, vsCodeRoot!);
        if (relative.Length == 0)
        {
            return "Сам каталог VS Code (Code) не очищается — только кэш-подпапки из перечня (FR-2.10)";
        }

        var firstSegment = relative[0];
        if (!VSCodeAllowedSubfolders.Contains(firstSegment))
        {
            return $"«{firstSegment}» не входит в перечень кэш-подпапок VS Code (Cache, CachedData, CachedExtensionVSIXs, Crashpad, GPUCache, logs, WebStorage); User/, extensions/ и файлы конфигурации не очищаются (FR-2.10)";
        }

        return item.Target == CleanupTarget.File
            ? "Файлы VS Code (например, settings.json) не очищаются — только кэш-подпапки (FR-2.10)"
            : null;
    }

    private static string? VSCodeRoot(Abstractions.IEnvironment environment)
    {
        try
        {
            return Path.GetFullPath(environment.ExpandPath(VSCodeRootPathPattern));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool PathStartsWith(string? path, string root)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string left;
        string right;
        try
        {
            left = Path.GetFullPath(path).TrimEnd('\\', '/');
            right = root.TrimEnd('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
               left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] RelativeSegments(string path, string root)
    {
        string left;
        string right;
        try
        {
            left = Path.GetFullPath(path).TrimEnd('\\', '/');
            right = root.TrimEnd('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Array.Empty<string>();
        }

        if (!left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        return left[(right.Length + 1)..]
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(part => part.Length > 0)
            .ToArray();
    }

    private static IEnumerable<string> PathSegments(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            yield break;
        }

        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length > 0)
            {
                yield return segment;
            }
        }
    }

    /// <summary>Промежуточное решение по объекту до материализации карточки (низкоуровневое состояние).</summary>
    private sealed record Candidate(
        CleanupItem Item,
        CacheTargetDefinition? Definition,
        CacheCleanMethod Method,
        string? NotAllowedReason,
        string? HoldReason);
}
