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

            if (string.IsNullOrWhiteSpace(app.Publisher))
            {
                anomalies.Add(AppAnomalyKind.SuspiciousPublisher);
                notes.Add("Пустой издатель (Publisher).");
            }
            else if (SuspiciousTokens.Any(t => app.Publisher.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                anomalies.Add(AppAnomalyKind.SuspiciousPublisher);
                notes.Add($"Издатель содержит подозрительный токен: {app.Publisher}");
            }

            if (SuspiciousTokens.Any(t => app.DisplayName.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                IsDomainLike(app.DisplayName))
            {
                anomalies.Add(AppAnomalyKind.SuspiciousDomain);
                notes.Add("Имя продукта похоже на домен/подозрительный идентификатор.");
            }

            result.Add(new InstalledAppAnalysis(
                app,
                anomalies.Distinct().ToList(),
                notes.Count == 0 ? null : string.Join(" ", notes)));
        }

        return result;
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
            if (best is null || CompareInstallDate(item.InstallDate, best.InstallDate) > 0)
            {
                best = item;
            }
        }

        return best?.ProductCode;
    }

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
