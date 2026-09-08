using System.Text.RegularExpressions;
using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Tests;

/// <summary>
/// Тесты массового удаления MSI-компонентов Windows SDK одной версии (фаза 23, модуль 04,
/// FR-4.7/4.8/4.9/4.14): изоляция по общему DisplayVersion (26100 не затрагивается при удалении
/// 19041), последовательное удаление GUID с перечитыванием инвентаря между проходами
/// («вернувшиеся» записи удаляются повторно), идемпотентный повторный прогон и зачистка
/// осиротевших каталогов Include/Lib/bin и Package Cache. Реестр моделируется изменяемым
/// инвентарём, деинсталлятор — фиктивным runner-ом с управляемыми exit-кодами.
/// </summary>
public class SdkBulkUninstallTests
{
    private const string Version19041 = "10.1.19041.5609";
    private const string Version26100 = "10.1.26100.1";

    private static InstalledApp SdkApp(string name, string version, string code, string? location = null) => new()
    {
        ProductCode = "{" + code + "}",
        ScopeKey = nameof(InstalledAppScope.CurrentUser),
        DisplayName = name,
        DisplayVersion = version,
        Publisher = "Microsoft Corporation",
        UninstallString = $"MsiExec.exe /X{{{code}}}",
        InstallLocation = location
    };

    [Fact]
    public void FamilyCatalog_IsolatesComponentsByDisplayVersion()
    {
        var apps = new[]
        {
            SdkApp("Windows SDK Desktop Tools x64", Version19041, "a".PadRight(32, '0').ToUpperInvariant()),
            SdkApp("Windows SDK for Desktop Apps x64", Version19041, "b".PadRight(32, '0').ToUpperInvariant()),
            SdkApp("Windows SDK Desktop Tools x64", Version26100, "c".PadRight(32, '0').ToUpperInvariant()),
            SdkApp("Java", Version19041, "d".PadRight(32, '0').ToUpperInvariant())
        };

        var components = WindowsSdkFamily.ComponentsOfVersion(apps, Version19041);

        Assert.Equal(2, components.Count);
        Assert.All(components, a => Assert.Equal(Version19041, a.DisplayVersion));
        Assert.Contains(components, a => a.DisplayName.StartsWith("Windows SDK"));
        Assert.DoesNotContain(components, a => a.DisplayName == "Java");
        Assert.DoesNotContain(components, a => a.DisplayVersion == Version26100);

        var versions = WindowsSdkFamily.FindVersions(apps);
        Assert.Contains(Version19041, versions);
        Assert.Contains(Version26100, versions);
    }

