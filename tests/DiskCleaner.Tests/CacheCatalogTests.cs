using System.Text;
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
        Assert.False(realSeed.IsOrphan);

        var orphanSeed = npmSeeds.Single(s => string.Equals(s.Path, orphan, StringComparison.OrdinalIgnoreCase));
        Assert.True(orphanSeed.IsOrphan);
        Assert.Contains("осиротевший", orphanSeed.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.Null(orphanSeed.CleanCommandFile);
        Assert.True(orphanSeed.AllowDirectDelete);
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

    [Fact]
    public async Task BuildSeeds_ResolvesRealPathOnAnotherDrive_FromCommandOutput()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        const string realPath = @"D:\npm-cache\Store";

        var runner = new FakeCommandRunner().Result(new CommandResult(0, realPath, false));
        var service = new CacheCatalogService(
            environment: environment,
            runner: runner,
            locator: new FakeCommandLocator());

        var seeds = await service.BuildSeedsAsync();
        var npmSeeds = seeds.Where(s => s.Key.StartsWith("npm-cache:", StringComparison.Ordinal)).ToList();

        var realSeed = npmSeeds.Single(s => string.Equals(s.Path, realPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("D:\\", Path.GetPathRoot(realSeed.Path!));
        Assert.Equal("cmd.exe", realSeed.CleanCommandFile);
        Assert.False(realSeed.IsOrphan);

        var orphanSeed = npmSeeds.Single(s => s.IsOrphan);
        Assert.Equal(environment.LocalApplicationData + "\\npm-cache", orphanSeed.Path);
        Assert.Null(orphanSeed.CleanCommandFile);
        Assert.True(orphanSeed.AllowDirectDelete);
    }

    [Fact]
    public async Task BuildSeeds_PreservesUtf8CyrillicPath_FromCommandOutput()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        const string realPath = @"D:\Кэш-пакеты\npm-cache";

        var runner = new FakeCommandRunner().Result(new CommandResult(0, realPath, false));
        var service = new CacheCatalogService(
            environment: environment,
            runner: runner,
            locator: new FakeCommandLocator());

        var seeds = await service.BuildSeedsAsync();
        var npmSeeds = seeds.Where(s => s.Key.StartsWith("npm-cache:", StringComparison.Ordinal)).ToList();

        var realSeed = npmSeeds.Single(s => !s.IsOrphan);
        Assert.Equal(realPath, realSeed.Path);
        Assert.Equal("cmd.exe", realSeed.CleanCommandFile);
        Assert.Contains("Кэш-пакеты", realSeed.Path!);
    }

    [Fact]
    public async Task BuildSeeds_ResolvesUtf8NpmrcCyrillicCachePath_WhenNpmCommandFails()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        const string realPath = @"D:\Кэш-пакеты\npm-cache";

        File.WriteAllText(
            Path.Combine(environment.UserProfile, ".npmrc"),
            "; npm user config\nregistry=https://registry.npmjs.org/\ncache=" + realPath + "\n",
            new UTF8Encoding(false));

        var runner = new FakeCommandRunner().Result(new CommandResult(1, "npm is not recognized", false));
        var service = new CacheCatalogService(
            environment: environment,
            runner: runner,
            locator: new FakeCommandLocator());

        var seeds = await service.BuildSeedsAsync();
        var npmSeeds = seeds.Where(s => s.Key.StartsWith("npm-cache:", StringComparison.Ordinal)).ToList();

        var realSeed = npmSeeds.Single(s => !s.IsOrphan);
        Assert.Equal(realPath, realSeed.Path);
        Assert.Equal("cmd.exe", realSeed.CleanCommandFile);

        var orphanSeed = npmSeeds.Single(s => s.IsOrphan);
        Assert.Equal(environment.LocalApplicationData + "\\npm-cache", orphanSeed.Path);
        Assert.Null(orphanSeed.CleanCommandFile);
    }

    [Fact]
    public async Task ProcessCommandRunner_DecodesUtf8CyrillicOutput_AsManagerStdout()
    {
        using var root = new TempRoot();
        var file = root.Combine("output") + "\\cache-path.txt";
        File.WriteAllText(file, @"D:\Кэш-пакеты\npm-cache" + "\n", new UTF8Encoding(false));

        var runner = new ProcessCommandRunner();
        var result = await runner.RunAsync(new CommandDefinition
        {
            FileName = "cmd.exe",
            Arguments = "/c type \"" + file + "\"",
            TimeoutSec = 30
        });

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains(@"D:\Кэш-пакеты\npm-cache", result.Output);
    }

    // ---- Фаза 3 модуля 02 (раздел 5 PRD 02): штатная команда менеджера в справочнике
    // и требование повышенного токена для глобальных менеджеров из Program Files. ----

    [Fact]
    public async Task BuildSeeds_RustupToolchains_CarriesNativeUninstallCommand_InUserContext()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var service = new CacheCatalogService(
            environment: environment,
            runner: new FakeCommandRunner(),
            locator: new FakeCommandLocator());

        var seeds = await service.BuildSeedsAsync();
        var rustup = seeds.Single(s => s.Key.StartsWith("rustup-toolchains:", StringComparison.Ordinal));

        Assert.Equal(Path.Combine(environment.UserProfile, ".rustup"), rustup.Path);
        Assert.Equal(CleanupCategory.DevToolchain, rustup.Category);
        Assert.Equal(CleanupRisk.Medium, rustup.Risk);
        Assert.Equal("cmd.exe", rustup.CleanCommandFile);
        Assert.Contains("rustup self uninstall -y", rustup.CleanCommandArgs, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(600, rustup.CleanCommandTimeoutSec);
        Assert.False(rustup.RequiresAdmin, "rustup self uninstall выполняется в контексте пользователя без админа (PRD 02, раздел 5).");
    }

    [Fact]
    public async Task BuildSeeds_GlobalManagerInProgramFiles_MapsRequiresAdminAndTimeout()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var document = new CacheCatalogDocument
        {
            Targets =
            [
                new CacheTargetDefinition
                {
                    Id = "global-tool-cache",
                    Label = "Global tool cache",
                    Group = "global-tool",
                    Category = "Cache",
                    Risk = "Low",
                    EnvPaths = [@"%ProgramFiles%\GlobalTool\cache"],
                    CleanCommand = new CommandDefinition
                    {
                        FileName = @"C:\Program Files\GlobalTool\global-tool.exe",
                        Arguments = "cache clean",
                        TimeoutSec = 600
                    },
                    RequiresAdmin = true
                }
            ]
        };

        var service = new CacheCatalogService(
            environment: environment,
            runner: new FakeCommandRunner(),
            locator: new FakeCommandLocator(),
            document: document);

        var seeds = await service.BuildSeedsAsync();
        var seed = Assert.Single(seeds);

        Assert.Equal(Path.Combine(environment.ProgramFiles, "GlobalTool\\cache"), seed.Path);
        Assert.True(seed.RequiresAdmin, "Глобальный менеджер из Program Files должен исполняться с UAC-подъёмом.");
        Assert.Equal(@"C:\Program Files\GlobalTool\global-tool.exe", seed.CleanCommandFile);
        Assert.Equal(600, seed.CleanCommandTimeoutSec);
    }
}
