using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace MAAUnified.Application.Services.VersionUpdate;

public enum UpdatePackageKind
{
    NotMatched = 0,
    FullPackage = 1,
    OtaPackage = 2,
}

/// <summary>
/// 更新包文件名解析结果。
/// </summary>
/// <param name="Kind">完整包 / OTA 差分包。</param>
/// <param name="SourceVersion">OTA 包声明的源版本；完整包为 <c>null</c>。</param>
/// <param name="TargetVersion">目标版本。</param>
/// <param name="Platform">包内平台标识（windows / linux / macos）。</param>
/// <param name="Architecture">包内架构标识（x64 / arm64）。</param>
/// <param name="IsLegacyWindowsLayout">是否为 WPF 时代的 <c>MAA-*-win-*.zip</c> 布局（仅 Windows 有效）。</param>
public sealed record UpdatePackageIdentity(
    UpdatePackageKind Kind,
    string? SourceVersion,
    string TargetVersion,
    string Platform,
    string Architecture,
    bool IsLegacyWindowsLayout);

/// <summary>
/// 更新包校验结果。
/// </summary>
public sealed record UpdatePackageValidation(bool Supported, string Reason)
{
    public static UpdatePackageValidation Accept { get; } = new(true, string.Empty);
}

/// <summary>
/// 拖入 / 手动指定的更新包文件名识别与校验。
/// 对应 WPF <c>PendingUpdateApplier.InspectLocalUpdatePackage</c>（<c>42473f1ede</c>、<c>6591943eaf</c>）：
/// 完整包 <c>MAA-*-win-*.zip</c>、OTA 包 <c>MAAComponent-OTA-*-win-*.zip</c> 做文件名正则校验并核对架构与版本方向；
/// MAAUnified 自身发布产物的命名见 <c>CI/ci-avalonia.yml</c>（<c>MAAUnified-{tag}-{platform}-{arch}</c>）。
/// 跨平台说明：WPF 的 <c>MAA-*-win-*</c> 布局只对 Windows 安装有效，其他平台显式拒绝而不是猜测安装路径。
/// </summary>
public static class UpdatePackageNameRecognizer
{
    private const RegexOptions PackageRegexOptions =
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly Regex MaaUnifiedFullPackageRegex = new(
        @"^MAAUnified-(?<version>v[0-9A-Za-z.+-]+?)-(?<platform>windows|linux|macos)-(?<arch>x64|arm64)\.(zip|tar\.gz|dmg|AppImage)$",
        PackageRegexOptions);

    private static readonly Regex MaaUnifiedOtaPackageRegex = new(
        @"^MAAUnifiedComponent-OTA-(?<from>v[0-9A-Za-z.+-]+?)_(?<to>v[0-9A-Za-z.+-]+?)-(?<platform>windows|linux|macos)-(?<arch>x64|arm64)\.zip$",
        PackageRegexOptions);

    private static readonly Regex LegacyFullPackageRegex = new(
        @"^MAA-(?<version>v.+?)-win-(?<arch>x64|arm64)\.zip$",
        PackageRegexOptions);

    private static readonly Regex LegacyOtaPackageRegex = new(
        @"^MAAComponent-OTA-(?<from>v.+?|DEBUG_VERSION)_(?<to>v.+?)-win-(?<arch>x64|arm64)\.zip$",
        PackageRegexOptions);

