namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Правило статического списка известных зависимостей (модуль 04, фаза 24, §7 PRD): если удаляется
/// продукт, соответствующий <see cref="RemovedNameToken"/> (и версии <see cref="RemovedBuildToken"/>),
/// он может потребоваться продукту <see cref="AffectedNameToken"/>, который всё ещё установлен —
/// для такого шага формируется предупреждение «удаление может затронуть X». v1.0 — статический
/// список (например: удаление Windows SDK 19041 может затронуть Visual Studio Build Tools 2019).
/// </summary>
public sealed record UninstallDependencyRule(
    string RemovedNameToken,
    string? RemovedBuildToken,
    string AffectedNameToken,
    IReadOnlyList<string>? AffectedExcludedTokens = null)
{
    /// <summary>Совпадает ли продукт-«зависимость» (удаляемый) с правилом.</summary>
    public bool MatchesRemoved(InstalledApp app) =>
        ContainsToken(app.DisplayName, RemovedNameToken) &&
        (RemovedBuildToken is null ||
         ContainsToken(app.DisplayName, RemovedBuildToken) ||
         ContainsToken(app.DisplayVersion, RemovedBuildToken));

    /// <summary>Совпадает ли установленный продукт с «затрагиваемым» правилом.</summary>
    public bool MatchesAffected(InstalledApp app) =>
        ContainsToken(app.DisplayName, AffectedNameToken) &&
        !ContainsAnyToken(app, AffectedExcludedTokens);

    private static bool ContainsToken(string? value, string token) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAnyToken(InstalledApp app, IReadOnlyList<string>? tokens)
    {
        if (tokens is null)
        {
            return false;
        }

        foreach (var token in tokens)
        {
            if (ContainsToken(app.DisplayName, token) || ContainsToken(app.DisplayVersion, token))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Статический каталог известных зависимостей (v1.0, §7 PRD 04): «удаление X может затронуть Y».
/// Используется для пообъектного подтверждения шагов плана — каждый шаг деинсталляции, чьё
/// удаление способно затронуть ещё установленные программы, несёт соответствующее предупреждение.
/// </summary>
public sealed class UninstallDependencyCatalog
{
    /// <summary>
    /// Список по умолчанию: удаление Windows SDK версии 19041 может затронуть установленные
    /// Visual Studio / VS Build Tools (кроме версий 2022/2025, использующих новые SDK).
    /// </summary>
    public static readonly IReadOnlyList<UninstallDependencyRule> DefaultRules =
    [
        new UninstallDependencyRule(
            RemovedNameToken: "windows sdk",
            RemovedBuildToken: "19041",
            AffectedNameToken: "visual studio",
            AffectedExcludedTokens: ["2022", "2025"])
    ];

    private readonly IReadOnlyList<UninstallDependencyRule> _rules;

    public UninstallDependencyCatalog(IEnumerable<UninstallDependencyRule>? rules = null)
    {
        _rules = (rules ?? DefaultRules).ToList();
    }

    /// <summary>
    /// Установленные продукты (названия), на которые может повлиять удаление
    /// <paramref name="removalTarget"/> (по статическому списку зависимостей). Сам удаляемый
    /// продукт в результат не включается.
    /// </summary>
    public IReadOnlyList<string> FindAffectedProducts(
        IEnumerable<InstalledApp> installedApps,
        InstalledApp removalTarget)
    {
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in installedApps)
        {
            if (string.IsNullOrWhiteSpace(app.DisplayName) ||
                string.Equals(app.DisplayName, removalTarget.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var rule in _rules)
            {
                if (rule.MatchesRemoved(removalTarget) && rule.MatchesAffected(app))
                {
                    affected.Add(app.DisplayName);
                    break;
                }
            }
        }

        return affected
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
