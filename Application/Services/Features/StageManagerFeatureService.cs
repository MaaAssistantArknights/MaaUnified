using System.Reflection;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Runtime;
using MAAUnified.CoreBridge;

namespace MAAUnified.Application.Services.Features;

public sealed class StageManagerFeatureService : IStageManagerFeatureService
{
    private const string DefaultClientType = "Official";
    private const string StageActivityApi = "gui/StageActivityV2.json";
    private const string TasksApi = "resource/tasks.json";
    private const string MaaApiBaseUrl = "https://api.maa.plus/MaaAssistantArknights/api/";
    private const string MaaApiFallbackBaseUrl = "https://api2.maa.plus/MaaAssistantArknights/api/";
    private static readonly string[] WebRootNames = ["publish", "install"];
    private static readonly HttpClient DefaultHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly UnifiedConfigurationService? _configService;
    private readonly string _baseDirectory;
    private readonly HttpClient _httpClient;
    private readonly Func<string?> _coreVersionResolver;
    private readonly object _snapshotGate = new();
    private readonly object _activitySnapshotGate = new();
    private readonly object _coreVersionGate = new();
    private readonly object _resourceCacheGate = new();
    private readonly Dictionary<string, StageSnapshot> _localSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StageSnapshot> _webSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StageActivityState> _activitySnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResourceJsonCacheEntry> _resourceJsonCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _coreVersionResolved;
    private string? _resolvedCoreVersion;

    public StageManagerFeatureService()
        : this(configService: null, baseDirectory: RuntimeLayout.ResolveRuntimeBaseDirectory())
    {
    }

    public StageManagerFeatureService(
        UnifiedConfigurationService? configService,
        string? baseDirectory = null,
        HttpClient? httpClient = null,
        Func<string?>? coreVersionResolver = null)
    {
        _configService = configService;
        _baseDirectory = ResolveBaseDirectory(configService, baseDirectory);
        _httpClient = httpClient ?? DefaultHttpClient;
        _coreVersionResolver = coreVersionResolver ?? (() => MaaCoreBridgeNative.TryReadInstalledVersion(_baseDirectory));
    }

