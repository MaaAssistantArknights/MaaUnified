using System.Text;
using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Compat.Constants;

namespace MAAUnified.Application.Configuration;

internal static class PostActionLegacyMigration
{
    public const string PostActionConfigKey = "TaskQueue.PostAction";

    public static PostActionResolution ResolveForProfile(UnifiedConfig config, UnifiedProfile profile)
    {
        if (profile.Values.TryGetValue(PostActionConfigKey, out var node) && node is not null)
        {
            var parsed = PostActionConfig.FromJson(node);
            var normalized = NormalizeForPersistentStorage(parsed, out var changed);
            return PostActionResolution.Resolved(
                normalized,
                shouldPersist: changed,
                usedGlobalStructured: false,
                usedLegacy: false,
                usedGlobalLegacy: false);
        }

        if (config.GlobalValues.TryGetValue(PostActionConfigKey, out var globalStructuredNode) && globalStructuredNode is not null)
        {
            var parsed = PostActionConfig.FromJson(globalStructuredNode);
            var normalized = NormalizeForPersistentStorage(parsed, out _);
            return PostActionResolution.Resolved(
                normalized,
                shouldPersist: true,
                usedGlobalStructured: true,
                usedLegacy: false,
                usedGlobalLegacy: false);
        }

        var hasProfileLegacy = profile.Values.TryGetValue(ConfigurationKeys.PostActions, out var profileLegacyNode) && profileLegacyNode is not null;
        var hasGlobalLegacy = config.GlobalValues.TryGetValue(ConfigurationKeys.PostActions, out var globalLegacyNode) && globalLegacyNode is not null;
        var hasProfileLegacyAction = profile.Values.TryGetValue(ConfigurationKeys.ActionAfterCompleted, out var profileLegacyActionNode) && profileLegacyActionNode is not null;
        var hasGlobalLegacyAction = config.GlobalValues.TryGetValue(ConfigurationKeys.ActionAfterCompleted, out var globalLegacyActionNode) && globalLegacyActionNode is not null;
        var hasLegacyPostActions = hasProfileLegacy || hasGlobalLegacy;
        var hasLegacyActionAfterCompleted = hasProfileLegacyAction || hasGlobalLegacyAction;
        if (!hasLegacyPostActions && !hasLegacyActionAfterCompleted)
        {
            return PostActionResolution.Empty;
        }

        var parsedLegacyPostActions = false;
        var legacyPostActionsConfig = PostActionConfig.Default;
        if (hasLegacyPostActions)
        {
            var legacyNode = hasProfileLegacy ? profileLegacyNode : globalLegacyNode;
            if (TryReadLegacyFlags(legacyNode!, out var flags))
            {
                parsedLegacyPostActions = true;
                legacyPostActionsConfig = MapLegacyFlags(flags);
            }
        }

        var parsedLegacyActionAfterCompleted = false;
        var legacyActionAfterCompletedConfig = PostActionConfig.Default;
        if (hasLegacyActionAfterCompleted)
        {
            var legacyActionNode = hasProfileLegacyAction ? profileLegacyActionNode : globalLegacyActionNode;
            if (TryReadLegacyActionAfterCompleted(legacyActionNode!, out var parsedActionConfig))
            {
                parsedLegacyActionAfterCompleted = true;
                legacyActionAfterCompletedConfig = parsedActionConfig;
            }
        }

        PostActionConfig migratedConfig;
        if (parsedLegacyPostActions && legacyPostActionsConfig.HasAnyAction())
        {
            migratedConfig = legacyPostActionsConfig;
        }
        else if (parsedLegacyActionAfterCompleted)
        {
            migratedConfig = legacyActionAfterCompletedConfig;
        }
        else if (parsedLegacyPostActions)
        {
            migratedConfig = legacyPostActionsConfig;
        }
        else
        {
            return PostActionResolution.Failure(
                hasLegacyPostActions && hasLegacyActionAfterCompleted
                    ? "Failed to parse legacy completion action config."
                    : hasLegacyPostActions
                        ? "Failed to parse legacy post action flags."
                        : "Failed to parse legacy completion action.");
        }

        var normalizedMigratedConfig = NormalizeForPersistentStorage(migratedConfig, out _);
        var usedGlobalLegacy = (!hasProfileLegacy && hasGlobalLegacy) || (!hasProfileLegacyAction && hasGlobalLegacyAction);
        return PostActionResolution.Resolved(
            normalizedMigratedConfig,
            shouldPersist: true,
            usedGlobalStructured: false,
            usedLegacy: true,
            usedGlobalLegacy: usedGlobalLegacy);
    }

