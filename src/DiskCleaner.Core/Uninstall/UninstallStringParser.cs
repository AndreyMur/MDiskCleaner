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

public sealed partial class UninstallStringParser
{
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

        var file = ExtractExecutable(text);
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
                string.Empty,
                Silent: false)
        };
    }

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

    private static string? ExtractExecutable(string text)
    {
        var candidate = ExtractQuotedPath(text) ?? ExtractFirstToken(text);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        candidate = candidate.Trim().Trim('"');
        return candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    private static string? ExtractQuotedPath(string text)
    {
        var match = QuotedPathRegex().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractFirstToken(string text)
    {
        var match = ExePathRegex().Match(text);
        return match.Success ? match.Value : null;
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
