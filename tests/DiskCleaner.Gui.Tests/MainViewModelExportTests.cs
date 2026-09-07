using System.IO;
using System.Text;
using System.Threading.Tasks;
using DiskCleaner.Core.Reports;
using DiskCleaner.Gui.Services;
using DiskCleaner.Gui.ViewModels;

namespace DiskCleaner.Gui.Tests;

public class MainViewModelExportTests
{
    [Fact]
    public void ExportCommand_DisabledUntilAnalysisHasItems()
    {
        var (vm, _, _) = CreateScreen();

        Assert.False(vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExportCommand_EnabledAfterAnalysis()
    {
        var (vm, fake, _) = CreateScreen();
        fake.Handler = _ => SamplePlan.Build();

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.True(vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Export_DefaultFormatIsMarkdown_DefaultFileNameEndsWithMd()
    {
        var (vm, fake, dialog) = CreateScreen();
        fake.Handler = _ => SamplePlan.Build();

        await vm.AnalyzeCommand.ExecuteAsync(null);
        Assert.Equal(PlanExportFormat.Markdown, vm.SelectedExportFormat!.Format);

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(".md", dialog.DefaultExtension);
        Assert.Contains("Markdown (*.md)", dialog.Filter);
        Assert.EndsWith(".md", dialog.DefaultFileName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("JSON (*.json)|*.json", PlanExportFormat.Json, ".json")]
    [InlineData("CSV (*.csv)|*.csv", PlanExportFormat.Csv, ".csv")]
    [InlineData("Markdown (*.md)|*.md", PlanExportFormat.Markdown, ".md")]
    public async Task Export_SelectedFormatDrivesDialogFilterAndExtension(
        string filterText,
        PlanExportFormat format,
        string extension)
    {
        var (vm, fake, dialog) = CreateScreen();
        fake.Handler = _ => SamplePlan.Build();
        vm.SelectedExportFormat = vm.ExportFormats.Single(f => f.Format == format);

        await vm.AnalyzeCommand.ExecuteAsync(null);
        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(extension, dialog.DefaultExtension);
        Assert.Equal(filterText, dialog.Filter);
        Assert.EndsWith(extension, dialog.DefaultFileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_Markdown_WritesPlanFileAndUpdatesStatus()
    {
        var (vm, fake, dialog) = CreateScreen();
        fake.Handler = _ => SamplePlan.Build();
        vm.SelectedExportFormat = vm.ExportFormats.Single(f => f.Format == PlanExportFormat.Markdown);
        var path = TempFilePath(".md");
        dialog.Result = path;

        await vm.AnalyzeCommand.ExecuteAsync(null);
        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("# План очистки — DiskCleaner", text);
        Assert.Contains("npm cache", text);
        Assert.Contains("Менеджеры пакетов — npm cache", text);
        Assert.Contains(path, vm.StatusText);
    }

    [Fact]
    public async Task Export_Json_WritesValidUtf8WithCyrillic()
    {
        var (vm, fake, dialog) = CreateScreen();
        fake.Handler = _ => SamplePlan.Build();
        vm.SelectedExportFormat = vm.ExportFormats.Single(f => f.Format == PlanExportFormat.Json);
        var path = TempFilePath(".json");
        dialog.Result = path;

        await vm.AnalyzeCommand.ExecuteAsync(null);
        await vm.ExportCommand.ExecuteAsync(null);

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("\"diskcleaner.plan\"", text);
        Assert.Contains("npm cache", text);
        Assert.Contains("Менеджеры пакетов", text);
        Assert.DoesNotContain("\\u043", text);
    }

    [Fact]
    public async Task Export_Csv_WritesBomUtf8WithSemicolonColumns()
    {
        var (vm, fake, dialog) = CreateScreen();
        fake.Handler = _ => SamplePlan.Build();
        vm.SelectedExportFormat = vm.ExportFormats.Single(f => f.Format == PlanExportFormat.Csv);
        var path = TempFilePath(".csv");
        dialog.Result = path;

        await vm.AnalyzeCommand.ExecuteAsync(null);
        await vm.ExportCommand.ExecuteAsync(null);

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "CSV записывается с UTF-8 BOM для корректного открытия в Excel.");

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.StartsWith("Категория;Группа;Объект;Путь", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_Cancel_DoesNotWriteFile()
    {
        var (vm, fake, dialog) = CreateScreen();
        fake.Handler = _ => SamplePlan.Build();
        var path = TempFilePath(".md");

        await vm.AnalyzeCommand.ExecuteAsync(null);
        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(".md", dialog.DefaultExtension);
        Assert.False(File.Exists(path));
        Assert.DoesNotContain(path, vm.StatusText);
    }

    private static string TempFilePath(string extension) =>
        Path.Combine(Path.GetTempPath(), "DiskCleaner.Gui.Tests", Guid.NewGuid().ToString("N") + extension);

    private static (MainViewModel Vm, FakeAnalysisCoordinator Fake, FakeSaveFileDialog Dialog) CreateScreen()
    {
        var fake = new FakeAnalysisCoordinator();
        var dialog = new FakeSaveFileDialog();
        return (MainViewModelFactory.Create(fake, dialog), fake, dialog);
    }

    private sealed class FakeSaveFileDialog : ISaveFileDialogService
    {
        public string? Result { get; set; }

        public string? DefaultExtension { get; private set; }

        public string? DefaultFileName { get; private set; }

        public string? Filter { get; private set; }

        public string? Show(string title, string defaultFileName, string defaultExtension, string filter)
        {
            DefaultFileName = defaultFileName;
            DefaultExtension = defaultExtension;
            Filter = filter;
            return Result;
        }
    }
}
