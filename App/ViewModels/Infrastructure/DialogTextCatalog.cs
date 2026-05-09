using MAAUnified.Application.Models;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.App.ViewModels.Infrastructure;

public static class DialogTextCatalog
{
    private static readonly IReadOnlyDictionary<string, string> EmptyNamedTexts =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private const string Scope = "DialogTextCatalog";

    public static class ChromeKeys
    {
        public const string Prompt = "Prompt";
        public const string LeadText = "LeadText";
        public const string EmphasisText = "EmphasisText";
        public const string DetailText = "DetailText";
        public const string FilterWatermark = "FilterWatermark";
        public const string RefreshButton = "RefreshButton";
        public const string RefreshingButton = "RefreshingButton";
        public const string SectionTitle = "SectionTitle";
        public const string CopyButton = "CopyButton";
        public const string IssueReportButton = "IssueReportButton";
        public const string TimestampLabel = "TimestampLabel";
        public const string ContextLabel = "ContextLabel";
        public const string CodeLabel = "CodeLabel";
        public const string MessageLabel = "MessageLabel";
        public const string DetailsLabel = "DetailsLabel";
        public const string SuggestionLabel = "SuggestionLabel";
        public const string DetailsButton = "DetailsButton";
    }

    public static bool UseChinese(string? language)
    {
        var normalized = UiLanguageCatalog.Normalize(language);
        return string.Equals(normalized, "zh-cn", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "zh-tw", StringComparison.OrdinalIgnoreCase);
    }

    public static string Select(string? language, string zh, string en)
    {
        return UseChinese(language) ? zh : en;
    }

    public static string ErrorDialogTitle(string? language)
    {
        return GetText(language, "Dialog.Error.Title", "错误提示", "Error");
    }

    public static string ErrorDialogConnectFailedTitle(string? language)
    {
        return GetText(language, "Dialog.Error.ConnectFailedTitle", "连接模拟器失败", "Failed to connect to emulator");
    }

    public static string ErrorDialogSectionTitle(string? language)
    {
        return GetText(language, "Dialog.Error.SectionTitle", "错误详情", "Error");
    }

    public static string ErrorDialogCopyButton(string? language)
    {
        return GetText(language, "Dialog.Error.CopyButton", "复制", "Copy");
    }

    public static string ErrorDialogCopyErrorInfoButton(string? language)
    {
        return GetText(language, "Dialog.Error.CopyErrorInfoButton", "详细报错", "Error details");
    }

    public static string ErrorDialogIssueReportButton(string? language)
    {
        return GetText(language, "Dialog.Error.IssueReportButton", "问题反馈", "IssueReport");
    }

    public static string ErrorDialogCloseButton(string? language)
    {
        return GetText(language, "Dialog.Error.CloseButton", "关闭", "Close");
    }

    public static string ErrorDialogIgnoreButton(string? language)
    {
        return GetText(language, "Dialog.Error.IgnoreButton", "忽略", "Ignore");
    }

    public static string ErrorDialogTimestampLabel(string? language)
    {
        return GetText(language, "Dialog.Error.TimestampLabel", "时间", "TimestampUtc");
    }

    public static string ErrorDialogContextLabel(string? language)
    {
        return GetText(language, "Dialog.Error.ContextLabel", "上下文", "Context");
    }

    public static string ErrorDialogCodeLabel(string? language)
    {
        return GetText(language, "Dialog.Error.CodeLabel", "错误码", "Code");
    }

    public static string ErrorDialogMessageLabel(string? language)
    {
        return GetText(language, "Dialog.Error.MessageLabel", "消息", "Message");
    }

    public static string ErrorDialogDetailsLabel(string? language)
    {
        return GetText(language, "Dialog.Error.DetailsLabel", "详情", "Details");
    }

    public static string ErrorDialogSuggestionLabel(string? language)
    {
        return GetText(language, "Dialog.Error.SuggestionLabel", "建议", "Suggestion");
    }

