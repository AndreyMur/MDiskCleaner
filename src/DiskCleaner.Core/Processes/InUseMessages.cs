using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Processes;

/// <summary>
/// Формулировки причин откладывания объекта, занятого запущенным процессом (FR-2.8, FR-2.10):
/// для известных «владельцев» (например, VS Code) пользователю показывается понятное имя
/// и рекомендация закрыть приложение.
/// </summary>
public static class InUseMessages
{
    /// <summary>Известные имена процессов и их человекочитаемые ярлыки (VS Code — FR-2.10).</summary>
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
            ["VSCodium.exe"] = "VSCodium"
        };

    /// <summary>
    /// Причина пропуска объекта, используемого запущенным процессом. Если объект принадлежит
    /// известному приложению (заданы <see cref="CleanupItem.OwnerProcessNames"/>) — сообщение
    /// называет приложение (например, «VS Code запущен») и рекомендует закрыть его (FR-2.10).
    /// </summary>
    public static string SkippedNote(CleanupItem item)
    {
        var label = OwnerLabel(item);
        return label is null
            ? "Используется запущенным процессом — объект пропущен, план не прерван"
            : $"{label} запущен — объект используется запущенным процессом и отложен; будет очищен после закрытия {label}. Пропущен, план не прерван";
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
}
