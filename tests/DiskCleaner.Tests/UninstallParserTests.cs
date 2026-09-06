using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

public class UninstallParserTests
{
    private readonly UninstallStringParser _parser = new();

    [Theory]
    [InlineData("MsiExec.exe /I{12345678-1234-1234-1234-123456789012}")]
    [InlineData("msiexec.exe /X{12345678-1234-1234-1234-123456789012} /qn")]
    [InlineData("\"C:\\Windows\\System32\\msiexec.exe\" /i {12345678-1234-1234-1234-123456789012}")]
    public void Parse_MsiUninstallString_ProducesSilentMsiCommand(string uninstallString)
    {
        var command = _parser.Parse(uninstallString);

        Assert.NotNull(command);
        Assert.Equal(UninstallerKind.Msi, command.Kind);
        Assert.True(command.Silent);
        Assert.Contains("msiexec.exe", command.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/x {12345678-1234-1234-1234-123456789012}", command.Arguments);
        Assert.Contains("/qn", command.Arguments);
        Assert.Contains("/norestart", command.Arguments);
    }

    [Fact]
    public void Parse_InnoUninstaller_AddsSilentFlags()
    {
        var command = _parser.Parse("\"C:\\Program Files\\K-Lite Codec Pack\\unins000.exe\"");

        Assert.NotNull(command);
        Assert.Equal(UninstallerKind.Inno, command.Kind);
        Assert.True(command.Silent);
        Assert.Equal(@"C:\Program Files\K-Lite Codec Pack\unins000.exe", command.FileName);
        Assert.Contains("/VERYSILENT", command.Arguments);
        Assert.Contains("/SUPPRESSMSGBOXES", command.Arguments);
        Assert.Contains("/NORESTART", command.Arguments);
    }

    [Fact]
    public void Parse_NsisUninstaller_AddsSlashS()
    {
        var command = _parser.Parse("\"C:\\Program Files\\CCleaner\\Uninstall.exe\"");

        Assert.NotNull(command);
        Assert.Equal(UninstallerKind.Nsis, command.Kind);
        Assert.True(command.Silent);
        Assert.Equal("/S", command.Arguments);
    }

    [Fact]
    public void Parse_GenericExe_MarksInteractive()
    {
        var command = _parser.Parse("\"D:\\vendor\\winsdksetup.exe\"");

        Assert.NotNull(command);
        Assert.Equal(UninstallerKind.Generic, command.Kind);
        Assert.False(command.Silent);
        Assert.Equal(string.Empty, command.Arguments);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsNull()
    {
        Assert.Null(_parser.Parse(null));
        Assert.Null(_parser.Parse(string.Empty));
    }

    [Theory]
    [InlineData(0, ProcessExitMeaning.Success)]
    [InlineData(3010, ProcessExitMeaning.RebootRequired)]
    [InlineData(1605, ProcessExitMeaning.NotInstalled)]
    [InlineData(1612, ProcessExitMeaning.NotInstalled)]
    [InlineData(1603, ProcessExitMeaning.Failed)]
    [InlineData(-1, ProcessExitMeaning.Failed)]
    public void ExitCodes_MsiexecCodes_AreClassified(int exitCode, ProcessExitMeaning expected)
    {
        Assert.Equal(expected, UninstallExitCodes.Classify(exitCode));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3010)]
    [InlineData(1605)]
    [InlineData(1612)]
    public void ExitCodes_OkCodes_ReturnTrue(int exitCode)
    {
        Assert.True(UninstallExitCodes.IsOk(exitCode));
    }
}
