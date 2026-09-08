using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Processes;

/// <summary>
/// Детектор занятости (FR-5.7): помечает объект <c>IN_USE</c>, если по переданному
/// снимку запущенных процессов найден процесс, чей ExecutablePath находится внутри
/// удаляемого пути, либо имя процесса совпадает с <see cref="CleanupItem.OwnerProcessNames"/>
/// (кейсы: <c>adb.exe</c> в <c>Android\Sdk</c>, <c>Code.exe</c> — VS Code, node).
/// Каждому помеченному объекту возвращается список блокирующих процессов
/// (<see cref="CleanupItem.BlockingProcesses"/>) и рекомендация
/// (<see cref="CleanupItem.InUseAdvice"/>, FR-5.8).
/// </summary>
public sealed class InUseDetector
{
    public int MarkInUse(
        IEnumerable<CleanupItem> items,
        IReadOnlyList<RunningProcessInfo> processes)
    {
        var marked = 0;
        foreach (var item in items)
        {
            if (item.IsGroup)
            {
                continue;
            }

            var blocking = FindBlocking(item, processes);
            if (blocking.Count == 0)
            {
                // Уже помеченный объект остаётся IN_USE (сохранена прежняя семантика);
                // список блокирующих процессов не перезаписывается пустым.
                continue;
            }

            if (!item.InUse)
            {
                marked++;
            }

            item.InUse = true;
            item.BlockingProcesses = blocking;
            item.InUseAdvice = InUseMessages.AdviceFor(item, blocking);
        }

        return marked;
    }

    public static IReadOnlyList<RunningProcessInfo> FindBlocking(
        CleanupItem item,
        IReadOnlyList<RunningProcessInfo> processes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocking = new List<RunningProcessInfo>();
        foreach (var process in processes)
        {
            if (IsBlocking(item, process) &&
                seen.Add(process.Name + "\0" + (process.ExecutablePath ?? string.Empty)))
            {
                blocking.Add(process);
            }
        }

        return blocking;
    }

    private static bool IsBlocking(CleanupItem item, RunningProcessInfo process)
    {
        if (item.OwnerProcessNames.Any(name =>
                string.Equals(name, process.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var executablePath = process.ExecutablePath;
        var target = item.Path;
        if (string.IsNullOrEmpty(executablePath) || string.IsNullOrEmpty(target))
        {
            return false;
        }

        if (string.Equals(executablePath, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return item.Target == CleanupTarget.Directory &&
               executablePath.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