    public Task<UiOperationResult<StageManagerState>> LoadStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var clientType = NormalizeClientType(ReadConfiguredClientType());
        var localSnapshot = EnsureSnapshot(clientType, preferWeb: false, forceReload: false);
        var webSnapshot = EnsureSnapshot(clientType, preferWeb: true, forceReload: false);
        return Task.FromResult(UiOperationResult<StageManagerState>.Ok(
            BuildState(clientType, localSnapshot, webSnapshot),
            BuildStageManagerStateLoadedMessage()));
    }

    public Task<UiOperationResult<StageManagerState>> RefreshLocalAsync(
        string? clientType = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedClientType = NormalizeClientType(clientType ?? ReadConfiguredClientType());
        if (!TryLoadSnapshot(normalizedClientType, preferWeb: false, out var localSnapshot, out var errorMessage))
        {
            return Task.FromResult(UiOperationResult<StageManagerState>.Fail(
                UiErrorCode.StageManagerServiceUnavailable,
                errorMessage));
        }

        lock (_snapshotGate)
        {
            _localSnapshots[normalizedClientType] = localSnapshot!;
        }

        var webSnapshot = GetCachedSnapshot(_webSnapshots, normalizedClientType);
        return Task.FromResult(UiOperationResult<StageManagerState>.Ok(
            BuildState(normalizedClientType, localSnapshot, webSnapshot),
            BuildStageResourcesLoadedMessage(normalizedClientType, preferWeb: false)));
    }

    public Task<UiOperationResult<StageManagerState>> RefreshWebAsync(
        string? clientType = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedClientType = NormalizeClientType(clientType ?? ReadConfiguredClientType());
        if (!TryLoadSnapshot(normalizedClientType, preferWeb: true, out var webSnapshot, out var errorMessage))
        {
            return Task.FromResult(UiOperationResult<StageManagerState>.Fail(
                UiErrorCode.StageManagerServiceUnavailable,
                errorMessage));
        }

        lock (_snapshotGate)
        {
            _webSnapshots[normalizedClientType] = webSnapshot!;
        }

        var localSnapshot = EnsureSnapshot(normalizedClientType, preferWeb: false, forceReload: false);
        return Task.FromResult(UiOperationResult<StageManagerState>.Ok(
            BuildState(normalizedClientType, localSnapshot, webSnapshot),
            BuildStageResourcesLoadedMessage(normalizedClientType, preferWeb: true)));
    }

    public IReadOnlyList<string> GetStageCodes(string? clientType = null, bool forceReload = false)
    {
        var normalizedClientType = NormalizeClientType(clientType ?? ReadConfiguredClientType());
        var localSnapshot = EnsureSnapshot(normalizedClientType, preferWeb: false, forceReload);
        var webSnapshot = EnsureSnapshot(normalizedClientType, preferWeb: true, forceReload);
        return BuildState(normalizedClientType, localSnapshot, webSnapshot).ActiveStageCodes;
    }

    public StageActivityState GetStageActivityState(string? clientType = null, bool forceReload = false)
    {
        var normalizedClientType = NormalizeClientType(clientType ?? ReadConfiguredClientType());
        var coreVersion = ResolveCoreVersion(forceReload);
        var isDebugVersion = IsDebugVersion(coreVersion);
        if (!forceReload)
        {
            lock (_activitySnapshotGate)
            {
                if (_activitySnapshots.TryGetValue(normalizedClientType, out var cached)
                    && string.Equals(cached.CoreVersion, coreVersion, StringComparison.Ordinal))
                {
                    return cached;
                }
            }
        }

        var loaded = LoadStageActivityStateFromCache(
            normalizedClientType,
            resourceTasksUpdated: false,
            coreVersion,
            isDebugVersion);
        lock (_activitySnapshotGate)
        {
            _activitySnapshots[normalizedClientType] = loaded;
        }

        return loaded;
    }

    public async Task<UiOperationResult<StageActivityState>> RefreshStageActivityWebAsync(
        string? clientType = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedClientType = NormalizeClientType(clientType ?? ReadConfiguredClientType());
        var activityTask = FetchMaaApiResourceAsync(StageActivityApi, cancellationToken);
        var tasksTask = FetchMaaApiResourceAsync(TasksApi, cancellationToken);
        await Task.WhenAll(activityTask, tasksTask).ConfigureAwait(false);
        var activity = await activityTask.ConfigureAwait(false);
        var tasks = await tasksTask.ConfigureAwait(false);
        var clientTasks = FetchResult.Empty;
        if (!string.Equals(normalizedClientType, DefaultClientType, StringComparison.OrdinalIgnoreCase) && tasks.Success)
        {
            clientTasks = await FetchMaaApiResourceAsync(
                $"resource/global/{NormalizeClientDirectory(normalizedClientType)}/resource/tasks.json",
                cancellationToken).ConfigureAwait(false);
        }

        if (!activity.Success
            || !tasks.Success
            || (!string.Equals(normalizedClientType, DefaultClientType, StringComparison.OrdinalIgnoreCase)
                && !clientTasks.Success))
        {
            return UiOperationResult<StageActivityState>.Fail(
                UiErrorCode.StageManagerServiceUnavailable,
                BuildStageActivityApiUnavailableMessage(normalizedClientType));
        }

        var coreVersion = ResolveCoreVersion(forceReload: true);
        var state = LoadStageActivityStateFromCache(
            normalizedClientType,
            resourceTasksUpdated: tasks.Updated || clientTasks.Updated,
            coreVersion,
            IsDebugVersion(coreVersion));
        lock (_activitySnapshotGate)
        {
            _activitySnapshots[normalizedClientType] = state;
        }

        return UiOperationResult<StageActivityState>.Ok(
            state,
            BuildStageActivityResourcesLoadedMessage(normalizedClientType, activity.Updated));
    }

    public Task<UiOperationResult<StageManagerConfig>> LoadConfigAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = StageManagerConfig.Default;
        if (_configService is null)
        {
            return Task.FromResult(UiOperationResult<StageManagerConfig>.Ok(config, BuildStageManagerConfigLoadedMessage()));
        }

        var current = _configService.CurrentConfig;
        config = new StageManagerConfig(
            StageCodes: ReadStageCodesFromConfig(current),
            AutoIterate: ReadBool(current, "Advanced.StageManager.AutoIterate", false),
            LastSelectedStage: ReadString(current, "Advanced.StageManager.LastSelectedStage", string.Empty),
            ClientType: NormalizeClientType(ReadString(current, "Advanced.StageManager.ClientType", DefaultClientType)));
        return Task.FromResult(UiOperationResult<StageManagerConfig>.Ok(config, BuildStageManagerConfigLoadedMessage()));
    }

    public async Task<UiOperationResult> SaveConfigAsync(StageManagerConfig config, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_configService is null)
        {
            return UiOperationResult.Fail(UiErrorCode.StageManagerServiceUnavailable, BuildStageManagerServiceUnavailableMessage());
        }

        foreach (var (key, value) in config.ToGlobalSettingUpdates())
        {
            _configService.CurrentConfig.GlobalValues[key] = JsonValue.Create(value);
        }

        await _configService.SaveAsync(cancellationToken);
        return UiOperationResult.Ok(BuildStageManagerConfigSavedMessage());
    }

    public Task<UiOperationResult<IReadOnlyList<string>>> ValidateStageCodesAsync(
        string stageCodesText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var codes = (stageCodesText ?? string.Empty)
            .Split(new[] { ';', ',', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var invalid = codes.FirstOrDefault(static code => !IsValidStageCode(code));
        if (!string.IsNullOrWhiteSpace(invalid))
        {
            return Task.FromResult(UiOperationResult<IReadOnlyList<string>>.Fail(
                UiErrorCode.StageManagerInvalidStageCode,
                BuildInvalidStageCodeMessage(invalid)));
        }

        return Task.FromResult(UiOperationResult<IReadOnlyList<string>>.Ok(codes, BuildStageCodesValidatedMessage(codes.Length)));
    }

    private StageActivityState LoadStageActivityStateFromCache(
        string clientType,
        bool resourceTasksUpdated,
        string? coreVersion,
        bool isDebugVersion)
    {
        if (!TryLoadStageActivityJson(out var root) || root is null
            || !TryGetStageActivityClientNode(root, clientType, out var clientNode))
        {
            return StageActivityState.Empty(clientType, coreVersion) with { ResourceTasksUpdated = resourceTasksUpdated };
        }

        var resourceCollection = ParseActivityWindow(clientNode["resourceCollection"], isResourceCollection: true);
        var permanentStages = StageActivityStage.CreatePermanentStages()
            .Select(stage => IsResourceStage(stage.Value)
                ? stage with { Activity = resourceCollection }
                : stage)
            .ToList();

        // WPF still loads side-story stages for debug builds without a parseable Core version.
        var isCoreVersionParsed = SemanticVersion.TryParse(coreVersion, out var currentCoreVersion);
        if (!isCoreVersionParsed && !isDebugVersion)
        {
            return new StageActivityState(clientType, permanentStages, DateTimeOffset.UtcNow, resourceTasksUpdated, coreVersion);
        }

        // WPF inserts active stages between its default entries and permanent stages.
        var stages = permanentStages.Where(IsDefaultStage).ToList();
        var stageKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            string.Empty,
            "Pormpt1",
            "Pormpt2",
        };

        if (clientNode["sideStoryStage"] is JsonObject sideStoryStage)
        {
            foreach (var groupPair in sideStoryStage)
            {
                if (groupPair.Value is not JsonObject group)
                {
                    continue;
                }

                var groupActivity = group["Activity"] ?? group["activity"];
                _ = TryReadString(group["MinimumRequired"] ?? group["minimumRequired"], out var groupMinimumRequired);
                if ((group["Stages"] ?? group["stages"]) is not JsonArray stageArray)
                {
                    continue;
                }

                foreach (var stageNode in stageArray)
                {
                    if (stageNode is not JsonObject stageObject
                        || !TryReadString(stageObject["Value"] ?? stageObject["value"], out var value))
                    {
                        continue;
                    }

                    if (!TryReadString(stageObject["MinimumRequired"] ?? stageObject["minimumRequired"], out var minimumRequired))
                    {
                        minimumRequired = groupMinimumRequired;
                    }

                    if (!SemanticVersion.TryParse(minimumRequired, out var requiredCoreVersion))
                    {
                        continue;
                    }

                    var display = TryReadString(stageObject["Display"] ?? stageObject["display"], out var parsedDisplay)
                        ? parsedDisplay
                        : value;
                    if (!stageKeys.Add(display))
                    {
                        continue;
                    }

                    var drop = TryReadString(stageObject["Drop"] ?? stageObject["drop"], out var parsedDrop)
                        ? parsedDrop
                        : null;
                    var activity = ParseActivityWindow(
                        stageObject["Activity"] ?? stageObject["activity"] ?? groupActivity,
                        isResourceCollection: false);
                    stages.Add(new StageActivityStage(
                        display,
                        value,
                        Activity: activity,
                        Drop: drop,
                        MinimumRequired: minimumRequired,
                        IsCoreVersionSupported: isDebugVersion
                            || (isCoreVersionParsed && currentCoreVersion.CompareTo(requiredCoreVersion) >= 0)));
                }
            }
        }

        foreach (var stage in permanentStages.Where(stage => !IsDefaultStage(stage)))
        {
            // WPF appends permanent stages with Dictionary.TryAdd after activity stages.
            if (stageKeys.Add(stage.Value))
            {
                stages.Add(stage);
            }
        }

        return new StageActivityState(clientType, stages, DateTimeOffset.UtcNow, resourceTasksUpdated, coreVersion);
    }

    private string? ResolveCoreVersion(bool forceReload = false)
    {
        lock (_coreVersionGate)
        {
            if (!forceReload && _coreVersionResolved)
            {
                return _resolvedCoreVersion;
            }
        }

        string? resolved;
        try
        {
            var version = _coreVersionResolver.Invoke()?.Trim();
            resolved = string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch
        {
            resolved = null;
        }

        lock (_coreVersionGate)
        {
            _resolvedCoreVersion = resolved;
            _coreVersionResolved = true;
            return _resolvedCoreVersion;
        }
    }

    private static bool IsDebugVersion(string? version)
    {
        return !string.IsNullOrWhiteSpace(version)
            && Regex.IsMatch(
                version.Trim(),
                @"^(.*DEBUG.*|v\d+(\.\d+){1,3}-\d+-g[0-9a-f]{6,}|[^v][0-9a-f]{6,})$",
                RegexOptions.CultureInvariant);
    }

    private static bool IsDefaultStage(StageActivityStage stage)
    {
        return string.IsNullOrEmpty(stage.Value)
            || stage.Value is "Pormpt1" or "Pormpt2";
    }

    private bool TryLoadStageActivityJson(out JsonObject? root)
    {
        root = null;
        foreach (var path in EnumerateStageActivityCachePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject parsed)
                {
                    root = parsed;
                    return true;
                }
            }
            catch
            {
                // Try the next compatible cache location.
            }
        }

        return false;
    }

    private IEnumerable<string> EnumerateStageActivityCachePaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseDirectory in EnumerateBaseDirectories())
        {
            foreach (var relativePath in new[]
            {
                Path.Combine("cache", "gui", "StageActivityV2.json"),
                Path.Combine("gui", "StageActivityV2.json"),
                Path.Combine("resource", "gui", "StageActivityV2.json"),
            })
            {
                var path = Path.Combine(baseDirectory, relativePath);
                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static bool TryGetStageActivityClientNode(JsonObject root, string clientType, out JsonObject clientNode)
    {
        foreach (var pair in root)
        {
            if (string.Equals(NormalizeClientType(pair.Key), clientType, StringComparison.OrdinalIgnoreCase)
                && pair.Value is JsonObject value)
            {
                clientNode = value;
                return true;
            }
        }

        clientNode = null!;
        return false;
    }

    private static StageActivityWindow ParseActivityWindow(JsonNode? node, bool isResourceCollection)
    {
        if (node is not JsonObject activity)
        {
            return new StageActivityWindow(string.Empty, string.Empty, DateTime.MinValue, DateTime.MinValue, isResourceCollection);
        }

        var timezone = activity["TimeZone"] is JsonValue timezoneValue
            && timezoneValue.TryGetValue(out int timezoneOffset)
            ? timezoneOffset
            : 0;
        var start = ParseActivityTime(activity["UtcStartTime"], timezone);
        var expire = ParseActivityTime(activity["UtcExpireTime"], timezone);
        _ = TryReadString(activity["Tip"], out var tip);
        _ = TryReadString(activity["StageName"], out var stageName);
        return new StageActivityWindow(tip, stageName, start, expire, isResourceCollection);
    }

    private static DateTime ParseActivityTime(JsonNode? node, int timezoneOffset)
    {
        if (!TryReadString(node, out var text))
        {
            return DateTime.MinValue;
        }

        if (DateTime.TryParseExact(
                text,
                "yyyy/MM/dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return DateTime.SpecifyKind(parsed.AddHours(-timezoneOffset), DateTimeKind.Utc);
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed)
            ? parsed.ToUniversalTime()
            : DateTime.MinValue;
    }

    private static bool IsResourceStage(string stageCode)
    {
        return stageCode is "CE-6" or "AP-5" or "CA-5" or "LS-6" or "SK-5"
            or "PR-A-1" or "PR-A-2" or "PR-B-1" or "PR-B-2"
            or "PR-C-1" or "PR-C-2" or "PR-D-1" or "PR-D-2";
    }

    private sealed record SemanticVersion(int Major, int Minor, int Patch, IReadOnlyList<string> PreRelease)
        : IComparable<SemanticVersion>
    {
        public static bool TryParse(string? value, out SemanticVersion version)
        {
            version = null!;
            var normalized = value?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (normalized.StartsWith('v'))
            {
                normalized = normalized[1..];
            }

            var buildSeparator = normalized.IndexOf('+');
            if (buildSeparator >= 0)
            {
                normalized = normalized[..buildSeparator];
            }

            var prereleaseSeparator = normalized.IndexOf('-');
            var core = prereleaseSeparator >= 0 ? normalized[..prereleaseSeparator] : normalized;
            var prerelease = prereleaseSeparator >= 0 ? normalized[(prereleaseSeparator + 1)..] : string.Empty;
            var components = core.Split('.');
            if (components.Length != 3
                || !int.TryParse(components[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
                || !int.TryParse(components[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
                || !int.TryParse(components[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            {
                return false;
            }

            var identifiers = string.IsNullOrEmpty(prerelease) ? [] : prerelease.Split('.');
            if (identifiers.Any(static identifier => string.IsNullOrWhiteSpace(identifier)
                || identifier.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
            {
                return false;
            }

            version = new SemanticVersion(major, minor, patch, identifiers);
            return true;
        }

        public int CompareTo(SemanticVersion? other)
        {
            if (other is null)
            {
                return 1;
            }

            var coreComparison = Major.CompareTo(other.Major);
            coreComparison = coreComparison != 0 ? coreComparison : Minor.CompareTo(other.Minor);
            coreComparison = coreComparison != 0 ? coreComparison : Patch.CompareTo(other.Patch);
            if (coreComparison != 0)
            {
                return coreComparison;
            }

            if (PreRelease.Count == 0 || other.PreRelease.Count == 0)
            {
                return PreRelease.Count == other.PreRelease.Count ? 0 : (PreRelease.Count == 0 ? 1 : -1);
            }

            for (var index = 0; index < Math.Min(PreRelease.Count, other.PreRelease.Count); index++)
            {
                var left = PreRelease[index];
                var right = other.PreRelease[index];
                var leftIsNumber = int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
                var rightIsNumber = int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
                var comparison = leftIsNumber && rightIsNumber
                    ? leftNumber.CompareTo(rightNumber)
                    : leftIsNumber ? -1 : rightIsNumber ? 1 : string.CompareOrdinal(left, right);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return PreRelease.Count.CompareTo(other.PreRelease.Count);
        }
    }

    private async Task<FetchResult> FetchMaaApiResourceAsync(string relativePath, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(_baseDirectory, "cache", relativePath.Replace('/', Path.DirectorySeparatorChar));
        var etagPath = cachePath + ".etag";
        var etag = TryReadText(etagPath);

        foreach (var baseUrl in new[] { MaaApiBaseUrl, MaaApiFallbackBaseUrl })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), relativePath));
            if (!string.IsNullOrWhiteSpace(etag) && EntityTagHeaderValue.TryParse(etag, out var entityTag))
            {
                request.Headers.IfNoneMatch.Add(entityTag);
            }

            try
            {
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    return File.Exists(cachePath) ? new FetchResult(true, false) : FetchResult.Empty;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (JsonNode.Parse(content) is null)
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(cachePath, content, cancellationToken).ConfigureAwait(false);
                if (relativePath.EndsWith("resource/tasks.json", StringComparison.OrdinalIgnoreCase))
                {
                    var tasksDirectory = Path.Combine(Path.GetDirectoryName(cachePath)!, "tasks");
                    Directory.CreateDirectory(tasksDirectory);
                    await File.WriteAllTextAsync(
                        Path.Combine(tasksDirectory, "tasks.json"),
                        content,
                        cancellationToken).ConfigureAwait(false);
                }

                var receivedEtag = response.Headers.ETag?.ToString();
                if (!string.IsNullOrWhiteSpace(receivedEtag))
                {
                    await File.WriteAllTextAsync(etagPath, receivedEtag, cancellationToken).ConfigureAwait(false);
                }

                return new FetchResult(true, true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Try the mirror endpoint.
            }
            catch (HttpRequestException)
            {
                // Try the mirror endpoint.
            }
            catch (IOException)
            {
                // Try the mirror endpoint.
            }
            catch (JsonException)
            {
                // Invalid API data is treated like an unavailable response.
            }
        }

        // StageManager.UpdateStageWeb disables cache fallback for these resources.
        return FetchResult.Empty;
    }

    private static string? TryReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private StageManagerState BuildState(
        string clientType,
        StageSnapshot? localSnapshot,
        StageSnapshot? webSnapshot)
    {
        var fallbackStageCodes = _configService is null
            ? Array.Empty<string>()
            : ReadStageCodesFromConfig(_configService.CurrentConfig);

        var localStageCodes = localSnapshot?.StageCodes.Count > 0 == true
            ? localSnapshot.StageCodes
            : fallbackStageCodes;
        var webStageCodes = webSnapshot?.StageCodes ?? Array.Empty<string>();

        return new StageManagerState(
            ClientType: clientType,
            LocalStageCodes: localStageCodes,
            WebStageCodes: webStageCodes,
            WebSourceUrl: webSnapshot?.SourceUrl,
            LocalRefreshedAt: localSnapshot?.RefreshedAt,
            WebRefreshedAt: webSnapshot?.RefreshedAt);
    }

    private StageSnapshot? EnsureSnapshot(string clientType, bool preferWeb, bool forceReload)
    {
        var cache = preferWeb ? _webSnapshots : _localSnapshots;
        if (!forceReload)
        {
            var cached = GetCachedSnapshot(cache, clientType);
            if (cached is not null)
            {
                return cached;
            }
        }

        if (!TryLoadSnapshot(clientType, preferWeb, out var loadedSnapshot, out _))
        {
            return GetCachedSnapshot(cache, clientType);
        }

        lock (_snapshotGate)
        {
            cache[clientType] = loadedSnapshot!;
        }

        return loadedSnapshot;
    }

    private static StageSnapshot? GetCachedSnapshot(IDictionary<string, StageSnapshot> cache, string clientType)
    {
        lock (cache)
        {
            return cache.TryGetValue(clientType, out var snapshot) ? snapshot : null;
        }
    }

    private bool TryLoadSnapshot(
        string clientType,
        bool preferWeb,
        out StageSnapshot? snapshot,
        out string errorMessage)
    {
        snapshot = null;
        errorMessage = string.Empty;

        var source = preferWeb
            ? ResolveWebSource(clientType)
            : ResolveLocalSource(clientType);
        if (source is null)
        {
            errorMessage = BuildStageResourcesMissingMessage(clientType, preferWeb);
            return false;
        }

        if (!TryReadStageCodes(source, out var stageCodes, out errorMessage))
        {
            return false;
        }

        snapshot = new StageSnapshot(
            clientType,
            stageCodes,
            source.SourceUrl,
            DateTimeOffset.UtcNow);
        return true;
    }

    private StageSourceDescriptor? ResolveLocalSource(string clientType)
    {
        foreach (var baseDirectory in EnumerateBaseDirectories())
        {
            var source = TryBuildSource(Path.Combine(baseDirectory, "resource"), clientType);
            if (source is not null)
            {
                return source;
            }
        }

        return null;
    }

    private StageSourceDescriptor? ResolveWebSource(string clientType)
    {
        foreach (var baseDirectory in EnumerateBaseDirectories())
        {
            foreach (var webRootName in WebRootNames)
            {
                var source = TryBuildSource(Path.Combine(baseDirectory, webRootName, "resource"), clientType);
                if (source is not null)
                {
                    return source;
                }
            }

            var currentName = Path.GetFileName(baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (WebRootNames.Any(name => string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase)))
            {
                var source = TryBuildSource(Path.Combine(baseDirectory, "resource"), clientType);
                if (source is not null)
                {
                    return source;
                }
            }
        }

        return null;
    }

    private static StageSourceDescriptor? TryBuildSource(string resourceRoot, string clientType)
    {
        var commonStagesPath = Path.Combine(resourceRoot, "stages.json");
        var commonTasksPath = Path.Combine(resourceRoot, "tasks", "tasks.json");
        var clientFolder = NormalizeClientDirectory(clientType);
        var clientStagesPath = Path.Combine(resourceRoot, "global", clientFolder, "resource", "stages.json");
        var clientTasksPath = Path.Combine(resourceRoot, "global", clientFolder, "resource", "tasks", "tasks.json");

        var stagesPath = File.Exists(clientStagesPath)
            ? clientStagesPath
            : File.Exists(commonStagesPath)
                ? commonStagesPath
                : null;
        var tasksPath = File.Exists(clientTasksPath)
            ? clientTasksPath
            : File.Exists(commonTasksPath)
                ? commonTasksPath
                : null;

        if (stagesPath is null && tasksPath is null)
        {
            return null;
        }

        var sourceFile = stagesPath ?? tasksPath!;
        return new StageSourceDescriptor(
            stagesPath,
            tasksPath,
            new Uri(sourceFile).AbsoluteUri);
    }

    private bool TryReadStageCodes(
        StageSourceDescriptor source,
        out IReadOnlyList<string> stageCodes,
        out string errorMessage)
    {
        var orderedCodes = new List<string>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!string.IsNullOrWhiteSpace(source.StagesPath))
            {
                if (!TryReadJsonRootFromFile(source.StagesPath!, out var root, out var readError))
                {
                    stageCodes = Array.Empty<string>();
                    errorMessage = BuildStageResourcesReadFailedMessage(source.SourceUrl, readError);
                    return false;
                }

                AppendStageCodesFromStagesJson(root, orderedCodes, seenCodes);
            }

            if (!string.IsNullOrWhiteSpace(source.TasksPath))
            {
                if (!TryReadJsonRootFromFile(source.TasksPath!, out var root, out var readError))
                {
                    stageCodes = Array.Empty<string>();
                    errorMessage = BuildStageResourcesReadFailedMessage(source.SourceUrl, readError);
                    return false;
                }

                AppendStageCodesFromTasksJson(root, orderedCodes, seenCodes);
            }
        }
        catch (Exception ex)
        {
            stageCodes = Array.Empty<string>();
            errorMessage = BuildStageResourcesReadFailedMessage(source.SourceUrl, ex.Message);
            return false;
        }

        stageCodes = orderedCodes;
        errorMessage = string.Empty;
        return orderedCodes.Count > 0;
    }

    private bool TryReadJsonRootFromFile(
        string path,
        out JsonNode? root,
        out string errorMessage)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                root = null;
                errorMessage = BuildStageResourceFileNotFoundMessage(path);
                return false;
            }

            var cacheKey = fileInfo.FullName;
            var cacheStamp = new ResourceCacheStamp(fileInfo.LastWriteTimeUtc, fileInfo.Length);
            lock (_resourceCacheGate)
            {
                if (_resourceJsonCache.TryGetValue(cacheKey, out var cached)
                    && cached.Stamp.Equals(cacheStamp))
                {
                    root = cached.Root;
                    errorMessage = string.Empty;
                    return true;
                }
            }

            var parsed = JsonNode.Parse(File.ReadAllText(cacheKey));
            lock (_resourceCacheGate)
            {
                _resourceJsonCache[cacheKey] = new ResourceJsonCacheEntry(cacheStamp, parsed);
            }

            root = parsed;
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            root = null;
            errorMessage = ex.Message;
            return false;
        }
    }

    private static void AppendStageCodesFromStagesJson(
        JsonNode? root,
        ICollection<string> target,
        ISet<string> seen)
    {
        if (root is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node is not JsonObject obj || !TryReadString(obj["code"], out var code))
                {
                    continue;
                }

                AddStageCode(code, target, seen);
            }

            return;
        }

        if (root is not JsonObject objectRoot)
        {
            return;
        }

        foreach (var pair in objectRoot)
        {
            if (pair.Value is not JsonObject obj || !TryReadString(obj["code"], out var code))
            {
                continue;
            }

            AddStageCode(code, target, seen);
        }
    }

    private static void AppendStageCodesFromTasksJson(
        JsonNode? root,
        ICollection<string> target,
        ISet<string> seen)
    {
        if (root is not JsonObject objectRoot)
        {
            return;
        }

        foreach (var pair in objectRoot)
        {
            if (!ShouldTreatTaskKeyAsStageCode(pair.Key))
            {
                continue;
            }

            AddStageCode(pair.Key, target, seen);
        }
    }

    private static bool ShouldTreatTaskKeyAsStageCode(string key)
    {
        var normalized = key.Trim();
        if (string.Equals(normalized, "Annihilation", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!normalized.Contains('-', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '#')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static void AddStageCode(string code, ICollection<string> target, ISet<string> seen)
    {
        var normalized = code.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
        {
            return;
        }

        target.Add(normalized);
    }

    private static IReadOnlyList<string> ReadStageCodesFromConfig(UnifiedConfig config)
    {
        if (!config.GlobalValues.TryGetValue("Advanced.StageManager.StageCodes", out var node) || node is null)
        {
            return Array.Empty<string>();
        }

        var text = node.ToString();
        return text.Split(new[] { ';', ',', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string ReadConfiguredClientType()
    {
        if (_configService is null)
        {
            return DefaultClientType;
        }

        return ReadString(_configService.CurrentConfig, "Advanced.StageManager.ClientType", DefaultClientType);
    }

    private static string NormalizeClientType(string? clientType)
    {
        var normalized = string.IsNullOrWhiteSpace(clientType) ? DefaultClientType : clientType.Trim();
        return string.Equals(normalized, "Bilibili", StringComparison.OrdinalIgnoreCase)
            ? DefaultClientType
            : normalized;
    }

    private static string NormalizeClientDirectory(string clientType)
    {
        return NormalizeClientType(clientType);
    }

    private static string ResolveBaseDirectory(UnifiedConfigurationService? configService, string? baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            return Path.GetFullPath(baseDirectory);
        }

        if (configService is not null)
        {
            try
            {
                var field = typeof(UnifiedConfigurationService).GetField("_baseDirectory", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field?.GetValue(configService) is string configuredBaseDirectory && !string.IsNullOrWhiteSpace(configuredBaseDirectory))
                {
                    return Path.GetFullPath(configuredBaseDirectory);
                }
            }
            catch
            {
                // Fall back to AppContext below.
            }
        }

        return RuntimeLayout.ResolveRuntimeBaseDirectory();
    }

    private IEnumerable<string> EnumerateBaseDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new DirectoryInfo(_baseDirectory);
        while (current is not null)
        {
            if (seen.Add(current.FullName))
            {
                yield return current.FullName;
            }

            current = current.Parent;
        }
    }

    private static string ReadString(UnifiedConfig config, string key, string fallback)
    {
        if (config.GlobalValues.TryGetValue(key, out var node) && node is not null)
        {
            var text = node.ToString().Trim();
            if (text.Length > 0)
            {
                return text;
            }
        }

        return fallback;
    }

    private static bool ReadBool(UnifiedConfig config, string key, bool fallback)
    {
        if (config.GlobalValues.TryGetValue(key, out var node) && node is not null)
        {
            if (bool.TryParse(node.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    private static bool IsValidStageCode(string code)
    {
        foreach (var ch in code)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '#')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool TryReadString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue(out string? text) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text.Trim();
        return true;
    }

    private string BuildStageManagerStateLoadedMessage()
    {
        return FormatStageManagerMessage(
            CreateLocalizer(),
            "Toolbox.Advanced.StageManager.Status.StateLoaded",
            "Loaded stage manager state.");
    }

    private string BuildStageResourcesLoadedMessage(string clientType, bool preferWeb)
    {
        var localizer = CreateLocalizer();
        return FormatStageManagerMessage(
            localizer,
            preferWeb
                ? "Toolbox.Advanced.StageManager.Status.WebResourcesLoaded"
                : "Toolbox.Advanced.StageManager.Status.LocalResourcesLoaded",
            preferWeb
                ? "Loaded web stage resources for `{0}`."
                : "Loaded local stage resources for `{0}`.",
            clientType);
    }

    private string BuildStageActivityResourcesLoadedMessage(string clientType, bool updated)
    {
        return updated
            ? $"Updated stage activity resources for `{clientType}`."
            : $"Loaded cached stage activity resources for `{clientType}`.";
    }

    private string BuildStageActivityApiUnavailableMessage(string clientType)
    {
        return $"Unable to load stage activity resources for `{clientType}` from the MAA API or local cache.";
    }

    private string BuildStageManagerConfigLoadedMessage()
    {
        return FormatStageManagerMessage(
            CreateLocalizer(),
            "Toolbox.Advanced.StageManager.Status.ConfigLoaded",
            "Loaded stage manager config.");
    }

    private string BuildStageManagerServiceUnavailableMessage()
    {
        return FormatStageManagerMessage(
            CreateLocalizer(),
            "Toolbox.Advanced.StageManager.Error.ServiceUnavailable",
            "Stage manager service is not initialized.");
    }

    private string BuildStageManagerConfigSavedMessage()
    {
        return FormatStageManagerMessage(
            CreateLocalizer(),
            "Toolbox.Advanced.StageManager.Status.ConfigSaved",
            "Stage manager config saved.");
    }

    private string BuildInvalidStageCodeMessage(string code)
    {
        return FormatStageManagerMessage(
            CreateLocalizer(),
            "Toolbox.Advanced.StageManager.Error.InvalidStageCode",
            "Invalid stage code: {0}",
            code);
    }

    private string BuildStageCodesValidatedMessage(int count)
    {
        return FormatStageManagerMessage(
            CreateLocalizer(),
            "Toolbox.Advanced.StageManager.Status.Validated",
            "Validated {0} stage code(s).",
            count);
    }

    private string BuildStageResourcesMissingMessage(string clientType, bool preferWeb)
    {
        var localizer = CreateLocalizer();
        return FormatStageManagerMessage(
            localizer,
            preferWeb
                ? "Toolbox.Advanced.StageManager.Error.NoWebResources"
                : "Toolbox.Advanced.StageManager.Error.NoLocalResources",
            preferWeb
                ? "No web stage resources found for `{0}` under `{1}`."
                : "No local stage resources found for `{0}` under `{1}`.",
            clientType,
            _baseDirectory);
    }

    private string BuildStageResourcesReadFailedMessage(string sourceUrl, string reason)
    {
        return FormatStageManagerMessage(
            CreateLocalizer(),
            "Toolbox.Advanced.StageManager.Error.ReadResources",
            "Failed to read stage resources from `{0}`: {1}",
            sourceUrl,
            reason);
    }

    private string BuildStageResourceFileNotFoundMessage(string path)
    {
        return FormatStageManagerMessage(
            CreateLocalizer(),
            "Toolbox.Advanced.StageManager.Error.FileNotFound",
            "File not found: {0}",
            path);
    }

    private IUiLocalizer CreateLocalizer()
    {
        return UiLocalizer.Create(ResolveLanguage());
    }

    private string ResolveLanguage()
    {
        if (_configService?.CurrentConfig.GlobalValues.TryGetValue("GUI.Localization", out var value) == true
            && value is JsonValue jsonValue
            && jsonValue.TryGetValue(out string? language)
            && !string.IsNullOrWhiteSpace(language))
        {
            return UiLanguageCatalog.Normalize(language);
        }

        return UiLanguageCatalog.DefaultLanguage;
    }

    private static string FormatStageManagerMessage(
        IUiLocalizer localizer,
        string key,
        string fallback,
        params object[] args)
    {
        var template = localizer.GetOrDefault(key, fallback, "Toolbox.Advanced.StageManager");
        return args.Length == 0
            ? template
            : string.Format(CultureInfo.CurrentCulture, template, args);
    }

    private sealed record StageSourceDescriptor(
        string? StagesPath,
        string? TasksPath,
        string SourceUrl);

    private sealed record ResourceCacheStamp(DateTime LastWriteTimeUtc, long Length);

    private sealed record ResourceJsonCacheEntry(ResourceCacheStamp Stamp, JsonNode? Root);

    private sealed record StageSnapshot(
        string ClientType,
        IReadOnlyList<string> StageCodes,
        string SourceUrl,
        DateTimeOffset RefreshedAt);

    private sealed record FetchResult(bool Success, bool Updated)
    {
        public static FetchResult Empty { get; } = new(false, false);
    }
}
