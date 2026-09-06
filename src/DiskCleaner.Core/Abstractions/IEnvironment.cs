namespace DiskCleaner.Core.Abstractions;

public interface IEnvironment
{
    string? GetEnvironmentVariable(string name);

    string LocalApplicationData { get; }

    string ApplicationData { get; }

    string UserProfile { get; }

    string ProgramFiles { get; }

    string ProgramFilesX86 { get; }

    string ProgramData { get; }

    string Temp { get; }

    string ExpandPath(string path);
}
