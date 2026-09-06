using System.Diagnostics;
using System.Text.Json;
using System.Text;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Tests;

public class CliE2eTests
{
    [Fact]
    public async Task Scan_WithTargets_WritesJsonReport_DeletesNothing()
    {
        using var root = new TempRoot();
        var cacheDir = root.Combine("npm-cache");
        root.CreateFile("npm-cache\\a.bin", 300);
        var targetsFile = WriteTargets(root, cacheDir);

        var (exitCode, output) = await RunCliAsync("--scan", $"--targets \"{targetsFile}\"", $"--json \"{Path.Combine(root.Path, "scan.json")}\"");

        Assert.Equal(0, exitCode);
        Assert.True(Directory.Exists(cacheDir), "--scan не должен удалять объекты.");

        var report = ReportJson.ReadFile(Path.Combine(root.Path, "scan.json"));
        Assert.Equal("scan", report.Mode);
        Assert.Equal(1, report.Summary.TotalItems);
        Assert.Single(report.Items, i => i.Path == cacheDir && i.Category == "Cache");
    }

    [Fact]
    public async Task Clean_DryRun_DeletesNothing_WritesDryRunReport()
    {
        using var root = new TempRoot();
        var cacheDir = root.Combine("npm-cache");
        root.CreateFile("npm-cache\\a.bin", 500);
        var targetsFile = WriteTargets(root, cacheDir);

        var (exitCode, output) = await RunCliAsync(
            "--clean",
            $"--targets \"{targetsFile}\"",
            "--category Cache",
            "--dry-run",
            $"--json \"{Path.Combine(root.Path, "dry.json")}\"");

        Assert.Equal(0, exitCode);
        Assert.True(Directory.Exists(cacheDir), "dry-run не должен удалять объекты.");

        var report = ReportJson.ReadFile(Path.Combine(root.Path, "dry.json"));
        Assert.Equal("dry-run", report.Mode);
        Assert.Single(report.Items);
        Assert.Equal(500, report.Summary.FreedBytes);
    }

    [Fact]
    public async Task Clean_WithYes_RemovesCache_WritesCleanReport()
    {
        using var root = new TempRoot();
        var cacheDir = root.Combine("npm-cache");
        root.CreateFile("npm-cache\\a.bin", 700);
        var targetsFile = WriteTargets(root, cacheDir);

        var (exitCode, output) = await RunCliAsync(
            "--clean",
            $"--targets \"{targetsFile}\"",
            "--category Cache",
            "--yes",
            $"--json \"{Path.Combine(root.Path, "clean.json")}\"");

        Assert.Equal(0, exitCode);
        Assert.False(Directory.Exists(cacheDir), "--clean --yes должен удалить объект.");

        var report = ReportJson.ReadFile(Path.Combine(root.Path, "clean.json"));
        Assert.Equal("clean", report.Mode);
        Assert.Equal(700, report.Summary.FreedBytes);
        Assert.Single(report.Removed, r => r.Path == cacheDir);
    }

    [Fact]
    public async Task Clean_WithoutYes_Refuses()
    {
        using var root = new TempRoot();
        var cacheDir = root.Combine("npm-cache");
        root.CreateFile("npm-cache\\a.bin", 700);
        var targetsFile = WriteTargets(root, cacheDir);

        var (exitCode, output) = await RunCliAsync(
            "--clean",
            $"--targets \"{targetsFile}\"",
            "--category Cache");

        Assert.NotEqual(0, exitCode);
        Assert.True(Directory.Exists(cacheDir));
    }

    [Fact]
    public void ReportJson_Utf8Cyrillic_RoundTrips()
    {
        using var root = new TempRoot();
        var document = new ReportDocument
        {
            Mode = "clean",
            Summary = new ReportSummaryJson { FreedBytes = 42, FreedText = "42 Б", TotalItems = 1, DeletedItems = 1 },
            Items =
            [
                new ReportItemJson
                {
                    Key = "test:1",
                    Name = "Кэш пакетов — «пример»",
                    Group = "кэш-группа",
                    Category = "Cache",
                    CategoryText = "Кэши",
                    Path = root.Path + "\\кэш-пакетов",
                    Outcome = "DirectDeleted",
                    FreedBytes = 42
                }
            ]
        };

        var path = Path.Combine(root.Path, "отчёт.json");
        ReportJson.WriteFile(path, document);

        var bytes = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("Кэш пакетов", text);
        Assert.DoesNotContain("\\u043", text);

        var loaded = ReportJson.ReadFile(path);
        Assert.Equal("Кэш пакетов — «пример»", loaded.Items[0].Name);
        Assert.Equal("clean", loaded.Mode);
    }

    [Fact]
    public async Task Scan_WithCategoryFilter_LimitsReport()
    {
        using var root = new TempRoot();
        var cacheDir = root.Combine("npm-cache");
        var leftoverDir = root.Combine("updater-folder");
        root.CreateFile("npm-cache\\a.bin", 300);
        root.CreateFile("updater-folder\\b.bin", 200);

        var targetsFile = Path.Combine(root.Path, "targets.json");
        File.WriteAllText(
            targetsFile,
            $$"""
              [
                { "path": "{{cacheDir.Replace("\\", "\\\\")}}", "category": "cache", "risk": "low", "name": "npm cache", "group": "npm" },
                { "path": "{{leftoverDir.Replace("\\", "\\\\")}}", "category": "leftover", "risk": "medium", "name": "Updater", "group": "app" }
              ]
              """,
            new UTF8Encoding(false));

        var (exitCode, _) = await RunCliAsync(
            "--scan",
            $"--targets \"{targetsFile}\"",
            "--category Cache",
            $"--json \"{Path.Combine(root.Path, "scan-filter.json")}\"");

        Assert.Equal(0, exitCode);
        var report = ReportJson.ReadFile(Path.Combine(root.Path, "scan-filter.json"));
        Assert.Equal(1, report.Summary.TotalItems);
        var category = Assert.Single(report.Categories);
        Assert.Equal("Cache", category.Category);
    }

    private static string WriteTargets(TempRoot root, string directory)
    {
        var file = Path.Combine(root.Path, "targets.json");
        File.WriteAllText(
            file,
            $$"""
              [
                { "path": "{{directory.Replace("\\", "\\\\")}}", "category": "cache", "risk": "low", "name": "npm cache", "group": "npm" }
              ]
              """,
            new UTF8Encoding(false));
        return file;
    }

    private static async Task<(int ExitCode, string Output)> RunCliAsync(params string[] args)
    {
        var exePath = FindCliExe();
        if (!File.Exists(exePath))
        {
            throw new InvalidOperationException($"CLI exe не найден: {exePath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = string.Join(' ', args),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await process.WaitForExitAsync(timeout.Token);

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        return (process.ExitCode, output + error);
    }

    private static string FindCliExe()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "Cli", "DiskCleaner.Cli.exe");
        return File.Exists(local) ? local : string.Empty;
    }
}
