namespace DiskCleaner.Core.Commanding;

public sealed class CommandLocator : ICommandLocator
{
    private static readonly string[] DefaultExtensions = [".COM", ".EXE", ".BAT", ".CMD"];

    public bool IsAvailable(string commandName)
    {
        if (Path.IsPathRooted(commandName))
        {
            return File.Exists(commandName);
        }

        var pathValue = System.Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathValue))
        {
            return false;
        }

        var extensions = GetExecutableExtensions();
        foreach (var directory in pathValue.Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
            {
                continue;
            }

            var full = Path.Combine(directory.Trim(), commandName);
            if (File.Exists(full))
            {
                return true;
            }

            foreach (var extension in extensions)
            {
                if (File.Exists(full + extension))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string[] GetExecutableExtensions()
    {
        var pathext = System.Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathext))
        {
            return DefaultExtensions;
        }

        return pathext.Split(Path.PathSeparator)
            .Where(e => e.Length > 0)
            .Concat(DefaultExtensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
