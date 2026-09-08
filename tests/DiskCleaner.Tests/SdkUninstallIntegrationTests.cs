using System.Text.RegularExpressions;
using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Elevated;
using DiskCleaner.Core.Environment;
using DiskCleaner.Core.Uninstall;
using Microsoft.Win32;

namespace DiskCleaner.Tests;

/// <summary>
/// Integration-тесты фазы 23 (модуль 04): фикстуры ветки реестра Uninstall (изолированная HKCU),
/// каталогов <c>Windows Kits\10</c> (Include/Lib/bin) и <c>Package Cache</c>; последовательности
/// exit-кодов msiexec (0 и 1605), повторный прогон (идемпотентность) и проверка, что компоненты
/// другой версии SDK (26100) не затрагиваются ни при удалении, ни при зачистке.
/// </summary>
public class SdkUninstallIntegrationTests
{
    private const string Version19041 = "10.1.19041.5609";
    private const string Version26100 = "10.1.26100.1";

    private const string CodeA = "aaaaaaaa-1111-2222-3333-aaaaaaaaaaaa";
    private const string CodeB = "bbbbbbbb-1111-2222-3333-bbbbbbbbbbbb";
    private const string CodeX = "cccccccc-1111-2222-3333-cccccccccccc";

    [Fact]
    public async Task Sdk19041_RemovedCompletely_26100Untouched_IdempotentRerun()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TempRoot();
        var kitsRoot = Path.Combine(root.ProgramFilesX86, "Windows Kits", "10");
        CreateKitsVersion(kitsRoot, "10.0.19041.0");
        CreateKitsVersion(kitsRoot, "10.0.26100.0");

        var cacheRoot = PackageCacheCleaner.DefaultCacheRoot(root.ProgramData);
        var cacheDirA = Path.Combine(cacheRoot, CodeA);
        var cacheDirB = Path.Combine(cacheRoot, CodeB);
        var cacheDirX = Path.Combine(cacheRoot, CodeX);
        Directory.CreateDirectory(cacheDirA);
        Directory.CreateDirectory(cacheDirB);
        Directory.CreateDirectory(cacheDirX);

        using var registry = new SdkRegistryFixture();
        registry.AddSdkComponent("Windows SDK Desktop Tools x64", Version19041, CodeA);
        registry.AddSdkComponent("Windows SDK for Desktop Apps x64", Version19041, CodeB);
        registry.AddSdkComponent(
            "Windows SDK Desktop Tools x64",
            Version26100,
            CodeX,
            Path.Combine(kitsRoot, "Include", "10.0.26100.0"));

