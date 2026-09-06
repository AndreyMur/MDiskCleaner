using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Tests;

public class CacheCatalogTests
{
    [Fact]
    public async Task BuildSeeds_ExpandsEnvironmentPaths()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var service = new CacheCatalogService(
            environment: environment,
            runner: new FakeCommandRunner(),
            locator: new FakeCommandLocator());

        var seeds = await service.BuildSeedsAsync();

        Assert.NotEmpty(seeds);
        Assert.All(seeds, seed => Assert.True(
            seed.CommandOnly || !string.IsNullOrEmpty(seed.Path),
            $"Seed '{seed.DisplayName}' must have a path unless command-only"));
    }

    [Fact]
    public async Task BuildSeeds_WhenQueryReturnsDifferentPath_EmitsRealAndOrphan()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var realPath = root.Combine("real-npm-cache");

        var runner = new FakeCommandRunner().Result(new CommandResult(0, realPath, false));
        var service = new CacheCatalogService(
            environment: environment,
            runner: runner,
            locator: new FakeCommandLocator());

        var seeds = await service.BuildSeedsAsync();
        var npmSeeds = seeds.Where(s => s.Key.StartsWith("npm-cache:", StringComparison.Ordinal)).ToList();

        Assert.Contains(npmSeeds, s => string.Equals(s.Path, realPath, StringComparison.OrdinalIgnoreCase));
        var orphan = environment.LocalApplicationData + "\\npm-cache";
        Assert.Contains(npmSeeds, s => string.Equals(s.Path, orphan, StringComparison.OrdinalIgnoreCase));

        var realSeed = npmSeeds.Single(s => string.Equals(s.Path, realPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("cmd.exe", realSeed.CleanCommandFile);
        Assert.True(realSeed.AllowDirectDelete);
        Assert.Equal(CleanupRisk.Low, realSeed.Risk);
    }

    [Fact]
    public async Task BuildSeeds_WhenQueryFails_UsesDefaultPathWithCleanCommand()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var runner = new FakeCommandRunner().Result(new CommandResult(1, "command not found", false));
        var service = new CacheCatalogService(
            environment: environment,
            runner: runner,
            locator: new FakeCommandLocator());

        var seeds = await service.BuildSeedsAsync();
        var npmSeeds = seeds.Where(s => s.Key.StartsWith("npm-cache:", StringComparison.Ordinal)).ToList();

        Assert.Single(npmSeeds);
        Assert.Equal(environment.LocalApplicationData + "\\npm-cache", npmSeeds[0].Path);
        Assert.NotNull(npmSeeds[0].CleanCommandFile);
    }

    [Fact]
    public async Task BuildSeeds_ResolvesNpmRealPathFromFakeNpmrc_WhenNpmCommandFails()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var realPath = root.Combine("real-npm-cache");

        File.WriteAllText(
            Path.Combine(environment.UserProfile, ".npmrc"),
            "; npm user config\nregistry=https://registry.npmjs.org/\ncache=" + realPath + "\n");

        var runner = new FakeCommandRunner().Result(new CommandResult(1, "npm is not recognized", false));
        var service = new CacheCatalogService(
            environment: environment,
            runner: runner,
            locator: new FakeCommandLocator());

        var seeds = await service.BuildSeedsAsync();
        var npmSeeds = seeds.Where(s => s.Key.StartsWith("npm-cache:", StringComparison.Ordinal)).ToList();

        var realSeed = npmSeeds.SingleOrDefault(s => string.Equals(s.Path, realPath, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(realSeed);
        Assert.Equal("cmd.exe", realSeed.CleanCommandFile);
        Assert.Equal(CleanupRisk.Low, realSeed.Risk);
    }

    [Fact]
    public async Task BuildSeeds_IgnoresNpmrcWithoutCacheKey()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);

        File.WriteAllText(
            Path.Combine(environment.UserProfile, ".npmrc"),
            "registry=https://registry.npmjs.org/\n; no cache= line here\n");

        var runner = new FakeCommandRunner().Result(new CommandResult(1, "npm is not recognized", false));
        var service = new CacheCatalogService(
            environment: environment,
            runner: runner,
            locator: new FakeCommandLocator());

        var seeds = await service.BuildSeedsAsync();
        var npmSeeds = seeds.Where(s => s.Key.StartsWith("npm-cache:", StringComparison.Ordinal)).ToList();

        Assert.Single(npmSeeds);
        Assert.Equal(environment.LocalApplicationData + "\\npm-cache", npmSeeds[0].Path);
    }

    [Fact]
    public async Task BuildSeeds_OmitsDockerWhenCliMissing()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var service = new CacheCatalogService(
            environment: environment,
            runner: new FakeCommandRunner(),
            locator: new FakeCommandLocator());

        var seeds = await service.BuildSeedsAsync();

        Assert.DoesNotContain(seeds, s => s.Key.StartsWith("docker-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildSeeds_IncludesDockerCommandOnlyWhenCliAvailable()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var service = new CacheCatalogService(
            environment: environment,
            runner: new FakeCommandRunner(),
            locator: new FakeCommandLocator("docker"));

        var seeds = await service.BuildSeedsAsync();
        var docker = seeds.SingleOrDefault(s => s.Key.StartsWith("docker-", StringComparison.Ordinal));

        Assert.NotNull(docker);
        Assert.True(docker.CommandOnly);
        Assert.Null(docker.Path);
        Assert.False(docker.AllowDirectDelete);
    }
}
