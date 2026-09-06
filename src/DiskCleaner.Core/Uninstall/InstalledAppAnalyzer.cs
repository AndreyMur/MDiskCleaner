using System.Text.RegularExpressions;

namespace DiskCleaner.Core.Uninstall;

public enum AppAnomalyKind
{
    Duplicate,
    OldVersion,
    SuspiciousPublisher,
    SuspiciousDomain,
    ReviewManually
}

public sealed record InstalledAppAnalysis(
    InstalledApp App,
    IReadOnlyList<AppAnomalyKind> Anomalies,
    string? Note)
{
    public bool IsDuplicate => Anomalies.Contains(AppAnomalyKind.Duplicate);

    public bool IsOldVersion => Anomalies.Contains(AppAnomalyKind.OldVersion);

    public bool NeedsReview => Anomalies.Contains(AppAnomalyKind.ReviewManually)
        || Anomalies.Contains(AppAnomalyKind.SuspiciousPublisher)
        || Anomalies.Contains(AppAnomalyKind.SuspiciousDomain);
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

    public IReadOnlyList<InstalledAppAnalysis> Analyze(IEnumerable<InstalledApp> apps)
    {
        var list = apps.ToList();
        var membership = BuildDuplicateMembership(list);
        var result = new List<InstalledAppAnalysis>(list.Count);

        foreach (var app in list)
        {
            var anomalies = new List<AppAnomalyKind>();
            var notes = new List<string>();

            if (membership.TryGetValue(app.ProductCode, out var member) &&
                member.Group.Items.Count > 1)
            {
                anomalies.Add(AppAnomalyKind.Duplicate);
                notes.Add($"Найдено {member.Group.Items.Count} установки одного продукта (семейство «{NormalizeName(app.DisplayName)}»).");

                if (member.Group.KeepProductCode != app.ProductCode)
                {
                    anomalies.Add(AppAnomalyKind.OldVersion);
                    notes.Add("Рекомендуется удалить старую версию, оставить самую свежую.");
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

            var requiresReview = anomalies.Contains(AppAnomalyKind.SuspiciousPublisher)
                || anomalies.Contains(AppAnomalyKind.SuspiciousDomain);
            if (requiresReview)
            {
                anomalies.Add(AppAnomalyKind.ReviewManually);
            }

            result.Add(new InstalledAppAnalysis(
                app,
                anomalies.Distinct().ToList(),
                notes.Count == 0 ? null : string.Join(" ", notes)));
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
