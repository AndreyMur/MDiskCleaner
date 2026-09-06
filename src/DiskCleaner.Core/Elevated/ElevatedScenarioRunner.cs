using Microsoft.Win32;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Core.Elevated;

public sealed class ElevatedScenarioRunner : IElevatedRunner
{
    private readonly DirectoryDeleter _deleter;
    private readonly ICommandRunner _commandRunner;

    public ElevatedScenarioRunner(
        DirectoryDeleter? deleter = null,
        ICommandRunner? commandRunner = null)
    {
        _deleter = deleter ?? new DirectoryDeleter();
        _commandRunner = commandRunner ?? new ProcessCommandRunner();
    }

    public async Task<ElevatedJournal> RunAsync(
        ElevatedScenario scenario,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var results = new List<ElevatedStepResult>(scenario.Steps.Count);

        foreach (var step in scenario.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ExecuteStepAsync(step, cancellationToken));
        }

        return new ElevatedJournal
        {
            StartedAt = startedAt,
            FinishedAt = DateTime.UtcNow,
            Results = results
        };
    }

    private async Task<ElevatedStepResult> ExecuteStepAsync(
        ElevatedStep step,
        CancellationToken cancellationToken)
    {
        try
        {
            return step.Kind switch
            {
                ElevatedStepKind.DeletePath => await DeletePathAsync(step, cancellationToken),
                ElevatedStepKind.RunProcess => await RunProcessAsync(step, cancellationToken),
                ElevatedStepKind.DeleteRegistryKey => DeleteRegistryKey(step),
                _ => new ElevatedStepResult
                {
                    Id = step.Id,
                    Success = false,
                    Error = $"Неизвестный тип шага: {step.Kind}"
                }
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ElevatedStepResult
            {
                Id = step.Id,
                Success = false,
                Error = ex.Message
            };
        }
    }

    private async Task<ElevatedStepResult> DeletePathAsync(
        ElevatedStep step,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.Path))
        {
            return new ElevatedStepResult { Id = step.Id, Success = false, Error = "Не задан путь удаления" };
        }

        var target = step.Target ?? CleanupTarget.Directory;
        var outcome = await _deleter.DeletePathAsync(
            step.Path,
            target,
            cancellationToken);

        return new ElevatedStepResult
        {
            Id = step.Id,
            Success = outcome.FullyDeleted,
            FreedBytes = outcome.FreedBytes,
            Error = outcome.Errors.Count == 0 ? null : string.Join("; ", outcome.Errors)
        };
    }

    private async Task<ElevatedStepResult> RunProcessAsync(
        ElevatedStep step,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.FileName))
        {
            return new ElevatedStepResult { Id = step.Id, Success = false, Error = "Не задан исполняемый файл" };
        }

        var result = await _commandRunner.RunAsync(new CommandDefinition
        {
            FileName = step.FileName!,
            Arguments = step.Arguments ?? string.Empty,
            TimeoutSec = step.TimeoutSec
        }, cancellationToken);

        var meaning = ClassifyExit(step, result);
        return new ElevatedStepResult
        {
            Id = step.Id,
            Success = meaning is ProcessExitMeaning.Success or ProcessExitMeaning.RebootRequired or ProcessExitMeaning.NotInstalled,
            RebootRequired = meaning == ProcessExitMeaning.RebootRequired,
            ExitCode = result.TimedOut ? null : result.ExitCode,
            Note = BuildNote(step, result, meaning),
            Error = meaning == ProcessExitMeaning.Failed
                ? (result.TimedOut ? "Команда превысила допустимое время ожидания" : $"Команда завершилась с кодом {result.ExitCode}")
                : null
        };
    }

    private static ProcessExitMeaning ClassifyExit(ElevatedStep step, CommandResult result)
    {
        if (result.TimedOut)
        {
            return ProcessExitMeaning.TimedOut;
        }

        return step.ExitCodes == ExitCodePolicy.Msiexec
            ? UninstallExitCodes.Classify(result.ExitCode)
            : result.ExitCode == 0 ? ProcessExitMeaning.Success : ProcessExitMeaning.Failed;
    }

    private static string? BuildNote(ElevatedStep step, CommandResult result, ProcessExitMeaning meaning)
    {
        var output = string.IsNullOrWhiteSpace(result.Output) ? null : $" Вывод: {Truncate(result.Output, 200)}";
        return meaning switch
        {
            ProcessExitMeaning.NotInstalled => $"Продукт уже не установлен (код {result.ExitCode}).{output}",
            ProcessExitMeaning.RebootRequired => "Требуется перезагрузка (код 3010).",
            _ => output
        };
    }

    private static ElevatedStepResult DeleteRegistryKey(ElevatedStep step)
    {
        if (step.RegistryHive is null || string.IsNullOrWhiteSpace(step.RegistrySubKeyPath))
        {
            return new ElevatedStepResult { Id = step.Id, Success = false, Error = "Не задан путь ветки реестра" };
        }

        var hive = step.RegistryHive == RegistryHiveKind.LocalMachine
            ? RegistryHive.LocalMachine
            : RegistryHive.CurrentUser;

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        if (baseKey.OpenSubKey(step.RegistrySubKeyPath) is null)
        {
            return new ElevatedStepResult { Id = step.Id, Success = true, Note = "Запись уже отсутствует (идемпотентно)" };
        }

        baseKey.DeleteSubKeyTree(step.RegistrySubKeyPath, throwOnMissingSubKey: false);
        return new ElevatedStepResult { Id = step.Id, Success = true, Note = "Запись реестра удалена" };
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
