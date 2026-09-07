using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Tests;

/// <summary>
/// Unit-тесты планировщика действий очистки кэшей (фаза 2 модуля 02, FR-2.5–2.7, 2.9–2.12):
/// матрица «команда vs прямое удаление», защитный порог свободного места, split Gradle,
/// whitelist VS Code и карточка действия.
/// </summary>
public class CacheCleanPlannerTests
{
    private static CacheCleanPlanner Planner(
        TempRoot root,
        IDriveSpaceService? space = null) =>
        new(new FakeEnvironment(root), space ?? new FakeDriveSpace(0.5));

    private static CleanupItem Cache(
        string key,
        string? path,
        CleanupCategory category = CleanupCategory.Cache,
        CleanupRisk risk = CleanupRisk.Low,
        string? group = null,
        CleanupTarget target = CleanupTarget.Directory,
        bool allowDirectDelete = true,
        bool commandOnly = false,
        bool isOrphan = false,
        bool inUse = false,
        string? cleanCommand = null,
        string? cleanFile = null,
        string? cleanArgs = null,
        string? description = null,
        string? warning = null,
        long? sizeBytes = null)
    {
        return new CleanupItem
        {
            Key = key,
            Path = path is null ? null : Path.GetFullPath(path),
            DisplayName = key,
            GroupName = group,
            Category = category,
            Risk = risk,
            Target = target,
            AllowDirectDelete = allowDirectDelete,
            CommandOnly = commandOnly,
            IsOrphan = isOrphan,
            InUse = inUse,
            CleanCommand = cleanCommand,
            CleanCommandFile = cleanFile,
            CleanCommandArgs = cleanArgs,
            Description = description,
            Warning = warning,
            SizeBytes = sizeBytes
        };
    }

    [Fact]
    public void BuildPlan_NativeCommandWithDirectFallbackAllowed_PrefersNativeWithFallback()
    {
        using var root = new TempRoot();
        var item = Cache(
            "npm-cache:path",
            root.Combine("npm"),
            group: "npm",
            cleanCommand: "cmd.exe /c npm cache clean --force",
            cleanFile: "cmd.exe",
            cleanArgs: "/c npm cache clean --force",
            warning: "Пакеты будут загружены заново.");

        var plan = Planner(root).BuildPlan([item]);
        var action = Assert.Single(plan.Actions);

        Assert.Equal(CacheCleanMethod.NativeWithDirectFallback, action.Method);
        Assert.True(action.Cleanable);
        Assert.Equal(CacheConsent.Auto, action.Consent);
        Assert.Equal("Пакеты будут загружены заново.", action.ConsequencesText);
        Assert.Single(plan.CleanableActions);
    }

    [Fact]
    public void BuildPlan_CommandOnlyWithoutPath_UsesNativeCommand()
    {
        using var root = new TempRoot();
        var item = Cache(
            "docker-prune:(command)",
            path: null,
            risk: CleanupRisk.Medium,
            group: "Docker",
            allowDirectDelete: false,
            commandOnly: true,
            cleanCommand: "docker system prune -af",
            cleanFile: "docker");

        var plan = Planner(root).BuildPlan([item]);
        var action = Assert.Single(plan.Actions);

        Assert.Equal(CacheCleanMethod.NativeCommand, action.Method);
        Assert.True(action.Cleanable);
        Assert.Equal("docker system prune -af", action.NativeCommandText);
    }

    [Fact]
    public void BuildPlan_NoCommandAndDirectAllowed_UsesDirectDelete()
    {
        using var root = new TempRoot();
        var item = Cache("gradle-caches:path", root.Combine(".gradle", "caches"), group: "Gradle");

        var plan = Planner(root).BuildPlan([item]);
        var action = Assert.Single(plan.Actions);

        Assert.Equal(CacheCleanMethod.DirectDelete, action.Method);
        Assert.True(action.Cleanable);
        Assert.Null(action.NativeCommandText);
    }

