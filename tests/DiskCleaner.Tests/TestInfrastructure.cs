using DiskCleaner.Core.Abstractions;
using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Processes;

namespace DiskCleaner.Tests;

public sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "DiskCleaner.Tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string LocalAppData => Combine("LocalAppData");

    public string AppData => Combine("AppData");

    public string UserProfile => Combine("UserProfile");

    public string ProgramFiles => Combine("ProgramFiles");

    public string ProgramFilesX86 => Combine("ProgramFilesX86");

    public string ProgramData => Combine("ProgramData");

    public string Combine(params string[] parts)
    {
        var full = new[] { Path }.Concat(parts).ToArray();
        var result = System.IO.Path.Combine(full);
        System.IO.Directory.CreateDirectory(result);
        return result;
    }

    public string CreateFile(string relativePath, int sizeBytes)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, new byte[sizeBytes]);
        return full;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Path, recursive: true);
        }
        catch
        {
        }
    }
}

public sealed class FakeEnvironment : IEnvironment
{
    private readonly TempRoot _root;
    private readonly Dictionary<string, string> _variables;

    public FakeEnvironment(TempRoot root, Dictionary<string, string>? variables = null)
    {
        _root = root;
        _variables = variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public string LocalApplicationData => _root.LocalAppData;

    public string ApplicationData => _root.AppData;

    public string UserProfile => _root.UserProfile;

    public string ProgramFiles => _root.ProgramFiles;

    public string ProgramFilesX86 => _root.ProgramFilesX86;

    public string ProgramData => _root.ProgramData;

    public string Temp => System.IO.Path.Combine(_root.Path, "Temp");

    public string? GetEnvironmentVariable(string name) =>
        _variables.TryGetValue(name, out var value) ? value : null;

    public string ExpandPath(string path)
    {
        var expanded = _variables.Aggregate(
            path,
            (current, pair) => current.Replace($"%{pair.Key}%", pair.Value, StringComparison.OrdinalIgnoreCase));

        expanded = expanded
            .Replace("%LOCALAPPDATA%", LocalApplicationData, StringComparison.OrdinalIgnoreCase)
            .Replace("%APPDATA%", ApplicationData, StringComparison.OrdinalIgnoreCase)
            .Replace("%USERPROFILE%", UserProfile, StringComparison.OrdinalIgnoreCase)
            .Replace("%PROGRAMFILES(X86)%", ProgramFilesX86, StringComparison.OrdinalIgnoreCase)
            .Replace("%PROGRAMFILES%", ProgramFiles, StringComparison.OrdinalIgnoreCase)
            .Replace("%PROGRAMDATA%", ProgramData, StringComparison.OrdinalIgnoreCase);

        return System.IO.Path.GetFullPath(expanded);
    }
}

public sealed class FakeCommandRunner : ICommandRunner
{
    private readonly Func<CommandDefinition, CancellationToken, Task<CommandResult>> _handler;

    public FakeCommandRunner(Func<CommandDefinition, CancellationToken, Task<CommandResult>>? handler = null)
    {
        _handler = handler ?? ((_, _) => Task.FromResult(new CommandResult(0, string.Empty, false)));
    }

    public FakeCommandRunner Result(CommandResult result) =>
        new((_, _) => Task.FromResult(result));

    public IReadOnlyList<CommandDefinition> Invocations { get; } = new List<CommandDefinition>();

    public Task<CommandResult> RunAsync(CommandDefinition command, CancellationToken cancellationToken = default)
    {
        ((List<CommandDefinition>)Invocations).Add(command);
        return _handler(command, cancellationToken);
    }
}

public sealed class FakeCommandLocator : ICommandLocator
{
    private readonly HashSet<string> _available;

    public FakeCommandLocator(params string[] available)
    {
        _available = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsAvailable(string commandName) =>
        _available.Contains(System.IO.Path.GetFileNameWithoutExtension(commandName)) ||
        _available.Contains(commandName);
}

public sealed class FakeProcessInspector : IProcessInspector
{
    private readonly IReadOnlyList<RunningProcessInfo> _processes;

    public FakeProcessInspector(params RunningProcessInfo[] processes)
    {
        _processes = processes;
    }

    public IReadOnlyList<RunningProcessInfo> GetRunningProcesses() => _processes;
}

public static class TestItems
{
    public static CleanupItem Directory(string path, string? group = null, CleanupCategory category = CleanupCategory.Cache, CleanupRisk risk = CleanupRisk.Low)
    {
        var full = System.IO.Path.GetFullPath(path);
        return new CleanupItem
        {
            Key = "test:" + full,
            Path = full,
            DisplayName = System.IO.Path.GetFileName(full),
            GroupName = group,
            Category = category,
            Risk = risk,
            Target = CleanupTarget.Directory,
            CleanCommandFile = null
        };
    }
}
