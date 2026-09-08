using System.Globalization;
using System.Text.RegularExpressions;

namespace DiskCleaner.Core.Uninstall;

public enum AppAnomalyKind
{
    Duplicate,
    OldVersion,
    SuspiciousPublisher,
    SuspiciousDomain,
    SuspiciousInstallDate,
    ReviewManually
}

/// <summary>
/// Результат анализа одной записи установленного ПО (FR-4.2/4.3): приложенная запись
/// <see cref="App"/>, найденные аномалии и человекочитаемое пояснение. Для участников
/// группы дублей дополнительно заполняются данные «какую запись держать» (самую свежую)
/// — <see cref="KeepProductCode"/>, версия и дата установки этой записи, чтобы рекомендация
/// «держать последнюю, удалять старые» была конкретной (FR-4.2).
/// </summary>
public sealed record InstalledAppAnalysis(
    InstalledApp App,
    IReadOnlyList<AppAnomalyKind> Anomalies,
    string? Note)
{
    public bool IsDuplicate => Anomalies.Contains(AppAnomalyKind.Duplicate);

    public bool IsOldVersion => Anomalies.Contains(AppAnomalyKind.OldVersion);

    public bool NeedsReview => Anomalies.Contains(AppAnomalyKind.ReviewManually)
        || Anomalies.Contains(AppAnomalyKind.SuspiciousPublisher)
        || Anomalies.Contains(AppAnomalyKind.SuspiciousDomain)
        || Anomalies.Contains(AppAnomalyKind.SuspiciousInstallDate);

    /// <summary>Семейство дублей (нормализованное DisplayName), если запись входит в группу из ≥ 2 записей.</summary>
    public string? DuplicateFamilyName { get; init; }

    /// <summary>Сколько установок одного продукта найдено (для записей-дублей).</summary>
    public int DuplicateGroupCount { get; init; }

    /// <summary>ProductCode записи, которую рекомендуется оставить (самую свежую). Заполнен у всех членов группы.</summary>
    public string? KeepProductCode { get; init; }

    /// <summary>DisplayVersion записи, которую рекомендуется оставить (для пометки рекомендации, FR-4.2).</summary>
    public string? KeepDisplayVersion { get; init; }

    /// <summary>InstallDate записи, которую рекомендуется оставить (для пометки рекомендации, FR-4.2).</summary>
    public string? KeepInstallDate { get; init; }
}

