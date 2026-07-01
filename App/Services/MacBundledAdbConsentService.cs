using MAAUnified.App.Features.Dialogs;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.App.Services;

internal static class MacBundledAdbConsentService
{
    private const string AndroidSdkLicenseUrl = "https://developer.android.com/studio/terms";
    private const string PlatformToolsUrl = "https://developer.android.com/tools/releases/platform-tools";
    private const string AdbDocsUrl = "https://developer.android.com/tools/adb";

    public static async Task<UiOperationResult> EnsureAcceptedAsync(
        MAAUnifiedRuntime runtime,
        IAppDialogService dialogService,
        bool bundledAdbInUse,
        string sourceScope,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (!MacBundledAdbPolicy.IsSupportedPlatform
            || !bundledAdbInUse
            || MacBundledAdbPolicy.IsCurrentTermsAccepted(runtime.ConfigurationService.CurrentConfig))
        {
            return UiOperationResult.Ok("macOS bundled ADB consent is already satisfied.");
        }

        var texts = GetTexts(language);
        var request = new WarningConfirmDialogRequest(
            Title: texts.Title,
            Message: texts.Message,
            ConfirmText: texts.ConfirmText,
            CancelText: texts.CancelText,
            Language: language,
            Links: CreateLinks(texts));

        var completion = await dialogService.ShowWarningConfirmAsync(request, sourceScope, cancellationToken);
        if (completion.Return != DialogReturnSemantic.Confirm || completion.Payload?.Confirmed != true)
        {
            return UiOperationResult.Cancelled(texts.CancelledMessage);
        }

        MacBundledAdbPolicy.MarkCurrentTermsAccepted(runtime.ConfigurationService.CurrentConfig, DateTimeOffset.UtcNow);
        await runtime.ConfigurationService.SaveAsync(cancellationToken);
        return UiOperationResult.Ok(texts.AcceptedMessage);
    }

    private static IReadOnlyList<DialogLinkItem> CreateLinks(ConsentTexts texts)
    {
        return
        [
            new(texts.AndroidSdkLicenseLinkText, AndroidSdkLicenseUrl),
            new(texts.PlatformToolsLinkText, PlatformToolsUrl),
            new(texts.AdbDocsLinkText, AdbDocsUrl),
        ];
    }

    internal static ConsentTexts GetTexts(string? language)
    {
        return UiLanguageCatalog.Normalize(language) switch
        {
            "zh-tw" => new ConsentTexts(
                Title: "Android SDK Platform-Tools 條款",
                Message: "MAAUnified 可使用隨附的 Android SDK Platform-Tools 中的 Android Debug Bridge（ADB）。繼續前，請先檢閱下方連結中的 Android SDK 條款與 Platform-Tools 文件。",
                ConfirmText: "同意並繼續",
                CancelText: "拒絕",
                AcceptedMessage: "已同意 Android SDK Platform-Tools 條款。",
                CancelledMessage: "尚未同意 Android SDK Platform-Tools 條款。",
                AndroidSdkLicenseLinkText: "Android SDK 授權協議",
                PlatformToolsLinkText: "SDK Platform-Tools 版本與下載頁面",
                AdbDocsLinkText: "Android Debug Bridge 文件"),
            "ja-jp" => new ConsentTexts(
                Title: "Android SDK Platform-Tools の利用規約",
                Message: "MAAUnified は、同梱された Android SDK Platform-Tools の Android Debug Bridge（ADB）を使用できます。続行する前に、下記リンクの Android SDK 利用規約と Platform-Tools ドキュメントを確認してください。",
                ConfirmText: "同意して続行",
                CancelText: "拒否",
                AcceptedMessage: "Android SDK Platform-Tools の利用規約に同意しました。",
                CancelledMessage: "Android SDK Platform-Tools の利用規約に同意していません。",
                AndroidSdkLicenseLinkText: "Android SDK ライセンス契約",
                PlatformToolsLinkText: "SDK Platform-Tools リリース / ダウンロードページ",
                AdbDocsLinkText: "Android Debug Bridge ドキュメント"),
            "ko-kr" => new ConsentTexts(
                Title: "Android SDK Platform-Tools 약관",
                Message: "MAAUnified는 함께 제공되는 Android SDK Platform-Tools의 Android Debug Bridge(ADB)를 사용할 수 있습니다. 계속하기 전에 아래 링크의 Android SDK 약관과 Platform-Tools 문서를 확인해 주세요.",
                ConfirmText: "동의하고 계속",
                CancelText: "거부",
                AcceptedMessage: "Android SDK Platform-Tools 약관에 동의했습니다.",
                CancelledMessage: "Android SDK Platform-Tools 약관에 동의하지 않았습니다.",
                AndroidSdkLicenseLinkText: "Android SDK 라이선스 계약",
                PlatformToolsLinkText: "SDK Platform-Tools 릴리스/다운로드 페이지",
                AdbDocsLinkText: "Android Debug Bridge 문서"),
            "en-us" or "pallas" => CreateEnglishTexts(),
            _ => new ConsentTexts(
                Title: "Android SDK Platform-Tools 条款",
                Message: "MAAUnified 可以使用随附的 Android SDK Platform-Tools 中的 Android Debug Bridge（ADB）。继续前，请先查看下方链接中的 Android SDK 条款和 Platform-Tools 文档。",
                ConfirmText: "同意并继续",
                CancelText: "拒绝",
                AcceptedMessage: "已同意 Android SDK Platform-Tools 条款。",
                CancelledMessage: "尚未同意 Android SDK Platform-Tools 条款。",
                AndroidSdkLicenseLinkText: "Android SDK 许可协议",
                PlatformToolsLinkText: "SDK Platform-Tools 版本与下载页面",
                AdbDocsLinkText: "Android Debug Bridge 文档"),
        };
    }

    private static ConsentTexts CreateEnglishTexts()
    {
        return new ConsentTexts(
            Title: "Android SDK Platform-Tools Terms",
            Message: "MAAUnified can use its bundled Android Debug Bridge (ADB) from Android SDK Platform-Tools. Review the linked Android SDK terms and Platform-Tools documentation before continuing.",
            ConfirmText: "Accept and continue",
            CancelText: "Reject",
            AcceptedMessage: "Android SDK Platform-Tools terms accepted.",
            CancelledMessage: "Android SDK Platform-Tools terms were not accepted.",
            AndroidSdkLicenseLinkText: "Android SDK License Agreement",
            PlatformToolsLinkText: "SDK Platform-Tools release/download page",
            AdbDocsLinkText: "Android Debug Bridge documentation");
    }

    internal sealed record ConsentTexts(
        string Title,
        string Message,
        string ConfirmText,
        string CancelText,
        string AcceptedMessage,
        string CancelledMessage,
        string AndroidSdkLicenseLinkText,
        string PlatformToolsLinkText,
        string AdbDocsLinkText);
}
