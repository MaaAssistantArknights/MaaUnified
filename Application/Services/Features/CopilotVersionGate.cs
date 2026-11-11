using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MAAUnified.Compat.Runtime;
using MAAUnified.CoreBridge;

namespace MAAUnified.Application.Services.Features;

// WPF CopilotContentConverter.ReadJson（冻结 CopilotHelper.cs:211-280）对齐：
// 非 Debug 版本（按 UI 版本号正则判定，对齐 VersionUpdateDialogViewModel.IsDebugVersion）下，
// 对普通作业与 SSS 作业统一强制 SemVersion 门控——minimum_required 缺失/不可解析
// （WPF CopilotBase.MinimumRequired 默认 string.Empty，SemVersion.TryParse 失败即抛）、
// 或高于当前 core 版本时拒绝解析（WPF 抛 JsonSerializationException → CopilotFileReadError）。
// core 版本来源不可用时（未安装 core 的开发/测试环境）跳过门控：
// WPF 发布包必含 core（CoreVersion = AsstGetVersion() ?? "0.0.1"），不存在该状态。
public static class CopilotVersionGate
{
    private static readonly object UiVersionSync = new();
    private static string? _cachedUiVersion;

    // 返回拒绝原因；null 表示通过（或按 Debug/版本来源不可用规则豁免）
    public static string? Evaluate(JsonObject jobObject, string? coreVersion, string? uiVersion)
    {
        ArgumentNullException.ThrowIfNull(jobObject);

        if (IsDebugVersion(uiVersion))
        {
            return null;
        }

        if (string.IsNullOrEmpty(coreVersion))
        {
            // 版本来源不可用（开发/测试环境未安装 core）：跳过门控
            return null;
        }

        // WPF/Semver 3.0.0 对齐：core 版本字符串原样进入 SemVersion.TryParse（锚定解析不裁剪），
        // 首尾空白等非法形式 → WPF "Unable to parse Maa version" 拒绝
        if (!SemanticVersion.TryParse(coreVersion, out var currentVersion))
        {
            // WPF: "Unable to parse Maa version"
            return string.Format(
                CultureInfo.InvariantCulture,
                "无法解析当前 core 版本号：{0}。",
                coreVersion);
        }

        var minimumRequired = ReadMinimumRequired(jobObject);
        if (!SemanticVersion.TryParse(minimumRequired, out var requiredVersion))
        {
            // WPF: "Unable to parse required version"（缺失/空串/非法版本号均落入此分支，即事实上必填）
            return "作业缺少可解析的 minimum_required 最低 core 版本号，无法导入。";
        }

        if (currentVersion.CompareTo(requiredVersion) < 0)
        {
            // WPF: "Current Maa version is lower than required version"
            return string.Format(
                CultureInfo.InvariantCulture,
                "作业要求的最低 core 版本为 {0}，当前 core 版本为 {1}，请更新 MAA 后再导入。",
                minimumRequired,
                coreVersion);
        }

        return null;
    }

    // WPF VersionUpdateDialogViewModel.IsDebugVersion 同款正则（冻结 :1221-1230）
    public static bool IsDebugVersion(string? version)
        => !string.IsNullOrEmpty(version)
            && Regex.IsMatch(
                version,
                @"^(.*DEBUG.*|v\d+(\.\d+){1,3}-\d+-g[0-9a-f]{6,}|[^v][0-9a-f]{6,})$",
                RegexOptions.CultureInvariant);

    public static string? ResolveInstalledCoreVersion(string? baseDirectory = null)
    {
        try
        {
            return MaaCoreBridgeNative.TryReadInstalledVersion(
                baseDirectory ?? RuntimeLayout.ResolveRuntimeBaseDirectory());
        }
        catch
        {
            return null;
        }
    }

    public static string? ResolveUiVersion()
    {
        lock (UiVersionSync)
        {
            if (_cachedUiVersion is not null)
            {
                return _cachedUiVersion;
            }

#if DEBUG
            // WPF 对齐：本地 Debug 配置构建的 UI 版本恒为 DEBUG_VERSION
            // （冻结 VersionUpdateDialogViewModel.IsDebugVersion 注释 "Debug (Local)：DEBUG_VERSION"），
            // 命中 IsDebugVersion 豁免正则、跳过 minimum_required 门控；
            // Release/CI 构建维持按版本字符串正则判定（WPF CI DevBuild 版本号同样不豁免）
            _cachedUiVersion = "DEBUG_VERSION";
            return _cachedUiVersion;
#else
            string? resolved = null;
            try
            {
                var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var informational = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
                resolved = string.IsNullOrWhiteSpace(informational)
                    ? assembly.GetName().Version?.ToString()
                    : informational.Split('+')[0];
            }
            catch
            {
                resolved = null;
            }

            _cachedUiVersion = resolved ?? string.Empty;
            return _cachedUiVersion;
#endif
        }
    }

