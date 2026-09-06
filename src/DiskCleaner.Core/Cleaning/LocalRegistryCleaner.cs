using Microsoft.Win32;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Core.Cleaning;

public sealed class LocalRegistryCleaner
{
    public CleanEntry Delete(CleanupItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (item.RegistryDeletePath is null)
        {
            return new CleanEntry(item, CleanOutcome.Error, 0, "Не задан путь удаления в реестре.");
        }

        var hive = RegistryDeletePathBuilder.HiveOf(item.RegistryDeletePath);
        var relativePath = RegistryDeletePathBuilder.RelativePathOf(item.RegistryDeletePath);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return new CleanEntry(item, CleanOutcome.Error, 0, "Некорректный путь в реестре.");
        }

        var baseKey = hive == RegistryHiveKind.CurrentUser
            ? Registry.CurrentUser
            : Registry.LocalMachine;

        using (baseKey)
        {
            if (baseKey.OpenSubKey(relativePath) is null)
            {
                return new CleanEntry(item, CleanOutcome.RegistryEntryDeleted, 0, "Запись уже отсутствует (идемпотентно).");
            }

            try
            {
                baseKey.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
                return new CleanEntry(item, CleanOutcome.RegistryEntryDeleted, 0, "Запись реестра удалена.");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                return new CleanEntry(item, CleanOutcome.Error, 0, $"Недостаточно прав для удаления записи: {ex.Message}");
            }
            catch (Exception ex)
            {
                return new CleanEntry(item, CleanOutcome.Error, 0, ex.Message);
            }
        }
    }
}