    [Fact]
    public async Task UninstallVersion_RemovesAllComponents_AndKeepsAnotherVersionIntact()
    {
        using var root = new TempRoot();
        var codeA = "11111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var codeB = "11111111-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        var code26100 = "11111111-cccc-cccc-cccc-cccccccccccc";
        var inventory = new MutableInventory(
            SdkApp("Windows SDK Desktop Tools x64", Version19041, codeA),
            SdkApp("Windows SDK for Desktop Apps x64", Version19041, codeB),
            SdkApp("Windows SDK Desktop Tools x64", Version26100, code26100));
        var runner = new InventoryCommandRunner(inventory);

        var service = Service(root, inventory, runner);
        var report = await service.UninstallVersionAsync(Version19041, readInventory: inventory.Read);

        Assert.True(report.FullyRemoved);
        Assert.Empty(report.RemainingProductCodes);
        Assert.Equal(2, report.OkCount);
        Assert.Equal(0, report.FailedCount);
        Assert.Equal(1, report.PassesUsed);
        Assert.Equal(2, report.Components.Count);

        Assert.All(runner.MsiexecInvocations(), args => Assert.Contains("/qn /norestart", args, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, runner.MsiexecInvocations().Count);
        Assert.DoesNotContain(runner.Invocations, i =>
            i.Arguments.Contains(code26100, StringComparison.OrdinalIgnoreCase));

        Assert.Single(inventory.Apps, a => a.DisplayVersion == Version26100);
        Assert.DoesNotContain(inventory.Apps, a => a.DisplayVersion == Version19041);
    }

    [Fact]
    public async Task UninstallVersion_AlreadyRemovedVersion_IsIdempotent_NoPassesNoErrors()
    {
        using var root = new TempRoot();
        var inventory = new MutableInventory(
            SdkApp("Windows SDK Desktop Tools x64", Version26100, "44444444-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var runner = new InventoryCommandRunner(inventory);

        var service = Service(root, inventory, runner);
        var report = await service.UninstallVersionAsync(Version19041, readInventory: inventory.Read);

        Assert.True(report.FullyRemoved);
        Assert.Equal(0, report.PassesUsed);
        Assert.Empty(report.Components);
        Assert.Equal(0, report.FailedCount);
    }

    [Fact]
    public async Task UninstallVersion_ReturnedComponent_IsRetriedInNextPass()
    {
        using var root = new TempRoot();
        var codeA = "22222222-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var codeReturned = "22222222-dddd-dddd-dddd-dddddddddddd";
        var inventory = new MutableInventory(
            SdkApp("Windows SDK Desktop Tools x64", Version19041, codeA),
            SdkApp("Windows SDK Desktop Tools x64", Version19041, codeReturned));
        var runner = new InventoryCommandRunner(inventory);
        runner.ReturnOnceWithoutDeleting(codeReturned);

        var service = Service(root, inventory, runner);
        var report = await service.UninstallVersionAsync(Version19041, readInventory: inventory.Read);

        Assert.True(report.FullyRemoved);
        Assert.Equal(2, report.PassesUsed);
        Assert.Equal(3, report.Components.Count);

        var returned = report.Components.Where(c => c.ProductCode.Contains(codeReturned, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(2, returned.Count);
        Assert.Equal(1, returned[0].Pass);
        Assert.Equal(2, returned[1].Pass);
        Assert.All(returned, c => Assert.True(c.IsOk));
    }

    [Fact]
    public async Task UninstallVersion_AfterRemoval_CleansOrphanedKitsFolders_AndPackageCache()
    {
        using var root = new TempRoot();
        var codeA = "33333333-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var codeB = "33333333-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        var code26100 = "33333333-cccc-cccc-cccc-cccccccccccc";

        var kitsRoot = Path.Combine(root.ProgramFilesX86, "Windows Kits", "10");
        CreateKitsVersion(kitsRoot, "10.0.19041.0");
        CreateKitsVersion(kitsRoot, "10.0.26100.0");

        var cacheRoot = PackageCacheCleaner.DefaultCacheRoot(root.ProgramData);
        Directory.CreateDirectory(Path.Combine(cacheRoot, codeA));
        Directory.CreateDirectory(Path.Combine(cacheRoot, codeB));
        Directory.CreateDirectory(Path.Combine(cacheRoot, code26100));

        var inventory = new MutableInventory(
            SdkApp("Windows SDK Desktop Tools x64", Version19041, codeA),
            SdkApp("Windows SDK for Desktop Apps x64", Version19041, codeB),
            SdkApp("Windows SDK Desktop Tools x64", Version26100, code26100,
                Path.Combine(kitsRoot, "Include", "10.0.26100.0")));
        var runner = new InventoryCommandRunner(inventory);

        var service = Service(
            root,
            inventory,
            runner,
            cleanupRunner: new ElevatedScenarioRunner(),
            programFilesX86Root: root.ProgramFilesX86);

        var report = await service.UninstallVersionAsync(Version19041, readInventory: inventory.Read);

        Assert.True(report.FullyRemoved);
        Assert.Equal(3, report.FolderCleanups.Count(r => r.Removed));
        Assert.Equal(2, report.PackageCacheCleanups.Count(r => r.Removed));

        Assert.False(Directory.Exists(Path.Combine(kitsRoot, "Include", "10.0.19041.0")));
        Assert.False(Directory.Exists(Path.Combine(kitsRoot, "Lib", "10.0.19041.0")));
        Assert.False(Directory.Exists(Path.Combine(kitsRoot, "bin", "10.0.19041.0")));
        Assert.True(Directory.Exists(Path.Combine(kitsRoot, "Include", "10.0.26100.0")));

        Assert.False(Directory.Exists(Path.Combine(cacheRoot, codeA)));
        Assert.False(Directory.Exists(Path.Combine(cacheRoot, codeB)));
        Assert.True(Directory.Exists(Path.Combine(cacheRoot, code26100)));
    }

    [Fact]
    public void OrphanScanner_KeepsFoldersReferencedByRemainingApps()
    {
        using var root = new TempRoot();
        var kitsRoot = Path.Combine(root.ProgramFilesX86, "Windows Kits", "10");
        CreateKitsVersion(kitsRoot, "10.0.19041.0");
        CreateKitsVersion(kitsRoot, "10.0.26100.0");

        var remaining = new[]
        {
            SdkApp("Windows SDK Desktop Tools x64", Version26100, "44444444-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                Path.Combine(kitsRoot, "Include", "10.0.26100.0"))
        };

        var orphans = new WindowsKitsOrphanScanner()
            .FindOrphanedVersionFolders(kitsRoot, remaining, Version19041);

        Assert.Equal(3, orphans.Count);
        Assert.All(orphans, path => Assert.Contains("10.0.19041.0", path, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(orphans, path => path.Contains("10.0.26100.0", StringComparison.OrdinalIgnoreCase));
    }

    private static SdkBulkUninstallService Service(
        TempRoot root,
        MutableInventory inventory,
        ICommandRunner commandRunner,
        IElevatedRunner? cleanupRunner = null,
        string? programFilesRoot = null,
        string? programFilesX86Root = null)
    {
        var executor = new UninstallExecutionService(
            elevatedRunner: new NeverElevatedRunner(),
            commandRunner: commandRunner,
            programDataRoot: root.ProgramData);

        return new SdkBulkUninstallService(
            executor: executor,
            cleanupRunner: cleanupRunner ?? new ElevatedScenarioRunner(),
            programDataRoot: root.ProgramData,
            programFilesRoot: programFilesRoot ?? root.ProgramFiles,
            programFilesX86Root: programFilesX86Root ?? root.ProgramFilesX86);
    }

    private static void CreateKitsVersion(string kitsRoot, string version)
    {
        foreach (var type in WindowsKitsOrphanScanner.TypeRootNames)
        {
            var dir = Path.Combine(kitsRoot, type, version);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "file.txt"), "x");
        }
    }

    private sealed class NeverElevatedRunner : IElevatedRunner
    {
        public Task<ElevatedJournal> RunAsync(ElevatedScenario scenario, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Пользовательский шаг не должен исполняться через elevated-процесс.");
    }
}

internal sealed class MutableInventory
{
    public MutableInventory(params InstalledApp[] apps)
    {
        Apps = apps.ToList();
    }

    public List<InstalledApp> Apps { get; }

    public IReadOnlyList<InstalledApp> Read() => Apps.ToList();
}

/// <summary>
/// Фиктивный деинсталлятор MSI: по умолчанию возвращает exit 0 и удаляет запись из инвентаря
/// (модель «msiexec удалил продукт и запись»). Часть компонентов может «вернуться» — первый
/// вызов возвращает 0 без удаления записи (реальный кейс FR-4.8), повторный удаляет.
/// </summary>
internal sealed class InventoryCommandRunner : ICommandRunner
{
    private readonly MutableInventory _inventory;
    private readonly HashSet<string> _returnedOnce = new(StringComparer.OrdinalIgnoreCase);

    public InventoryCommandRunner(MutableInventory inventory)
    {
        _inventory = inventory;
    }

    public IReadOnlyList<CommandDefinition> Invocations { get; } = new List<CommandDefinition>();

    public void ReturnOnceWithoutDeleting(string productCode) =>
        _returnedOnce.Add(Normalize(productCode));

    public Task<CommandResult> RunAsync(CommandDefinition command, CancellationToken cancellationToken = default)
    {
        ((List<CommandDefinition>)Invocations).Add(command);
        var code = Normalize(GuidIn(command.Arguments) ?? string.Empty);

        if (_returnedOnce.Contains(code))
        {
            _returnedOnce.Remove(code);
            return Task.FromResult(new CommandResult(0, string.Empty, false));
        }

        _inventory.Apps.RemoveAll(app => Normalize(app.ProductCode) == code);
        return Task.FromResult(new CommandResult(0, string.Empty, false));
    }

    public IReadOnlyList<string> MsiexecInvocations() =>
        Invocations.Where(i => i.FileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Arguments)
            .ToList();

    private static string Normalize(string code) => code.Trim('{', '}').ToUpperInvariant();

    private static string? GuidIn(string text) =>
        Regex.Match(text, @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")
            .Value;
}