    private static string? ReadMinimumRequired(JsonObject jobObject)
    {
        // Newtonsoft 属性名匹配大小写不敏感（先精确后忽略大小写）；非字符串值与 JSON null
        // 在 WPF 侧同样导致 SemVersion.TryParse 失败，按不可解析处理
        if (!TryGetPropertyValue(jobObject, "minimum_required", out var node))
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue(out string? raw) ? raw : null;
    }

    private static bool TryGetPropertyValue(JsonObject obj, string key, out JsonNode? value)
    {
        // Newtonsoft POCO 绑定对齐：键大小写不敏感；大小写变体重复键按文档序后出现的覆盖先出现的
        // （last-wins，JsonSerializer 顺序读取逐属性赋值）
        value = null;
        var found = false;
        foreach (var property in obj)
        {
            if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }

        return found;
    }

    // 与 StageManagerFeatureService.SemanticVersion 同源：SemVersion AllowLowerV 严格三段 +
    // 预 release 标识符比较（semver 2.0 排序，对齐 WPF CompareSortOrderTo）
    private sealed record SemanticVersion(int Major, int Minor, int Patch, IReadOnlyList<string> PreRelease)
        : IComparable<SemanticVersion>
    {
        public static bool TryParse(string? value, out SemanticVersion version)
        {
            version = null!;
            // WPF Semver 3.0.0 锚定解析对齐：不裁剪首尾空白（含空白即非法）
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            var normalized = value;
            if (normalized.StartsWith('v'))
            {
                normalized = normalized[1..];
            }

            var buildSeparator = normalized.IndexOf('+');
            if (buildSeparator >= 0)
            {
                // Semver 对齐：build 元数据标识符同样要求非空 [0-9A-Za-z-]
                var buildMetadata = normalized[(buildSeparator + 1)..];
                if (buildMetadata.Length == 0
                    || buildMetadata.Split('.').Any(static identifier => identifier.Length == 0
                        || identifier.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
                {
                    return false;
                }

                normalized = normalized[..buildSeparator];
            }

            var prereleaseSeparator = normalized.IndexOf('-');
            var core = prereleaseSeparator >= 0 ? normalized[..prereleaseSeparator] : normalized;
            var prerelease = prereleaseSeparator >= 0 ? normalized[(prereleaseSeparator + 1)..] : string.Empty;
            // Semver 3.0 锚定解析对齐："-" 之后必须至少一个 prerelease 标识符，
            // "1.2.3-" / "v1.2.3-" / "1.2.3-+build" 等空 prerelease 形式一律解析失败
            if (prereleaseSeparator >= 0 && prerelease.Length == 0)
            {
                return false;
            }

            var components = core.Split('.');
            if (components.Length != 3
                // Semver 对齐：主/次/补丁版本号禁止前导零
                || components.Any(static component => component.Length > 1 && component[0] == '0')
                || !int.TryParse(components[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
                || !int.TryParse(components[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
                || !int.TryParse(components[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            {
                return false;
            }

            var identifiers = string.IsNullOrEmpty(prerelease) ? [] : prerelease.Split('.');
            if (identifiers.Any(static identifier => string.IsNullOrWhiteSpace(identifier)
                || identifier.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '-')
                // Semver 对齐：数字 prerelease 标识符禁止前导零（如 1.0.0-01）
                || (identifier.All(char.IsAsciiDigit) && identifier.Length > 1 && identifier[0] == '0')))
            {
                return false;
            }

            version = new SemanticVersion(major, minor, patch, identifiers);
            return true;
        }

        public int CompareTo(SemanticVersion? other)
        {
            if (other is null)
            {
                return 1;
            }

            var coreComparison = Major.CompareTo(other.Major);
            coreComparison = coreComparison != 0 ? coreComparison : Minor.CompareTo(other.Minor);
            coreComparison = coreComparison != 0 ? coreComparison : Patch.CompareTo(other.Patch);
            if (coreComparison != 0)
            {
                return coreComparison;
            }

            if (PreRelease.Count == 0 || other.PreRelease.Count == 0)
            {
                return PreRelease.Count == other.PreRelease.Count ? 0 : (PreRelease.Count == 0 ? 1 : -1);
            }

            for (var index = 0; index < Math.Min(PreRelease.Count, other.PreRelease.Count); index++)
            {
                var left = PreRelease[index];
                var right = other.PreRelease[index];
                var leftIsNumber = left.All(char.IsAsciiDigit);
                var rightIsNumber = right.All(char.IsAsciiDigit);
                int comparison;
                if (leftIsNumber && rightIsNumber)
                {
                    // 任意精度数值比较：先比长度再按字典序（解析已排除前导零，对齐 Semver 数字
                    // 语义，避免 > int.MaxValue 标识符溢出后退化为字符串比较）
                    comparison = left.Length != right.Length
                        ? left.Length.CompareTo(right.Length)
                        : string.CompareOrdinal(left, right);
                }
                else if (leftIsNumber)
                {
                    comparison = -1;
                }
                else if (rightIsNumber)
                {
                    comparison = 1;
                }
                else
                {
                    comparison = string.CompareOrdinal(left, right);
                }

                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return PreRelease.Count.CompareTo(other.PreRelease.Count);
        }
    }
}
