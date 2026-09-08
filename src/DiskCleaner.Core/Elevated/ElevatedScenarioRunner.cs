using System.ComponentModel;
using System.ServiceProcess;
using Microsoft.Win32;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Scanning;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Core.Elevated;

public sealed class ElevatedScenarioRunner : IElevatedRunner
{
    private readonly DirectoryDeleter _deleter;
    private readonly ICommandRunner _commandRunner;
    private readonly BundleFallbackResolver? _bundleResolver;
    private readonly string? _packageCacheRoot;

    public ElevatedScenarioRunner(
        DirectoryDeleter? deleter = null,
        ICommandRunner? commandRunner = null,
        BundleFallbackResolver? bundleResolver = null,
        string? packageCacheRoot = null)
    {
        _deleter = deleter ?? new DirectoryDeleter();
        _commandRunner = commandRunner ?? new ProcessCommandRunner();
        _bundleResolver = bundleResolver ?? new BundleFallbackResolver();
        _packageCacheRoot = packageCacheRoot;
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
                ElevatedStepKind.ServiceCleanDirectory => await CleanServiceDirectoryAsync(step, cancellationToken),
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

        var probe = NativeDirectory.Probe(step.Path);
        if (!probe.Exists)
        {
            return new ElevatedStepResult { Id = step.Id, Success = true, Note = "Объект уже отсутствует (идемпотентно)" };
        }

        var target = step.Target ?? CleanupTarget.Directory;
        var outcome = await _deleter.DeletePathAsync(
            step.Path,
            target,
            step.DeleteContentsOnly,
            cancellationToken);

        if (outcome.Denied)
        {
            return new ElevatedStepResult
            {
                Id = step.Id,
                Success = false,
                FreedBytes = outcome.FreedBytes,
                Error = string.Join("; ", outcome.Errors)
            };
        }

        return new ElevatedStepResult
        {
            Id = step.Id,
            Success = outcome.FullyDeleted,
            FreedBytes = outcome.FreedBytes,
            Error = outcome.Errors.Count == 0 ? null : string.Join("; ", outcome.Errors)
        };
    }

