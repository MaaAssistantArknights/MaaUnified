using System.Text.Json;
using System.Text.Json.Serialization;

namespace MAAUnified.Application.Security;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SensitiveStorageBackendKind
{
    Uninitialized = 0,
    SystemProtected = 1,
    LinuxLocalSqlCipher = 2,
    Plaintext = 3,
}

public sealed class SensitiveStorageMetadata
{
    public const int LatestFormatVersion = 1;

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = LatestFormatVersion;

    [JsonPropertyName("backend")]
    public SensitiveStorageBackendKind Backend { get; set; } = SensitiveStorageBackendKind.Uninitialized;

    [JsonPropertyName("storeId")]
    public string StoreId { get; set; } = string.Empty;

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
