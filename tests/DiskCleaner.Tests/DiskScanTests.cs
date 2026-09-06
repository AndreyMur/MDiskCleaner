using DiskCleaner.Core.Models;
using DiskCleaner.Core.Scanning;

namespace DiskCleaner.Tests;

public class DiskScanTests
{
    [Fact]
    public async Task DiskScan_IgnoresSystemDirectoriesByDefault_AndIncludesOnRequest()
    {
        using var root = new TempRoot();
        root.CreateFile("keep\\a.bin", 100);
        root.CreateFile("Windows\\b.bin", 200);
        root.CreateFile("System Volume Information\\c.bin", 300);
        root.CreateFile("$Recycle.Bin\\d.bin", 400);
        root.CreateFile("WindowsApps\\e.bin", 500);

        var service = new DiskScanService();

        var excluded = await service.ScanAsync(new DiskScanRequest { RootPath = root.Path });
        Assert.Contains(excluded.TopLevelDirectories, m => Path.GetFileName(m.Path) == "keep");
        Assert.DoesNotContain(excluded.TopLevelDirectories, m => Path.GetFileName(m.Path) == "Windows");
        Assert.DoesNotContain(excluded.TopLevelDirectories, m => Path.GetFileName(m.Path) == "WindowsApps");
        Assert.Equal(
            new[] { "$Recycle.Bin", "System Volume Information", "Windows", "WindowsApps" },
            excluded.IgnoredDirectoryNames.OrderBy(n => n));
        Assert.Equal(100, excluded.ScannedBytes);

        var included = await service.ScanAsync(new DiskScanRequest
        {
            RootPath = root.Path,
            IncludeSystemDirectories = true
        });

        Assert.Empty(included.IgnoredDirectoryNames);
        Assert.Equal(1500, included.ScannedBytes);
        Assert.Equal(200, MeasurementSize(included, "Windows"));
        Assert.Equal(300, MeasurementSize(included, "System Volume Information"));
        Assert.Equal(400, MeasurementSize(included, "$Recycle.Bin"));
        Assert.Equal(500, MeasurementSize(included, "WindowsApps"));
    }

    [Fact]
    public async Task DiskScan_MeasuresTopLevelDirectoriesAndRootFiles()
    {
        using var root = new TempRoot();
        root.CreateFile("pagefile.sys", 500);
        root.CreateFile("d1\\a.bin", 100);
        root.CreateFile("d1\\sub\\b.bin", 200);
        root.CreateFile("d2\\x.bin", 700);

        var result = await new DiskScanService().ScanAsync(new DiskScanRequest { RootPath = root.Path });

        Assert.Equal(2, result.TopLevelDirectories.Count);
        Assert.Equal(300, MeasurementSize(result, "d1"));
        Assert.Equal(700, MeasurementSize(result, "d2"));
        Assert.Equal(500, result.RootFileBytes);
        Assert.Equal(1, result.RootFileCount);
        Assert.Equal(1500, result.ScannedBytes);
        Assert.Equal(3, result.Statistics.DirectoriesEnumerated);
    }

    [Fact]
    public async Task DiskScan_ReportsRecycleBinAndHibernationAsSystemObjects()
    {
        const string sid = "S-1-5-21-1000000000-1000000000-1000000000-1234";
        using var root = new TempRoot();
        root.CreateFile("$Recycle.Bin\\" + sid + "\\$R0.bin", 700);
        root.CreateFile("hiberfil.sys", 1000);

        var service = new DiskScanService(currentUserSid: sid);
        var result = await service.ScanAsync(new DiskScanRequest { RootPath = root.Path });

        Assert.Contains("$Recycle.Bin", result.IgnoredDirectoryNames);

        var recycle = result.SystemObjects.Single(o => o.Key.StartsWith("recycle-bin:", StringComparison.Ordinal));
        Assert.True(recycle.Present);
        Assert.Equal(700, recycle.SizeBytes);
        Assert.Equal(1, recycle.FileCount);

        var hibernation = result.SystemObjects.Single(o => o.Key.StartsWith("hibernation:", StringComparison.Ordinal));
        Assert.True(hibernation.Present);
        Assert.Equal(1000, hibernation.SizeBytes);

        Assert.Equal(1000, result.RootFileBytes);
    }

    [Fact]
    public async Task DiskScan_DetailizesGradle_VsCode_AndroidSdk()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        root.CreateFile("UserProfile\\.gradle\\caches\\lib.jar", 300);
        root.CreateFile("UserProfile\\.gradle\\wrapper\\dists\\gradle.zip", 200);
        root.CreateFile("UserProfile\\.gradle\\jdks\\jdk\\bin\\java.dll", 100);

        root.CreateFile("AppData\\Code\\Cache\\a.cache", 150);
        root.CreateFile("AppData\\Code\\CachedData\\b.dat", 60);
        root.CreateFile("AppData\\Code\\User\\settings.json", 999_999);