    public static int MaterializeImportedProfiles(UnifiedConfig config, ImportReport? report = null)
    {
        var migratedProfiles = 0;
        var usedGlobalStructured = false;
        var usedGlobalLegacy = false;

        foreach (var profile in config.Profiles.Values)
        {
            var resolution = ResolveForProfile(config, profile);
            if (!resolution.Success)
            {
                if (!string.IsNullOrWhiteSpace(resolution.FailureMessage))
                {
                    report?.Warnings.Add(resolution.FailureMessage);
                }

                continue;
            }

            if (!resolution.HasConfig)
            {
                continue;
            }

            if (resolution.ShouldPersist)
            {
                profile.Values[PostActionConfigKey] = resolution.Config.ToJson();
                migratedProfiles++;
                if (report is not null)
                {
                    report.MappedFieldCount += 1;
                }
            }

            if (resolution.UsedLegacy || resolution.ShouldPersist)
            {
                profile.Values.Remove(ConfigurationKeys.PostActions);
                profile.Values.Remove(ConfigurationKeys.ActionAfterCompleted);
            }

            usedGlobalStructured |= resolution.UsedGlobalStructured;
            usedGlobalLegacy |= resolution.UsedGlobalLegacy;
        }

        if (usedGlobalStructured)
        {
            config.GlobalValues.Remove(PostActionConfigKey);
        }

        if (usedGlobalLegacy)
        {
            config.GlobalValues.Remove(ConfigurationKeys.PostActions);
            config.GlobalValues.Remove(ConfigurationKeys.ActionAfterCompleted);
        }

        return migratedProfiles;
    }

    public static PostActionConfig NormalizeForPersistentStorage(PostActionConfig source, out bool changed)
    {
        var normalized = source.Clone();
        changed = false;

        if (OperatingSystem.IsMacOS() && normalized.Hibernate)
        {
            normalized.Hibernate = false;
            if (!normalized.Sleep)
            {
                normalized.Sleep = true;
            }

            changed = true;
        }

        return normalized;
    }

    private static PostActionConfig MapLegacyFlags(LegacyPostActionFlags flags)
    {
        return new PostActionConfig
        {
            ExitSelf = flags.HasFlag(LegacyPostActionFlags.ExitSelf),
            ExitEmulator = flags.HasFlag(LegacyPostActionFlags.ExitEmulator),
            Hibernate = flags.HasFlag(LegacyPostActionFlags.Hibernate),
            Shutdown = flags.HasFlag(LegacyPostActionFlags.Shutdown),
            Sleep = flags.HasFlag(LegacyPostActionFlags.Sleep),
            IfNoOtherMaa = flags.HasFlag(LegacyPostActionFlags.IfNoOtherMaa),
        };
    }