    [Fact]
    public void BuildPlan_OrphanCache_IsDirectDeleteOnly()
    {
        using var root = new TempRoot();
        var item = Cache("npm-cache:orphan", root.Combine("LocalAppData", "npm-cache"), group: "npm", isOrphan: true);

        var plan = Planner(root).BuildPlan([item]);
        var action = Assert.Single(plan.Actions);

        Assert.Equal(CacheCleanMethod.DirectDelete, action.Method);
        Assert.Null(action.NativeCommandText);
        Assert.True(action.Cleanable);
    }

    [Fact]
    public void BuildPlan_NativeCommandWithoutDirect_DoesNotOfferFallback()
    {
        using var root = new TempRoot();
        var item = Cache(
            "npm-cache:path",
            root.Combine("npm"),
            group: "npm",
            allowDirectDelete: false,
            cleanFile: "cmd.exe",
            cleanCommand: "cmd.exe /c npm cache clean --force");

        var plan = Planner(root).BuildPlan([item]);
        var action = Assert.Single(plan.Actions);

        Assert.Equal(CacheCleanMethod.NativeCommand, action.Method);
        Assert.True(action.Cleanable);
    }

    [Fact]
    public void BuildPlan_NoCommandAndDirectForbidden_IsNotAllowed()
    {
        using var root = new TempRoot();
        var item = Cache("orphan:blocked", root.Combine("caches"), group: "npm", allowDirectDelete: false);

        var plan = Planner(root).BuildPlan([item]);

        var action = Assert.Single(plan.Actions);
        Assert.Equal(CacheCleanMethod.NotAllowed, action.Method);
        Assert.False(action.Cleanable);
        Assert.NotNull(action.NotAllowedReason);
        Assert.Equal(1, plan.NotAllowedCount);
        Assert.Empty(plan.CleanableActions);
    }

    [Fact]
    public void BuildPlan_InUseCache_IsDeferredAndNeverInAutoPlan()
    {
        using var root = new TempRoot();
        var item = Cache("vscode-logs:path", Path.Combine(root.AppData, "Code", "logs"), group: "VS Code", inUse: true);

        var plan = Planner(root).BuildPlan([item]);
        var action = Assert.Single(plan.Actions);

        Assert.Equal(CacheCleanMethod.DeferredInUse, action.Method);
        Assert.True(action.IsHeld);
        Assert.Equal(CleanupDefaultAction.Keep, action.DefaultAction);
        Assert.Empty(plan.CleanableActions);
    }

