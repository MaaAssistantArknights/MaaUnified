using System.Globalization;
using System.Text.Json.Nodes;

namespace MAAUnified.Application.Configuration;

internal static class LegacyConfigValueMappings
{
    private static readonly char[] LegacyListSeparators = ['\r', '\n', ';', '；', ',', '|'];

    private static readonly Dictionary<string, string> ProfileKeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Connect.Address"] = "ConnectAddress",
        ["Connect.ConnectConfig"] = "ConnectConfig",
        ["Connect.AdbPath"] = "AdbPath",
        ["Connect.AddressHistory"] = "ConnectAddressHistory",
        ["Connect.TouchMode"] = "TouchMode",
        ["Connect.AutoDetect"] = "AutoDetect",
        ["Connect.AlwaysAutoDetect"] = "AlwaysAutoDetect",
        ["Connect.RetryOnDisconnected"] = "RetryOnDisconnected",
        ["Connect.AllowADBRestart"] = "AllowAdbRestart",
        ["Connect.AllowADBHardRestart"] = "AllowAdbHardRestart",
        ["Connect.AdbLiteEnabled"] = "AdbLiteEnabled",
        ["Connect.KillAdbOnExit"] = "KillAdbOnExit",
        ["Connect.AdbReplaced"] = "AdbReplaced",
        ["Connect.MuMu12Extras.Enabled"] = "MuMu12ExtrasEnabled",
        ["Connect.MuMu12EmulatorPath"] = "MuMu12EmulatorPath",
        ["Connect.MumuBridgeConnection"] = "MuMuBridgeConnection",
        ["Connect.MuMu12Index"] = "MuMu12Index",
        ["Connect.LdPlayerExtras.Enabled"] = "LdPlayerExtrasEnabled",
        ["Connect.LdPlayerEmulatorPath"] = "LdPlayerEmulatorPath",
        ["Connect.LdPlayerManualSetIndex"] = "LdPlayerManualSetIndex",
        ["Connect.LdPlayerIndex"] = "LdPlayerIndex",
        ["Connect.AttachWindow.ScreencapMethod"] = "AttachWindowScreencapMethod",
        ["Connect.AttachWindow.MouseMethod"] = "AttachWindowMouseMethod",
        ["Connect.AttachWindow.KeyboardMethod"] = "AttachWindowKeyboardMethod",
        ["Start.ClientType"] = "ClientType",
        ["Start.StartGame"] = "StartGame",
        ["Penguin.Id"] = "PenguinId",
        ["Penguin.EnablePenguin"] = "EnablePenguin",
        ["Yituliu.EnableYituliu"] = "EnableYituliu",
    };

    private static readonly Dictionary<string, string> GlobalKeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GUI.DarkMode"] = "Theme.Mode",
        ["GUI.LogItemDateFormat"] = "GUI.LogItemDateFormatString",
        ["GUI.WindowPlacement"] = "GUI.Placement",
        ["GUI.LoadWindowPlacement"] = "GUI.Placement.Load",
        ["GUI.SaveWindowPlacement"] = "GUI.Placement.SaveOnClosing",
        ["VersionUpdate.UpdateCheck"] = "VersionUpdate.StartupUpdateCheck",
        ["VersionUpdate.UpdateAutoCheck"] = "VersionUpdate.ScheduledUpdateCheck",
    };

    // dev-v2 (`382009b97b`) rewrote the configuration tree and moved every per-profile setting
    // into Configurations.<profile>.Gui.<Section>. These entries invert
    // `src/MaaWpfGui/Helper/ConfigConverter.cs` (dev-v2) so imported values land on the flat
    // keys MAAUnified still reads and writes.
    private static readonly Dictionary<string, string> DevV2ProfileGuiKeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Gui.ConnectSettings
        ["ConnectSettings.Address"] = "Connect.Address",
        ["ConnectSettings.Config"] = "Connect.ConnectConfig",
        ["ConnectSettings.AdbPath"] = "Connect.AdbPath",
        ["ConnectSettings.AddressHistory"] = "Connect.AddressHistory",
        ["ConnectSettings.TouchMode"] = "Connect.TouchMode",
        ["ConnectSettings.AutoDetect"] = "Connect.AutoDetect",
        ["ConnectSettings.AlwaysAutoDetect"] = "Connect.AlwaysAutoDetect",
        ["ConnectSettings.AllowAdbRestart"] = "Connect.AllowADBRestart",
        ["ConnectSettings.AllowAdbHardRestart"] = "Connect.AllowADBHardRestart",
        ["ConnectSettings.EnableAdbLite"] = "Connect.AdbLiteEnabled",
        ["ConnectSettings.KillAdbOnExit"] = "Connect.KillAdbOnExit",
        ["ConnectSettings.AdbReplaced"] = "Connect.AdbReplaced",
        ["ConnectSettings.Extras.MuMuEmulator12.IsEnabled"] = "Connect.MuMu12Extras.Enabled",
        ["ConnectSettings.Extras.MuMuEmulator12.EmulatorPath"] = "Connect.MuMu12EmulatorPath",
        ["ConnectSettings.Extras.MuMuEmulator12.InstanceIndex"] = "Connect.MuMu12Index",
        ["ConnectSettings.Extras.MuMuEmulator12.EnableBridgeConnection"] = "Connect.MumuBridgeConnection",
        ["ConnectSettings.Extras.LDPlayer.IsEnabled"] = "Connect.LdPlayerExtras.Enabled",
        ["ConnectSettings.Extras.LDPlayer.EmulatorPath"] = "Connect.LdPlayerEmulatorPath",
        ["ConnectSettings.Extras.LDPlayer.ManualSetIndex"] = "Connect.LdPlayerManualSetIndex",
        ["ConnectSettings.Extras.LDPlayer.InstanceIndex"] = "Connect.LdPlayerIndex",
        ["ConnectSettings.Extras.Win32Extra.ScreencapMethod"] = "Connect.AttachWindow.ScreencapMethod",
        ["ConnectSettings.Extras.Win32Extra.MouseMethod"] = "Connect.AttachWindow.MouseMethod",
        ["ConnectSettings.Extras.Win32Extra.KeyboardMethod"] = "Connect.AttachWindow.KeyboardMethod",

        // Gui.StartUpSettings
        ["StartUpSettings.RunDirectly"] = "Start.RunDirectly",
        ["StartUpSettings.StartEmulator"] = "Start.OpenEmulatorAfterLaunch",
        ["StartUpSettings.RestartEmulatorWhenAdbFailed"] = "Connect.RetryOnDisconnected",
        ["StartUpSettings.EmulatorPath"] = "Start.EmulatorPath",
        ["StartUpSettings.EmulatorAddCommand"] = "Start.EmulatorAddCommand",
        ["StartUpSettings.EmulatorWaitSeconds"] = "Start.EmulatorWaitSeconds",

        // Gui.RuntimeSettings
        ["RuntimeSettings.ClientType"] = "Start.ClientType",
        ["RuntimeSettings.StartGame"] = "Start.StartGame",
        ["RuntimeSettings.DeployWithPause"] = "Roguelike.DeploymentWithPause",
        ["RuntimeSettings.RoguelikeDelayAbortUntilCombatComplete"] = "Roguelike.RoguelikeDelayAbortUntilCombatComplete",
        ["RuntimeSettings.PreRunScript"] = "Start.StartsWithScript",
        ["RuntimeSettings.PostRunScript"] = "Start.EndsWithScript",
        ["RuntimeSettings.ExecuteScriptOnCopilot"] = "Start.CopilotWithScript",
        ["RuntimeSettings.ExecuteScriptOnManualStop"] = "Start.ManualStopWithScript",
        ["RuntimeSettings.BlockSleep"] = "Start.BlockSleep",
        ["RuntimeSettings.BlockSleepWithScreenOn"] = "Start.BlockSleepWithScreenOn",

        // Gui.Notification
        ["Notification.ReminderIntervalMinutes"] = "TimeOut.Timer.ReminderIntervalMinutes",

        // Gui.RemoteControl
        ["RemoteControl.RemoteControlGetTaskEndpointUri"] = "RemoteControl.RemoteControlGetTaskEndpointUri",
        ["RemoteControl.RemoteControlReportStatusUri"] = "RemoteControl.RemoteControlReportStatusUri",
        ["RemoteControl.RemoteControlUserIdentity"] = "RemoteControl.RemoteControlUserIdentity",
        ["RemoteControl.RemoteControlDeviceIdentity"] = "RemoteControl.RemoteControlDeviceIdentity",
        ["RemoteControl.RemoteControlPollIntervalMs"] = "RemoteControl.RemoteControlPollIntervalMs",

        // Gui.ThirdParty
        ["ThirdParty.ReportToPenguin"] = "Penguin.EnablePenguin",
        ["ThirdParty.PenguinId"] = "Penguin.Id",
        ["ThirdParty.ReportToYituliu"] = "Yituliu.EnableYituliu",

        // Gui.ExternalNotification (structured `Delivery` section only; channel instances are P1-06/P1-07)
        ["ExternalNotification.Delivery.Enable"] = "ExternalNotification.Enabled",
        ["ExternalNotification.Delivery.SendWhenComplete"] = "ExternalNotification.SendWhenComplete",
        ["ExternalNotification.Delivery.SendWhenError"] = "ExternalNotification.SendWhenError",
        ["ExternalNotification.Delivery.SendWhenStalled"] = "ExternalNotification.SendWhenStalled",

        // Gui.* scalars
        ["WindowTitlePrefix"] = "GUI.WindowTitlePrefix",
        ["PostActions"] = "MainFunction.PostActions",
        ["AchievementPopupDisabled"] = "Achievement.PopupDisabled",
        ["AchievementPopupAutoClose"] = "Achievement.PopupAutoClose",
    };

    // dev-v2 `382009b97b` renamed the root `VersionUpdate` object to `Update` and renamed
    // several sub keys (`53870a7a52` added the credentials/source fields).
    private static readonly Dictionary<string, string> DevV2UpdateKeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Name"] = "VersionUpdate.name",
        ["UpdatePackage"] = "VersionUpdate.package",
        ["IsFirstBoot"] = "VersionUpdate.isfirstboot",
        ["DoNotShowUpdate"] = "VersionUpdate.doNotShowUpdate",
        ["VersionType"] = "VersionUpdate.VersionType",
        ["AllowNightlyUpdates"] = "VersionUpdate.AllowNightlyUpdates",
        ["HasAcknowledgedNightlyWarning"] = "VersionUpdate.HasAcknowledgedNightlyWarning",
        ["UpdateSource"] = "VersionUpdate.ResourceUpdateSource",
        ["MirrorChyanCdk"] = "VersionUpdate.ResourceUpdateSource.MirrorChyanCdk",
        ["MirrorChyanCdkExpiredTime"] = "VersionUpdate.UpdateSource.MirrorChyanCdkExpired",
        ["CheckOnStartup"] = "VersionUpdate.StartupUpdateCheck",
        ["CheckOnSchedule"] = "VersionUpdate.ScheduledUpdateCheck",
        ["Proxy"] = "VersionUpdate.Proxy",
        ["ProxyType"] = "VersionUpdate.ProxyType",
        ["ResourceApi"] = "VersionUpdate.ResourceApi",
        ["ForceGithubGlobalSource"] = "VersionUpdate.UpdateSource.ForceGithubGlobalSource",
        ["AutoDownloadUpdatePackage"] = "VersionUpdate.AutoDownloadUpdatePackage",
        ["AutoInstallUpdatePackage"] = "VersionUpdate.AutoInstallUpdatePackage",
        ["ShowUpdaterConsole"] = "VersionUpdate.ShowUpdaterConsole",
        ["ShowUpdaterProgress"] = "VersionUpdate.ShowUpdaterProgress",
    };

    // dev-v2 root `Gui` object (renamed from v6.3.7 root `GUI`); pairs come from
    // `src/MaaWpfGui/Helper/ConfigConverter.cs` at `3badedda9b`.
    private static readonly Dictionary<string, string> DevV2RootGuiKeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Localization"] = "GUI.Localization",
        ["OperNameLanguage"] = "GUI.OperNameLanguage",
        ["UseTray"] = "GUI.UseTray",
        ["MinimizeToTray"] = "GUI.MinimizeToTray",
        ["MinimizeOnStartup"] = "GUI.MinimizeDirectly",
        ["HideCloseButton"] = "GUI.HideCloseButton",
        ["WindowTitleScrollable"] = "GUI.WindowTitleScrollable",
        ["WindowTitleSelectShowList"] = "GUI.WindowTitleSelectShowList",
        ["LogItemDateFormat"] = "GUI.LogItemDateFormat",
        ["WindowPlacement"] = "GUI.WindowPlacement",
        ["LoadWindowPlacement"] = "GUI.LoadWindowPlacement",
        ["SaveWindowPlacement"] = "GUI.SaveWindowPlacement",
        ["InverseClearMode"] = "GUI.InverseClearMode",
        ["DarkMode"] = "GUI.DarkMode",
        ["TaskQueueInverseMode"] = "MainFunction.InverseMode",
        ["UseCardLog"] = "GUI.UseCardLog",
        ["MaxNumberOfLogThumbnails"] = "GUI.MaxNumberOfLogThumbnails",
        ["SoberLanguage"] = "GUI.SoberLanguage",
        ["Hangover"] = "GUI.Hangover",
        ["LastBuyWineTime"] = "GUI.LastBuyWineTime",
        ["CustomCulture"] = "GUI.CustomCulture",
        ["GuideStep"] = "Guide.StepIndex",
        ["Background.ImagePath"] = "GUI.Background.ImagePath",
        ["Background.StretchMode"] = "GUI.Background.StretchMode",
        ["Background.Opacity"] = "GUI.Background.Opacity",
        ["Background.BlurEffectRadius"] = "GUI.Background.BlurEffectRadius",
        ["Performance.UseGpu"] = "Performance.UseGpu",
        ["Performance.GpuDescription"] = "Performance.PreferredGpuDescription",
        ["Performance.GpuInstancePath"] = "Performance.PreferredGpuInstancePath",
        ["Performance.AllowDeprecatedGpu"] = "Performance.AllowDeprecatedGpu",
        ["Performance.IgnoreBadModulesAndUseSoftwareRendering"] = "GUI.IgnoreBadModulesAndUseSoftwareRendering",
    };

    private static readonly Dictionary<string, string> ClientTypeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["official"] = "Official",
        ["officialcn"] = "Official",
        ["官服"] = "Official",
        ["cnofficial"] = "Official",
        ["bilibili"] = "Bilibili",
        ["bilibilicn"] = "Bilibili",
        ["b服"] = "Bilibili",
        ["yostaren"] = "YoStarEN",
        ["yostarjp"] = "YoStarJP",
        ["yostarkr"] = "YoStarKR",
        ["txwy"] = "txwy",
        ["txwytw"] = "txwy",
    };

    private static readonly Dictionary<string, string> ConnectConfigAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mumu"] = "MuMuEmulator12",
        ["mumuemulator12"] = "MuMuEmulator12",
        ["androws"] = "Androws",
    };

    private static readonly Dictionary<string, string> TouchModeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["maatouch"] = "maatouch",
        ["minitouch"] = "minitouch",
        ["adb"] = "adb",
        ["adbinput"] = "adb",
        ["maaframework"] = "MaaFwAdb",
        ["maafwadb"] = "MaaFwAdb",
        ["maaframeworkadb"] = "MaaFwAdb",
    };

    private static readonly Dictionary<string, string> RoguelikeThemeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["phantom"] = "Phantom",
        // Localized display text from WPF options.
        ["傀影"] = "Phantom",
        ["mizuki"] = "Mizuki",
        ["水月"] = "Mizuki",
        ["sami"] = "Sami",
        ["萨米"] = "Sami",
        ["薩米"] = "Sami",
        ["sarkaz"] = "Sarkaz",
        ["萨卡兹"] = "Sarkaz",
        ["薩卡茲"] = "Sarkaz",
        ["jiegarden"] = "JieGarden",
        ["界园"] = "JieGarden",
        ["界園"] = "JieGarden",
    };

    private static readonly Dictionary<string, int> RoguelikeModeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["exp"] = 0,
        ["normal"] = 0,
        ["investment"] = 1,
        ["collectible"] = 4,
        ["clp_pds"] = 5,
        ["clppds"] = 5,
        ["collapse"] = 5,
        ["collapsalparadigms"] = 5,
        ["squad"] = 6,
        ["monthlysquad"] = 6,
        ["exploration"] = 7,
        ["deepexploration"] = 7,
        ["findplaytime"] = 20001,
    };

    private static readonly Dictionary<string, int> CollectibleAwardFlagAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hotwater"] = 1 << 0,
        ["hot_water"] = 1 << 0,
        ["water"] = 1 << 0,
        ["热水"] = 1 << 0,
        ["熱水"] = 1 << 0,
        ["shield"] = 1 << 1,
        ["护盾"] = 1 << 1,
        ["護盾"] = 1 << 1,
        ["ingot"] = 1 << 2,
        ["锭"] = 1 << 2,
        ["錠"] = 1 << 2,
        ["hope"] = 1 << 3,
        ["希望"] = 1 << 3,
        ["random"] = 1 << 4,
        ["随机"] = 1 << 4,
        ["隨機"] = 1 << 4,
        ["key"] = 1 << 5,
        ["钥匙"] = 1 << 5,
        ["鑰匙"] = 1 << 5,
        ["dice"] = 1 << 6,
        ["骰子"] = 1 << 6,
        ["idea"] = 1 << 7,
        ["ideas"] = 1 << 7,
        ["构想"] = 1 << 7,
        ["構想"] = 1 << 7,
        ["ticket"] = 1 << 8,
        ["券"] = 1 << 8,
    };

    public static string NormalizeProfileKey(string key)
    {
        return ProfileKeyAliases.TryGetValue(key, out var mapped)
            ? mapped
            : key;
    }

    public static string NormalizeGlobalKey(string key)
    {
        return GlobalKeyAliases.TryGetValue(key, out var mapped) ? mapped : key;
    }

    /// <summary>
    /// Maps a dev-v2 per-profile <c>Gui</c> path (relative to <c>Gui.</c>, dotted) to the flat
    /// legacy profile key that MAAUnified still reads.
    /// </summary>
    public static string? TryMapDevV2ProfileGuiKey(string relativePath)
    {
        return DevV2ProfileGuiKeyAliases.TryGetValue(relativePath, out var mapped) ? mapped : null;
    }

    /// <summary>
    /// Indicates whether a dev-v2 per-profile <c>Gui</c> path is a section that contains mapped keys.
    /// </summary>
    public static bool IsDevV2ProfileGuiSection(string relativePath)
    {
        return HasMappedDescendant(DevV2ProfileGuiKeyAliases, relativePath);
    }

    /// <summary>
    /// Maps a dev-v2 root <c>Update</c> sub key to the legacy <c>VersionUpdate.*</c> global key.
    /// </summary>
    public static string? TryMapDevV2UpdateKey(string key)
    {
        return DevV2UpdateKeyAliases.TryGetValue(key, out var mapped) ? mapped : null;
    }

    /// <summary>
    /// Maps a dev-v2 root <c>Gui</c> path (relative to <c>Gui.</c>, dotted) to the legacy global key.
    /// </summary>
    public static string? TryMapDevV2RootGuiKey(string relativePath)
    {
        return DevV2RootGuiKeyAliases.TryGetValue(relativePath, out var mapped) ? mapped : null;
    }

    /// <summary>
    /// Indicates whether a dev-v2 root <c>Gui</c> path is a section that contains mapped keys.
    /// </summary>
    public static bool IsDevV2RootGuiSection(string relativePath)
    {
        return HasMappedDescendant(DevV2RootGuiKeyAliases, relativePath);
    }

    private static bool HasMappedDescendant(IReadOnlyDictionary<string, string> aliases, string relativePath)
    {
        var prefix = relativePath + ".";
        foreach (var key in aliases.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static JsonNode? NormalizeProfileValue(string key, JsonNode? value)
    {
        return key switch
        {
            "ClientType" or "Start.ClientType" => NormalizeStringJsonValue(value, NormalizeClientType),
            "ConnectConfig" or "Connect.ConnectConfig" => NormalizeStringJsonValue(value, NormalizeConnectConfig),
            "TouchMode" or "Connect.TouchMode" => NormalizeStringJsonValue(value, NormalizeTouchMode),
            "AttachWindowScreencapMethod"
                or "AttachWindowMouseMethod" or "AttachWindowKeyboardMethod"
                or "Connect.AttachWindow.ScreencapMethod" or "Connect.AttachWindow.MouseMethod" or "Connect.AttachWindow.KeyboardMethod"
                or "MuMu12Index" or "Connect.MuMu12Index"
                or "LdPlayerIndex" or "Connect.LdPlayerIndex" => NormalizeScalarToStringValue(value),
            "ConnectAddressHistory" or "Connect.AddressHistory" => NormalizeAddressHistory(value),
            _ => value,
        };
    }

    public static string NormalizeClientType(string? value)
    {
        return NormalizeAlias(value, ClientTypeAliases);
    }

    public static string NormalizeConnectConfig(string? value)
    {
        return NormalizeAlias(value, ConnectConfigAliases);
    }

    public static string NormalizeTouchMode(string? value)
    {
        return NormalizeAlias(value, TouchModeAliases);
    }

    public static string NormalizeFightStageResetMode(JsonNode? value)
    {
        if (TryReadString(value, out var text))
        {
            var trimmed = text.Trim();
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed == 1 ? "Ignore" : "Current";
            }

            var token = NormalizeToken(trimmed);
            if (token is "ignore" or "invalid")
            {
                return "Ignore";
            }

            if (token is "current")
            {
                return "Current";
            }
        }

        if (TryReadInt(value, out var number))
        {
            return number == 1 ? "Ignore" : "Current";
        }

        return "Current";
    }

    public static int NormalizeFightSeries(JsonNode? value, int fallback = 0)
    {
        if (TryReadString(value, out var text))
        {
            var trimmed = text.Trim();
            var token = NormalizeToken(trimmed);
            if (token is "auto" or "自动" or "自動")
            {
                return 0;
            }

            if (token is "notswitch" or "不切换" or "不切換")
            {
                return -1;
            }

            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return Math.Clamp(parsed, -1, 6);
            }
        }

        if (TryReadInt(value, out var number))
        {
            return Math.Clamp(number, -1, 6);
        }

        return Math.Clamp(fallback, -1, 6);
    }

    public static int NormalizeInfrastMode(JsonNode? value, int fallback = 0)
    {
        if (TryReadInt(value, out var number))
        {
            return number;
        }

        if (!TryReadString(value, out var text))
        {
            return fallback;
        }

        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return NormalizeToken(text) switch
        {
            "normal" => 0,
            "custom" => 10000,
            "rotation" => 20000,
            _ => fallback,
        };
    }

    public static string NormalizeRoguelikeTheme(JsonNode? value)
    {
        if (TryReadInt(value, out var number))
        {
            return number switch
            {
                0 => "Phantom",
                1 => "Mizuki",
                2 => "Sami",
                3 => "Sarkaz",
                4 => "JieGarden",
                _ => "JieGarden",
            };
        }

        if (!TryReadString(value, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return "JieGarden";
        }

        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return NormalizeRoguelikeTheme(JsonValue.Create(number));
        }

        return NormalizeAlias(text, RoguelikeThemeAliases, "JieGarden");
    }

    public static int NormalizeRoguelikeMode(JsonNode? value, int fallback = 0)
    {
        if (TryReadInt(value, out var number))
        {
            return number;
        }

        if (!TryReadString(value, out var text))
        {
            return fallback;
        }

        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return RoguelikeModeAliases.TryGetValue(NormalizeToken(text), out var mapped)
            ? mapped
            : fallback;
    }

    public static int NormalizeFindPlaytimeTarget(JsonNode? value, int fallback = 1)
    {
        if (TryReadInt(value, out var number))
        {
            return number;
        }

        if (!TryReadString(value, out var text))
        {
            return fallback;
        }

        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return NormalizeToken(text) switch
        {
            "unknown" => 0,
            "ling" => 1,
            "shu" => 2,
            "nian" => 3,
            _ => fallback,
        };
    }

    public static int NormalizeRoguelikeCollectibleAwardsMask(JsonNode? value)
    {
        if (TryReadInt(value, out var number))
        {
            return number;
        }

        if (!TryReadString(value, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        var mask = 0;
        foreach (var item in SplitLegacyList(text))
        {
            if (CollectibleAwardFlagAliases.TryGetValue(NormalizeToken(item), out var flag))
            {
                mask |= flag;
            }
        }

        return mask;
    }

    public static string NormalizeReclamationTheme(JsonNode? value)
    {
        if (TryReadInt(value, out var number))
        {
            return number switch
            {
                0 => "Fire",
                2 => "RelaunchAnchor",
                _ => "Tales",
            };
        }

        if (!TryReadString(value, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return "Tales";
        }

        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return NormalizeReclamationTheme(JsonValue.Create(number));
        }

        return NormalizeToken(text) switch
        {
            "fire" or "沙中之火" => "Fire",
            "tales" or "沙洲遗闻" or "沙洲遺聞" => "Tales",
            "relaunchanchor" or "ra" or "重启锚点" or "重啟錨點" => "RelaunchAnchor",
            _ => "Tales",
        };
    }

    public static int NormalizeReclamationMode(JsonNode? value, int fallback = 1)
    {
        if (TryReadInt(value, out var number))
        {
            return number is 0 or 1 or 16 or 32 or 48 ? number : fallback;
        }

        if (!TryReadString(value, out var text))
        {
            return fallback;
        }

        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number is 0 or 1 or 16 or 32 or 48 ? number : fallback;
        }

        return NormalizeToken(text) switch
        {
            "prosperitynosave" => 0,
            "prosperityinsave" => 1,
            "ra1" => 16,
            "ra4" => 48,
            "ra15" => 32,
            "ra" or "relaunchanchor" => 16,
            _ => fallback,
        };
    }

    public static int NormalizeReclamationIncrementMode(JsonNode? value, int fallback = 0)
    {
        if (TryReadInt(value, out var number))
        {
            return number is 0 or 1 ? number : fallback;
        }

        if (!TryReadString(value, out var text))
        {
            return fallback;
        }

        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number is 0 or 1 ? number : fallback;
        }

        return NormalizeToken(text) switch
        {
            "click" or "连点" or "連點" => 0,
            "hold" or "长按" or "長按" => 1,
            _ => fallback,
        };
    }

    public static IReadOnlyList<string> SplitLegacyList(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        return input
            .Split(LegacyListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static JsonNode? NormalizeStringJsonValue(JsonNode? value, Func<string?, string> normalize)
    {
        return TryReadString(value, out var text)
            ? JsonValue.Create(normalize(text))
            : value;
    }

    private static JsonNode? NormalizeScalarToStringValue(JsonNode? value)
    {
        return TryReadInvariantScalarString(value, out var text)
            ? JsonValue.Create(text)
            : value;
    }

    private static JsonNode? NormalizeAddressHistory(JsonNode? value)
    {
        if (value is JsonArray array)
        {
            return ToJsonArray(array.Select(ReadAddressHistoryEntry));
        }

        if (!TryReadString(value, out var text))
        {
            return value;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                if (JsonNode.Parse(trimmed) is JsonArray parsed)
                {
                    return ToJsonArray(parsed.Select(ReadAddressHistoryEntry));
                }
            }
            catch
            {
                // Fall through to delimiter parsing.
            }
        }

        return ToJsonArray(SplitLegacyList(trimmed));
    }

    private static string? ReadAddressHistoryEntry(JsonNode? node)
    {
        return TryReadString(node, out var text)
            ? text.Trim()
            : null;
    }

    private static JsonArray ToJsonArray(IEnumerable<string?> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                array.Add(value.Trim());
            }
        }

        return array;
    }

    private static string NormalizeAlias(
        string? value,
        IReadOnlyDictionary<string, string> aliases,
        string? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback ?? string.Empty;
        }

        var trimmed = value.Trim();
        return aliases.TryGetValue(NormalizeToken(trimmed), out var mapped)
            ? mapped
            : trimmed;
    }

    private static bool TryReadString(JsonNode? node, out string text)
    {
        if (node is JsonValue value && value.TryGetValue(out string? parsed) && parsed is not null)
        {
            text = parsed;
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static bool TryReadInt(JsonNode? node, out int number)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out int parsed))
            {
                number = parsed;
                return true;
            }

            if (value.TryGetValue(out long parsedLong))
            {
                number = (int)Math.Clamp(parsedLong, int.MinValue, int.MaxValue);
                return true;
            }
        }

        number = 0;
        return false;
    }

    private static bool TryReadInvariantScalarString(JsonNode? node, out string text)
    {
        if (node is not JsonValue value)
        {
            text = string.Empty;
            return false;
        }

        if (value.TryGetValue(out string? parsedString) && parsedString is not null)
        {
            text = parsedString.Trim();
            return true;
        }

        if (value.TryGetValue(out int parsedInt))
        {
            text = parsedInt.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (value.TryGetValue(out long parsedLong))
        {
            text = parsedLong.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (value.TryGetValue(out double parsedDouble))
        {
            text = parsedDouble.ToString("G", CultureInfo.InvariantCulture);
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static string NormalizeToken(string? value)
    {
        return (value ?? string.Empty)
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("(", string.Empty, StringComparison.Ordinal)
            .Replace(")", string.Empty, StringComparison.Ordinal)
            .Replace("（", string.Empty, StringComparison.Ordinal)
            .Replace("）", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }
}
