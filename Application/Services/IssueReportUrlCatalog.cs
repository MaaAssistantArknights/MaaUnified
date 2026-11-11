using MAAUnified.Application.Services.Localization;

namespace MAAUnified.Application.Services;

// Mirrors the frozen WPF MaaUrls entries used by the Issue Report page and the Error dialog.
public static class IssueReportUrlCatalog
{
    public const string IssueListUri = "https://github.com/MaaAssistantArknights/MaaAssistantArknights/issues";

    // WPF MaaUrls.QqGroups; the Error dialog exposes it only for zh-cn.
    public const string QqGroupsUri = "https://api.maa.plus/MaaAssistantArknights/api/qqgroup/index.html";

    private const string MaaDocsBase = "https://docs.maa.plus";

    // WPF MaaUrls.HelpUri => $"{MaaDocs}/{Language}/manual/faq.html".
    public static string GetHelpUri(string? language)
        => $"{MaaDocsBase}/{UiLanguageCatalog.Normalize(language)}/manual/faq.html";

    // WPF MaaUrls.NewIssueUri selects the Chinese template for zh-cn/zh-tw and the English
    // template otherwise, keeping the assignees/labels query parameters from the frozen build.
    public static string GetNewIssueUri(string? language)
    {
        var template = UiLanguageCatalog.Normalize(language) switch
        {
            "zh-cn" or "zh-tw" => "cn-bug-report.yaml",
            _ => "en-bug-report.yaml",
        };
        return $"{IssueListUri}/new?assignees=&labels=bug&template={template}";
    }
}