        root.CreateFile("LocalAppData\\Android\\Sdk\\ndk\\lib\\x.bin", 250);
        root.CreateFile("LocalAppData\\Android\\Sdk\\system-images\\img.bin", 400);
        root.CreateFile("LocalAppData\\Android\\Sdk\\platforms\\p.bin", 100);

        var service = new DiskScanService(environment: environment);
        var result = await service.ScanAsync(new DiskScanRequest { RootPath = root.Path });

        var gradle = result.LargeObjects.Single(o => o.Key == "gradle");
        Assert.Equal(600, gradle.Total?.SizeBytes);
        Assert.Equal(300, ComponentSize(gradle, "caches"));
        Assert.Equal(200, ComponentSize(gradle, "wrapper/dists"));
        Assert.Equal(100, ComponentSize(gradle, "jdks"));
        Assert.Equal(CleanupCategory.DevToolchain,
            gradle.Components.Single(c => c.Label == "jdks").Category);

        var vscode = result.LargeObjects.Single(o => o.Key == "vscode");
        Assert.Equal(1_000_209, vscode.Total?.SizeBytes);
        Assert.Equal(2, vscode.Components.Count);
        Assert.DoesNotContain(vscode.Components, c => c.Label == "User");
        Assert.Contains(vscode.Components, c => c.Label == "Cache" && c.Measurement.SizeBytes == 150);
        Assert.Contains(vscode.Components, c => c.Label == "CachedData" && c.Measurement.SizeBytes == 60);

        var androidSdk = result.LargeObjects.Single(o => o.Key == "android-sdk");
        Assert.Equal(750, androidSdk.Total?.SizeBytes);
        Assert.Equal(CleanupCategory.DevToolchain, androidSdk.Category);
        Assert.Equal(CleanupRisk.Medium, androidSdk.Risk);
        Assert.Equal(3, androidSdk.Components.Count);
        Assert.Equal(400, ComponentSize(androidSdk, "system-images"));
        Assert.Equal(250, ComponentSize(androidSdk, "ndk"));
        Assert.Equal(100, ComponentSize(androidSdk, "platforms"));
    }

    [Fact]
    public async Task DiskScan_CacheReusesUnchangedBranches_AndRecomputesChangedOnes()
    {
        using var root = new TempRoot();
        var scanTree = root.Combine("scan");
        var cacheDirectory = root.Combine("cache");

        root.CreateFile("scan\\data\\a.bin", 100);
        root.CreateFile("scan\\data\\sub\\b.bin", 200);
        root.CreateFile("scan\\данные\\файл.bin", 50);

        var data = root.Combine("scan", "data");
        var sub = root.Combine("scan", "data", "sub");
        var russian = root.Combine("scan", "данные");

        var unchangedTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var changedTime = new DateTime(2020, 1, 1, 0, 1, 0, DateTimeKind.Utc);

        await Task.Delay(50);
        Directory.SetLastWriteTimeUtc(data, unchangedTime);
        Directory.SetLastWriteTimeUtc(sub, unchangedTime);
        Directory.SetLastWriteTimeUtc(russian, unchangedTime);

        var request = new DiskScanRequest { RootPath = scanTree };

        var first = await new DiskScanService(new ScanCacheStore(cacheDirectory)).ScanAsync(request);
        Assert.Equal(350, first.ScannedBytes);
        Assert.Equal(3, first.Statistics.DirectoriesEnumerated);

        var second = await new DiskScanService(new ScanCacheStore(cacheDirectory)).ScanAsync(request);
        Assert.Equal(350, second.ScannedBytes);
        Assert.Equal(0, second.Statistics.DirectoriesEnumerated);
        Assert.True(second.Statistics.DirectoriesReused >= 3);

        root.CreateFile("scan\\data\\sub\\c.bin", 50);
        await Task.Delay(50);
        Directory.SetLastWriteTimeUtc(data, unchangedTime);
        Directory.SetLastWriteTimeUtc(sub, changedTime);
        Directory.SetLastWriteTimeUtc(russian, unchangedTime);

        var third = await new DiskScanService(new ScanCacheStore(cacheDirectory)).ScanAsync(request);
        Assert.Equal(400, third.ScannedBytes);
        Assert.Equal(1, third.Statistics.DirectoriesEnumerated);

        var store = new ScanCacheStore(cacheDirectory);
        Assert.True(File.Exists(store.FilePath));

        var reloaded = new ScanCacheStore(cacheDirectory);
        Assert.NotNull(reloaded.TryGet(data));
        Assert.NotNull(reloaded.TryGet(sub));
        Assert.NotNull(reloaded.TryGet(russian));
        Assert.Equal(Directory.GetLastWriteTimeUtc(data).Ticks, reloaded.TryGet(data)?.DirectoryLastWriteTicks);
    }

    private static long MeasurementSize(DiskScanResult result, string directoryName) =>
        result.TopLevelDirectories
            .Single(m => string.Equals(Path.GetFileName(m.Path), directoryName, StringComparison.OrdinalIgnoreCase))
            .SizeBytes;

    private static long ComponentSize(LargeObjectDetail detail, string label) =>
        detail.Components.Single(c => c.Label == label).Measurement.SizeBytes;
}
