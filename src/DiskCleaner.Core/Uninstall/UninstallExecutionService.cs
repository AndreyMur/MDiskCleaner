using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Elevated;
using Serilog;

namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Исполнитель пачки удалений приложений (фаза 21, модуль 04, FR-4.5/4.15/4.16, NFR):
/// <list type="bullet">
/// <item>команда строится из записи реестра (<see cref="UninstallStringParser.ParseFor"/>);</item>
/// <item>записи LocalMachine удаляются одним elevated-процессом через <see cref="IElevatedRunner"/>
/// — одна UAC-проверка на пачку (FR-4.15); HKCU и пользовательские приложения — напрямую
/// в контексте пользователя без повышения (FR-4.16);</item>
/// <item>контроль exit-кодов: 0 и 3010 — успех; 1605/1612 — «не установлено», не ошибка
/// (NFR);</item>
/// <item>повторный запуск на уже удалённых объектах не ломается: для non-MSI деинсталлятор,
/// файл которого отсутствует на диске, помечается как уже удалённый (идемпотентность);</item>
/// <item>журнал каждой записи содержит исход, exit-код, пояснение и время (NFR PRD 04).</item>
/// </list>
/// </summary>
public sealed class UninstallExecutionService
{
    private readonly UninstallStringParser _parser;
    private readonly IElevatedRunner _elevatedRunner;
    private readonly ICommandRunner _commandRunner;

    public UninstallExecutionService(
        UninstallStringParser? parser = null,
        IElevatedRunner? elevatedRunner = null,
        ICommandRunner? commandRunner = null)
    {
        _parser = parser ?? new UninstallStringParser();
        _elevatedRunner = elevatedRunner ?? new ElevatedProcessLauncher();
        _commandRunner = commandRunner ?? new ProcessCommandRunner();
    }

    public UninstallExecutionItem BuildItem(InstalledApp app) =>
        new(app, _parser.ParseFor(app));