    private async Task<ElevatedStepResult> CleanServiceDirectoryAsync(
        ElevatedStep step,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.Path) || string.IsNullOrWhiteSpace(step.ServiceName))
        {
            return new ElevatedStepResult
            {
                Id = step.Id,
                Success = false,
                Error = "Не заданы путь каталога и имя службы"
            };
        }

        var probe = NativeDirectory.Probe(step.Path);
        if (!probe.Exists)
        {
            return new ElevatedStepResult { Id = step.Id, Success = true, Note = "Каталог уже отсутствует (идемпотентно)" };
        }

        var errors = new List<string>();
        var restartErrors = new List<string>();
        var serviceNotes = new List<string>();
        var originallyRunning = false;

        using (var controller = OpenService(step.ServiceName))
        {
            if (controller is not null)
            {
                try
                {
                    controller.Refresh();
                    originallyRunning = controller.Status == ServiceControllerStatus.Running;
                    if (originallyRunning && !TryStopService(controller))
                    {
                        serviceNotes.Add($"Не удалось остановить службу '{step.ServiceName}' — очистка выполняется без остановки службы.");
                        originallyRunning = false;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    serviceNotes.Add($"Служба '{step.ServiceName}' недоступна ({ex.Message}) — очистка выполняется без остановки службы.");
                    originallyRunning = false;
                }
            }

            try
            {
                var outcome = await _deleter.DeletePathAsync(
                    step.Path,
                    CleanupTarget.Directory,
                    deleteContentsOnly: true,
                    cancellationToken);

                foreach (var deletionError in outcome.Errors)
                {
                    errors.Add(deletionError);
                }

                if (outcome.Denied)
                {
                    return new ElevatedStepResult
                    {
                        Id = step.Id,
                        Success = false,
                        FreedBytes = outcome.FreedBytes,
                        Error = string.Join("; ", outcome.Errors)
                    };
                }

                if (originallyRunning && controller is not null)
                {
                    try
                    {
                        if (!TryStartService(controller))
                        {
                            restartErrors.Add(
                                $"Служба '{step.ServiceName}' не перезапущена автоматически в течение 30 секунд — запустите её вручную.");
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                    {
                        restartErrors.Add(
                            $"Служба '{step.ServiceName}' не перезапущена автоматически: {ex.Message} — запустите её вручную.");
                    }
                }

                var notes = new List<string>(serviceNotes);
                if (outcome.FullyDeleted)
                {
                    notes.Add("Содержимое очищено.");
                }
                else if (outcome.FreedBytes > 0)
                {
                    notes.Add("Очищено частично (часть файлов заблокирована).");
                }

                var allErrors = errors.Concat(restartErrors).ToList();
                return new ElevatedStepResult
                {
                    Id = step.Id,
                    Success = allErrors.Count == 0 && outcome.FullyDeleted,
                    FreedBytes = outcome.FreedBytes,
                    Note = string.Join(" ", notes),
                    Error = allErrors.Count == 0 ? null : string.Join("; ", allErrors)
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }

    private static ServiceController? OpenService(string serviceName)
    {
        try
        {
            return new ServiceController(serviceName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static bool TryStopService(ServiceController controller)
    {
        controller.Stop();
        return WaitForStatus(controller, ServiceControllerStatus.Stopped);
    }

    private static bool TryStartService(ServiceController controller)
    {
        controller.Start();
        return WaitForStatus(controller, ServiceControllerStatus.Running);
    }

    private static bool WaitForStatus(ServiceController controller, ServiceControllerStatus target)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                controller.Refresh();
                if (controller.Status == target)
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            Thread.Sleep(300);
        }

        return false;
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

        // FR-4.6: msiexec /x для пакетной (bundle) записи возвращает 1605 («это не MSI») —
        // запускаем штатный деинсталлятор из %ProgramData%\Package Cache\{code}.
        var fallback = await TryBundleFallback(step, meaning, cancellationToken);
        if (fallback is not null)
        {
            return fallback;
        }

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

    private async Task<ElevatedStepResult?> TryBundleFallback(
        ElevatedStep step,
        ProcessExitMeaning meaning,
        CancellationToken cancellationToken)
    {
        if (meaning != ProcessExitMeaning.NotInstalled ||
            step.ExitCodes != ExitCodePolicy.Msiexec ||
            _bundleResolver is null ||
            string.IsNullOrWhiteSpace(step.BundleProductCode))
        {
            return null;
        }

        var fallbackCommand = _bundleResolver.TryResolveBundleUninstallCommand(
            step.BundleProductCode,
            step.PackageCacheRoot ?? _packageCacheRoot);
        if (fallbackCommand is null)
        {
            return null;
        }

        var engineResult = await _commandRunner.RunAsync(new CommandDefinition
        {
            FileName = fallbackCommand.FileName,
            Arguments = fallbackCommand.Arguments,
            TimeoutSec = step.TimeoutSec
        }, cancellationToken);

        if (engineResult.TimedOut)
        {
            return new ElevatedStepResult
            {
                Id = step.Id,
                Success = false,
                ExitCode = null,
                Error = $"Открыт мастер удаления пакета ({Path.GetFileName(fallbackCommand.FileName)}) — " +
                        $"завершите его в течение отведённого времени; ожидание истекло."
            };
        }

        var engineName = Path.GetFileName(fallbackCommand.FileName);
        if (engineResult.ExitCode == 0)
        {
            var note = IsGuiMaster(engineName)
                ? $"Запущен штатный деинсталлятор пакета (bundle): {engineName}. Мастер завершён пользователем (код 0)."
                : $"Запись — пакетная установка (bundle); запущен штатный деинсталлятор {engineName} (код 0).";
            return new ElevatedStepResult
            {
                Id = step.Id,
                Success = true,
                ExitCode = engineResult.ExitCode,
                Note = note
            };
        }

        return new ElevatedStepResult
        {
            Id = step.Id,
            Success = false,
            ExitCode = engineResult.ExitCode,
            Error = $"Штатный деинсталлятор пакета {engineName} завершился с кодом {engineResult.ExitCode}."
        };
    }

    private static bool IsGuiMaster(string engineName) =>
        engineName.Equals("winsdksetup.exe", StringComparison.OrdinalIgnoreCase) ||
        engineName.Equals("winsdk.exe", StringComparison.OrdinalIgnoreCase);

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
