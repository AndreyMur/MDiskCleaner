using DiskCleaner.Core.Processes;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// Скан «папка → кандидат-остаток» (модуль 03): перебираются только верхние уровни
/// ключевых каталогов (без рекурсивного обхода, NFR ≤ 60 с), а каждый каталог
/// классифицируется движком эвристик <see cref="LeftoverRuleEngine"/> (маска апдейтеров,
/// справочник брендов, «пустые/почти пустые» каталоги, пороги > 1 ГБ, проверка процессов)
/// в группы с автоматическим основанием — почему каталог остаток (FR-3.1–3.4, FR-3.6, §5).
/// </summary>
public sealed class LeftoverCandidateScanner
{
    public const string UpdatersGroupName = LeftoverRuleEngine.UpdatersGroupName;

    private readonly Abstractions.IEnvironment _environment;
    private readonly IProcessInspector _processInspector;
    private readonly LeftoverRuleEngine _ruleEngine;

    public LeftoverCandidateScanner(
        Abstractions.IEnvironment? environment = null,
        IProcessInspector? processInspector = null,
        LeftoverRuleEngine? ruleEngine = null)
    {
        _environment = environment ?? new Environment.EnvironmentProvider();
        _processInspector = processInspector ?? new ProcessInspector();
        _ruleEngine = ruleEngine ?? new LeftoverRuleEngine();
    }

    public IReadOnlyList<LeftoverCandidate> Scan(
        IReadOnlyList<InstalledApp> installedApps,
        IReadOnlyCollection<string>? exclusions = null)
    {
        var context = new LeftoverRuleContext
        {
            Environment = _environment,
            Whitelist = new InstalledWhitelist(installedApps, _environment),
            RunningProcesses = _processInspector.GetRunningProcesses(),
            Exclusions = exclusions ?? Array.Empty<string>()
        };

        var candidates = new List<LeftoverCandidate>();

        foreach (var root in LeftoverScanRoots.Resolve(_environment))
        {
            ScanRoot(context, root, candidates);
        }

        ScanLocalAppDataProgramsUpdaters(context, candidates);

        candidates.AddRange(_ruleEngine.ScanRemovedAppConfigs(context));

        return candidates;
    }

    private void ScanRoot(
        LeftoverRuleContext context,
        LeftoverScanRoot root,
        List<LeftoverCandidate> candidates)
    {
        IReadOnlyList<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(root.Path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

        foreach (var directory in directories)
        {
            var candidate = _ruleEngine.Classify(context, root, directory);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }
    }

    /// <summary>
    /// Апдейтеры, установленные вместе с приложением в <c>%LOCALAPPDATA%\Programs</c>
    /// (например <c>lm-studio-updater</c>) — находятся на уровень глубже верхнего корня.
    /// </summary>
    private void ScanLocalAppDataProgramsUpdaters(
        LeftoverRuleContext context,
        List<LeftoverCandidate> candidates)
    {
        var programs = Path.Combine(_environment.LocalApplicationData, "Programs");
        if (!Directory.Exists(programs))
        {
            return;
        }

        var root = new LeftoverScanRoot(
            LeftoverRootKind.LocalApplicationData,
            programs,
            "Осиротевшие каталоги в %LOCALAPPDATA%",
            RequiresAdmin: false);

        IReadOnlyList<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(programs).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

        foreach (var directory in directories)
        {
            var candidate = _ruleEngine.Classify(context, root, directory);
            if (candidate is not null &&
                candidate.Reason == LeftoverReason.UpdaterFolder)
            {
                candidates.Add(candidate);
            }
        }
    }
}