    /// <summary>
    /// 按文件名识别更新包。两种布局都不匹配时返回 <c>false</c>（可能是资源包或无关文件）。
    /// </summary>
    public static bool TryRecognize(string? packageNameOrPath, out UpdatePackageIdentity identity)
    {
        identity = default!;
        if (string.IsNullOrWhiteSpace(packageNameOrPath))
        {
            return false;
        }

        var fileName = Path.GetFileName(packageNameOrPath.Trim());
        if (fileName.Length == 0)
        {
            return false;
        }

        var fullMatch = MaaUnifiedFullPackageRegex.Match(fileName);
        if (fullMatch.Success)
        {
            identity = new UpdatePackageIdentity(
                UpdatePackageKind.FullPackage,
                SourceVersion: null,
                TargetVersion: fullMatch.Groups["version"].Value,
                Platform: fullMatch.Groups["platform"].Value.ToLowerInvariant(),
                Architecture: fullMatch.Groups["arch"].Value.ToLowerInvariant(),
                IsLegacyWindowsLayout: false);
            return true;
        }

        var otaMatch = MaaUnifiedOtaPackageRegex.Match(fileName);
        if (otaMatch.Success)
        {
            identity = new UpdatePackageIdentity(
                UpdatePackageKind.OtaPackage,
                SourceVersion: otaMatch.Groups["from"].Value,
                TargetVersion: otaMatch.Groups["to"].Value,
                Platform: otaMatch.Groups["platform"].Value.ToLowerInvariant(),
                Architecture: otaMatch.Groups["arch"].Value.ToLowerInvariant(),
                IsLegacyWindowsLayout: false);
            return true;
        }

        var legacyFullMatch = LegacyFullPackageRegex.Match(fileName);
        if (legacyFullMatch.Success)
        {
            identity = new UpdatePackageIdentity(
                UpdatePackageKind.FullPackage,
                SourceVersion: null,
                TargetVersion: legacyFullMatch.Groups["version"].Value,
                Platform: "windows",
                Architecture: legacyFullMatch.Groups["arch"].Value.ToLowerInvariant(),
                IsLegacyWindowsLayout: true);
            return true;
        }

        var legacyOtaMatch = LegacyOtaPackageRegex.Match(fileName);
        if (legacyOtaMatch.Success)
        {
            identity = new UpdatePackageIdentity(
                UpdatePackageKind.OtaPackage,
                SourceVersion: legacyOtaMatch.Groups["from"].Value,
                TargetVersion: legacyOtaMatch.Groups["to"].Value,
                Platform: "windows",
                Architecture: legacyOtaMatch.Groups["arch"].Value.ToLowerInvariant(),
                IsLegacyWindowsLayout: true);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 校验更新包能否用于当前安装：平台、架构、版本方向。
    /// </summary>
    /// <param name="identity">文件名识别结果。</param>
    /// <param name="currentVersion">当前版本（如 <c>v1.2.3</c>）。</param>
    /// <param name="currentPlatform">当前平台标识，取值同 <see cref="UpdatePackageIdentity.Platform"/>。</param>
    /// <param name="currentArchitecture">当前架构标识，取值同 <see cref="UpdatePackageIdentity.Architecture"/>。</param>
    /// <param name="allowSameVersion">允许目标版本等于当前版本（完整性修复重装场景）。</param>
    public static UpdatePackageValidation Validate(
        UpdatePackageIdentity identity,
        string currentVersion,
        string currentPlatform,
        string currentArchitecture,
        bool allowSameVersion = false)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var normalizedPlatform = NormalizePlatform(currentPlatform);
        if (!string.Equals(identity.Platform, normalizedPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return new UpdatePackageValidation(
                false,
                $"Package platform '{identity.Platform}' does not match current platform '{normalizedPlatform}'.");
        }

        if (!string.Equals(identity.Architecture, NormalizeArchitecture(currentArchitecture), StringComparison.OrdinalIgnoreCase))
        {
            return new UpdatePackageValidation(
                false,
                $"Package architecture '{identity.Architecture}' does not match current architecture '{NormalizeArchitecture(currentArchitecture)}'.");
        }

        var baselineVersion = identity.Kind == UpdatePackageKind.OtaPackage
            ? identity.SourceVersion ?? string.Empty
            : currentVersion;
        var targetComparison = CompareVersions(identity.TargetVersion, baselineVersion);
        if (targetComparison is null)
        {
            return new UpdatePackageValidation(false, $"Cannot compare package version '{identity.TargetVersion}' with '{baselineVersion}'.");
        }

        if (targetComparison < 0)
        {
            return new UpdatePackageValidation(
                false,
                $"Package target version '{identity.TargetVersion}' is older than '{baselineVersion}'.");
        }

        if (targetComparison == 0 && !allowSameVersion)
        {
            return new UpdatePackageValidation(
                false,
                $"Package target version '{identity.TargetVersion}' equals '{baselineVersion}'.");
        }

        if (identity.Kind == UpdatePackageKind.OtaPackage)
        {
            var sourceComparison = CompareVersions(identity.SourceVersion, currentVersion);
            if (sourceComparison is null)
            {
                return new UpdatePackageValidation(
                    false,
                    $"Cannot compare OTA source version '{identity.SourceVersion}' with '{currentVersion}'.");
            }

            if (sourceComparison != 0)
            {
                return new UpdatePackageValidation(
                    false,
                    $"OTA source version '{identity.SourceVersion}' does not match current version '{currentVersion}'.");
            }
        }

        return UpdatePackageValidation.Accept;
    }

    public static string NormalizePlatform(string? platform)
    {
        var normalized = (platform ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "win" or "win32" or "windows" => "windows",
            "osx" or "macos" or "mac" or "macosx" => "macos",
            "linux" => "linux",
            _ => normalized,
        };
    }

    public static string NormalizeArchitecture(string? architecture)
    {
        var normalized = (architecture ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "amd64" or "x86_64" or "x64" => "x64",
            "arm64" or "aarch64" => "arm64",
            _ => normalized,
        };
    }

    /// <summary>
    /// 比较 <c>vX.Y.Z</c> 形式版本号。任一侧无法解析时返回 <c>null</c>。
    /// </summary>
    public static int? CompareVersions(string? left, string? right)
    {
        if (!TryParseVersion(left, out var leftVersion) || !TryParseVersion(right, out var rightVersion))
        {
            return null;
        }

        return leftVersion.CompareTo(rightVersion);
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().TrimStart('v', 'V');
        var dashIndex = normalized.IndexOfAny(['-', '+']);
        if (dashIndex >= 0)
        {
            normalized = normalized[..dashIndex];
        }

        return Version.TryParse(normalized, out version!);
    }
}