    [Fact]
    public void BuildPlan_BelowLowSpaceThreshold_MarksCachesDoNotTouch()
    {
        using var root = new TempRoot();
        var item = Cache("gradle-caches:path", root.Combine(".gradle", "caches"), group: "Gradle");
        var planner = Planner(root, new FakeDriveSpace(0.10));

        var plan = planner.BuildPlan([item]);
        var action = Assert.Single(plan.Actions);

        Assert.Equal(CacheCleanMethod.DirectDelete, action.Method);
        Assert.True(action.IsHeld);
        Assert.Contains("«не трогать»", action.HoldReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CleanupDefaultAction.Keep, action.DefaultAction);
        Assert.Empty(plan.CleanableActions);
        Assert.NotEmpty(plan.LowSpaceDrives);
        Assert.NotNull(plan.GuardNote);
        Assert.Contains("Защитный режим", plan.GuardNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPlan_EnoughFreeSpace_KeepsCachesCleanable()
    {
        using var root = new TempRoot();
        var item = Cache("gradle-caches:path", root.Combine(".gradle", "caches"), group: "Gradle");

        var plan = Planner(root, new FakeDriveSpace(0.40)).BuildPlan([item]);

        var action = Assert.Single(plan.Actions);
        Assert.True(action.Cleanable);
        Assert.False(action.IsHeld);
        Assert.Single(plan.CleanableActions);
        Assert.Empty(plan.LowSpaceDrives);
        Assert.Null(plan.GuardNote);
    }

    [Fact]
    public void BuildPlan_LowSpaceOnOneDrive_HoldsOnlyThatDrive()
    {
        using var root = new TempRoot();
        var onC = Cache("npm-cache:path", root.Combine("npm"), group: "npm");
        const string dPath = @"D:\npm-cache";
        var onD = Cache("npm-cache:d", dPath, group: "npm");

        var fractions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetPathRoot(root.Path)!] = 0.08,
            [@"D:\"] = 0.6
        };
        var planner = Planner(root, new FakeDriveSpace(fractions));

        var plan = planner.BuildPlan([onC, onD]);

        var cAction = plan.Actions.Single(a => a.Item.Path!.StartsWith(root.Path, StringComparison.OrdinalIgnoreCase));
        var dAction = plan.Actions.Single(a => string.Equals(a.Item.Path, dPath, StringComparison.OrdinalIgnoreCase));

        Assert.True(cAction.IsHeld);
        Assert.False(dAction.IsHeld);
        Assert.Single(plan.LowSpaceDrives);
    }

    [Fact]
    public async Task BuildPlan_GradleSubfolders_ProduceSeparateActions_WithJdksAskConsent()
    {
        using var root = new TempRoot();
        var environment = new FakeEnvironment(root);
        var catalog = new CacheCatalogService(
            environment: environment,
            runner: new FakeCommandRunner(),
            locator: new FakeCommandLocator());

        var seeds = await catalog.BuildSeedsAsync();
        var plan = Planner(root).BuildPlan(seeds);

        var gradleActions = plan.Actions
            .Where(a => string.Equals(a.Item.GroupName, "Gradle", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(4, gradleActions.Count);

        var jdks = gradleActions.Single(a => a.Item.Key.StartsWith("gradle-jdks:", StringComparison.Ordinal));
        Assert.Equal(CacheConsent.Ask, jdks.Consent);
        Assert.Equal(CleanupDefaultAction.Ask, jdks.DefaultAction);
        Assert.Equal(CacheCleanMethod.DirectDelete, jdks.Method);

        var otherIds = gradleActions
            .Where(a => !a.Item.Key.StartsWith("gradle-jdks:", StringComparison.Ordinal))
            .Select(a => a.Item.Key[..(a.Item.Key.IndexOf(':') + 1)])
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { "gradle-caches:", "gradle-daemon:", "gradle-wrapper-dists:" },
            otherIds);

        Assert.All(
            gradleActions.Where(a => !a.Item.Key.StartsWith("gradle-jdks:", StringComparison.Ordinal)),
            a => Assert.Equal(CacheConsent.Auto, a.Consent));
    }

    [Fact]
    public void BuildPlan_VSCode_AllowsOnlyWhitelistedCacheSubfolders()
    {
        using var root = new TempRoot();
        var codeRoot = Path.Combine(root.AppData, "Code");

        var cache = Cache("vscode-cache:path", Path.Combine(codeRoot, "Cache"), group: "VS Code");
        var user = Cache("vscode-user:path", Path.Combine(codeRoot, "User"), group: "VS Code");
        var extensions = Cache("vscode-ext:path", Path.Combine(codeRoot, "extensions"), group: "VS Code");
        var settingsFile = Cache(
            "vscode-settings:file",
            Path.Combine(codeRoot, "Cache", "settings.json"),
            group: "VS Code",
            target: CleanupTarget.File);
        var wholeRoot = Cache("vscode-root:path", codeRoot, group: "VS Code");

        var plan = Planner(root).BuildPlan([cache, user, extensions, settingsFile, wholeRoot]);

        var allowed = plan.Actions.Single(a => a.Item.Key.StartsWith("vscode-cache:", StringComparison.Ordinal));
        Assert.Equal(CacheCleanMethod.DirectDelete, allowed.Method);
        Assert.True(allowed.Cleanable);

        var blocked = plan.Actions
            .Where(a => a.Method == CacheCleanMethod.NotAllowed)
            .ToList();
        Assert.Equal(4, blocked.Count);
        Assert.All(blocked, a =>
        {
            Assert.False(a.Cleanable);
            Assert.Contains("FR-2.10", a.NotAllowedReason, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(4, plan.NotAllowedCount);
        Assert.Single(plan.CleanableActions);
    }

    [Fact]
    public void BuildPlan_UpdaterAndLeftoverPaths_AreExcludedAsLeftover()
    {
        using var root = new TempRoot();
        var updater = Cache("updater:path", root.Combine("SomeApp-updater"), group: "Обновления");
        var leftover = Cache("leftover:cfg", root.Combine("AppData", "RemovedApp"), category: CleanupCategory.Leftover);

        var plan = Planner(root).BuildPlan([updater, leftover]);

        var updaterAction = Assert.Single(plan.Actions);
        Assert.Equal(CacheCleanMethod.NotAllowed, updaterAction.Method);
        Assert.Contains("updater", updaterAction.NotAllowedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Leftover", updaterAction.NotAllowedReason, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(plan.Actions, a => a.Item.Key.StartsWith("leftover:", StringComparison.Ordinal));
        Assert.Equal(1, plan.OutOfScopeCount);
        Assert.Equal(1, plan.NotAllowedCount);
        Assert.Empty(plan.CleanableActions);
    }

    [Fact]
    public void BuildPlan_NonCacheCategories_AreOutOfScope()
    {
        using var root = new TempRoot();
        var installedApp = Cache(
            "app:x",
            root.Combine("ProgramFiles", "App"),
            category: CleanupCategory.InstalledApp,
            risk: CleanupRisk.High);
        var cache = Cache("npm-cache:path", root.Combine("npm"), group: "npm");

        var plan = Planner(root).BuildPlan([installedApp, cache]);

        Assert.Equal(1, plan.OutOfScopeCount);
        Assert.DoesNotContain(plan.Actions, a => a.Item.Key.StartsWith("app:", StringComparison.Ordinal));
        Assert.Single(plan.CleanableActions);
    }

    [Fact]
    public void BuildPlan_ActionCard_ExposesCommandSavingsConsequencesAndRestoreEstimate()
    {
        using var root = new TempRoot();
        var item = Cache(
            "npm-cache:path",
            root.Combine("npm"),
            group: "npm",
            cleanCommand: "cmd.exe /c npm cache clean --force",
            cleanFile: "cmd.exe",
            cleanArgs: "/c npm cache clean --force",
            description: "Кэш пакетов npm",
            warning: "При следующей установке пакеты будут загружены заново (трафик и время).",
            sizeBytes: 1_500_000_000);

        var plan = Planner(root).BuildPlan([item]);
        var action = Assert.Single(plan.Actions);

        Assert.Equal(CacheCleanMethod.NativeWithDirectFallback, action.Method);
        Assert.Equal("cmd.exe /c npm cache clean --force", action.NativeCommandText);
        Assert.Equal(1_500_000_000, action.EstimatedSavingsBytes);
        Assert.Contains("Освободит", action.SavingsText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(item.Warning, action.ConsequencesText);
        Assert.Contains("Повторная загрузка", action.RestoreEstimateText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CacheConsent.Auto, action.Consent);
    }

    [Fact]
    public void BuildPlan_RecreateHint_DescribesLocalRegenerationWithoutTraffic()
    {
        using var root = new TempRoot();
        var item = Cache(
            "vscode-logs:path",
            Path.Combine(root.AppData, "Code", "logs"),
            group: "VS Code",
            warning: "Пересоздаются при следующем запуске.",
            sizeBytes: 120_000_000);

        var plan = Planner(root).BuildPlan([item]);
        var action = Assert.Single(plan.Actions);

        Assert.Equal(CacheCleanMethod.DirectDelete, action.Method);
        Assert.Contains("Пересоздаётся автоматически", action.RestoreEstimateText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Повторная загрузка", action.RestoreEstimateText, StringComparison.OrdinalIgnoreCase);
    }
}
