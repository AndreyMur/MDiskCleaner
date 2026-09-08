using DiskCleaner.Core.Caches;

namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Запрос перезагрузки Windows после завершения деинсталляции (фаза 24, модуль 04, FR-4.12).
/// Перезагрузка никогда не выполняется принудительно: команды MSI строятся с флагом
/// <c>/norestart</c> (<c>msiexec /restart</c> не используется), а по завершении операции система
/// <b>только запрашивает</b> у пользователя согласие перезагрузиться. <see cref="Recommended"/>
/// означает, что хотя бы один шаг деинсталляции вернул признак «требуется перезагрузка»
/// (MSI exit-код 3010); <see cref="RequiringItems"/> — названия таких объектов.
/// </summary>
public sealed class UninstallRebootRequest
{
    public UninstallRebootRequest(bool recommended, IReadOnlyList<string> requiringItems)
    {
        Recommended = recommended;
        RequiringItems = requiringItems;
    }

    public static UninstallRebootRequest NotRequired { get; } =
        new(false, Array.Empty<string>());

    /// <summary>Нужно ли предложить пользователю перезагрузку после операции.</summary>
    public bool Recommended { get; }

    /// <summary>Названия объектов деинсталляции, требующих перезагрузки.</summary>
    public IReadOnlyList<string> RequiringItems { get; }

    /// <summary>Текст запроса для диалога (интерфейс сам показывает кнопки «Перезагрузить сейчас»/«Позже»).</summary>
    public string PromptMessage => RequiringItems.Count == 0
        ? "Некоторые операции требуют перезагрузки Windows. Перезагрузить компьютер сейчас?"
        : $"Некоторые операции требуют перезагрузки Windows: {string.Join(", ", RequiringItems)}. Перезагрузить компьютер сейчас?";

    /// <summary>Запрос по журналу очистки (в т.ч. elevated-пачки деинсталляций, <see cref="CleanOutcome.RebootRequired"/>).</summary>
    public static UninstallRebootRequest FromCleanReport(CleanReport report)
    {
        var items = report.Entries
            .Where(entry => entry.Outcome == CleanOutcome.RebootRequired)
            .Select(entry => entry.Item.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToList();

        return items.Count == 0 ? NotRequired : new UninstallRebootRequest(true, items);
    }

    /// <summary>Запрос по журналу исполнения пачки удалений (модуль 04, <see cref="UninstallExecutionReport"/>).</summary>
    public static UninstallRebootRequest FromUninstallReport(UninstallExecutionReport report)
    {
        var items = report.Entries
            .Where(entry => entry.Outcome == UninstallExecutionOutcome.RebootRequired)
            .Select(entry => entry.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToList();

        return items.Count == 0 ? NotRequired : new UninstallRebootRequest(true, items);
    }

    /// <summary>Запрос по отчёту массового удаления версии SDK (модуль 04, <see cref="SdkBulkUninstallReport"/>).</summary>
    public static UninstallRebootRequest FromSdkBulkReport(SdkBulkUninstallReport report)
    {
        var items = report.Components
            .Where(component => component.Outcome == UninstallExecutionOutcome.RebootRequired)
            .Select(component => component.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToList();

        return items.Count == 0 ? NotRequired : new UninstallRebootRequest(true, items);
    }
}
