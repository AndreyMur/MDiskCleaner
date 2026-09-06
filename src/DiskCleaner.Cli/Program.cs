using System.Text;
using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Localization;
using DiskCleaner.Core.Logging;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Cli;

public static class Program
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;
    public const int ExitNoSelection = 3;

    private const string CategorySeparators = ",;";

    private static readonly CleanupCategory[] BulkCleanableCategories =
    [
        CleanupCategory.Cache,
        CleanupCategory.Temp,
        CleanupCategory.RecycleBin,
        CleanupCategory.Leftover
    ];

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);

        CliOptions options;
        try
        {
            options = ParseArguments(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitUsage;
        }

        if (options.ShowHelp)
        {
            PrintHelp();
            return ExitOk;
        }

        if (options.ShowVersion)
        {
            var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0";
            Console.WriteLine("DiskCleaner.Cli " + version);
            return ExitOk;
        }

        try
        {
            return await RunAsync(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Ошибка: " + ex.Message);
            return ExitFailed;
        }
    }

    internal static async Task<int> RunAsync(CliOptions options)
    {
        DiskCleanerLog.Initialize();
        Serilog.Log.Information("DiskCleaner.Cli started: {Arguments}", string.Join(' ', options.OriginalArgs));

        if (options.Mode is null)
        {
            Console.Error.WriteLine("Укажите режим: --scan или --clean. Справка: --help");
            return ExitUsage;
        }

        var seeds = await LoadSeedsAsync(options);
        if (seeds.Count == 0)
        {
            Console.Error.WriteLine("Объекты для анализа не найдены.");
            return ExitNoSelection;
        }

        var analysis = new AnalysisService();
        var result = await analysis.AnalyzeAsync(seeds);
        var leaves = AnalysisService.DescendantLeaves(result.CategoryTree);

        if (options.Mode == CliMode.Scan)
        {
            return RunScanAsync(options, result);
        }

        return await RunCleanAsync(options, leaves);
    }

    private static int RunScanAsync(CliOptions options, AnalysisResult result)
    {
        var doc = options.Categories.Count == 0
            ? ReportDocumentBuilder.FromScan(result)
            : BuildFilteredScanDocument(options, result);

        WriteReport(options, doc);

        PrintScanSummary(result, doc);

        if (options.JsonPath is not null)
        {
            Console.WriteLine("JSON-отчёт: " + options.JsonPath);
        }

        return ExitOk;
    }

    private static ReportDocument BuildFilteredScanDocument(CliOptions options, AnalysisResult result)
    {
        var categorySet = ParseCategories(options.Categories)!;
        var filtered = result.Items
            .Where(i => !i.IsGroup && categorySet.Contains(i.Category))
            .ToList();

        var filteredResult = new AnalysisResult
        {
            CategoryTree = new TreeBuilder().Build(filtered),
            Items = filtered,
            Errors = result.Errors,
            Elapsed = result.Elapsed,
            InUseItems = filtered.Count(i => i.InUse),
            SkippedNonexistent = result.SkippedNonexistent
        };

        return ReportDocumentBuilder.FromScan(filteredResult);
    }

    private static async Task<int> RunCleanAsync(
        CliOptions options,
        IReadOnlyList<CleanupItem> leaves)
    {
        var selection = SelectLeaves(options, leaves);
        var explicitSelection = selection.Explicit;
        var bulkSelection = selection.Bulk;

        if (explicitSelection.Count == 0 && bulkSelection.Count == 0)
        {
            if (selection.Protected.Count > 0)
            {
                Console.Error.WriteLine("Под категорию попали только объекты, требующие явного выбора.");
                PrintProtectedItems(selection.Protected, Console.Error);
                Console.Error.WriteLine("Уточните выбор флагом --key или --name.");
            }
            else
            {
                Console.Error.WriteLine("Под выбранные фильтры не попало ни одного объекта очистки.");
            }

            return ExitNoSelection;
        }

        var selected = explicitSelection.Concat(bulkSelection)
            .DistinctBy(i => i.Key)
            .ToList();

        PrintSelection(options, selected, selection.Protected);

        if (!options.DryRun && !options.Yes)
        {
            Console.Error.WriteLine("Очистка не выполнена: подтвердите действие флагом --yes (или используйте --dry-run для предпросмотра).");
            return ExitUsage;
        }

        var cleanOptions = new CleanOptions { DryRun = options.DryRun };
        var report = await new PlanExecutor().CleanAsync(selected, cleanOptions);
        var doc = ReportDocumentBuilder.FromCleanReport(report);
        WriteReport(options, doc);

        PrintCleanSummary(options, report);
        if (options.JsonPath is not null)
        {
            Console.WriteLine("JSON-отчёт: " + options.JsonPath);
        }

        return options.DryRun ? ExitOk : (report.FailedItems > 0 ? ExitFailed : ExitOk);
    }

    private static async Task<IReadOnlyList<CleanupItem>> LoadSeedsAsync(CliOptions options)
    {
        if (options.TargetsPath is not null)
        {
            return TargetFileLoader.Load(options.TargetsPath);
        }

        var seeds = new ScanSeedsProvider(includeAllApps: options.IncludeAllApps);
        return await seeds.BuildSeedsAsync();
    }

    private static (IReadOnlyList<CleanupItem> Explicit, IReadOnlyList<CleanupItem> Bulk, IReadOnlyList<CleanupItem> Protected)
        SelectLeaves(CliOptions options, IReadOnlyList<CleanupItem> leaves)
    {
        var categorySet = ParseCategories(options.Categories);

        var explicitKeys = new HashSet<string>(options.Keys, StringComparer.OrdinalIgnoreCase);
        var nameFilters = options.Names
            .Select(n => n.ToLowerInvariant())
            .ToList();

        var matchingByCategory = categorySet is null
            ? leaves
            : leaves.Where(l => categorySet.Contains(l.Category)).ToList();

        var explicitLeaves = matchingByCategory
            .Where(l => explicitKeys.Contains(l.Key) || nameFilters.Any(n => ContainsIgnoreCase(l.DisplayName, n) || ContainsIgnoreCase(l.GroupName ?? string.Empty, n)))
            .ToList();

        var bulkCandidates = matchingByCategory
            .Where(l => !explicitKeys.Contains(l.Key) && !nameFilters.Any(n => ContainsIgnoreCase(l.DisplayName, n) || ContainsIgnoreCase(l.GroupName ?? string.Empty, n)))
            .ToList();

        var (bulkLeaves, protectedLeaves) = PartitionBulk(bulkCandidates);

        return (explicitLeaves, bulkLeaves, protectedLeaves);
    }

    private static (IReadOnlyList<CleanupItem> Bulk, IReadOnlyList<CleanupItem> Protected) PartitionBulk(
        IReadOnlyList<CleanupItem> candidates)
    {
        if (candidates.Count == 0)
        {
            return (Array.Empty<CleanupItem>(), Array.Empty<CleanupItem>());
        }

        var categories = candidates.Select(c => c.Category).Distinct().ToList();
        if (!categories.All(BulkCleanableCategories.Contains))
        {
            return (Array.Empty<CleanupItem>(), candidates);
        }

        var bulk = candidates.Where(c => IsBulkSafe(c)).ToList();
        var notSafe = candidates.Where(c => !IsBulkSafe(c)).ToList();
        return (bulk, notSafe);
    }

    private static bool IsBulkSafe(CleanupItem item) =>
        item.RegistryDeletePath is null &&
        !item.UninstallMode &&
        item.Category != CleanupCategory.UserData;

    private static HashSet<CleanupCategory>? ParseCategories(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var result = new HashSet<CleanupCategory>();
        foreach (var raw in values)
        {
            foreach (var part in raw.Split(CategorySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryParseCategory(part, out var category))
                {
                    throw new ArgumentException($"Неизвестная категория: {part}. Допустимые: {string.Join(", ", Enum.GetNames<CleanupCategory>())}");
                }

                result.Add(category);
            }
        }

        return result;
    }

    private static bool TryParseCategory(string value, out CleanupCategory category)
    {
        if (Enum.TryParse(value, ignoreCase: true, out category))
        {
            return true;
        }

        var byText = Enum.GetValues<CleanupCategory>()
            .ToDictionary(LocalizedNames.Category, StringComparer.OrdinalIgnoreCase);

        if (byText.TryGetValue(value, out var matched))
        {
            category = matched;
            return true;
        }

        category = default;
        return false;
    }

    private static bool ContainsIgnoreCase(string value, string needle) =>
        value.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static void PrintScanSummary(AnalysisResult result, ReportDocument doc)
    {
        Console.WriteLine($"Анализ завершён за {result.Elapsed.TotalSeconds:F1} с. Найдено объектов: {doc.Summary.TotalItems}.");
        if (doc.Categories.Count == 0)
        {
            Console.WriteLine("Объекты для очистки не найдены.");
            return;
        }

        var keyWidth = doc.Categories.Max(c => c.CategoryText.Length);
        foreach (var category in doc.Categories)
        {
            Console.WriteLine($"  {category.CategoryText.PadRight(keyWidth)}  {category.Items,5} шт.  {CleanReportFormatter.FormatBytes(category.Bytes)}");
        }

        if (result.InUseItems > 0)
        {
            Console.WriteLine($"Используется процессами (IN_USE): {result.InUseItems}.");
        }

        if (result.SkippedNonexistent > 0)
        {
            Console.WriteLine($"Пропущено (нет пути): {result.SkippedNonexistent}.");
        }

        if (result.Errors.Count > 0)
        {
            Console.WriteLine($"Ошибок доступа: {result.Errors.Count} (подробности в журнале).");
        }
    }

    private static void PrintSelection(
        CliOptions options,
        IReadOnlyList<CleanupItem> selected,
        IReadOnlyList<CleanupItem> protectedItems)
    {
        Console.WriteLine(options.DryRun ? "Предпросмотр (dry-run), ничего не удаляется." : $"Будет выполнено действие над {selected.Count} объектами.");

        foreach (var item in selected.OrderByDescending(i => i.EffectiveSizeBytes))
        {
            var name = (item.GroupName is null ? string.Empty : item.GroupName + " / ") + item.DisplayName;
            Console.WriteLine($"  • {name} — {CleanReportFormatter.FormatBytes(item.EffectiveSizeBytes)} [{item.Category}, {LocalizedNames.Risk(item.Risk)}] key={item.Key}");
            if (item.Warning is not null)
            {
                Console.WriteLine($"    ! {item.Warning}");
            }
        }

        if (protectedItems.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Не включены в массовую очистку (требуют явного выбора --key/--name):");
            PrintProtectedItems(protectedItems, Console.Out);
        }
    }

    private static void PrintProtectedItems(IReadOnlyList<CleanupItem> items, TextWriter writer)
    {
        foreach (var item in items)
        {
            var name = (item.GroupName is null ? string.Empty : item.GroupName + " / ") + item.DisplayName;
            writer.WriteLine($"  • {name} [{item.Category}] key={item.Key}");
        }
    }

    private static void PrintCleanSummary(CliOptions options, CleanReport report)
    {
        if (report.DryRun)
        {
            Console.WriteLine($"Предпросмотр: объектов в плане {report.Entries.Count}; освободится ≈ {CleanReportFormatter.FormatBytes(report.TotalFreedBytes)}.");
            return;
        }

        Console.WriteLine(
            $"Освобождено: {CleanReportFormatter.FormatBytes(report.TotalFreedBytes)}. " +
            $"Успешно: {report.Entries.Count(e => e.Outcome is CleanOutcome.NativeCleaned or CleanOutcome.DirectDeleted or CleanOutcome.CommandOnlyCleaned or CleanOutcome.Uninstalled or CleanOutcome.RegistryEntryDeleted or CleanOutcome.MovedToRecycleBin)}. " +
            $"С ошибками/частично: {report.FailedItems}. Отложено (используется): {report.DeferredItems}.");
    }

    private static void WriteReport(CliOptions options, ReportDocument doc)
    {
        if (options.JsonPath is null)
        {
            return;
        }

        var path = Path.GetFullPath(options.JsonPath);
        var directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        ReportJson.WriteFile(path, doc);
    }

    internal static CliOptions ParseArguments(string[] args)
    {
        var options = new CliOptions { OriginalArgs = args };

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var (name, inlineValue) = SplitArgument(arg);

            switch (name)
            {
                case "--help":
                case "-h":
                case "-?":
                    options.ShowHelp = true;
                    break;
                case "--version":
                    options.ShowVersion = true;
                    break;
                case "--scan":
                    options.Mode = CliMode.Scan;
                    break;
                case "--clean":
                    options.Mode = CliMode.Clean;
                    break;
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--yes":
                    options.Yes = true;
                    break;
                case "--json":
                    options.JsonPath = inlineValue ?? ReadValue(args, ref i);
                    break;
                case "--category":
                    options.Categories.Add(inlineValue ?? ReadValue(args, ref i));
                    break;
                case "--key":
                    options.Keys.Add(inlineValue ?? ReadValue(args, ref i));
                    break;
                case "--name":
                    options.Names.Add(inlineValue ?? ReadValue(args, ref i));
                    break;
                case "--targets":
                    options.TargetsPath = inlineValue ?? ReadValue(args, ref i);
                    break;
                case "--include-all-apps":
                    options.IncludeAllApps = true;
                    break;
                case "--recommended-only":
                    options.IncludeAllApps = false;
                    break;
                default:
                    throw new ArgumentException($"Неизвестный аргумент: {arg}. Справка: --help");
            }
        }

        if (options.ShowHelp || options.ShowVersion)
        {
            return options;
        }

        if (options.Mode == CliMode.Clean)
        {
            var hasSelector = options.Categories.Count > 0 || options.Keys.Count > 0 || options.Names.Count > 0;
            if (!hasSelector)
            {
                throw new ArgumentException("Для --clean укажите хотя бы --category, --key или --name.");
            }
        }

        return options;
    }

    private static (string Name, string? Value) SplitArgument(string arg)
    {
        var index = arg.IndexOf('=');
        return index < 0
            ? (arg, null)
            : (arg[..index], arg[(index + 1)..]);
    }

    private static string ReadValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Для аргумента {args[index]} требуется значение.");
        }

        return args[++index];
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            DiskCleaner.Cli — консольный режим очистки диска (FR-0.5).

            Использование:
              DiskCleaner.Cli --scan [--category Cache,Temp] [--json отчёт.json] [--targets объекты.json]
              DiskCleaner.Cli --clean --category Cache,Temp --yes|--dry-run [--json отчёт.json] [--targets объекты.json]
              DiskCleaner.Cli --clean --key <key> --yes
              DiskCleaner.Cli --clean --name <подстрока> --yes

            Параметры:
              --scan                 Выполнить анализ и показать план (ничего не удаляет).
              --clean                Выполнить очистку выбранных объектов.
              --category <список>    Категории через запятую: Cache,Leftover,DevToolchain,InstalledApp,SystemFile,Temp,RecycleBin,UserData,Other (или русские названия).
              --key <ключ>           Точный ключ объекта (можно повторять).
              --name <подстрока>     Подстрока в имени/группе объекта (можно повторять).
              --yes                  Подтвердить очистку без интерактивного запроса.
              --dry-run              Предпросмотр: показать план, ничего не удалять.
              --json <файл>          Записать JSON-отчёт (единая схема, UTF-8).
              --targets <файл>       JSON-файл с объектами очистки {path,category,risk} вместо анализа системы.
              --include-all-apps     В анализе учитывать все установленные приложения (по умолчанию).
              --recommended-only     В анализе только рекомендуемые приложения (дубли/осиротевшие).
              --help                 Справка.
              --version              Версия.

            Безопасность:
              Категории Cache/Temp/RecycleBin/Leftover очищаются массово (после --yes).
              Деинсталляция, удаление записей реестра, системные шаги и пользовательские
              данные выполняются только по явному выбору (--key/--name).
            """);
    }
}

public enum CliMode
{
    Scan,
    Clean
}
