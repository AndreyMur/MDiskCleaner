using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;
using DiskCleaner.Core.Uninstall;
using Serilog;

namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// Исполнитель подтверждённых удалений остатков (FR-3.8, FR-3.5, §5 PRD 03):
/// <list type="bullet">
/// <item>dry-run-предпросмотр ничего не удаляет (FR-3.8);</item>
/// <item>перед каждым удалением повторяются проверки «живых» объектов Фазы 3 —
/// запущенные процессы / вхождение пути в <c>%PATH%</c> / исполняемый файл
/// зарегистрированной службы — такие объекты пропускаются (FR-3.4, §5);</item>
/// <item>пользовательские пути удаляются в контексте пользователя; Program Files /
/// ProgramData / Windows.old — одним elevated-процессом через UAC
/// (<see cref="IElevatedRunner"/>, PRD 05);</item>
/// <item>журнал фиксирует основание удаления (почему объект остаток) для каждого объекта
/// (FR-3.6, NFR).</item>
/// </list>
/// Опасные объекты (<see cref="LeftoverPlanItem.RequiresConfirmation"/>) исполняются только
/// при <see cref="LeftoverCleanOptions.ConfirmDangerous"/>.
/// </summary>
public sealed class LeftoverCleanService
{
    private readonly Abstractions.IEnvironment _environment;
    private readonly IProcessInspector _processInspector;
    private readonly IServiceInspector _serviceInspector;
    private readonly DirectoryDeleter _deleter;
    private readonly IElevatedRunner _elevatedRunner;

    public LeftoverCleanService(
        Abstractions.IEnvironment? environment = null,
        IProcessInspector? processInspector = null,
        IServiceInspector? serviceInspector = null,
        DirectoryDeleter? deleter = null,
        IElevatedRunner? elevatedRunner = null)
    {
        _environment = environment ?? new Environment.EnvironmentProvider();
        _processInspector = processInspector ?? new ProcessInspector();
        _serviceInspector = serviceInspector ?? new ServiceInspector();
        _deleter = deleter ?? new DirectoryDeleter();
        _elevatedRunner = elevatedRunner ?? new ElevatedProcessLauncher();
    }

    public async Task<LeftoverCleanReport> CleanAsync(
        IEnumerable<LeftoverPlanItem> selectedItems,
        LeftoverCleanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var cleanOptions = options ?? new LeftoverCleanOptions();
        var startedAt = DateTime.UtcNow;
        var items = selectedItems.DistinctBy(i => i.Key).ToList();

        if (items.Count == 0)
        {
            return new LeftoverCleanReport
            {
                Entries = Array.Empty<LeftoverDeletionEntry>(),
                DryRun = cleanOptions.DryRun,
                Elapsed = DateTime.UtcNow - startedAt
            };
        }

        // Повторная проверка «живых» объектов перед удалением (Фаза 3, FR-3.4, §5):
        // свежий снимок процессов/служб делается уже после построения плана.
        var context = FreshRuleContext();
        var entries = new List<LeftoverDeletionEntry>(items.Count);

        if (cleanOptions.DryRun)
        {
            foreach (var item in items)
            {
                entries.Add(CreateDryRunEntry(item, context));
            }

            AuditEntries(entries);
            return new LeftoverCleanReport
            {
                Entries = entries,
                DryRun = true,
                Elapsed = DateTime.UtcNow - startedAt
            };
        }

        var localItems = new List<LeftoverPlanItem>();
        var elevatedItems = new List<LeftoverPlanItem>();

        foreach (var item in items)
        {
            if (item.RequiresConfirmation && !cleanOptions.ConfirmDangerous)
            {
                entries.Add(new LeftoverDeletionEntry(
                    item,
                    LeftoverCleanOutcome.ConfirmationRequired,
                    0,
                    $"Объект требует обязательного пообъектного подтверждения перед удалением (FR-3.4, §5). Основание: {item.ReasonText}"));
                continue;
            }

            var liveReason = DescribeLiveGate(context, item.Path);
            if (liveReason is not null)
            {
                entries.Add(new LeftoverDeletionEntry(
                    item,
                    LeftoverCleanOutcome.LiveObjectSkipped,
                    0,
                    $"Повторная проверка перед удалением: объект используется ({liveReason}) — удаление отменено. Основание: {item.ReasonText}"));
                continue;
            }

            if (item.RequiresAdmin)
            {
                elevatedItems.Add(item);
            }
            else
            {
                localItems.Add(item);
            }
        }

        foreach (var item in localItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(await DeleteInUserContextAsync(item, cancellationToken));
        }

        if (elevatedItems.Count > 0)
        {
            entries.AddRange(await DeleteViaElevatedAsync(elevatedItems, cancellationToken));
        }

        AuditEntries(entries);

        return new LeftoverCleanReport
        {
            Entries = entries,
            DryRun = false,
            Elapsed = DateTime.UtcNow - startedAt
        };
    }

