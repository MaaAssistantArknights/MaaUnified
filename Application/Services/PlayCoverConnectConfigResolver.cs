namespace MAAUnified.Application.Services;

public static class PlayCoverConnectConfigResolver
{
    public const string DefaultPlayToolsAddress = "127.0.0.1:1717";

    public static string ResolveEffectiveConnectConfig(string? connectConfig, string? playCoverScreencapMode)
    {
        var normalizedConnectConfig = (connectConfig ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedConnectConfig))
        {
            normalizedConnectConfig = "General";
        }

        if (!IsPlayCoverConnectConfig(normalizedConnectConfig))
        {
            return normalizedConnectConfig;
        }

        if (string.IsNullOrWhiteSpace(playCoverScreencapMode)
            && !string.Equals(normalizedConnectConfig, "MacPlayTools", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizePlayCoverConnectConfig(normalizedConnectConfig);
        }

        return NormalizePlayCoverScreencapMode(playCoverScreencapMode) switch
        {
            "BGR" => "MacBGR",
            "MacSCK" => "MacSCK",
            _ => "CompatMac",
        };
    }

    public static string NormalizePlayCoverConnectConfig(string? connectConfig)
    {
        if (string.Equals(connectConfig, "MacBGR", StringComparison.OrdinalIgnoreCase))
        {
            return "MacBGR";
        }

        if (string.Equals(connectConfig, "MacSCK", StringComparison.OrdinalIgnoreCase))
        {
            return "MacSCK";
        }

        return "CompatMac";
    }

    public static bool IsPlayCoverConnectConfig(string? connectConfig)
    {
        return string.Equals(connectConfig, "MacPlayTools", StringComparison.OrdinalIgnoreCase)
            || string.Equals(connectConfig, "CompatMac", StringComparison.OrdinalIgnoreCase)
            || string.Equals(connectConfig, "MacSCK", StringComparison.OrdinalIgnoreCase)
            || string.Equals(connectConfig, "MacBGR", StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveDefaultConnectAddress(string? connectConfig, string fallbackAddress = "127.0.0.1:5555")
        => IsPlayCoverConnectConfig(connectConfig) ? DefaultPlayToolsAddress : fallbackAddress;

    public static string NormalizePlayCoverScreencapMode(string? playCoverScreencapMode)
    {
        var normalized = (playCoverScreencapMode ?? string.Empty).Trim();
        if (string.Equals(normalized, "MacSCK", StringComparison.OrdinalIgnoreCase))
        {
            return "MacSCK";
        }

        if (string.Equals(normalized, "MacBGR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "BGR", StringComparison.OrdinalIgnoreCase))
        {
            return "BGR";
        }

        return "RGBA";
    }

    public static string ResolveTouchMode(
        string? connectConfig,
        string? configuredTouchMode,
        string fallbackTouchMode = "MaaFwAdb",
        string playCoverTouchMode = "MacPlayTools")
    {
        var normalizedTouchMode = (configuredTouchMode ?? string.Empty).Trim();
        if (IsPlayCoverConnectConfig(connectConfig))
        {
            return playCoverTouchMode;
        }

        return string.IsNullOrWhiteSpace(normalizedTouchMode)
            || string.Equals(normalizedTouchMode, playCoverTouchMode, StringComparison.OrdinalIgnoreCase)
            ? fallbackTouchMode
            : normalizedTouchMode;
    }
}
