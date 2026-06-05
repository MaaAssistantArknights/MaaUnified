using System.Globalization;
using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Compat.Constants;

namespace MAAUnified.Application.Services;

public static class MacBundledAdbPolicy
{
    public const string CurrentTermsVersion = "macos-bundled-adb-2026-06-02";
    public const string ProfileUseBundledAdbKey = "MacUseBundledAdb";
    public const string LegacyUseBundledAdbKey = "Connect.MacUseBundledAdb";

    private const string BundledAdbDirectoryName = "platform-tools";
    private const string BundledAdbFileName = "adb";

    public static bool IsSupportedPlatform => OperatingSystem.IsMacOS();

    public static bool ShouldUseBundledAdb(bool useBundledAdb)
    {
        return IsSupportedPlatform && useBundledAdb;
    }

    public static string ResolveBundledAdbPath()
        => ResolveBundledAdbPath(AppContext.BaseDirectory);

    public static string ResolveBundledAdbPath(string baseDirectory)
        => Path.Combine(baseDirectory, BundledAdbDirectoryName, BundledAdbFileName);

    public static bool IsBundledAdbPath(string? adbPath)
    {
        if (!IsSupportedPlatform || string.IsNullOrWhiteSpace(adbPath))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(adbPath.Trim());
        var bundledPath = Path.GetFullPath(ResolveBundledAdbPath());
        return string.Equals(fullPath, bundledPath, StringComparison.Ordinal);
    }

    public static bool ReadUseBundledAdb(UnifiedProfile profile, bool fallback = true)
    {
        return TryReadBool(profile.Values, ProfileUseBundledAdbKey, out var profileValue)
            ? profileValue
            : TryReadBool(profile.Values, LegacyUseBundledAdbKey, out var legacyValue)
                ? legacyValue
                : fallback;
    }

    public static bool IsCurrentTermsAccepted(UnifiedConfig config)
    {
        return TryReadString(config.GlobalValues, ConfigurationKeys.MacBundledAdbTermsAcceptedVersion, out var acceptedVersion)
               && string.Equals(acceptedVersion, CurrentTermsVersion, StringComparison.Ordinal);
    }

    public static void MarkCurrentTermsAccepted(UnifiedConfig config, DateTimeOffset acceptedAtUtc)
    {
        config.GlobalValues[ConfigurationKeys.MacBundledAdbTermsAcceptedVersion] = JsonValue.Create(CurrentTermsVersion);
        config.GlobalValues[ConfigurationKeys.MacBundledAdbTermsAcceptedAtUtc] =
            JsonValue.Create(acceptedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }

    public static UiOperationResult BuildMissingConsentFailure()
    {
        return UiOperationResult.Fail(
            UiErrorCode.SessionStateNotAllowed,
            "macOS bundled ADB requires Android SDK Platform-Tools terms acceptance before use.");
    }

    private static bool TryReadBool(IDictionary<string, JsonNode?> values, string key, out bool value)
    {
        if (!values.TryGetValue(key, out var node) || node is not JsonValue jsonValue)
        {
            value = false;
            return false;
        }

        if (jsonValue.TryGetValue(out bool parsedBool))
        {
            value = parsedBool;
            return true;
        }

        if (jsonValue.TryGetValue(out int parsedInt))
        {
            value = parsedInt != 0;
            return true;
        }

        if (jsonValue.TryGetValue(out string? text))
        {
            if (bool.TryParse(text, out var parsedTextBool))
            {
                value = parsedTextBool;
                return true;
            }

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTextInt))
            {
                value = parsedTextInt != 0;
                return true;
            }
        }

        value = false;
        return false;
    }

    private static bool TryReadString(IDictionary<string, JsonNode?> values, string key, out string value)
    {
        if (values.TryGetValue(key, out var node) && node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
            {
                value = text.Trim();
                return true;
            }

            var raw = node.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                value = raw.Trim();
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