    private LeftoverRuleContext FreshRuleContext() => new()
    {
        Environment = _environment,
        Whitelist = new InstalledWhitelist(Array.Empty<InstalledApp>(), _environment),
        RunningProcesses = _processInspector.GetRunningProcesses(),
        RegisteredServices = _serviceInspector.GetServices(),
        Exclusions = Array.Empty<string>()
    };

    /// <summary>
    /// Повторная проверка «живости» каталога (Фаза 3): запущенный процесс из каталога,
    /// вхождение в <c>%PATH%</c>, исполняемый файл зарегистрированной службы (§5 PRD 03).
    /// Возвращает описание найденного «живого» использования или null.
    /// </summary>
    private static string? DescribeLiveGate(LeftoverRuleContext context, string path)
    {
        var reasons = new List<string>(3);
        if (context.HasRunningProcessUnder(path))
        {
            reasons.Add("запущен процесс с исполняемым файлом из этого каталога");
        }

        if (context.HasRegisteredServiceUnder(path))
        {
            reasons.Add("в каталоге лежит исполняемый файл зарегистрированной службы");
        }

        if (context.IsPartOfPathEnvironment(path))
        {
            reasons.Add("путь входит в переменную окружения %PATH%");
        }

        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }

    private static LeftoverDeletionEntry CreateDryRunEntry(LeftoverPlanItem item, LeftoverRuleContext context)
    {
        var liveReason = DescribeLiveGate(context, item.Path);
        if (liveReason is not null)
        {
            return new LeftoverDeletionEntry(
                item,
                LeftoverCleanOutcome.DryRun,
                0,
                $"Будет пропущено: объект используется ({liveReason}). Основание: {item.ReasonText}");
        }

        var scope = item.RequiresAdmin
            ? "через elevated-процесс (один UAC-подъём)"
            : "напрямую в контексте пользователя";
        var confirmation = item.RequiresConfirmation
            ? "Требуется пообъектное подтверждение. "
            : string.Empty;
        var removalMethod = item.RecommendedRemovalMethod is null
            ? string.Empty
            : $" Рекомендуется: {item.RecommendedRemovalMethod}";

        return new LeftoverDeletionEntry(
            item,
            LeftoverCleanOutcome.DryRun,
            item.SizeBytes ?? 0,
            $"{confirmation}Будет удалено {scope}. Основание: {item.ReasonText}.{removalMethod}");
    }

    private async Task<LeftoverDeletionEntry> DeleteInUserContextAsync(
        LeftoverPlanItem item,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(item.Path))
        {
            return new LeftoverDeletionEntry(
                item,
                LeftoverCleanOutcome.AlreadyAbsent,
                0,
                $"Объект уже отсутствует (идемпотентно). Основание: {item.ReasonText}");
        }

