using System.Globalization;
using System.Text.RegularExpressions;

namespace DiskCleaner.Core.Caches;

/// <summary>
/// Разбор выходных данных штатных команд очистки менеджеров (фаза 3 модуля 02):
/// извлекает объём, о котором сообщил менеджер («Total reclaimed space: 4.1GB» и т.п.).
/// Используется для контроля результата command-only объектов (docker prune и т.п.),
/// у которых нет каталога-цели для повторного измерения (FR-2.6–2.7, журнал действий NFR).
/// </summary>
public static class ManagerOutputParser
{
    private static readonly Regex SizeLiteralRegex = new(
        @"(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>ТБ|ТиБ|TiB|TB|ГБ|ГиБ|GiB|GB|МБ|МиБ|MiB|MB|КБ|КиБ|KiB|KB|Б|B)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] ReclaimKeywords =
    [
        "reclaim", "freed", "cleaned", "purge", "removed", "deleted", "space",
        "освобожд", "удален", "очищен"
    ];

    private static readonly IReadOnlyDictionary<string, long> UnitBytes =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["B"] = 1L,
            ["Б"] = 1L,
            ["KB"] = 1L << 10,
            ["КБ"] = 1L << 10,
            ["KiB"] = 1L << 10,
            ["КиБ"] = 1L << 10,
            ["MB"] = 1L << 20,
            ["МБ"] = 1L << 20,
            ["MiB"] = 1L << 20,
            ["МиБ"] = 1L << 20,
            ["GB"] = 1L << 30,
            ["ГБ"] = 1L << 30,
            ["GiB"] = 1L << 30,
            ["ГиБ"] = 1L << 30,
            ["TB"] = 1L << 40,
            ["ТБ"] = 1L << 40,
            ["TiB"] = 1L << 40,
            ["ТиБ"] = 1L << 40
        };

    /// <summary>
    /// Пытается определить объём, освобождённый по словам менеджера. Возвращает <c>null</c>,
    /// если в выводе нет понятной строки с объёмом (например, docker «Deleted Containers: 2»).
    /// </summary>
    public static long? TryParseReclaimedBytes(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        long? best = null;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine;
            if (!ContainsReclaimKeyword(line))
            {
                continue;
            }

            foreach (Match match in SizeLiteralRegex.Matches(line))
            {
                var bytes = SizeLiteralToBytes(match);
                if (bytes is not null && (best is null || bytes > best))
                {
                    best = bytes;
                }
            }
        }

        return best;
    }

    private static bool ContainsReclaimKeyword(string line)
    {
        foreach (var keyword in ReclaimKeywords)
        {
            if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static long? SizeLiteralToBytes(Match match)
    {
        var unit = match.Groups["unit"].Value;
        if (!UnitBytes.TryGetValue(unit, out var multiplier))
        {
            return null;
        }

        var numberText = match.Groups["value"].Value.Replace(',', '.');
        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            value < 0)
        {
            return null;
        }

        return (long)(value * multiplier);
    }
}
