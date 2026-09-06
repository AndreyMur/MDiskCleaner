using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Tests;

public class DenyListTests
{
    [Theory]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"D:\pagefile.sys")]
    [InlineData(@"C:\swapfile.sys")]
    [InlineData(@"C:\hiberfil.sys")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows\WinSxS")]
    [InlineData(@"C:\System Volume Information")]
    [InlineData(@"C:\System Volume Information\foo")]
    public void IsProtectedPath_True_ForDeniedPaths(string path)
    {
        Assert.True(DenyList.IsProtectedPath(path), path);
    }

    [Theory]
    [InlineData(@"C:\Windows\Temp")]
    [InlineData(@"C:\Windows\Temp\sub")]
    [InlineData(@"C:\Windows\SoftwareDistribution\Download")]
    [InlineData(@"C:\Windows.old")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\$Recycle.Bin")]
    [InlineData(@"C:\Users\me\AppData\Local\Temp")]
    public void IsProtectedPath_False_ForAllowedPaths(string path)
    {
        Assert.False(DenyList.IsProtectedPath(path), path);
    }

    [Fact]
    public async Task DirectoryDeleter_NeverDeletesDeniedPath()
    {
        var deleter = new DirectoryDeleter();
        var outcome = await deleter.DeletePathAsync(@"C:\Windows", CleanupTarget.Directory);

        Assert.True(outcome.Denied);
        Assert.False(outcome.FullyDeleted);
        Assert.NotEmpty(outcome.Errors);
    }
}
