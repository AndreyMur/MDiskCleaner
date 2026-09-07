using DiskCleaner.Core.Leftovers;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

/// <summary>
/// Сопоставление каталогов с белым списком установленного ПО из реестра Uninstall
/// (FR-3.1): по InstallLocation и по нормализованному DisplayName. Фикстуры реестра —
/// InstalledApp (результат <see cref="UninstallRegistryService.ReadInstalledApps"/>).
/// </summary>
public class LeftoverWhitelistTests
{
    private static InstalledApp App(
        string name,
        string? location = null,
        bool systemComponent = false) => new()
    {
        ProductCode = "{" + Guid.NewGuid() + "}",
        ScopeKey = nameof(InstalledAppScope.LocalMachine64),
        DisplayName = name,
        InstallLocation = location,
        IsSystemComponent = systemComponent
    };

    [Theory]
    [InlineData("lm-studio")]
    [InlineData("LM Studio")]
    [InlineData("LM-STUDIO")]
    public void IsKnownFolderName_VersionedDisplayName_MatchByNormalizedPrefix(string folder)
    {
        var whitelist = new InstalledWhitelist([App("LM Studio 0.3.2")]);
        Assert.True(whitelist.IsKnownFolderName(folder));
    }

    [Fact]
    public void IsKnownFolderName_DisplayNameWithCompany_BrandFolderIsKnown()
    {
        var whitelist = new InstalledWhitelist([App("Google Chrome")]);
        Assert.True(whitelist.IsKnownFolderName("Google"));
        Assert.True(whitelist.IsKnownFolderName("google chrome"));
    }

    [Fact]
    public void IsKnownFolderName_JunkFolderWithUpdaterSuffix_NotProtectedByInstalledBrand()
    {
        var whitelist = new InstalledWhitelist([App("Wondershare Filmora", @"C:\Program Files\Wondershare")]);
        Assert.True(whitelist.IsKnownFolderName("Wondershare"));
        Assert.False(whitelist.IsKnownFolderName("WondershareUpdate"));
        Assert.False(whitelist.IsKnownFolderName("Wondershare Update"));
    }

    [Fact]
    public void IsKnownFolderName_DifferentProductsOfSameVendor_DoNotProtectEachOther()
    {
        var whitelist = new InstalledWhitelist([App("4DDiG Data Recovery")]);
        Assert.False(whitelist.IsKnownFolderName("4DDiG File Repair"));
        Assert.True(whitelist.IsKnownFolderName("4DDiG Data Recovery"));
    }

    [Fact]
    public void IsKnownPath_InstallLocationNestedInsideCandidateDir_ProtectsBrandRoot()
    {
        var whitelist = new InstalledWhitelist(
            [App("Google Chrome", @"C:\Program Files\Google\Chrome\Application")]);

        Assert.True(whitelist.IsKnownPath(@"C:\Program Files\Google"));
        Assert.True(whitelist.IsKnownPath(@"C:\Program Files\Google\Chrome\Application"));
        Assert.True(whitelist.IsKnownPath(@"C:\Program Files\Google\Chrome"));
        Assert.False(whitelist.IsKnownPath(@"C:\Program Files\Google Updater"));
    }

    [Fact]
    public void IsKnownPath_PerUserInstallUnderLocalAppData_ProtectsLeaf()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiskCleaner.Tests", "wl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var location = Path.Combine(root, "AppData", "Local", "Programs", "lm-studio");
            var whitelist = new InstalledWhitelist([App("LM Studio", location)]);

            Assert.True(whitelist.IsKnownPath(location));
            Assert.True(whitelist.IsKnownPath(location + "\\resources"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void IsKnownFolderName_SystemComponent_IsIgnored()
    {
        var whitelist = new InstalledWhitelist(
            [App("Windows SDK", @"C:\Program Files (x86)\Windows Kits\10", systemComponent: true)]);
        Assert.False(whitelist.IsKnownFolderName("Windows Kits"));
    }

    [Fact]
    public void IsKnownFolderName_ExactDisplayName_IsKnown()
    {
        var whitelist = new InstalledWhitelist([App("Slack")]);
        Assert.True(whitelist.IsKnownFolderName("Slack"));
        Assert.False(whitelist.IsKnownFolderName("Slack Tech"));
    }

    [Fact]
    public void ContainsName_CoversVersionedDisplayName()
    {
        var whitelist = new InstalledWhitelist([App("Android Studio 2024.1.1")]);
        Assert.True(whitelist.ContainsName("Android Studio"));
        Assert.False(whitelist.ContainsName("Android SDK"));
    }
}
