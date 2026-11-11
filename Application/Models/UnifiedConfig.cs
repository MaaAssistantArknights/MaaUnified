using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MAAUnified.Application.Security;

namespace MAAUnified.Application.Models;

public sealed class UnifiedConfig
{
    private Dictionary<string, UnifiedProfile> _profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Default"] = new UnifiedProfile(),
    };
    private Dictionary<string, JsonNode?> _globalValues = new(StringComparer.OrdinalIgnoreCase);
    private string _currentProfile = "Default";
    private SensitiveStorageMetadata _sensitiveStorage = new();
    private UnifiedMigrationMetadata _migration = new();
    private bool _containsPersistedNullProfile;

    public const int LatestSchemaVersion = 3;

    public int SchemaVersion { get; set; } = LatestSchemaVersion;

    public string CurrentProfile
    {
        get => _currentProfile;
        set => _currentProfile = string.IsNullOrWhiteSpace(value) ? "Default" : value;
    }

    public Dictionary<string, UnifiedProfile> Profiles
    {
        get => _profiles;
        set => _profiles = CopyProfilesCaseInsensitive(value);
    }

    public Dictionary<string, JsonNode?> GlobalValues
    {
        get => _globalValues;
        set => _globalValues = CopyCaseInsensitive(value);
    }

    [JsonPropertyName("sensitiveStorage")]
    public SensitiveStorageMetadata SensitiveStorage
    {
        get => _sensitiveStorage;
        set => _sensitiveStorage = value ?? new();
    }

    public UnifiedMigrationMetadata Migration
    {
        get => _migration;
        set => _migration = value ?? new();
    }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    // The serializer cannot report repaired collection members, so retain this until the repair is persisted.
    internal bool HasPersistedNullEntries => _containsPersistedNullProfile
        || _profiles.Values.Any(profile => profile.HasPersistedNullTaskEntries);

    internal void MarkNullEntryRepairsPersisted()
    {
        _containsPersistedNullProfile = false;
        foreach (var profile in _profiles.Values)
        {
            profile.MarkNullTaskEntryRepairsPersisted();
        }
    }

    private static Dictionary<string, TValue> CopyCaseInsensitive<TValue>(
        IEnumerable<KeyValuePair<string, TValue>>? values)
    {
        var result = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
        {
            return result;
        }

        foreach (var (key, value) in values)
        {
            result[key] = value;
        }

        return result;
    }

    private Dictionary<string, UnifiedProfile> CopyProfilesCaseInsensitive(
        IEnumerable<KeyValuePair<string, UnifiedProfile>>? values)
    {
        var result = new Dictionary<string, UnifiedProfile>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
        {
            _containsPersistedNullProfile = true;
            return result;
        }

        foreach (var (key, value) in values)
        {
            // A persisted null profile has no meaningful payload; retain its name as an empty profile.
            _containsPersistedNullProfile |= value is null;
            result[key] = value ?? new UnifiedProfile();
        }

        return result;
    }
}

public sealed class UnifiedProfile
{
    private Dictionary<string, JsonNode?> _values = new(StringComparer.OrdinalIgnoreCase);
    private List<UnifiedTaskItem> _taskQueue = [];
    private bool _containsPersistedNullTask;

    public Dictionary<string, JsonNode?> Values
    {
        get => _values;
        set => _values = CopyCaseInsensitive(value);
    }

    public List<UnifiedTaskItem> TaskQueue
    {
        get => _taskQueue;
        set
        {
            if (value is null)
            {
                _containsPersistedNullTask = true;
                _taskQueue = [];
                return;
            }

            _taskQueue = value.Where(static task => task is not null).ToList();
            _containsPersistedNullTask |= _taskQueue.Count != value.Count;
        }
    }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    internal bool HasPersistedNullTaskEntries => _containsPersistedNullTask;

    internal void MarkNullTaskEntryRepairsPersisted() => _containsPersistedNullTask = false;

    private static Dictionary<string, JsonNode?> CopyCaseInsensitive(
        IEnumerable<KeyValuePair<string, JsonNode?>>? values)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
        {
            return result;
        }

        foreach (var (key, value) in values)
        {
            result[key] = value;
        }

        return result;
    }
}

public sealed class UnifiedTaskItem
{
    private string _type = "Unknown";
    private string _name = "UnnamedTask";
    private JsonObject _params = [];

    public string Type { get => _type; set => _type = value ?? "Unknown"; }

    public string Name { get => _name; set => _name = value ?? "UnnamedTask"; }

    public bool? IsEnabled { get; set; } = true;

    public JsonObject Params { get => _params; set => _params = value ?? []; }

    // Compatibility read path for schema v1 config.
    [JsonPropertyName("RawTask")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? LegacyRawTask { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class UnifiedMigrationMetadata
{
    private List<string> _warnings = [];

    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;

    public string ImportedBy { get; set; } = "MAAUnified";

    public bool ImportedFromGuiNew { get; set; }

    public bool ImportedFromGui { get; set; }

    public List<string> Warnings
    {
        get => _warnings;
        set => _warnings = value ?? [];
    }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
