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

    public static bool TryResolveAdbPathForConnect(string? adbPath, out string? effectiveAdbPath, out string? diagnostic)
    {
        effectiveAdbPath = string.IsNullOrWhiteSpace(adbPath) ? null : adbPath.Trim();
        diagnostic = null;
        if (!IsSupportedPlatform || string.IsNullOrWhiteSpace(effectiveAdbPath))
        {
            return true;
        }

        if (LooksLikeWindowsPath(effectiveAdbPath))
        {
            diagnostic = AppendResolutionContext(
                $"macOS cannot use a Windows ADB path: {effectiveAdbPath}",
                adbPath,
                effectiveAdbPath);
            effectiveAdbPath = null;
            return false;
        }

        if (string.Equals(effectiveAdbPath, BundledAdbFileName, StringComparison.OrdinalIgnoreCase))
        {
            diagnostic = AppendResolutionContext(
                "macOS ADB path uses PATH lookup",
                adbPath,
                effectiveAdbPath);
            return true;
        }

        var extension = Path.GetExtension(effectiveAdbPath);
        if (string.Equals(extension, ".dmg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            diagnostic = AppendResolutionContext(
                $"ADB path points to an archive/image instead of an adb executable: {effectiveAdbPath}",
                adbPath,
                effectiveAdbPath);
            return false;
        }

        if (Directory.Exists(effectiveAdbPath))
        {
            var resolved = TryFindAdbUnderDirectory(effectiveAdbPath);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                diagnostic = AppendResolutionContext(
                    $"ADB path is a directory, but no adb executable was found under it: {effectiveAdbPath}",
                    adbPath,
                    effectiveAdbPath);
                return false;
            }

            effectiveAdbPath = resolved;
        }

        if (!File.Exists(effectiveAdbPath))
        {
            diagnostic = AppendResolutionContext(
                $"ADB path does not exist: {effectiveAdbPath}",
                adbPath,
                effectiveAdbPath,
                resolutionFailure: "missing");
            return false;
        }

        var fileName = Path.GetFileName(effectiveAdbPath);
        if (!string.Equals(fileName, BundledAdbFileName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fileName, "adb.exe", StringComparison.OrdinalIgnoreCase))
        {
            diagnostic = AppendResolutionContext(
                $"ADB path must point to the adb executable, not `{fileName}`: {effectiveAdbPath}",
                adbPath,
                effectiveAdbPath);
            return false;
        }

        if (!HasAnyExecuteBit(effectiveAdbPath))
        {
            diagnostic = AppendResolutionContext(
                $"ADB exists but is not executable. Expected executable bit on: {effectiveAdbPath}",
                adbPath,
                effectiveAdbPath);
            return false;
        }

        return true;
    }

    public static string BuildResolutionContext(
        string? requestedAdbPath,
        string? effectiveAdbPath,
        bool? macBundledAdbRequested = null,
        string? resolutionFailure = null)
        => AppendResolutionContext(
            "macOS ADB resolution",
            requestedAdbPath,
            effectiveAdbPath,
            macBundledAdbRequested,
            resolutionFailure);

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

    private static string? TryFindAdbUnderDirectory(string directory)
    {
        var candidates = new[]
        {
            Path.Combine(directory, BundledAdbFileName),
            Path.Combine(directory, BundledAdbDirectoryName, BundledAdbFileName),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool LooksLikeWindowsPath(string path)
    {
        return path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';
    }

    private static bool HasAnyExecuteBit(string path)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);
            return mode.HasFlag(UnixFileMode.UserExecute)
                   || mode.HasFlag(UnixFileMode.GroupExecute)
                   || mode.HasFlag(UnixFileMode.OtherExecute);
        }
        catch
        {
            return true;
        }
    }

    private static string AppendResolutionContext(
        string message,
        string? requestedAdbPath,
        string? effectiveAdbPath,
        bool? macBundledAdbRequested = null,
        string? resolutionFailure = null)
    {
        if (!IsSupportedPlatform)
        {
            return message;
        }

        var expectedBundledPath = ResolveBundledAdbPath();
        var effective = string.IsNullOrWhiteSpace(effectiveAdbPath)
            ? "<system adb>"
            : effectiveAdbPath.Trim();
        var requested = string.IsNullOrWhiteSpace(requestedAdbPath)
            ? "<empty/system adb>"
            : requestedAdbPath.Trim();
        var usingBundled = !string.IsNullOrWhiteSpace(effectiveAdbPath) && IsBundledAdbPath(effectiveAdbPath);
        var expectedBundledExists = File.Exists(expectedBundledPath);
        var expectedBundledExecutable = expectedBundledExists && HasAnyExecuteBit(expectedBundledPath);
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var adbPathDiagnostic = BuildAdbPathDiagnostic(effectiveAdbPath, path);
        var requestedBundled = macBundledAdbRequested is bool requestedValue
            ? requestedValue.ToString(CultureInfo.InvariantCulture)
            : "<unknown>";
        var failure = string.IsNullOrWhiteSpace(resolutionFailure)
            ? "<none>"
            : resolutionFailure.Trim();

        return $"{message}; requested={requested}; effective={effective}; useBundled={usingBundled}; macBundledAdbRequested={requestedBundled}; effectiveIsBundled={usingBundled}; expectedBundled={expectedBundledPath}; expectedBundledExists={expectedBundledExists}; expectedBundledExecutable={expectedBundledExecutable}; resolvedPath={adbPathDiagnostic.ResolvedPath}; exists={adbPathDiagnostic.Exists}; reason={adbPathDiagnostic.Reason}; path={adbPathDiagnostic.Path}; resolutionFailure={failure}; PATH={path}";
    }

    private static AdbPathDiagnostic BuildAdbPathDiagnostic(string? effectiveAdbPath, string path)
    {
        if (string.IsNullOrWhiteSpace(effectiveAdbPath))
        {
            return new AdbPathDiagnostic("<system adb>", "<unknown>", "<system adb>", "system");
        }

        var trimmed = effectiveAdbPath.Trim();
        if (string.Equals(trimmed, BundledAdbFileName, StringComparison.OrdinalIgnoreCase))
        {
            var resolved = TryResolveExecutableFromPath(BundledAdbFileName, path);
            return string.IsNullOrWhiteSpace(resolved)
                ? new AdbPathDiagnostic("<not found>", "False", path, "not found")
                : new AdbPathDiagnostic(resolved, "True", path, "<none>");
        }

        var exists = File.Exists(trimmed);
        return new AdbPathDiagnostic(
            "<none>",
            exists.ToString(CultureInfo.InvariantCulture),
            trimmed,
            exists ? "<none>" : "missing");
    }

    private static string? TryResolveExecutableFromPath(string fileName, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(segment, fileName);
            if (File.Exists(candidate) && HasAnyExecuteBit(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed record AdbPathDiagnostic(string ResolvedPath, string Exists, string Path, string Reason);
}
