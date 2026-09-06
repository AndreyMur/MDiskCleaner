using DiskCleaner.Core.Analysis;
using DiskCleaner.Core.Environment;

namespace DiskCleaner.Core.Scanning;

/// <summary>
/// Полный скан диска (FR-1.1–1.5, NFR): верхний уровень выбранного корня, игнор-список
/// системных каталогов, системные объекты (Корзина, hiberfil.sys), известные категории
/// и детализация крупных объектов. Обход веток кэшируется инкрементально (mtime + размер,
/// FR-1.13): повторный скан пересчитывает только изменённые ветки.
/// </summary>
public sealed class DiskScanService
{
    private static readonly HashSet<string> SystemDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "System Volume Information", "$Recycle.Bin", "WindowsApps"
    };

    private static readonly HashSet<string> NestedSystemDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information", "$Recycle.Bin", "WindowsApps"
    };

    private readonly ScanCacheStore? _cacheStore;
    private readonly CachedDirectoryMeasurer _measurer;
    private readonly CategorizationService _categorizer = new();
    private readonly Abstractions.IEnvironment _environment;
    private readonly string? _currentUserSid;

    public DiskScanService(
        ScanCacheStore? cacheStore = null,
        Abstractions.IEnvironment? environment = null,
        string? currentUserSid = null)
    {
        _cacheStore = cacheStore;
        _environment = environment ?? new EnvironmentProvider();
        _measurer = new CachedDirectoryMeasurer(cacheStore);
        _currentUserSid = currentUserSid;
    }

    public async Task<DiskScanResult> ScanAsync(
        DiskScanRequest? request = null,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        request ??= new DiskScanRequest();
        var startedAt = DateTime.UtcNow;
        var scanRoot = ResolveScanRoot(request.RootPath);

        if (!Directory.Exists(scanRoot))
        {
            return new DiskScanResult
            {
                ScanRoot = scanRoot,
                Errors = new[] { $"Указанный корень не существует или недоступен: {scanRoot}" },
                Elapsed = DateTime.UtcNow - startedAt
            };
        }

        var ctx = new DiskScanContext(request, progress);
        var includeSystem = request.IncludeSystemDirectories;
        _measurer.SetExcludedNames(name => !includeSystem && NestedSystemDirectoryNames.Contains(name));

        try
        {
            List<NativeDirectory.Entry> rootEntries;
            try
            {
                rootEntries = NativeDirectory.Enumerate(scanRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                ctx.RecordError(scanRoot, ex.Message);
                return new DiskScanResult
                {
                    ScanRoot = scanRoot,
                    Errors = ctx.Errors,
                    Elapsed = DateTime.UtcNow - startedAt
                };
            }

            var measureDirs = new List<string>();
            var ignored = new List<string>();
            long rootFileBytes = 0;
            var rootFileCount = 0;

            foreach (var entry in rootEntries)
            {
                if (entry.IsDirectory)
                {
                    if (entry.IsReparsePoint)
                    {
                        continue;
                    }

                    if (IsSystemDirectoryName(entry.Name, includeSystem))
                    {
                        ignored.Add(entry.Name);
                    }
                    else
                    {
                        measureDirs.Add(entry.Name);
                    }
                }
                else
                {
                    rootFileBytes += entry.Size;
                    rootFileCount++;
                }
            }

            var topLevel = new List<DirectoryMeasurement>(measureDirs.Count);
            ctx.BeginTopLevel(measureDirs.Count);

            await Parallel.ForEachAsync(
                measureDirs,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Min(8, Math.Max(2, System.Environment.ProcessorCount))
                },
                async (name, token) =>
                {
                    var path = Path.Combine(scanRoot, name);
                    var measurement = await MeasureBranchWithTimeoutAsync(path, ctx, token);
                    lock (topLevel)
                    {
                        topLevel.Add(measurement);
                    }

                    ctx.RootCompleted();
                });

            var systemObjects = await MeasureSystemObjectsAsync(scanRoot, ctx, cancellationToken);
            var flatObjects = await MeasureKnownObjectsAsync(scanRoot, ctx, cancellationToken);
            var largeObjects = await MeasureLargeObjectsAsync(scanRoot, ctx, cancellationToken);

            topLevel.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

            return new DiskScanResult
            {
                ScanRoot = scanRoot,
                TopLevelDirectories = topLevel,
                IgnoredDirectoryNames = ignored,
                RootFileBytes = rootFileBytes,
                RootFileCount = rootFileCount,
                SystemObjects = systemObjects,
                Objects = flatObjects,
                LargeObjects = largeObjects,
                Errors = ctx.Errors,
                Elapsed = DateTime.UtcNow - startedAt,
                Statistics = ctx.Statistics
            };
        }
        finally
        {
            _cacheStore?.Save();
        }
    }

    private async Task<DirectoryMeasurement> MeasureBranchWithTimeoutAsync(
        string directory,
        DiskScanContext ctx,
        CancellationToken cancellationToken)
    {
        var timeout = ctx.Request.BranchTimeout;
        if (timeout is null || timeout <= TimeSpan.Zero)
        {
            return await _measurer.MeasureBranchAsync(directory, ctx, cancellationToken);
        }

        using var branchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var measureTask = _measurer.MeasureBranchAsync(directory, ctx, branchCts.Token);

        while (true)
        {
            if (measureTask.IsCompleted)
            {
                return await measureTask;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return await measureTask;
            }

            if (System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) >= timeout.Value)
            {
                branchCts.Cancel();
                try
                {
                    return await measureTask.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return TimedOutMeasurement(directory, ctx, timeout.Value);
                }
                catch (TimeoutException)
                {
                    return TimedOutMeasurement(directory, ctx, timeout.Value);
                }
            }

            await Task.Delay(5, cancellationToken);
        }
    }

    private static DirectoryMeasurement TimedOutMeasurement(string directory, DiskScanContext ctx, TimeSpan timeout)
    {
        ctx.RecordBranchTimeout(directory, timeout);
        return new DirectoryMeasurement(directory, 0, 0, Directory.Exists(directory), TimedOut: true);
    }

    private async Task<IReadOnlyList<SystemObjectMeasurement>> MeasureSystemObjectsAsync(
        string scanRoot,
        DiskScanContext ctx,
        CancellationToken cancellationToken)
    {
        var result = new List<SystemObjectMeasurement>();

        var recycleDirectory = await MeasureRecycleBinAsync(scanRoot, ctx, cancellationToken);
        if (recycleDirectory is not null)
        {
            result.Add(recycleDirectory);
        }

        var hiberfil = await MeasureHibernationAsync(scanRoot, cancellationToken);
        if (hiberfil is not null)
        {
            result.Add(hiberfil);
        }

        return result;
    }

    private async Task<SystemObjectMeasurement?> MeasureRecycleBinAsync(
        string scanRoot,
        DiskScanContext ctx,
        CancellationToken cancellationToken)
    {
        var sid = _currentUserSid ?? ResolveCurrentUserSid();
        if (string.IsNullOrWhiteSpace(sid))
        {
            return null;
        }

        var recycleDirectory = Path.Combine(scanRoot, "$Recycle.Bin", sid);
        if (!Directory.Exists(recycleDirectory))
        {
            return null;
        }

        var measurement = await _measurer.MeasureBranchAsync(recycleDirectory, ctx, cancellationToken);
        return new SystemObjectMeasurement(
            "recycle-bin:" + scanRoot.TrimEnd('\\', '/'),
            "Корзина (" + FormatDriveLabel(scanRoot) + ")",
            recycleDirectory,
            measurement.SizeBytes,
            (int)measurement.FileCount,
            Present: true);
    }

    private Task<SystemObjectMeasurement?> MeasureHibernationAsync(
        string scanRoot,
        CancellationToken cancellationToken)
    {
        var hiberfil = Path.Combine(scanRoot, "hiberfil.sys");
        if (!File.Exists(hiberfil))
        {
            return Task.FromResult<SystemObjectMeasurement?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        long? size = null;
        try
        {
            size = new FileInfo(hiberfil).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return Task.FromResult<SystemObjectMeasurement?>(new SystemObjectMeasurement(
            "hibernation:" + scanRoot.TrimEnd('\\', '/'),
            "hiberfil.sys (файл гибернации)",
            hiberfil,
            size,
            FileCount: size is null ? null : 1,
            Present: true));
    }

    private async Task<IReadOnlyList<DiskObjectMeasurement>> MeasureKnownObjectsAsync(
        string scanRoot,
        DiskScanContext ctx,
        CancellationToken cancellationToken)
    {
        var result = new List<DiskObjectMeasurement>();
        foreach (var known in KnownLayout.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = ResolveUnderScanRoot(scanRoot, known.PathPattern);
            if (path is null || !Directory.Exists(path))
            {
                continue;
            }

            var measurement = await _measurer.MeasureBranchAsync(path, ctx, cancellationToken);
            result.Add(new DiskObjectMeasurement(
                path,
                known.Label,
                known.Group,
                _categorizer.CategorizePath(path),
                _categorizer.RiskForPath(path),
                measurement));
        }

        return result.OrderByDescending(o => o.Measurement.SizeBytes).ToList();
    }

    private async Task<IReadOnlyList<LargeObjectDetail>> MeasureLargeObjectsAsync(
        string scanRoot,
        DiskScanContext ctx,
        CancellationToken cancellationToken)
    {
        var result = new List<LargeObjectDetail>();
        foreach (var spec in KnownLayout.LargeObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rootPath = ResolveUnderScanRoot(scanRoot, spec.RootPathPattern);
            if (rootPath is null || !Directory.Exists(rootPath))
            {
                continue;
            }

            var total = await _measurer.MeasureBranchAsync(rootPath, ctx, cancellationToken);
            var components = await MeasureComponentsAsync(rootPath, spec, ctx, cancellationToken);

            var category = _categorizer.CategorizePath(rootPath);
            result.Add(new LargeObjectDetail(
                spec.Key,
                spec.Label,
                rootPath,
                category,
                _categorizer.RiskForPath(rootPath),
                total,
                components));
        }

        return result;
    }

    private async Task<IReadOnlyList<DiskObjectMeasurement>> MeasureComponentsAsync(
        string rootPath,
        KnownLayout.LargeObjectSpec spec,
        DiskScanContext ctx,
        CancellationToken cancellationToken)
    {
        var result = new List<DiskObjectMeasurement>();

        if (spec.FixedComponents.Count > 0)
        {
            foreach (var component in spec.FixedComponents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var parts = component.Split('/', '\\');
                var candidate = Path.Combine(new[] { rootPath }.Concat(parts).ToArray());
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                var measurement = await _measurer.MeasureBranchAsync(candidate, ctx, cancellationToken);
                result.Add(new DiskObjectMeasurement(
                    candidate,
                    component,
                    spec.Label,
                    _categorizer.CategorizePath(candidate),
                    _categorizer.RiskForPath(candidate),
                    measurement));
            }
        }
        else
        {
            List<NativeDirectory.Entry> children;
            try
            {
                children = NativeDirectory.Enumerate(rootPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ctx.RecordError(rootPath, ex.Message);
                return result;
            }

            foreach (var entry in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.IsDirectory || entry.IsReparsePoint)
                {
                    continue;
                }

                var candidate = Path.Combine(rootPath, entry.Name);
                var measurement = await _measurer.MeasureBranchAsync(candidate, ctx, cancellationToken);
                result.Add(new DiskObjectMeasurement(
                    candidate,
                    entry.Name,
                    spec.Label,
                    _categorizer.CategorizePath(candidate),
                    _categorizer.RiskForPath(candidate),
                    measurement));
            }
        }

        return result.OrderByDescending(c => c.Measurement.SizeBytes).ToList();
    }

    private static bool IsSystemDirectoryName(string name, bool includeSystemDirectories) =>
        !includeSystemDirectories && SystemDirectoryNames.Contains(name);

    private string? ResolveUnderScanRoot(string scanRoot, string pathPattern)
    {
        string expanded;
        try
        {
            expanded = _environment.ExpandPath(pathPattern);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var target = Path.GetFullPath(expanded);
        var scanRootFull = Path.GetFullPath(scanRoot);
        var scanTrimmed = Path.TrimEndingDirectorySeparator(scanRootFull);

        if (target.StartsWith(scanTrimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, scanTrimmed, StringComparison.OrdinalIgnoreCase))
        {
            return target;
        }

        var targetRoot = Path.GetPathRoot(target);
        var scanRootPathRoot = Path.GetPathRoot(scanRootFull);
        var isVolumeRoot = scanRootPathRoot is not null &&
                           string.Equals(
                               scanTrimmed,
                               Path.TrimEndingDirectorySeparator(scanRootPathRoot),
                               StringComparison.OrdinalIgnoreCase);

        if (isVolumeRoot && !string.IsNullOrEmpty(targetRoot))
        {
            var relative = target.Substring(targetRoot.Length).TrimStart('\\', '/');
            if (relative.Length > 0)
            {
                return Path.Combine(scanTrimmed + Path.DirectorySeparatorChar, relative);
            }
        }

        return null;
    }

    private string ResolveScanRoot(string requestedRoot)
    {
        if (!string.IsNullOrWhiteSpace(requestedRoot))
        {
            return Path.GetFullPath(requestedRoot);
        }

        var windows = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
        return Path.GetPathRoot(windows) ?? string.Empty;
    }

    private static string? ResolveCurrentUserSid()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string FormatDriveLabel(string directory) =>
        (Path.GetPathRoot(directory) ?? string.Empty).TrimEnd('\\', '/');
}

/// <summary>Справочник известных объектов и крупных объектов для полного скана диска.</summary>
internal static class KnownLayout
{
    internal sealed record KnownObjectSpec(string Label, string Group, string PathPattern);

    internal sealed record LargeObjectSpec(
        string Key,
        string Label,
        string RootPathPattern,
        IReadOnlyList<string> FixedComponents);

    internal static readonly IReadOnlyList<KnownObjectSpec> Objects = new[]
    {
        new KnownObjectSpec("npm cache", "Менеджеры пакетов", "%LocalAppData%\\npm-cache"),
        new KnownObjectSpec("pnpm store", "Менеджеры пакетов", "%LocalAppData%\\pnpm-cache"),
        new KnownObjectSpec("pip cache", "Менеджеры пакетов", "%LocalAppData%\\pip\\Cache"),
        new KnownObjectSpec("uv cache", "Менеджеры пакетов", "%LocalAppData%\\uv\\cache"),
        new KnownObjectSpec("dotslash cache", "Менеджеры пакетов", "%LocalAppData%\\dotslash"),
        new KnownObjectSpec("Cargo registry", "Cargo", "%UserProfile%\\.cargo\\registry"),
        new KnownObjectSpec("Cargo git checkouts", "Cargo", "%UserProfile%\\.cargo\\git"),
        new KnownObjectSpec("rustup toolchains", "rustup", "%UserProfile%\\.rustup"),
        new KnownObjectSpec("Playwright browsers", "Playwright", "%LocalAppData%\\ms-playwright")
    };

    internal static readonly IReadOnlyList<LargeObjectSpec> LargeObjects = new[]
    {
        new LargeObjectSpec(
            "gradle",
            "Gradle (.gradle)",
            "%UserProfile%\\.gradle",
            new[] { "caches", "wrapper/dists", "jdks", "daemon" }),
        new LargeObjectSpec(
            "vscode",
            "VS Code",
            "%AppData%\\Code",
            new[] { "Cache", "CachedData", "CachedExtensionVSIXs", "Crashpad", "GPUCache", "logs", "WebStorage" }),
        new LargeObjectSpec(
            "android-sdk",
            "Android SDK",
            "%LocalAppData%\\Android\\Sdk",
            Array.Empty<string>())
    };
}
