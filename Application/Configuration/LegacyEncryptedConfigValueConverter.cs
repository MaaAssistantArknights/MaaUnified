using System.Text;
using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Application.Security;
using MAAUnified.Compat.Constants;

namespace MAAUnified.Application.Configuration;

internal static class LegacyEncryptedConfigValueConverter
{
    private const string GlobalConfigurationName = "Global";

    public static JsonNode? ConvertProfileValue(string profileName, string key, JsonNode? value, ImportReport report)
    {
        if (!SensitiveSettingKeyRegistry.TryGet(key, out var definition)
            || definition.Scope != SensitiveSettingScope.Profile)
        {
            return value;
        }

        return ConvertEncryptedStringValue(profileName, key, value, report, isGlobal: false);
    }

    public static JsonNode? ConvertGlobalValue(string key, JsonNode? value, ImportReport report)
    {
        if (!SensitiveSettingKeyRegistry.TryGet(key, out var definition)
            || definition.Scope != SensitiveSettingScope.Global)
        {
            return value;
        }

        return ConvertEncryptedStringValue(GlobalConfigurationName, key, value, report, isGlobal: true);
    }

    private static JsonNode? ConvertEncryptedStringValue(
        string configurationName,
        string key,
        JsonNode? value,
        ImportReport report,
        bool isGlobal)
    {
        if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text))
        {
            return value;
        }

        if (string.IsNullOrEmpty(text) || IsKnownPlainDefault(key, text))
        {
            return value;
        }

        if (!TryDecodeWindowsDpapiBlob(text, out var encryptedBytes))
        {
            return value;
        }

        if (!OperatingSystem.IsWindows())
        {
            AppendUnreadableValue(
                report,
                configurationName,
                key,
                isGlobal,
                "unsupported-platform",
                $"Legacy encrypted config value `{configurationName}.{key}` could not be decrypted on this platform and was cleared. Please re-enter it after import.");
            return JsonValue.Create(string.Empty);
        }

        try
        {
            var decrypted = InvokeWindowsProtectedData("Unprotect", encryptedBytes);
            if (decrypted is null)
            {
                AppendUnreadableValue(
                    report,
                    configurationName,
                    key,
                    isGlobal,
                    "decrypt-failed",
                    $"Legacy encrypted config value `{configurationName}.{key}` could not be decrypted and was cleared. Please re-enter it after import.");
                return JsonValue.Create(string.Empty);
            }

            return JsonValue.Create(Encoding.UTF8.GetString(decrypted));
        }
        catch
        {
            AppendUnreadableValue(
                report,
                configurationName,
                key,
                isGlobal,
                "decrypt-failed",
                $"Legacy encrypted config value `{configurationName}.{key}` could not be decrypted and was cleared. Please re-enter it after import.");
            return JsonValue.Create(string.Empty);
        }
    }

    private static bool IsKnownPlainDefault(string key, string value)
    {
        return string.Equals(key, ConfigurationKeys.ExternalNotificationBarkServer, StringComparison.OrdinalIgnoreCase)
               && string.Equals(value, "https://api.day.app", StringComparison.Ordinal);
    }

    private static bool TryDecodeWindowsDpapiBlob(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length < 64)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(text);
        }
        catch
        {
            return false;
        }

        ReadOnlySpan<byte> dpapiProviderGuid =
        [
            0xd0, 0x8c, 0x9d, 0xdf, 0x01, 0x15, 0xd1, 0x11,
            0x8c, 0x7a, 0x00, 0xc0, 0x4f, 0xc2, 0x97, 0xeb,
        ];

        return bytes.Length > 20
               && bytes[0] == 0x01
               && bytes[1] == 0x00
               && bytes[2] == 0x00
               && bytes[3] == 0x00
               && bytes.AsSpan(4, dpapiProviderGuid.Length).SequenceEqual(dpapiProviderGuid);
    }

    private static byte[]? InvokeWindowsProtectedData(string methodName, byte[] data)
    {
        var protectedDataType = Type.GetType(
            "System.Security.Cryptography.ProtectedData, System.Security.Cryptography.ProtectedData");
        var dataProtectionScopeType = Type.GetType(
            "System.Security.Cryptography.DataProtectionScope, System.Security.Cryptography.ProtectedData");
        if (protectedDataType is null || dataProtectionScopeType is null)
        {
            return null;
        }

        var method = protectedDataType.GetMethod(
            methodName,
            [typeof(byte[]), typeof(byte[]), dataProtectionScopeType]);
        if (method is null)
        {
            return null;
        }

        var currentUserScope = Enum.Parse(dataProtectionScopeType, "CurrentUser");
        return method.Invoke(null, [data, null, currentUserScope]) as byte[];
    }

    private static void AppendWarningOnce(ImportReport report, string warning)
    {
        if (!report.Warnings.Contains(warning, StringComparer.Ordinal))
        {
            report.Warnings.Add(warning);
        }
    }

    private static void AppendUnreadableValue(
        ImportReport report,
        string configurationName,
        string key,
        bool isGlobal,
        string reason,
        string warning)
    {
        AppendWarningOnce(report, warning);

        if (report.UnreadableValues.Any(value =>
                string.Equals(value.ConfigurationName, configurationName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(value.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var display = SensitiveSettingKeyRegistry.TryGet(key, out var known)
            ? (known.DisplayName, known.LocalizationKey)
            : (DisplayName: key, LocalizationKey: string.Empty);
        report.UnreadableValues.Add(new ImportUnreadableConfigValue(
            configurationName,
            key,
            display.DisplayName,
            display.LocalizationKey,
            isGlobal,
            reason));
    }
}