        var runner = new RegistryMsiexecRunner(registry, exitCodeByCode: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [CodeA] = 0,
            [CodeB] = 1605
        });

        var service = new SdkBulkUninstallService(
            executor: new UninstallExecutionService(
                elevatedRunner: new NeverElevatedRunner(),
                commandRunner: runner,
                programDataRoot: root.ProgramData),
            cleanupRunner: new ElevatedScenarioRunner(),
            programDataRoot: root.ProgramData,
            programFilesRoot: root.ProgramFiles,
            programFilesX86Root: root.ProgramFilesX86);

        var report = await service.UninstallVersionAsync(
            Version19041,
            readInventory: registry.Read);

        // Последовательность exit-кодов 1605/0: B — «не установлено» (не ошибка), A — успех.
        Assert.True(report.FullyRemoved);
        Assert.Empty(report.RemainingProductCodes);
        Assert.Equal(1, report.PassesUsed);
        Assert.Equal(2, report.Components.Count);
        Assert.Equal(2, report.OkCount);
        Assert.Equal(0, report.FailedCount);

        var byCodeA = Assert.Single(report.Components, c => c.ProductCode.Contains(CodeA, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(UninstallExecutionOutcome.Uninstalled, byCodeA.Outcome);
        Assert.Equal(0, byCodeA.ExitCode);
        var byCodeB = Assert.Single(report.Components, c => c.ProductCode.Contains(CodeB, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(UninstallExecutionOutcome.NotInstalled, byCodeB.Outcome);
        Assert.Equal(1605, byCodeB.ExitCode);

        // Реестр: записей 19041 нет; запись 26100 на месте.
        var remaining = registry.Read();
        Assert.DoesNotContain(remaining, a => a.DisplayVersion == Version19041);
        Assert.Contains(remaining, a => a.DisplayVersion == Version26100);

        // msiexec вызывался только для 19041-компонентов.
        Assert.Equal(2, runner.Invocations.Count);
        Assert.DoesNotContain(runner.Invocations, i => i.Arguments.Contains(CodeX, StringComparison.OrdinalIgnoreCase));

        // Зачистка: каталоги версии 19041 удалены, 26100 — нет; Package Cache очищен для A/B, для 26100 — нет.
        Assert.False(Directory.Exists(Path.Combine(kitsRoot, "Include", "10.0.19041.0")));
        Assert.False(Directory.Exists(Path.Combine(kitsRoot, "Lib", "10.0.19041.0")));
        Assert.False(Directory.Exists(Path.Combine(kitsRoot, "bin", "10.0.19041.0")));
        Assert.True(Directory.Exists(Path.Combine(kitsRoot, "Include", "10.0.26100.0")));
        Assert.False(Directory.Exists(cacheDirA));
        Assert.False(Directory.Exists(cacheDirB));
        Assert.True(Directory.Exists(cacheDirX));

        // Повторный прогон идемпотентен: версия уже удалена — успех без проходов и без ошибок.
        var second = await service.UninstallVersionAsync(Version19041, readInventory: registry.Read);

        Assert.True(second.FullyRemoved);
        Assert.Equal(0, second.PassesUsed);
        Assert.Empty(second.Components);
        Assert.Equal(0, second.FailedCount);
        Assert.Equal(2, runner.Invocations.Count);
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
            throw new InvalidOperationException("HKCU-запись не должна исполняться через elevated-процесс.");
    }

    /// <summary>
    /// Фикстура ветки реестра Uninstall в изолированном HKCU-каталоге: запись продукта и чтение
    /// инвентаря через <see cref="UninstallRegistryService"/> по этой ветке.
    /// </summary>
    private sealed class SdkRegistryFixture : IDisposable
    {
        private readonly string _rootPath;
        private readonly string _branch;

        public SdkRegistryFixture()
        {
            var guid = Guid.NewGuid().ToString("N");
            _rootPath = $@"Software\DiskCleaner.Tests\{guid}";
            _branch = _rootPath + @"\Uninstall";
        }

        public void AddSdkComponent(string name, string version, string code, string? location = null)
        {
            using var uninstall = Registry.CurrentUser.CreateSubKey(_branch);
            using var product = uninstall.CreateSubKey("{" + code + "}");
            product.SetValue("DisplayName", name);
            product.SetValue("DisplayVersion", version);
            product.SetValue("Publisher", "Microsoft Corporation");
            product.SetValue("UninstallString", $"MsiExec.exe /X{{{code}}}");
            if (location is not null)
            {
                product.SetValue("InstallLocation", location);
            }
        }

        public IReadOnlyList<InstalledApp> Read()
        {
            using var temp = new TempRoot();
            var environment = new FakeEnvironment(temp);
            var service = new UninstallRegistryService(
                environment,
                branches: [new RegistryBranchSpec(RegistryHiveKind.CurrentUser, _branch)]);
            return service.ReadInstalledApps();
        }

        public void DeleteProductCode(string code)
        {
            using var uninstall = Registry.CurrentUser.OpenSubKey(_branch, writable: true);
            uninstall?.DeleteSubKeyTree("{" + code + "}", throwOnMissingSubKey: false);
        }

        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// Фиктивный msiexec: возвращает заданный exit-код для каждого ProductCode и удаляет запись
    /// из ветки-фикстуры (имитация того, что msiexec снимает запись после успеха; для 1605 —
    /// продукта уже нет, запись убирается).
    /// </summary>
    private sealed class RegistryMsiexecRunner : ICommandRunner
    {
        private readonly SdkRegistryFixture _registry;
        private readonly IReadOnlyDictionary<string, int> _exitCodeByCode;

        public RegistryMsiexecRunner(
            SdkRegistryFixture registry,
            IReadOnlyDictionary<string, int> exitCodeByCode)
        {
            _registry = registry;
            _exitCodeByCode = exitCodeByCode;
        }

        public IReadOnlyList<CommandDefinition> Invocations { get; } = new List<CommandDefinition>();

        public Task<CommandResult> RunAsync(CommandDefinition command, CancellationToken cancellationToken = default)
        {
            ((List<CommandDefinition>)Invocations).Add(command);
            var code = GuidIn(command.Arguments)?.Trim('{', '}') ?? string.Empty;
            var exitCode = _exitCodeByCode.TryGetValue(code, out var value) ? value : 0;

            _registry.DeleteProductCode(code);
            return Task.FromResult(new CommandResult(exitCode, string.Empty, false));
        }

        private static string? GuidIn(string text) =>
            Regex.Match(text, @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")
                .Value;
    }
}
