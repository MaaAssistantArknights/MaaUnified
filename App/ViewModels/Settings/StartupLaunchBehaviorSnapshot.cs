using System.Globalization;
using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Compat.Constants;

namespace MAAUnified.App.ViewModels.Settings;

internal sealed record StartupLaunchBehaviorSnapshot(
    bool RunDirectly,
    bool MinimizeDirectly,
    bool OpenEmulatorAfterLaunch,
    bool SkipStartupAutoRunAfterUpdate,
    int StartupAutoRunCountdownSeconds)
{
    /// <summary>WPF <c>60cf643a2d</c>：启动自动运行倒计时默认 10 s。</summary>
    public const int DefaultStartupAutoRunCountdownSeconds = 10;

    public static StartupLaunchBehaviorSnapshot Default { get; } = new(
        RunDirectly: false,
        MinimizeDirectly: false,
        OpenEmulatorAfterLaunch: false,
        SkipStartupAutoRunAfterUpdate: true,
        StartupAutoRunCountdownSeconds: DefaultStartupAutoRunCountdownSeconds);

    public static StartupLaunchBehaviorSnapshot FromConfig(UnifiedConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new StartupLaunchBehaviorSnapshot(
            RunDirectly: ReadCurrentProfileBool(config, ConfigurationKeys.RunDirectly, false),
            MinimizeDirectly: ReadGlobalBool(config, ConfigurationKeys.MinimizeDirectly, false),
            OpenEmulatorAfterLaunch: ReadCurrentProfileBool(config, ConfigurationKeys.StartEmulator, false),
            // 旧配置无该键时按 WPF 默认开启：更新后立即重启不自动运行。
            SkipStartupAutoRunAfterUpdate: ReadCurrentProfileBool(config, ConfigurationKeys.SkipStartupAutoRunAfterUpdate, true),
            // 0 = 关闭倒计时确认；负值按 0 处理，避免配置脏值导致异常等待。
            StartupAutoRunCountdownSeconds: Math.Max(
                0,
                ReadCurrentProfileInt(
                    config,
                    ConfigurationKeys.StartupAutoRunCountdownSeconds,
                    DefaultStartupAutoRunCountdownSeconds)));
    }

    private static int ReadCurrentProfileInt(UnifiedConfig config, string key, int fallback)
    {
        if (!config.Profiles.TryGetValue(config.CurrentProfile, out var profile))
        {
            return fallback;
        }

        return ReadInt(profile.Values, key, fallback);
    }

    private static int ReadInt(IDictionary<string, JsonNode?> values, string key, int fallback)
    {
        if (!values.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue ? 1 : 0;
            }

            if (value.TryGetValue<string>(out var stringValue)
                && int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fallbackParsed)
            ? fallbackParsed
            : fallback;
    }

    private static bool ReadCurrentProfileBool(UnifiedConfig config, string key, bool fallback)
    {
        if (!config.Profiles.TryGetValue(config.CurrentProfile, out var profile))
        {
            return fallback;
        }

        return ReadBool(profile.Values, key, fallback);
    }

    private static bool ReadGlobalBool(UnifiedConfig config, string key, bool fallback)
        => ReadBool(config.GlobalValues, key, fallback);

    private static bool ReadBool(IDictionary<string, JsonNode?> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue;
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue != 0;
            }

            if (value.TryGetValue<string>(out var stringValue))
            {
                if (bool.TryParse(stringValue, out var parsedBool))
                {
                    return parsedBool;
                }

                if (int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
                {
                    return parsedInt != 0;
                }
            }
        }

        return bool.TryParse(node.ToString(), out var parsedFallback) ? parsedFallback : fallback;
    }
}
