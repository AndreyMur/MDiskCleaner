using System.Text.RegularExpressions;

namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// Нормализация имён для сопоставления каталогов с реестром Uninstall (FR-3.1):
/// «Google Chrome 131.0.0.0», «google-chrome», «GOOGLE  CHROME» сводятся к общему
/// набору токенов. Регистр игнорируется, не-буквенно-цифровые разделители схлопываются.
/// </summary>
public static partial class LeftoverNameNormalizer
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var collapsed = SeparatorsRegex().Replace(value, " ");
        return collapsed.Trim().ToLowerInvariant();
    }

    public static IReadOnlyList<string> Tokens(string value) =>
        Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// True, когда один набор токенов полностью равен другому либо является его
    /// префиксом («Google» ⊆ «Google Chrome», «Android Studio» ⊆ «Android Studio 2024.1»).
    /// </summary>
    public static bool TokensPrefixOrEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var (shorter, longer) = left.Count <= right.Count
            ? (left, right)
            : (right, left);

        return TokensPrefix(shorter, longer);
    }

    /// <summary>
    /// True, когда <paramref name="prefix"/> является префиксом <paramref name="value"/>
    /// (или равен ему целиком). Используется для имени каталога-бренда против DisplayName:
    /// «Google» ⊆ «Google Chrome», но «Slack Tech» не покрывается установленным «Slack».
    /// </summary>
    public static bool TokensPrefix(IReadOnlyList<string> prefix, IReadOnlyList<string> value)
    {
        if (prefix.Count == 0 || prefix.Count > value.Count)
        {
            return false;
        }

        for (var i = 0; i < prefix.Count; i++)
        {
            if (!string.Equals(prefix[i], value[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True, когда <paramref name="prefix"/> покрывает начало <paramref name="value"/>, а
    /// «хвост» (если есть) состоит только из версионных токенов: «Wondershare Filmora» ⊇
    /// «Wondershare Filmora 12», но не ⊇ «WondershareUpdate» (хвост «Update» — не версия).
    /// </summary>
    public static bool TokensPrefixWithVersionTail(IReadOnlyList<string> prefix, IReadOnlyList<string> value)
    {
        if (prefix.Count == 0 || prefix.Count > value.Count)
        {
            return false;
        }

        for (var i = 0; i < prefix.Count; i++)
        {
            if (!string.Equals(prefix[i], value[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        for (var i = prefix.Count; i < value.Count; i++)
        {
            if (!value[i].Any(char.IsDigit))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+", RegexOptions.IgnoreCase)]
    private static partial Regex SeparatorsRegex();
}
