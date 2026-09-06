using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Processes;

public sealed class InUseDetector
{
    public int MarkInUse(
        IEnumerable<CleanupItem> items,
        IReadOnlyList<RunningProcessInfo> processes)
    {
        var marked = 0;
        foreach (var item in items)
        {
            if (item.IsGroup || item.InUse)
            {
                continue;
            }

            if (IsInUse(item, processes))
            {
                item.InUse = true;
                marked++;
            }
        }

        return marked;
    }

    private static bool IsInUse(CleanupItem item, IReadOnlyList<RunningProcessInfo> processes)
    {
        foreach (var process in processes)
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
                continue;
            }

            if (string.Equals(executablePath, target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (item.Target == CleanupTarget.Directory &&
                executablePath.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
