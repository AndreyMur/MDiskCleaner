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

            var resolvedPath = await ResolveConfiguredPathAsync(definition, cancellationToken);
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (resolvedPath is not null)
            {
                seeds.Add(CreateItem(
                    definition,
                    resolvedPath,
                    category,
                    risk,
                    group,
                    definition.CleanCommand,
                    allowDirectDelete: definition.AllowDirectDelete,
                    commandOnly: false,
                    isOrphan: false));
                added.Add(resolvedPath);
            }

            foreach (var envPath in envPaths)
            {
                if (added.Contains(envPath))
                {
                    continue;
                }

                var isOrphan = resolvedPath is not null && !SamePath(envPath, resolvedPath);
                seeds.Add(CreateItem(
                    definition,
                    envPath,
                    category,
                    risk,
                    group,
                    resolvedPath is null ? definition.CleanCommand : null,
                    allowDirectDelete: definition.AllowDirectDelete,
                    commandOnly: false,
                    isOrphan: isOrphan));
                added.Add(envPath);
            }
        }

        return seeds;
    }

    private async Task<string?> ResolveConfiguredPathAsync(
        CacheTargetDefinition definition,
        CancellationToken cancellationToken)
    {
        var pathQuery = definition.PathQuery;
        if (pathQuery is not null && !string.IsNullOrWhiteSpace(pathQuery.FileName))
        {
            var result = await _runner.RunAsync(pathQuery, cancellationToken);
            if (result.ExitCode == 0 && !result.TimedOut)
            {
                var commandPath = FirstRootedOutputPath(result.Output);
                if (commandPath is not null)
                {
                    return commandPath;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(definition.ConfigPathKey))
        {
            return null;
        }

        foreach (var configPathPattern in definition.ConfigFilePaths)
        {
            if (string.IsNullOrWhiteSpace(configPathPattern))
            {
                continue;
            }

            string configPath;
            try
            {
                configPath = _environment.ExpandPath(configPathPattern);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!File.Exists(configPath))
            {
                continue;
            }

            var value = ReadIniKeyValue(configPath, definition.ConfigPathKey);
            if (value is null)
            {
                continue;
            }

            var expandedValue = ExpandConfigPathValue(value);
            if (expandedValue is not null)
            {
                return expandedValue;
            }
        }

        return null;
    }

    private static string? FirstRootedOutputPath(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var candidate = line.Trim().Trim('"').Trim();
            if (candidate.Length > 0 && Path.IsPathRooted(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string? ExpandConfigPathValue(string value)
    {
        var trimmed = value.Trim().Trim('"', '\'');
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed == "~")
        {
            trimmed = _environment.UserProfile;
        }
        else if (trimmed.StartsWith("~/", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.StartsWith(@"~\", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = Path.Combine(_environment.UserProfile, trimmed.Substring(2));
        }

        string expanded;
        try
        {
            expanded = _environment.ExpandPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return Path.IsPathRooted(expanded) ? expanded : null;
    }

    private static string? ReadIniKeyValue(string configPath, string key)
    {
        foreach (var rawLine in File.ReadAllLines(configPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator < 0)
            {
                separator = line.IndexOf(':');
            }

            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim().Trim('"', '\'');
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            return value;
        }

        return null;
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd('\\', '/'),
                Path.GetFullPath(right).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static CleanupItem CreateItem(
        CacheTargetDefinition definition,
        string? path,
        CleanupCategory category,
        CleanupRisk risk,
        string group,
        CommandDefinition? cleanCommand,
        bool allowDirectDelete,
        bool commandOnly,
        bool isOrphan = false)
    {
        return new CleanupItem
        {
            Key = $"{definition.Id}:{path ?? "(command)"}",
            Path = path is null ? null : Path.GetFullPath(path),
            DisplayName = isOrphan ? $"{definition.Label} (осиротевший)" : definition.Label,
            GroupName = group,
            Category = category,
            Risk = risk,
            Target = CleanupTarget.Directory,
            Description = isOrphan
                ? "Осиротевший кэш прежней конфигурации: менеджер больше не использует этот путь, данные остались на диске."
                : definition.Description,
            Warning = definition.Warning,
            ManagerName = group,
            CleanCommand = FormatCommand(cleanCommand),
            CleanCommandFile = cleanCommand?.FileName,
            CleanCommandArgs = cleanCommand?.Arguments,
            AllowDirectDelete = allowDirectDelete,
            CommandOnly = commandOnly,
            IsOrphan = isOrphan,
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
