using System.Text.RegularExpressions;

namespace DiskCleaner.Core.Environment;

public sealed partial class EnvironmentProvider : Abstractions.IEnvironment
{
    public string LocalApplicationData =>
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);

    public string ApplicationData =>
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);

    public string UserProfile =>
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

    public string ProgramFiles =>
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);

    public string ProgramFilesX86 =>
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);

    public string ProgramData =>
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData);

    public string Temp => Path.GetTempPath();

    public string? GetEnvironmentVariable(string name) =>
        System.Environment.GetEnvironmentVariable(name);

    public string ExpandPath(string path)
    {
        var expanded = TokenRegex().Replace(path, match => match.Groups[1].Value.ToUpperInvariant() switch
        {
            "LOCALAPPDATA" => LocalApplicationData,
            "APPDATA" => ApplicationData,
            "USERPROFILE" => UserProfile,
            "PROGRAMFILES" => ProgramFiles,
            "PROGRAMFILES(X86)" => ProgramFilesX86,
            "PROGRAMDATA" => ProgramData,
            "TEMP" => Temp,
            _ => System.Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? match.Value
        });

        return Path.GetFullPath(expanded);
    }

    [GeneratedRegex(@"%([^%]+)%", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();
}