    public static string WarningDialogTitle(string? language)
    {
        return GetText(language, "Dialog.Warning.Title", "警告", "Warning");
    }

    public static string WarningDialogPrompt(string? language)
    {
        return GetText(language, "Dialog.Warning.Prompt", "确认执行此操作？", "Do you want to continue?");
    }

    public static string WarningDialogConfirmButton(string? language)
    {
        return GetText(language, "Dialog.Warning.ConfirmButton", "确认", "Confirm");
    }

    public static string WarningDialogCancelButton(string? language)
    {
        return GetText(language, "Dialog.Warning.CancelButton", "取消", "Cancel");
    }

    public static string WarningDialogDetailsButton(string? language)
    {
        return GetText(language, "Dialog.Warning.DetailsButton", "详细报错", "Error details");
    }

    private static string GetText(string? language, string key, string zhFallback, string enFallback)
    {
        return UiLocalizer.Create(UiLanguageCatalog.Normalize(language))
            .GetOrDefault(key, Select(language, zhFallback, enFallback), Scope);
    }

    public static DialogChromeCatalog CreateCatalog(
        string? language,
        Func<string, DialogChromeSnapshot> snapshotFactory)
    {
        var normalized = UiLanguageCatalog.Normalize(language);
        return new DialogChromeCatalog(
            normalized,
            nextLanguage => snapshotFactory(UiLanguageCatalog.Normalize(nextLanguage)));
    }

    public static DialogChromeCatalog CreateRootCatalog(
        string? language,
        string scope,
        Func<RootLocalizationTextMap, DialogChromeSnapshot> snapshotFactory,
        Action<LocalizationFallbackInfo>? fallbackReporter = null)
    {
        return CreateCatalog(
            language,
            nextLanguage =>
            {
                var texts = new RootLocalizationTextMap(scope)
                {
                    Language = nextLanguage,
                };
                if (fallbackReporter is not null)
                {
                    texts.FallbackReported += fallbackReporter;
                }

                return snapshotFactory(texts);
            });
    }

    public static IReadOnlyDictionary<string, string> CreateNamedTexts(params (string Key, string Value)[] entries)
    {
        if (entries.Length == 0)
        {
            return EmptyNamedTexts;
        }

        var namedTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            namedTexts[key] = value;
        }

