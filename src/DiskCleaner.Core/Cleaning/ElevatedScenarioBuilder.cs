using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Core.Cleaning;

public sealed class ElevatedScenarioBuilder
{
    public ElevatedScenario Build(IReadOnlyList<CleanupItem> items)
    {
        var steps = items
            .Select(item => new ElevatedStep
            {
                Id = item.Key,
                Kind = StepKindOf(item),
                Path = item.Path,
                Target = item.Target,
                DeleteContentsOnly = item.DeleteContentsOnly,
                FileName = item.CleanCommandFile,
                Arguments = item.CleanCommandArgs,
                TimeoutSec = item.CleanCommandTimeoutSec is > 0 ? item.CleanCommandTimeoutSec.Value : 300,
                ExitCodes = IsMsiexecCommand(item.CleanCommandFile) ? ExitCodePolicy.Msiexec : ExitCodePolicy.Generic,
                RegistryHive = RegistryHiveOf(item),
                RegistrySubKeyPath = item.RegistryDeletePath is null ? null : RegistryDeletePathBuilder.RelativePathOf(item.RegistryDeletePath),
                ServiceName = item.ServiceName
            })
            .ToList();

        return new ElevatedScenario { Steps = steps };
    }

    public IReadOnlyList<CleanEntry> MapResults(
        IReadOnlyList<CleanupItem> items,
        ElevatedJournal journal)
    {
        var byId = journal.Results.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var entries = new List<CleanEntry>(items.Count);

        foreach (var item in items)
        {
            byId.TryGetValue(item.Key, out var result);
            entries.Add(MapEntry(item, result));
        }

        return entries;
    }

    private static CleanEntry MapEntry(CleanupItem item, ElevatedStepResult? result)
    {
        if (result is null)
        {
            return new CleanEntry(item, CleanOutcome.Error, 0, "Нет результата для шага.");
        }

        if (item.RegistryDeletePath is not null)
        {
            return new CleanEntry(
                item,
                result.Success ? CleanOutcome.RegistryEntryDeleted : CleanOutcome.Error,
                0,
                result.Success ? "Запись реестра удалена." : result.Error ?? "Не удалось удалить запись реестра.");
        }

        if (item.UninstallMode || item.CleanCommandFile is not null)
        {
            return MapProcessEntry(item, result);
        }

        if (!result.Success)
        {
            return new CleanEntry(
                item,
                result.FreedBytes > 0 ? CleanOutcome.Partial : CleanOutcome.Error,
                result.FreedBytes,
                result.Error ?? "Не удалось удалить объект.");
        }

        return new CleanEntry(
            item,
            CleanOutcome.DirectDeleted,
            result.FreedBytes,
            result.Note);
    }

    private static CleanEntry MapProcessEntry(CleanupItem item, ElevatedStepResult result)
    {
        if (!result.Success)
        {
            return new CleanEntry(
                item,
                CleanOutcome.Error,
                0,
                result.Error ?? result.Note ?? $"Деинсталлятор завершился с ошибкой (код {result.ExitCode}).");
        }

        var note = result.Note;
        var exitCode = result.ExitCode;

        if (!item.UninstallMode)
        {
            return new CleanEntry(
                item,
                CleanOutcome.CommandOnlyCleaned,
                0,
                note ?? $"Команда выполнена успешно (код {exitCode}).");
        }

        if (exitCode is UninstallExitCodes.ProductNotInstalled or UninstallExitCodes.InstallSourceAbsent)
        {
            return new CleanEntry(
                item,
                CleanOutcome.AlreadyUninstalled,
                0,
                $"Продукт уже не установлен (код {exitCode}). Запись останется до проверки реестра.");
        }

        if (result.RebootRequired || exitCode == UninstallExitCodes.RebootRequired)
        {
            return new CleanEntry(item, CleanOutcome.RebootRequired, 0, "Деинсталляция завершена; требуется перезагрузка.");
        }

        return new CleanEntry(
            item,
            CleanOutcome.Uninstalled,
            item.SizeBytes ?? 0,
            note ?? $"Деинсталлятор завершился успешно (код {exitCode}).");
    }

    private static ElevatedStepKind StepKindOf(CleanupItem item)
    {
        if (item.RegistryDeletePath is not null)
        {
            return ElevatedStepKind.DeleteRegistryKey;
        }

        if (!string.IsNullOrEmpty(item.ServiceName))
        {
            return ElevatedStepKind.ServiceCleanDirectory;
        }

        if (item.UninstallMode || item.CleanCommandFile is not null)
        {
            return ElevatedStepKind.RunProcess;
        }

        return ElevatedStepKind.DeletePath;
    }

    private static RegistryHiveKind? RegistryHiveOf(CleanupItem item) =>
        item.RegistryDeletePath is null
            ? null
            : RegistryDeletePathBuilder.HiveOf(item.RegistryDeletePath);

    private static bool IsMsiexecCommand(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        Path.GetFileName(fileName).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);
}
