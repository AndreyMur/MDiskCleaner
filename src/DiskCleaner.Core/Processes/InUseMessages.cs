using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Processes;

/// <summary>
/// Формулировки причин откладывания объекта, занятого запущенным процессом (FR-2.8,
/// FR-2.10, FR-5.7, FR-5.8): для известных «владельцев» (VS Code, браузеры) пользователю
/// показывается понятное имя, список блокирующих процессов и рекомендация —
/// «закройте приложение и повторите» либо «отложить шаг».
/// </summary>
public static class InUseMessages
{
    /// <summary>Известные процессы и их человекочитаемые ярлыки (VS Code — FR-2.10, браузеры — FR-5.8).</summary>
    private static readonly IReadOnlyDictionary<string, string> KnownProcessLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Code"] = "VS Code",
            ["Code.exe"] = "VS Code",
            ["code"] = "VS Code",
            ["code.exe"] = "VS Code",
            ["Code - Insiders"] = "VS Code Insiders",
            ["Code - Insiders.exe"] = "VS Code Insiders",
            ["VSCodium"] = "VSCodium",
            ["VSCodium.exe"] = "VSCodium",
            ["msedge"] = "Microsoft Edge",
            ["msedge.exe"] = "Microsoft Edge",
            ["chrome"] = "Google Chrome",
            ["chrome.exe"] = "Google Chrome",
            ["firefox"] = "Mozilla Firefox",
            ["firefox.exe"] = "Mozilla Firefox",
            ["brave"] = "Brave",
            ["brave.exe"] = "Brave",
            ["opera"] = "Opera",
            ["opera.exe"] = "Opera",
            ["vivaldi"] = "Vivaldi",
            ["vivaldi.exe"] = "Vivaldi"
        };

    /// <summary>
    /// Причина пропуска объекта, используемого запущенным процессом. Если объект принадлежит
    /// известному приложению (заданы <see cref="CleanupItem.OwnerProcessNames"/>) — сообщение
    /// называет приложение (например, «VS Code запущен») и рекомендует закрыть его (FR-2.10);
    /// дополняется списком блокирующих процессов и рекомендацией (FR-5.7, FR-5.8).
    /// </summary>
    public static string SkippedNote(CleanupItem item)
    {
        var label = OwnerLabel(item);
        var parts = new List<string>(3);
        parts.Add(label is null
            ? "Используется запущенным процессом — объект пропущен, план не прерван"
            : $"{label} запущен — объект используется запущенным процессом и отложен; будет очищен после закрытия {label}. Пропущен, план не прерван");

        parts.Add(AdviceText(item, label));

        var blockers = ProcessNamesText(item);
        if (blockers.Length > 0)
        {
            parts.Add(blockers);
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Рекомендация для занятого объекта (FR-5.8): для «долгоживущих» приложений (VS Code,
    /// браузеры) — «закройте приложение и повторите», иначе — «отложите шаг».
    /// </summary>
    public static InUseAdvice AdviceFor(
        CleanupItem item,
        IReadOnlyList<RunningProcessInfo>? blocking = null)
    {
        foreach (var process in blocking ?? item.BlockingProcesses)
        {
            if (KnownProcessLabels.ContainsKey(process.Name))
            {
                return InUseAdvice.CloseAndRetry;
            }
        }

        foreach (var name in item.OwnerProcessNames)
        {
            if (KnownProcessLabels.ContainsKey(name))
            {
                return InUseAdvice.CloseAndRetry;
            }
        }

        return InUseAdvice.DeferStep;
    }

    /// <summary>Человекочитаемый ярлык процесса-владельца по <see cref="CleanupItem.OwnerProcessNames"/>.</summary>
    public static string? OwnerLabel(CleanupItem item)
    {
        foreach (var name in item.OwnerProcessNames)
        {
            if (KnownProcessLabels.TryGetValue(name, out var label))
            {
                return label;
            }
        }

        return null;
    }

    /// <summary>
    /// Список блокирующих процессов (имена через запятую) для показа пользователю (FR-5.7);
    /// пустая строка, если список не заполнен.
    /// </summary>
    public static string ProcessNamesText(CleanupItem item)
    {
        if (item.BlockingProcesses.Count == 0)
        {
            return string.Empty;
        }

        var names = item.BlockingProcesses
            .Select(p => string.IsNullOrWhiteSpace(p.Name) ? p.ExecutablePath : p.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return "Блокирующие процессы: " + string.Join(", ", names);
    }

    private static string AdviceText(CleanupItem item, string? label)
    {
        return AdviceFor(item, item.BlockingProcesses) == InUseAdvice.CloseAndRetry
            ? label is null
                ? "Закройте приложение и повторите попытку, либо отложите шаг (FR-5.8)."
                : $"Закройте {label} и повторите попытку, либо отложите шаг (FR-5.8)."
            : "Шаг отложен — повторите позже (FR-5.8).";
    }
}
