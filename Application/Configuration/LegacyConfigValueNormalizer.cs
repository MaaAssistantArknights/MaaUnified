namespace MAAUnified.Application.Configuration;

internal static class LegacyConfigValueNormalizer
{
    public static System.Text.Json.Nodes.JsonNode? NormalizeProfileValue(
        string key,
        System.Text.Json.Nodes.JsonNode? value)
        => LegacyConfigValueMappings.NormalizeProfileValue(key, value);

    public static string NormalizeClientType(string? value)
        => LegacyConfigValueMappings.NormalizeClientType(value);
}