public sealed partial class InstalledAppAnalyzer
{
    private static readonly HashSet<string> SuspiciousTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "${PRODUCT_PUBLISHER}",
        "networkserviceproxyd",
        "ilovehackit.ru",
        "crack",
        "keygen",
        "activation"
    };

    /// <summary>Источник даты первой загрузки Windows (для аномалии «InstallDate = первому запуску», FR-4.3).</summary>
    private readonly Func<DateTime?>? _windowsFirstRunUtcProvider;

    public InstalledAppAnalyzer(Func<DateTime?>? windowsFirstRunUtcProvider = null)
    {
        _windowsFirstRunUtcProvider = windowsFirstRunUtcProvider;
    }

    public IReadOnlyList<InstalledAppAnalysis> Analyze(IEnumerable<InstalledApp> apps)
    {
        var list = apps.ToList();
        var membership = BuildDuplicateMembership(list);
        var windowsFirstRun = _windowsFirstRunUtcProvider?.Invoke();
        var result = new List<InstalledAppAnalysis>(list.Count);

        foreach (var app in list)
        {
            var anomalies = new List<AppAnomalyKind>();
            var notes = new List<string>();

            DuplicateGroup? group = null;
            if (membership.TryGetValue(app.ProductCode, out var member))
            {
                group = member.Group;
            }

            if (group is { Items.Count: > 1 })
            {
                anomalies.Add(AppAnomalyKind.Duplicate);
                notes.Add($"Найдено {group.Items.Count} установки одного продукта (семейство «{NormalizeName(app.DisplayName)}»).");

                if (group.KeepProductCode != app.ProductCode)
                {
                    anomalies.Add(AppAnomalyKind.OldVersion);
                    var keep = group.Items.FirstOrDefault(i => i.ProductCode == group.KeepProductCode);
                    notes.Add(
                        $"Рекомендуется удалить эту старую версию (версия {VersionText(app.DisplayVersion)}, установлена {FormatDate(app.InstallDate)}); " +
                        $"оставить самую свежую: версия {VersionText(keep?.DisplayVersion)} (установлена {FormatDate(keep?.InstallDate)}).");
                }
                else
                {
                    notes.Add(
                        $"Это самая свежая установка семейства (версия {VersionText(app.DisplayVersion)}, " +
                        $"установлена {FormatDate(app.InstallDate)}) — рекомендуется её оставить.");
                }
            }

            if (IsSuspiciousPublisher(app.Publisher, out var publisherReason))
            {
                anomalies.Add(AppAnomalyKind.SuspiciousPublisher);
                notes.Add(publisherReason);
            }

            if (IsSuspiciousName(app.DisplayName, out var nameReason))
            {
                anomalies.Add(AppAnomalyKind.SuspiciousDomain);
                notes.Add(nameReason);
            }

            if (windowsFirstRun is { } firstRun &&
                IsSameDay(app.InstallDate, firstRun))
            {
                anomalies.Add(AppAnomalyKind.SuspiciousInstallDate);
                notes.Add(
                    $"Дата установки ({FormatDate(app.InstallDate)}) совпадает с датой первой загрузки Windows — " +
                    "возможно, приложение появилось при первом включении машины; рекомендуется проверить вручную.");
            }

            var requiresReview = anomalies.Contains(AppAnomalyKind.SuspiciousPublisher)
                || anomalies.Contains(AppAnomalyKind.SuspiciousDomain)
                || anomalies.Contains(AppAnomalyKind.SuspiciousInstallDate);
            if (requiresReview)
            {
                anomalies.Add(AppAnomalyKind.ReviewManually);
            }

            result.Add(new InstalledAppAnalysis(
                app,
                anomalies.Distinct().ToList(),
                notes.Count == 0 ? null : string.Join(" ", notes))
            {
                DuplicateFamilyName = group is { Items.Count: > 1 } ? NormalizeName(app.DisplayName) : null,
                DuplicateGroupCount = group?.Items.Count ?? 0,
                KeepProductCode = group is { Items.Count: > 1 } ? group.KeepProductCode : null,
                KeepDisplayVersion = group is { Items.Count: > 1 } && group.KeepProductCode is { } keepCode
                    ? group.Items.FirstOrDefault(i => i.ProductCode == keepCode)?.DisplayVersion
                    : null,
                KeepInstallDate = group is { Items.Count: > 1 } && group.KeepProductCode is { } keepDateCode
                    ? group.Items.FirstOrDefault(i => i.ProductCode == keepDateCode)?.InstallDate
                    : null
            });
        }

        return result;
    }

    private static bool IsSuspiciousPublisher(string? publisher, out string reason)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            reason = "Пустой издатель (Publisher) — требует ручной проверки.";
            return true;
        }

        if (SuspiciousTokens.Any(t => publisher.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"Издатель содержит подозрительный токен: {publisher}";
            return true;
        }

        if (TemplateVariableRegex().IsMatch(publisher))
        {
            reason = $"Издатель содержит нераскрытую переменную шаблона: {publisher}";
            return true;
        }

        if (IsDomainLike(publisher))
        {
            reason = $"Издатель похож на домен: {publisher}";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool IsSuspiciousName(string? name, out string reason)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            reason = string.Empty;
            return false;
        }

        if (SuspiciousTokens.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"Имя продукта содержит подозрительный токен: {name}";
            return true;
        }

        if (IsDomainLike(name))
        {
            reason = $"Имя продукта похоже на домен: {name}";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static Dictionary<string, DuplicateMembership> BuildDuplicateMembership(IEnumerable<InstalledApp> apps)
    {
        var groups = new Dictionary<string, DuplicateGroup>(StringComparer.OrdinalIgnoreCase);
        var membership = new Dictionary<string, DuplicateMembership>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in apps)
        {
            var key = $"{NormalizeName(app.DisplayName)}|{NormalizePublisher(app.Publisher)}";
            if (!groups.TryGetValue(key, out var group))
            {
                group = new DuplicateGroup(key);
                groups.Add(key, group);
            }

            group.Items.Add(app);
            membership[app.ProductCode] = new DuplicateMembership(group);
        }

        foreach (var group in groups.Values)
        {
            group.KeepProductCode = SelectKeep(group.Items);
        }

        return membership;
    }

    private static string? SelectKeep(IReadOnlyList<InstalledApp> items)
    {
        InstalledApp? best = null;
        foreach (var item in items)
        {
            if (best is null || CompareKeepOrder(item, best) > 0)
            {
                best = item;
            }
        }

        return best?.ProductCode;
    }

    /// <summary>Свежее предпочитается по дате установки, при равенстве — по более новой версии (FR-1.9).</summary>
    private static int CompareKeepOrder(InstalledApp left, InstalledApp right)
    {
        var byDate = CompareInstallDate(left.InstallDate, right.InstallDate);
        if (byDate != 0)
        {
            return byDate;
        }

        var byVersion = CompareVersions(left.DisplayVersion, right.DisplayVersion);
        if (byVersion != 0)
        {
            return byVersion;
        }

        return string.CompareOrdinal(right.ProductCode, left.ProductCode);
    }

    private static int CompareVersions(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(left))
        {
            return -1;
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return 1;
        }

        var leftNumbers = VersionNumbers(left);
        var rightNumbers = VersionNumbers(right);
        var common = Math.Min(leftNumbers.Length, rightNumbers.Length);
        for (var i = 0; i < common; i++)
        {
            if (leftNumbers[i] != rightNumbers[i])
            {
                return leftNumbers[i] > rightNumbers[i] ? 1 : -1;
            }
        }

        if (leftNumbers.Length != rightNumbers.Length)
        {
            return leftNumbers.Length > rightNumbers.Length ? 1 : -1;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static int[] VersionNumbers(string value) =>
        DigitRegex().Matches(value)
            .Select(match => int.TryParse(match.Value, out var number) ? number : 0)
            .ToArray();

    private static int CompareInstallDate(string? left, string? right)
    {
        if (left == right)
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(left))
        {
            return -1;
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static bool IsDomainLike(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant();
        return DomainRegex().IsMatch(trimmed) &&
               trimmed.Contains('.') &&
               !trimmed.Contains(' ') &&
               !trimmed.Contains("\\") &&
               !trimmed.Contains('/');
    }

    private static bool IsSameDay(string? installDate, DateTime firstRun)
    {
        if (string.IsNullOrWhiteSpace(installDate))
        {
            return false;
        }

        if (installDate.Length == 8 &&
            DateTime.TryParseExact(installDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.Date == firstRun.Date;
        }

        return false;
    }

    private static string VersionText(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "не указана" : version;

    private static string FormatDate(string? installDate)
    {
        if (string.IsNullOrWhiteSpace(installDate))
        {
            return "не указана";
        }

        if (installDate.Length == 8 &&
            DateTime.TryParseExact(installDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToString("yyyy-MM-dd");
        }

        return installDate;
    }

    private static string NormalizePublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return string.Empty;
        }

        return VersionedSuffixRegex().Replace(publisher.Trim().ToLowerInvariant(), string.Empty);
    }

    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var normalized = name.ToLowerInvariant().Trim();
        normalized = ParentheticalRegex().Replace(normalized, " ");
        normalized = VersionedSuffixRegex().Replace(normalized, string.Empty);
        normalized = WhitespaceRegex().Replace(normalized, " ").Trim();
        return normalized;
    }

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex ParentheticalRegex();

    [GeneratedRegex(@"(^|\s)\d+([\.,]\d+)+(-\d+)?\s*$")]
    private static partial Regex VersionedSuffixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^[a-z0-9\-\.]+\.[a-z]{2,}$")]
    private static partial Regex DomainRegex();

    [GeneratedRegex(@"\$\{[^}]+\}")]
    private static partial Regex TemplateVariableRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitRegex();

    private sealed class DuplicateMembership
    {
        public DuplicateMembership(DuplicateGroup group)
        {
            Group = group;
        }

        public DuplicateGroup Group { get; }
    }

    private sealed class DuplicateGroup
    {
        public DuplicateGroup(string key)
        {
            Key = key;
        }

        public string Key { get; }

        public List<InstalledApp> Items { get; } = new();

        public string? KeepProductCode { get; set; }
    }
}
