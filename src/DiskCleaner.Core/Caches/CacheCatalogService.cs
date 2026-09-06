using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskCleaner.Core.Commanding;
using DiskCleaner.Core.Environment;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Caches;

public sealed class CacheCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly Abstractions.IEnvironment _environment;
    private readonly ICommandRunner _runner;
    private readonly ICommandLocator _locator;

    public CacheCatalogService(
        Abstractions.IEnvironment? environment = null,
        ICommandRunner? runner = null,
        ICommandLocator? locator = null)
    {
        _environment = environment ?? new EnvironmentProvider();
        _runner = runner ?? new ProcessCommandRunner();
        _locator = locator ?? new CommandLocator();
    }

    public CacheCatalogDocument LoadDocument()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("DiskCleaner.Core.Caches.caches.json")
            ?? throw new InvalidOperationException("Встроенный каталог кэшей caches.json не найден.");

        return JsonSerializer.Deserialize<CacheCatalogDocument>(stream, JsonOptions)
            ?? new CacheCatalogDocument();
    }

    public async Task<IReadOnlyList<CleanupItem>> BuildSeedsAsync(CancellationToken cancellationToken = default)
    {
        var document = LoadDocument();
        var seeds = new List<CleanupItem>();

        foreach (var definition in document.Targets)
        {
            var category = ParseEnum(definition.Category, CleanupCategory.Cache);
            var risk = ParseEnum(definition.Risk, CleanupRisk.Low);
            var group = string.IsNullOrEmpty(definition.Group) ? definition.Label : definition.Group;

            if (definition.CommandOnly)
            {
                if (definition.CleanCommand is not null &&
                    _locator.IsAvailable(definition.CleanCommand.FileName))
                {
                    seeds.Add(CreateItem(definition, null, category, risk, group, definition.CleanCommand, false, true));
                }

                continue;
            }

            var envPaths = definition.EnvPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => _environment.ExpandPath(p!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var queryPath = await ResolveConfiguredPathAsync(definition.PathQuery, cancellationToken);
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (queryPath is not null)
            {
                seeds.Add(CreateItem(
                    definition,
                    queryPath,
                    category,
                    risk,
                    group,
                    definition.CleanCommand,
                    allowDirectDelete: definition.AllowDirectDelete,
                    commandOnly: false));
                added.Add(queryPath);
            }

            var hasQuery = queryPath is not null;
            foreach (var envPath in envPaths)
            {
                if (added.Contains(envPath))
                {
                    continue;
                }

                seeds.Add(CreateItem(
                    definition,
                    envPath,
                    category,
                    risk,
                    group,
                    hasQuery ? null : definition.CleanCommand,
                    allowDirectDelete: definition.AllowDirectDelete,
                    commandOnly: false));
                added.Add(envPath);
            }
        }

        return seeds;
    }

    private async Task<string?> ResolveConfiguredPathAsync(
        CommandDefinition? pathQuery,
        CancellationToken cancellationToken)
    {
        if (pathQuery is null || string.IsNullOrWhiteSpace(pathQuery.FileName))
        {
            return null;
        }

        var result = await _runner.RunAsync(pathQuery, cancellationToken);
        if (result.ExitCode != 0 || result.TimedOut)
        {
            return null;
        }

        foreach (var line in result.Output.Split('\n'))
        {
            var candidate = line.Trim().Trim('"').Trim();
            if (candidate.Length > 0 && Path.IsPathRooted(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static CleanupItem CreateItem(
        CacheTargetDefinition definition,
        string? path,
        CleanupCategory category,
        CleanupRisk risk,
        string group,
        CommandDefinition? cleanCommand,
        bool allowDirectDelete,
        bool commandOnly)
    {
        return new CleanupItem
        {
            Key = $"{definition.Id}:{path ?? "(command)"}",
            Path = path is null ? null : Path.GetFullPath(path),
            DisplayName = definition.Label,
            GroupName = group,
            Category = category,
            Risk = risk,
            Target = CleanupTarget.Directory,
            Description = definition.Description,
            Warning = definition.Warning,
            ManagerName = group,
            CleanCommand = FormatCommand(cleanCommand),
            CleanCommandFile = cleanCommand?.FileName,
            CleanCommandArgs = cleanCommand?.Arguments,
            AllowDirectDelete = allowDirectDelete,
            CommandOnly = commandOnly,
            OwnerProcessNames = definition.OwnerProcessNames
        };
    }

    private static string? FormatCommand(CommandDefinition? command) =>
        command is null
            ? null
            : string.IsNullOrWhiteSpace(command.Arguments)
                ? command.FileName
                : $"{command.FileName} {command.Arguments}";

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback)
        where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out TEnum parsed) ? parsed : fallback;
}
