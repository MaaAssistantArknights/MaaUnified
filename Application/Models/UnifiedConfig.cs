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

    public const int LatestSchemaVersion = 3;

    public int SchemaVersion { get; set; } = LatestSchemaVersion;

    public string CurrentProfile { get; set; } = "Default";

    public Dictionary<string, UnifiedProfile> Profiles
    {
        get => _profiles;
        set => _profiles = CopyCaseInsensitive(value);
    }

    public Dictionary<string, JsonNode?> GlobalValues
    {
        get => _globalValues;
        set => _globalValues = CopyCaseInsensitive(value);
    }

    [JsonPropertyName("sensitiveStorage")]
    public SensitiveStorageMetadata SensitiveStorage { get; set; } = new();

    public UnifiedMigrationMetadata Migration { get; set; } = new();

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
}

public sealed class UnifiedProfile
{
    private Dictionary<string, JsonNode?> _values = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, JsonNode?> Values
    {
        get => _values;
        set => _values = CopyCaseInsensitive(value);
    }

    public List<UnifiedTaskItem> TaskQueue { get; set; } = [];

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
    public string Type { get; set; } = "Unknown";

    public string Name { get; set; } = "UnnamedTask";

    public bool? IsEnabled { get; set; } = true;

    public JsonObject Params { get; set; } = [];

    // Compatibility read path for schema v1 config.
    [JsonPropertyName("RawTask")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? LegacyRawTask { get; set; }
}

public sealed class UnifiedMigrationMetadata
{
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;

    public string ImportedBy { get; set; } = "MAAUnified";

    public bool ImportedFromGuiNew { get; set; }

    public bool ImportedFromGui { get; set; }

    public List<string> Warnings { get; set; } = [];
}