    public async Task<UninstallExecutionReport> UninstallAsync(
        IEnumerable<InstalledApp> apps,
        UninstallExecutionOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await UninstallAsync(apps.Select(BuildItem).ToList(), options, cancellationToken);

    public async Task<UninstallExecutionReport> UninstallAsync(
        IReadOnlyList<UninstallExecutionItem> items,
        UninstallExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var executionOptions = options ?? new UninstallExecutionOptions();
        var startedAt = DateTime.UtcNow;
        var distinct = items.DistinctBy(item => item.Key).ToList();
        var entries = new List<UninstallExecutionEntry>(distinct.Count);
        var userItems = new List<UninstallExecutionItem>();
        var elevatedItems = new List<UninstallExecutionItem>();

        foreach (var item in distinct)
        {
            if (item.Command is null)
            {
                entries.Add(CreateEntry(
                    item,
                    UninstallExecutionOutcome.NoUninstaller,
                    null,
                    "По записи реестра не удалось построить команду удаления: отсутствует UninstallString/QuietUninstallString или тип не распознан."));
                continue;
            }

            if (!item.RequiresAdmin && UninstallerFileMissing(item))
            {
                entries.Add(CreateEntry(
                    item,
                    UninstallExecutionOutcome.AlreadyUninstalled,
                    null,
                    "Файл деинсталлятора отсутствует на диске — продукт уже удалён (идемпотентно)."));
                continue;
            }

            if (item.RequiresAdmin)
            {
                elevatedItems.Add(item);
            }
            else
            {
                userItems.Add(item);
            }
        }

        foreach (var item in userItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(await RunInUserContextAsync(item, executionOptions, cancellationToken));
        }

        if (elevatedItems.Count > 0)
        {
            entries.AddRange(await RunElevatedBatchAsync(elevatedItems, executionOptions, cancellationToken));
        }

        AuditEntries(entries);

        return new UninstallExecutionReport
        {
            Entries = entries,
            StartedAt = startedAt,
            FinishedAt = DateTime.UtcNow
        };
    }

    private async Task<UninstallExecutionEntry> RunInUserContextAsync(
        UninstallExecutionItem item,
        UninstallExecutionOptions options,
        CancellationToken cancellationToken)
    {
        var command = item.Command!;

        CommandResult result;
        try
        {
            result = await _commandRunner.RunAsync(new CommandDefinition
            {
                FileName = command.FileName,
                Arguments = command.Arguments,
                TimeoutSec = options.CommandTimeoutSec
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreateEntry(
                item,
                UninstallExecutionOutcome.Failed,
                null,
                $"Не удалось запустить деинсталлятор '{command.FileName}': {ex.Message}");
        }

        return MapProcessResult(item, result, scope: "пользователя");
    }

    private async Task<IReadOnlyList<UninstallExecutionEntry>> RunElevatedBatchAsync(
        IReadOnlyList<UninstallExecutionItem> items,
        UninstallExecutionOptions options,
        CancellationToken cancellationToken)
    {
        var scenario = UninstallElevatedScenarioBuilder.Build(items, options.CommandTimeoutSec);

        ElevatedJournal journal;
        try
        {
            journal = await _elevatedRunner.RunAsync(scenario, cancellationToken);
        }
        catch (ElevationDeclinedException)
        {
            return items
                .Select(item => CreateEntry(
                    item,
                    UninstallExecutionOutcome.ElevationDeclined,
                    null,
                    "Повышение прав отклонено пользователем (UAC) — пачка шагов не выполнялась."))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return items
                .Select(item => CreateEntry(
                    item,
                    UninstallExecutionOutcome.Failed,
                    null,
                    $"Не удалось выполнить elevated-сценарий: {ex.Message}"))
                .ToList();
        }

        var byId = journal.Results.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        return items
            .Select(item => MapElevatedResult(item, byId.TryGetValue(item.Key, out var result) ? result : null))
            .ToList();
    }

    private static UninstallExecutionEntry MapElevatedResult(
        UninstallExecutionItem item,
        ElevatedStepResult? result)
    {
        if (result is null)
        {
            return CreateEntry(item, UninstallExecutionOutcome.Failed, null, "Нет результата для шага elevated-сценария.");
        }

        if (!result.Success)
        {
            if (result.ExitCode is null)
            {
                return CreateEntry(
                    item,
                    UninstallExecutionOutcome.TimedOut,
                    null,
                    result.Error ?? "Деинсталлятор превысил допустимое время ожидания и был остановлен.");
            }

            return CreateEntry(
                item,
                UninstallExecutionOutcome.Failed,
                result.ExitCode,
                result.Error ?? $"Деинсталлятор завершился с кодом {result.ExitCode}.");
        }

        if (item.IsMsiexec)
        {
            return MapSuccessfulMsiexec(item, result);
        }

        return CreateEntry(
            item,
            UninstallExecutionOutcome.Uninstalled,
            result.ExitCode,
            result.Note ?? $"Деинсталлятор завершился успешно (код {result.ExitCode}).");
    }

    private static UninstallExecutionEntry MapProcessResult(
        UninstallExecutionItem item,
        CommandResult result,
        string scope)
    {
        if (result.TimedOut)
        {
            return CreateEntry(
                item,
                UninstallExecutionOutcome.TimedOut,
                null,
                "Деинсталлятор превысил допустимое время ожидания и был остановлен.");
        }

        if (item.IsMsiexec)
        {
            var meaning = UninstallExitCodes.Classify(result.ExitCode);
            switch (meaning)
            {
                case ProcessExitMeaning.Success:
                    return CreateEntry(
                        item,
                        UninstallExecutionOutcome.Uninstalled,
                        result.ExitCode,
                        $"Деинсталляция завершена в контексте {scope} успешно (код {result.ExitCode}).");
                case ProcessExitMeaning.RebootRequired:
                    return CreateEntry(
                        item,
                        UninstallExecutionOutcome.RebootRequired,
                        result.ExitCode,
                        "Деинсталляция завершена; требуется перезагрузка (код 3010).");
                case ProcessExitMeaning.NotInstalled:
                    return CreateEntry(
                        item,
                        UninstallExecutionOutcome.NotInstalled,
                        result.ExitCode,
                        $"Продукт уже не установлен (код {result.ExitCode}) — не ошибка (идемпотентно).");
                default:
                    return CreateEntry(
                        item,
                        UninstallExecutionOutcome.Failed,
                        result.ExitCode,
                        $"Деинсталлятор завершился с кодом {result.ExitCode}: {Truncate(result.Output, 200)}");
            }
        }

        return result.ExitCode == 0
            ? CreateEntry(
                item,
                UninstallExecutionOutcome.Uninstalled,
                result.ExitCode,
                $"Деинсталлятор завершился в контексте {scope} успешно (код 0).")
            : CreateEntry(
                item,
                UninstallExecutionOutcome.Failed,
                result.ExitCode,
                $"Деинсталлятор завершился с кодом {result.ExitCode}: {Truncate(result.Output, 200)}");
    }

    private static UninstallExecutionEntry MapSuccessfulMsiexec(
        UninstallExecutionItem item,
        ElevatedStepResult result)
    {
        if (result.RebootRequired || result.ExitCode == UninstallExitCodes.RebootRequired)
        {
            return CreateEntry(
                item,
                UninstallExecutionOutcome.RebootRequired,
                result.ExitCode,
                "Деинсталляция завершена; требуется перезагрузка (код 3010).");
        }

        if (result.ExitCode is UninstallExitCodes.ProductNotInstalled or UninstallExitCodes.InstallSourceAbsent)
        {
            return CreateEntry(
                item,
                UninstallExecutionOutcome.NotInstalled,
                result.ExitCode,
                $"Продукт уже не установлен (код {result.ExitCode}) — не ошибка (идемпотентно).");
        }

        return CreateEntry(
            item,
            UninstallExecutionOutcome.Uninstalled,
            result.ExitCode,
            result.Note ?? $"Деинсталляция завершена через elevated-процесс успешно (код {result.ExitCode}).");
    }

    private static bool UninstallerFileMissing(UninstallExecutionItem item)
    {
        var command = item.Command!;
        if (command.Kind == UninstallerKind.Msi)
        {
            return false;
        }

        // Неабсолютные имена (например, msiexec.exe) резолвятся через %PATH% — не гейтим.
        if (!Path.IsPathRooted(command.FileName))
        {
            return false;
        }

        return !File.Exists(command.FileName);
    }

    private static UninstallExecutionEntry CreateEntry(
        UninstallExecutionItem item,
        UninstallExecutionOutcome outcome,
        int? exitCode,
        string? note) =>
        new(item, outcome, exitCode, note, DateTime.UtcNow);

    private static void AuditEntries(IReadOnlyList<UninstallExecutionEntry> entries)
    {
        foreach (var entry in entries)
        {
            Log.Information(
                "Uninstall action: time={Time:yyyy-MM-dd HH:mm:ss} app={App} scope={Scope} mode={Mode} result={Outcome} exitCode={ExitCode} note={Note}",
                DateTime.Now,
                entry.Item.App.DisplayName,
                entry.Item.App.ScopeKey,
                entry.Item.RequiresAdmin ? "elevated" : "user",
                entry.Outcome,
                entry.ExitCode,
                entry.Note);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
