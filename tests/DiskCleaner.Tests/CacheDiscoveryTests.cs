using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Tests;

/// <summary>
/// Интеграционные проверки tracer-bullet фазы модуля 02 (FR-2.2–2.4, приёмка
/// «не чистить несуществующий кэш»): реальный путь менеджера + осиротевший кэш,
/// а объекты очистки создаются только для существующих каталогов.
/// </summary>
public class CacheDiscoveryTests
{
    private static CacheCatalogService NpmCatalog(FakeEnvironment environment, string npmRealPath)
    {
        var runner = new FakeCommandRunner((command, _) =>
        {
            var isNpm = command.Arguments.Contains("npm config get cache", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(
                isNpm
                    ? new CommandResult(0, npmRealPath, false)
                    : new CommandResult(1, "not available", false));
        });

        return new CacheCatalogService(
            environment: environment,
            runner: runner,
            locator: new FakeCommandLocator());
    }

    [Fact]
    public async Task Analyze_KeepsRealAndOrphanCache_ForExistingDirectories()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var realNpmDir = Path.Combine(root.Path, "real-npm-cache");
        root.CreateFile("real-npm-cache\\pkg\\file.bin", 500);
        root.CreateFile("LocalAppData\\npm-cache\\old\\file.bin", 1400);

        var service = NpmCatalog(environment, realNpmDir);
        var seeds = await service.BuildSeedsAsync();
        var result = await new AnalysisService().AnalyzeAsync(seeds);

        var npmItems = result.Items
            .Where(i => i.Key.StartsWith("npm-cache:", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, npmItems.Count);

        var real = npmItems.Single(i => i.Path!.StartsWith(realNpmDir, StringComparison.OrdinalIgnoreCase));
        Assert.False(real.IsOrphan);
        Assert.Equal("cmd.exe", real.CleanCommandFile);
        Assert.True(real.SizeBytes > 0);

        var orphan = npmItems.Single(i => i.Path!.StartsWith(root.Path + "\\LocalAppData\\npm-cache", StringComparison.OrdinalIgnoreCase));
        Assert.True(orphan.IsOrphan);
        Assert.Contains("осиротевший", orphan.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.Null(orphan.CleanCommandFile);
        Assert.True(orphan.AllowDirectDelete);
        Assert.True(orphan.SizeBytes > 0);
    }

    [Fact]
    public async Task Analyze_ReportsOrphanWhenRealPathIsMissing_AndSkipsNonexistentRealPath()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        var missingReal = Path.Combine(root.Path, "real-npm-cache-missing");
        root.CreateFile("LocalAppData\\npm-cache\\old\\file.bin", 1400);

        var service = NpmCatalog(environment, missingReal);
        var seeds = await service.BuildSeedsAsync();
        var result = await new AnalysisService().AnalyzeAsync(seeds);

        var npmItems = result.Items
            .Where(i => i.Key.StartsWith("npm-cache:", StringComparison.Ordinal))
            .ToList();

        var orphan = Assert.Single(npmItems);
        Assert.True(orphan.IsOrphan);
        Assert.Equal(environment.LocalApplicationData + "\\npm-cache", orphan.Path);
        Assert.True(orphan.SizeBytes > 0);
    }

    [Fact]
    public async Task Analyze_CreatesNoItemForNonexistentCacheDefault()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        root.CreateFile("LocalAppData\\uv\\cache\\pkg\\wheel.bin", 300);

        var runner = new FakeCommandRunner((command, _) =>
            Task.FromResult(new CommandResult(1, "not available", false)));
        var service = new CacheCatalogService(
            environment: environment,
            runner: runner,
            locator: new FakeCommandLocator());
        var seeds = await service.BuildSeedsAsync();
        var result = await new AnalysisService().AnalyzeAsync(seeds);

        var uvItems = result.Items
            .Where(i => i.Key.StartsWith("uv-cache:", StringComparison.Ordinal))
            .ToList();
        var pipItems = result.Items
            .Where(i => i.Key.StartsWith("pip-cache:", StringComparison.Ordinal))
            .ToList();

        var uv = Assert.Single(uvItems);
        Assert.Equal(environment.LocalApplicationData + "\\uv\\cache", uv.Path);
        Assert.True(uv.SizeBytes > 0);

        Assert.Empty(pipItems);
    }
}