    private static bool TryReadLegacyFlags(JsonNode node, out LegacyPostActionFlags flags)
    {
        flags = default;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out int intValue))
        {
            flags = (LegacyPostActionFlags)intValue;
            return true;
        }

        if (jsonValue.TryGetValue(out string? text))
        {
            if (int.TryParse(text, out intValue))
            {
                flags = (LegacyPostActionFlags)intValue;
                return true;
            }

            if (Enum.TryParse(text, out LegacyPostActionFlags enumValue))
            {
                flags = enumValue;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadLegacyActionAfterCompleted(JsonNode node, out PostActionConfig config)
    {
        config = PostActionConfig.Default;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out int intValue))
        {
            return TryMapLegacyCompletionAction(intValue, out config);
        }

        if (!jsonValue.TryGetValue(out string? text))
        {
            return false;
        }

        if (int.TryParse(text, out intValue))
        {
            return TryMapLegacyCompletionAction(intValue, out config);
        }

        var normalized = NormalizeLegacyCompletionActionName(text);
        return TryMapLegacyCompletionAction(normalized, out config);
    }

    private static string NormalizeLegacyCompletionActionName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static bool TryMapLegacyCompletionAction(int value, out PostActionConfig config)
    {
        LegacyCompletionAction? action = value switch
        {
            0 => LegacyCompletionAction.DoNothing,
            1 => LegacyCompletionAction.StopGame,
            2 => LegacyCompletionAction.ExitSelf,
            3 => LegacyCompletionAction.ExitEmulator,
            4 => LegacyCompletionAction.ExitEmulatorAndSelf,
            5 => LegacyCompletionAction.Suspend,
            6 => LegacyCompletionAction.Hibernate,
            7 => LegacyCompletionAction.ExitEmulatorAndSelfAndHibernate,
            8 => LegacyCompletionAction.Shutdown,
            9 => LegacyCompletionAction.HibernateWithoutPersist,
            10 => LegacyCompletionAction.ExitEmulatorAndSelfAndHibernateWithoutPersist,
            11 => LegacyCompletionAction.ShutdownWithoutPersist,
            12 => LegacyCompletionAction.ExitEmulatorAndSelfIfOtherMaaElseExitEmulatorAndSelfAndHibernate,
            13 => LegacyCompletionAction.ExitSelfIfOtherMaaElseShutdown,
            14 => LegacyCompletionAction.BackToAndroidHome,
            _ => null,
        };

        if (action is null)
        {
            config = PostActionConfig.Default;
            return false;
        }

        config = MapLegacyCompletionAction(action.Value);
        return true;
    }

    private static bool TryMapLegacyCompletionAction(string normalizedAction, out PostActionConfig config)
    {
        LegacyCompletionAction? action = normalizedAction switch
        {
            "" or "none" or "noaction" or "donothing" or "nothing" => LegacyCompletionAction.DoNothing,
            "stopgame" or "exitarknights" or "closearknights" => LegacyCompletionAction.StopGame,
            "backtoandroidhome" or "backtohome" or "returntoandroidhome" or "returntohome" => LegacyCompletionAction.BackToAndroidHome,
            "exitemulator" or "closeemulator" => LegacyCompletionAction.ExitEmulator,
            "exitself" or "exitmaa" or "closemaa" or "quitmaa" => LegacyCompletionAction.ExitSelf,
            "exitemulatorandself" => LegacyCompletionAction.ExitEmulatorAndSelf,
            "hibernate" => LegacyCompletionAction.Hibernate,
            "hibernatewithoutpersist" => LegacyCompletionAction.HibernateWithoutPersist,
            "shutdown" or "poweroff" => LegacyCompletionAction.Shutdown,
            "shutdownwithoutpersist" => LegacyCompletionAction.ShutdownWithoutPersist,
            "sleep" or "suspend" or "standby" => LegacyCompletionAction.Suspend,
            "exitemulatorandselfandhibernate" => LegacyCompletionAction.ExitEmulatorAndSelfAndHibernate,
            "exitemulatorandselfandhibernatewithoutpersist" => LegacyCompletionAction.ExitEmulatorAndSelfAndHibernateWithoutPersist,
            "exitemulatorandselfifothermaaelseexitemulatorandselfandhibernate" => LegacyCompletionAction.ExitEmulatorAndSelfIfOtherMaaElseExitEmulatorAndSelfAndHibernate,
            "exitselfifothermaaelseshutdown" => LegacyCompletionAction.ExitSelfIfOtherMaaElseShutdown,
            _ => null,
        };

        if (action is null)
        {
            config = PostActionConfig.Default;
            return false;
        }

        config = MapLegacyCompletionAction(action.Value);
        return true;
    }

    private static PostActionConfig MapLegacyCompletionAction(LegacyCompletionAction action)
    {
        return action switch
        {
            LegacyCompletionAction.DoNothing => PostActionConfig.Default,
            LegacyCompletionAction.StopGame => new PostActionConfig { ExitArknights = true },
            LegacyCompletionAction.ExitSelf => new PostActionConfig { ExitSelf = true },
            LegacyCompletionAction.ExitEmulator => new PostActionConfig { ExitEmulator = true },
            LegacyCompletionAction.ExitEmulatorAndSelf => new PostActionConfig
            {
                ExitEmulator = true,
                ExitSelf = true,
            },
            LegacyCompletionAction.Suspend => new PostActionConfig { Sleep = true },
            LegacyCompletionAction.Hibernate => new PostActionConfig { Hibernate = true },
            LegacyCompletionAction.ExitEmulatorAndSelfAndHibernate => new PostActionConfig
            {
                ExitEmulator = true,
                ExitSelf = true,
                Hibernate = true,
            },
            LegacyCompletionAction.Shutdown => new PostActionConfig { Shutdown = true },
            LegacyCompletionAction.HibernateWithoutPersist => new PostActionConfig { Hibernate = true },
            LegacyCompletionAction.ExitEmulatorAndSelfAndHibernateWithoutPersist => new PostActionConfig
            {
                ExitEmulator = true,
                ExitSelf = true,
                Hibernate = true,
            },
            LegacyCompletionAction.ShutdownWithoutPersist => new PostActionConfig { Shutdown = true },
            LegacyCompletionAction.ExitEmulatorAndSelfIfOtherMaaElseExitEmulatorAndSelfAndHibernate => new PostActionConfig
            {
                ExitEmulator = true,
                ExitSelf = true,
                IfNoOtherMaa = true,
                Hibernate = true,
            },
            LegacyCompletionAction.ExitSelfIfOtherMaaElseShutdown => new PostActionConfig
            {
                ExitSelf = true,
                IfNoOtherMaa = true,
                Shutdown = true,
            },
            LegacyCompletionAction.BackToAndroidHome => new PostActionConfig { BackToAndroidHome = true },
            _ => PostActionConfig.Default,
        };
    }

    [Flags]
    private enum LegacyPostActionFlags
    {
        ExitSelf = 8,
        ExitEmulator = 16,
        Hibernate = 32,
        Shutdown = 64,
        Sleep = 128,
        IfNoOtherMaa = 256,
    }

    private enum LegacyCompletionAction
    {
        DoNothing = 0,
        StopGame = 1,
        ExitSelf = 2,
        ExitEmulator = 3,
        ExitEmulatorAndSelf = 4,
        Suspend = 5,
        Hibernate = 6,
        ExitEmulatorAndSelfAndHibernate = 7,
        Shutdown = 8,
        HibernateWithoutPersist = 9,
        ExitEmulatorAndSelfAndHibernateWithoutPersist = 10,
        ShutdownWithoutPersist = 11,
        ExitEmulatorAndSelfIfOtherMaaElseExitEmulatorAndSelfAndHibernate = 12,
        ExitSelfIfOtherMaaElseShutdown = 13,
        BackToAndroidHome = 14,
    }
}