        return namedTexts.Count == 0 ? EmptyNamedTexts : namedTexts;
    }

    public static UiOperationResult LocalizeErrorResult(string? language, UiOperationResult result)
    {
        if (result.Success || result.Error is null)
        {
            return result;
        }

        var localizedMessage = BuildErrorMessage(language, result);
        var details = BuildLocalizedDetails(language, result, localizedMessage);
        return result with
        {
            Message = localizedMessage,
            Error = result.Error with
            {
                Message = localizedMessage,
                Details = details,
            },
        };
    }

    public static string BuildErrorSuggestion(string? language, UiOperationResult result)
    {
        var rawMessage = result.Error?.Message ?? result.Message;
        return result.Error?.Code switch
        {
            UiErrorCode.ConfigurationProfileInvalidName
                when rawMessage.Contains("cannot be empty", StringComparison.OrdinalIgnoreCase)
                => Select(language, "请输入配置名称后再试。", "Enter a profile name and try again."),

            UiErrorCode.ConfigurationProfileInvalidName
                when rawMessage.Contains("control characters", StringComparison.OrdinalIgnoreCase)
                => Select(language, "请移除换行等不可见控制字符后重试。", "Remove control characters such as line breaks and try again."),

            UiErrorCode.ConfigurationProfileAlreadyExists
                => Select(language, "请换一个未使用的配置名称。", "Choose a different unused profile name."),

            UiErrorCode.ConfigurationProfileNotFound or UiErrorCode.ProfileMissing
                => Select(language, "请刷新配置列表，确认目标配置存在后重试。", "Refresh the profile list and make sure the target profile still exists."),

            UiErrorCode.TaskNameMissing
                => Select(language, "请输入任务名称后再试。", "Enter a task name and try again."),

            UiErrorCode.EmulatorPathMissing
                => Select(language, "请先填写模拟器路径。", "Set the emulator path before trying again."),

            UiErrorCode.EmulatorPathNotFound
                => Select(language, "请检查模拟器路径是否存在。", "Check whether the emulator path exists."),

            UiErrorCode.ConnectFailed
                => Select(
                    language,
                    "请确认模拟器已启动，并检查连接地址、ADB 路径和连接配置后重试；如使用局域网地址，请确认设备与电脑在同一网络内。",
                    "Make sure the emulator is running, then check the connection address, ADB path, and connection profile. For LAN addresses, confirm the device and computer are on the same network."),

            UiErrorCode.PlatformOperationFailed
                => Select(
                    language,
                    "请检查当前平台能力状态后重试；如仍失败，可复制错误详情并通过 IssueReport 上报。",
                    "Check the platform capability state and try again. If it still fails, copy the error details and submit an IssueReport."),

            _ => Select(
                language,
                "可以先检查当前输入或配置，修正后重试；如仍失败，可复制错误详情并通过 IssueReport 上报。",
                "Check the current input or configuration, fix it, and try again. If it still fails, copy the error details and submit an IssueReport."),
        };
    }

    private static string BuildErrorMessage(string? language, UiOperationResult result)
    {
        var rawMessage = result.Error?.Message ?? result.Message;
        return result.Error?.Code switch
        {
            UiErrorCode.ConfigurationProfileInvalidName
                when rawMessage.Contains("cannot be empty", StringComparison.OrdinalIgnoreCase)
                => Select(language, "配置名称不能为空。", "Profile name cannot be empty."),

            UiErrorCode.ConfigurationProfileInvalidName
                when rawMessage.Contains("control characters", StringComparison.OrdinalIgnoreCase)
                => Select(language, "配置名称不能包含控制字符。", "Profile name cannot contain control characters."),

            UiErrorCode.ConfigurationProfileInvalidName
                => Select(language, "配置名称无效。", "Profile name is invalid."),

            UiErrorCode.ConfigurationProfileAlreadyExists
                => Select(language, "配置名称已存在。", "Profile name already exists."),

            UiErrorCode.ConfigurationProfileNotFound
                => Select(language, "配置不存在。", "Profile does not exist."),

            UiErrorCode.ConfigurationProfileSaveFailed
                => Select(language, "配置保存失败。", "Failed to save configuration profile."),

            UiErrorCode.ProfileMissing
                => GetText(language, "TaskQueue.Error.ProfileMissingShort", "当前配置不存在。", "Current profile is missing."),

            UiErrorCode.TaskNameMissing
                => GetText(language, "TaskQueue.Error.TaskNameMissingShort", "任务名称不能为空。", "Task name cannot be empty."),

            UiErrorCode.EmulatorPathMissing
                => Select(language, "模拟器路径为空。", "Emulator path is missing."),

            UiErrorCode.EmulatorPathNotFound
                => Select(language, "找不到模拟器路径。", "Emulator path was not found."),

            UiErrorCode.ConnectFailed
                => Select(language, "连接模拟器失败。", "Failed to connect to the emulator."),

            _ => PlatformCapabilityTextMap.FormatErrorCode(
                UseChinese(language) ? "zh-cn" : "en-us",
                result.Error?.Code,
                result.Message),
        };
    }

    private static string BuildLocalizedDetails(string? language, UiOperationResult result, string localizedMessage)
    {
        var details = result.Error?.Details ?? string.Empty;
        if (string.Equals(localizedMessage, result.Message, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(result.Message))
        {
            return details;
        }

        var originalMessage = $"{Select(language, "原始消息", "Original message")}: {result.Message}";
        if (string.IsNullOrWhiteSpace(details))
        {
            return originalMessage;
        }

        if (details.Contains(result.Message, StringComparison.Ordinal))
        {
            return details;
        }

        return $"{details}{Environment.NewLine}{originalMessage}";
    }
}
