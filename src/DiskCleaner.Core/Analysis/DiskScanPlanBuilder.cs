using DiskCleaner.Core.Models;
using DiskCleaner.Core.Scanning;

namespace DiskCleaner.Core.Analysis;

/// <summary>
/// Преобразует результат полного скана диска (<see cref="DiskScanService"/>) в «объекты очистки»
/// (FR-1.1, FR-1.5, FR-1.11): известные кэши, детализация крупных объектов (Android SDK,
/// .gradle, VS Code) и системные объекты (Корзина, hiberfil.sys). Чистая логика без IO —
/// данные уже измерены сканом.
/// </summary>
public sealed class DiskScanPlanBuilder
{
    public IReadOnlyList<CleanupItem> BuildItems(DiskScanResult scan)
    {
        var items = new List<CleanupItem>();

        foreach (var known in scan.Objects)
        {
            if (!known.Measurement.Exists || known.Measurement.TimedOut)
            {
                continue;
            }

            items.Add(CreateMeasuredItem(
                "disk:known:" + known.Path,
                known.Path,
                known.Label,
                known.Group,
                known.Category,
                known.Risk,
                known.Measurement));
        }

        foreach (var large in scan.LargeObjects)
        {
            AddLargeObjectComponents(large, items);
        }

        AddSystemObjects(scan.SystemObjects, items);

        return items;
    }

    private static void AddLargeObjectComponents(LargeObjectDetail large, ICollection<CleanupItem> items)
    {
        foreach (var component in large.Components)
        {
            if (!component.Measurement.Exists || component.Measurement.TimedOut)
            {
                continue;
            }

            items.Add(CreateMeasuredItem(
                "disk:large:" + component.Path,
                component.Path,
                component.Label,
                large.Label,
                component.Category,
                component.Risk,
                component.Measurement));
        }
    }

    private static void AddSystemObjects(
        IEnumerable<SystemObjectMeasurement> systemObjects,
        ICollection<CleanupItem> items)
    {
        foreach (var system in systemObjects)
        {
            if (!system.Present)
            {
                continue;
            }

            if (system.Key.StartsWith("recycle-bin:", StringComparison.Ordinal))
            {
                items.Add(new CleanupItem
                {
                    Key = "disk:" + system.Key,
                    Path = system.Path,
                    DisplayName = system.Label,
                    GroupName = "Системная очистка",
                    Category = CleanupCategory.RecycleBin,
                    Risk = CleanupRisk.Low,
                    Target = CleanupTarget.Directory,
                    EmptyRecycleBinDrive = Path.GetPathRoot(system.Path),
                    SizeBytes = system.SizeBytes,
                    FileCount = system.FileCount,
                    Description = "Очистка Корзины выбранного диска штатным API оболочки Windows (SHEmptyRecycleBin).",
                    Warning = "Содержимое Корзины будет удалено безвозвратно (восстановление станет невозможным)."
                });
                continue;
            }

            if (system.Key.StartsWith("hibernation:", StringComparison.Ordinal))
            {
                items.Add(CreateHibernationItem(system));
            }
        }
    }

    private static CleanupItem CreateHibernationItem(SystemObjectMeasurement system)
    {
        var powerCfg = Path.Combine(System.Environment.SystemDirectory, "powercfg.exe");
        return new CleanupItem
        {
            Key = "disk:" + system.Key,
            Path = null,
            DisplayName = system.SizeBytes is null
                ? "Отключить гибернацию (hiberfil.sys)"
                : $"Отключить гибернацию (hiberfil.sys, {Reports.CleanReportFormatter.FormatBytes(system.SizeBytes.Value)})",
            GroupName = "Системная очистка",
            Category = CleanupCategory.SystemFile,
            Risk = CleanupRisk.Low,
            Target = CleanupTarget.File,
            RequiresAdmin = true,
            CommandOnly = true,
            AllowDirectDelete = false,
            CleanCommand = "powercfg /h off",
            CleanCommandFile = powerCfg,
            CleanCommandArgs = "/h off",
            SizeBytes = system.SizeBytes,
            FileCount = system.FileCount,
            VerifyPathAbsent = system.Path,
            Description = "Файл гибернации hiberfil.sys (файл на системном диске) будет удалён.",
            Warning = "Отключает гибернацию; быстрый запуск сохраняется. Операция обратима (powercfg /h on). Требуются права администратора (UAC)."
        };
    }

    private static CleanupItem CreateMeasuredItem(
        string key,
        string path,
        string displayName,
        string? groupName,
        CleanupCategory category,
        CleanupRisk risk,
        DirectoryMeasurement measurement)
    {
        var (description, warning) = DescribeByRisk(category, risk);
        return new CleanupItem
        {
            Key = key,
            Path = path,
            DisplayName = displayName,
            GroupName = string.IsNullOrWhiteSpace(groupName) ? null : groupName,
            Category = category,
            Risk = risk,
            Target = CleanupTarget.Directory,
            AllowDirectDelete = true,
            SizeBytes = measurement.SizeBytes,
            FileCount = measurement.FileCount,
            Description = description,
            Warning = warning
        };
    }

    private static (string Description, string? Warning) DescribeByRisk(
        CleanupCategory category,
        CleanupRisk risk)
    {
        if (risk == CleanupRisk.Low)
        {
            return (
                "Объект будет пересоздан автоматически при следующем использовании инструмента.",
                null);
        }

        if (category == CleanupCategory.InstalledApp)
        {
            return (
                "Установленное приложение; удаление выполняется штатным деинсталлятором после подтверждения.",
                "Требуется явное подтверждение; операция может быть необратимой.");
        }

        return (
            "Инструментарий/SDK или остатки, которые при необходимости переустанавливаются повторно.",
            "Объект требует согласия перед очисткой (перекачивается/переустанавливается).");
    }
}