        DeletionOutcome outcome;
        try
        {
            outcome = await _deleter.DeletePathAsync(
                item.Path,
                CleanupTarget.Directory,
                deleteContentsOnly: false,
                cancellationToken: cancellationToken,
                sizeHint: item.SizeBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LeftoverDeletionEntry(
                item,
                LeftoverCleanOutcome.Error,
                0,
                $"Не удалось удалить каталог: {ex.Message}. Основание: {item.ReasonText}");
        }

        if (outcome.Denied)
        {
            return new LeftoverDeletionEntry(
                item,
                LeftoverCleanOutcome.Error,
                0,
                $"Удаление запрещено: {string.Join("; ", outcome.Errors)}. Основание: {item.ReasonText}");
        }

        var errors = outcome.Errors.Count == 0 ? string.Empty : " " + string.Join("; ", outcome.Errors);
        if (outcome.FullyDeleted)
        {
            return new LeftoverDeletionEntry(
                item,
                LeftoverCleanOutcome.DirectDeleted,
                outcome.FreedBytes,
                $"Каталог удалён в контексте пользователя.{errors} Основание: {item.ReasonText}");
        }

        var partial = outcome.FreedBytes > 0;
        return new LeftoverDeletionEntry(
            item,
            partial ? LeftoverCleanOutcome.Partial : LeftoverCleanOutcome.Error,
            outcome.FreedBytes,
            $"{(partial ? "Каталог удалён частично (часть файлов недоступна)." : "Каталог не удалён.")}{errors} Основание: {item.ReasonText}");
    }

    private async Task<IReadOnlyList<LeftoverDeletionEntry>> DeleteViaElevatedAsync(
        IReadOnlyList<LeftoverPlanItem> items,
        CancellationToken cancellationToken)
    {
        var scenario = LeftoverElevatedScenarioBuilder.Build(items);

        ElevatedJournal journal;
        try
        {
            journal = await _elevatedRunner.RunAsync(scenario, cancellationToken);
        }
        catch (ElevationDeclinedException)
        {
            return items
                .Select(item => new LeftoverDeletionEntry(
                    item,
                    LeftoverCleanOutcome.ElevationDeclined,
                    0,
                    $"Повышение прав отклонено пользователем (UAC). Основание: {item.ReasonText}"))
                .ToList();
        }

        var byId = journal.Results.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        return items
            .Select(item => MapElevatedResult(item, byId.TryGetValue(item.Key, out var result) ? result : null))
            .ToList();
    }

    private static LeftoverDeletionEntry MapElevatedResult(LeftoverPlanItem item, ElevatedStepResult? result)
    {
        if (result is null)
        {
            return new LeftoverDeletionEntry(
                item,
                LeftoverCleanOutcome.Error,
                0,
                $"Нет результата для шага. Основание: {item.ReasonText}");
        }

        if (!result.Success)
        {
            var partial = result.FreedBytes > 0;
            var detail = result.Error ?? result.Note ?? "Ошибка удаления.";
            return new LeftoverDeletionEntry(
                item,
                partial ? LeftoverCleanOutcome.Partial : LeftoverCleanOutcome.Error,
                result.FreedBytes,
                $"{(partial ? "Удалено частично." : "Не удалось удалить каталог.")} {detail} Основание: {item.ReasonText}");
        }

        var alreadyAbsent = result.Note is not null &&
                            (result.Note.Contains("отсутствует", StringComparison.OrdinalIgnoreCase) ||
                             result.Note.Contains("идемпотентно", StringComparison.OrdinalIgnoreCase));
        var summary = result.Note is null
            ? (alreadyAbsent ? "Объект уже отсутствует (идемпотентно)." : "Каталог удалён.")
            : result.Note;

        return new LeftoverDeletionEntry(
            item,
            alreadyAbsent ? LeftoverCleanOutcome.AlreadyAbsent : LeftoverCleanOutcome.ElevatedDeleted,
            result.FreedBytes,
            $"{summary} Основание: {item.ReasonText}");
    }

    private static void AuditEntries(IReadOnlyList<LeftoverDeletionEntry> entries)
    {
        foreach (var entry in entries)
        {
            Log.Information(
                "Leftover clean action: time={Time:yyyy-MM-dd HH:mm:ss} object={Object} group={Group} basis={Basis} mode={Mode} result={Outcome} freedBytes={Freed} note={Note}",
                DateTime.Now,
                entry.Item.Path,
                entry.Item.GroupName,
                entry.Basis,
                entry.Item.RequiresAdmin ? "elevated" : "user",
                entry.Outcome,
                entry.FreedBytes,
                entry.Note);
        }
    }
}