internal readonly record struct PostActionResolution(
    bool Success,
    bool HasConfig,
    bool ShouldPersist,
    bool UsedGlobalStructured,
    bool UsedLegacy,
    bool UsedGlobalLegacy,
    PostActionConfig Config,
    string FailureMessage)
{
    public static PostActionResolution Empty => new(
        Success: true,
        HasConfig: false,
        ShouldPersist: false,
        UsedGlobalStructured: false,
        UsedLegacy: false,
        UsedGlobalLegacy: false,
        Config: PostActionConfig.Default,
        FailureMessage: string.Empty);

    public static PostActionResolution Resolved(
        PostActionConfig config,
        bool shouldPersist,
        bool usedGlobalStructured,
        bool usedLegacy,
        bool usedGlobalLegacy)
        => new(
            Success: true,
            HasConfig: true,
            ShouldPersist: shouldPersist,
            UsedGlobalStructured: usedGlobalStructured,
            UsedLegacy: usedLegacy,
            UsedGlobalLegacy: usedGlobalLegacy,
            Config: config,
            FailureMessage: string.Empty);

    public static PostActionResolution Failure(string message)
        => new(
            Success: false,
            HasConfig: true,
            ShouldPersist: false,
            UsedGlobalStructured: false,
            UsedLegacy: false,
            UsedGlobalLegacy: false,
            Config: PostActionConfig.Default,
            FailureMessage: message);
}
