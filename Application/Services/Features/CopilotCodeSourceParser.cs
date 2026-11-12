namespace MAAUnified.Application.Services.Features;

/// <summary>
/// 作业站神秘代码类型。
/// </summary>
public enum CopilotCodeSourceKind
{
    /// <summary>不是作业站代码。</summary>
    None,

    /// <summary>单个作业。</summary>
    Copilot,

    /// <summary>作业集。</summary>
    CopilotSet,
}

/// <summary>
/// WPF TryParseCopilotCode（dev-v2 72213def33）对齐：支持 maa://、prts://、prts://s、s 前缀与纯数字，
/// 前缀按从长到短匹配。作业与作业集的区分只由前缀决定，数字本身不参与类型判定。
/// </summary>
public static class CopilotCodeSourceParser
{
    public const string CopilotIdPrefix = "maa://";
    public const string CopilotNewIdPrefix = "prts://";
    public const string CopilotNewSetIdPrefix = "prts://s";

    public static bool TryParse(string? source, out CopilotCodeSourceKind kind, out int id)
    {
        kind = CopilotCodeSourceKind.None;
        id = 0;

        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var normalized = source.Trim();

        // 从长到短匹配，避免 prts://s 被 prts:// 抢先；前缀命中但数字非法时返回 false，
        // 且不留下部分解析结果（调用方可安全忽略 out 值）。
        if (normalized.StartsWith(CopilotNewSetIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(normalized[CopilotNewSetIdPrefix.Length..], out id))
            {
                return false;
            }

            kind = CopilotCodeSourceKind.CopilotSet;
            return true;
        }

        if (normalized.StartsWith(CopilotNewIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(normalized[CopilotNewIdPrefix.Length..], out id))
            {
                return false;
            }

            kind = CopilotCodeSourceKind.Copilot;
            return true;
        }

        if (normalized.StartsWith(CopilotIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(normalized[CopilotIdPrefix.Length..], out id))
            {
                return false;
            }

            kind = CopilotCodeSourceKind.Copilot;
            return true;
        }

        // s12345 格式作业集
        if (normalized.Length > 1
            && (normalized[0] is 's' or 'S')
            && int.TryParse(normalized[1..], out id))
        {
            kind = CopilotCodeSourceKind.CopilotSet;
            return true;
        }

        // 纯数字，默认当单个作业
        if (int.TryParse(normalized, out id))
        {
            kind = CopilotCodeSourceKind.Copilot;
            return true;
        }

        return false;
    }
}
