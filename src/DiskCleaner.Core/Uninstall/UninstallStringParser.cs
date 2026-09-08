using System.Text.RegularExpressions;

namespace DiskCleaner.Core.Uninstall;

public enum UninstallerKind
{
    Msi,
    Inno,
    Nsis,
    Generic
}

public sealed record UninstallCommand(UninstallerKind Kind, string FileName, string Arguments, bool Silent)
{
    public string DisplayText =>
        string.IsNullOrWhiteSpace(Arguments) ? FileName : $"{FileName} {Arguments}";
}

/// <summary>
/// Разбор <c>UninstallString</c>/<c>QuietUninstallString</c> и построение команды удаления
/// (FR-4.5): MSI → <c>msiexec /x {GUID} /qn /norestart</c>; Inno (<c>unins000.exe</c>) →
/// тихие ключи; NSIS (<c>Uninstall.exe</c>) → <c>/S</c>; прочие — запуск «как есть»
/// с сохранением аргументов: если в строке уже есть известный тихий ключ — команда тихая,
/// иначе это GUI-профиль (<see cref="UninstallCommand.Silent"/> = false).
/// </summary>
public sealed partial class UninstallStringParser
{
    private static readonly HashSet<string> KnownSilentSwitches = new(StringComparer.OrdinalIgnoreCase)
    {
        "s", "silent", "quiet", "verysilent", "qn", "qb", "norestart"
    };

    public UninstallCommand? Parse(string? uninstallString, string? productCode = null)
    {
        if (string.IsNullOrWhiteSpace(uninstallString))
        {
            return null;
        }

        var text = uninstallString.Trim();

        if (TryParseMsi(text, out var msi))
        {
            return msi;
        }

        var (file, trailingArguments) = SplitExecutable(text);
        if (file is null)
        {
            return null;
        }

        var fileName = Path.GetFileName(file);
        var kind = ClassifyByFileName(fileName);

        return kind switch
        {
            UninstallerKind.Inno => new UninstallCommand(
                kind,
                file,
                "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                Silent: true),
            UninstallerKind.Nsis => new UninstallCommand(
                kind,
                file,
                "/S",
                Silent: true),
            _ => new UninstallCommand(
                UninstallerKind.Generic,
                file,
                trailingArguments,
                HasSilentSwitch(trailingArguments))
        };
    }

    /// <summary>
    /// Строит команду для записи <see cref="InstalledApp"/>: для тихого удаления
    /// предпочитается <see cref="InstalledApp.QuietUninstallString"/> (FR-4.5), при его
    /// отсутствии — обычный <see cref="InstalledApp.UninstallString"/>.
    /// </summary>
    public UninstallCommand? ParseFor(InstalledApp app) =>
        Parse(app.QuietUninstallString ?? app.UninstallString, app.ProductCode);

    private bool TryParseMsi(string text, out UninstallCommand? command)
    {
        command = null;
        if (!text.Contains("msiexec", StringComparison.OrdinalIgnoreCase) && !text.Contains(".msi", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var product = ExtractProductCode(text);
        if (string.IsNullOrEmpty(product))
        {
            return false;
        }

        command = new UninstallCommand(
            UninstallerKind.Msi,
            "msiexec.exe",
            $"/x {product} /qn /norestart",
            Silent: true);
        return true;
    }

    private static UninstallerKind ClassifyByFileName(string fileName)
    {
        if (UninstallerRegex().IsMatch(fileName))
        {
            return UninstallerKind.Inno;
        }

        if (fileName.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("uninstall.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("uninst.exe", StringComparison.OrdinalIgnoreCase))
        {
            return UninstallerKind.Nsis;
        }

        return UninstallerKind.Generic;
    }

    /// <summary>Выделяет исполняемый файл и сохраняет аргументы после него («запуск как есть», FR-4.5).</summary>
    private static (string? File, string Arguments) SplitExecutable(string text)
    {
        var quotedMatch = QuotedPathRegex().Match(text);
        if (quotedMatch.Success)
        {
            var arguments = text[(quotedMatch.Index + quotedMatch.Length)..].Trim();
            return (quotedMatch.Groups[1].Value, arguments);
        }

        var tokenMatch = ExePathRegex().Match(text);
        if (tokenMatch.Success &&
            tokenMatch.Value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var arguments = text[(tokenMatch.Index + tokenMatch.Length)..].Trim();
            return (tokenMatch.Value, arguments);
        }

        return (null, string.Empty);
    }

    private static bool HasSilentSwitch(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        var tokens = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim().TrimStart('/', '-');
            if (KnownSilentSwitches.Contains(token))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtractProductCode(string text)
    {
        var match = ProductCodeRegex().Match(text);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex ProductCodeRegex();

    [GeneratedRegex(@"""(?<path>[^""]*\.exe)""", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedPathRegex();

    [GeneratedRegex(@"(?<path>[A-Za-z]:\\[^"" ]*\.exe)", RegexOptions.IgnoreCase)]
    private static partial Regex ExePathRegex();

    [GeneratedRegex(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex UninstallerRegex();
}
