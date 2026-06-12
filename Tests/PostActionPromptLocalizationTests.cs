using System.Reflection;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.Services;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class PostActionPromptLocalizationTests
{
    [Theory]
    [InlineData("zh-cn", "连接配置存在风险", "一键修改", "强行运行", "组合存在 macOS RawByNc/POSIX 连接问题")]
    [InlineData("en-us", "Risky connection settings", "Apply Fix", "Force Run", "can trigger the macOS RawByNc/POSIX connection issue")]
    [InlineData("zh-tw", "連線設定存在風險", "一鍵修改", "強制執行", "組合存在 macOS RawByNc/POSIX 連線問題")]
    [InlineData("ja-jp", "接続設定にリスクがあります", "一括修正", "強制実行", "macOS RawByNc/POSIX 接続問題")]
    [InlineData("ko-kr", "연결 설정에 위험이 있습니다", "한 번에 수정", "강제로 실행", "macOS RawByNc/POSIX 연결 문제")]
    public async Task MacRawByNcRiskPrompt_ShouldUseLocalizedResources(
        string language,
        string expectedTitle,
        string expectedConfirm,
        string expectedCancel,
        string expectedMessagePart)
    {
        var dialog = new RecordingDialogService(DialogReturnSemantic.Cancel);
        var service = new MacRawByNcRiskConnectionPromptService(dialog);

        var decision = await service.ConfirmAsync(new MacRawByNcRiskConnectionPrompt(
            "Test.Connect",
            "127.0.0.1:5555",
            "General",
            "minitouch",
            ConfiguredAdbLiteEnabled: false,
            RecommendedTouchMode: "MaaFwAdb",
            RecommendedAdbLiteEnabled: true,
            language));

        Assert.Equal(MacRawByNcRiskConnectionDecision.ForceRun, decision);
        Assert.Equal(expectedTitle, dialog.LastWarningConfirmRequest?.Title);
        Assert.Equal(expectedConfirm, dialog.LastWarningConfirmRequest?.ConfirmText);
        Assert.Equal(expectedCancel, dialog.LastWarningConfirmRequest?.CancelText);
        Assert.Contains(expectedMessagePart, dialog.LastWarningConfirmRequest?.Message, StringComparison.Ordinal);
        Assert.Contains("minitouch", dialog.LastWarningConfirmRequest?.Message, StringComparison.Ordinal);
        Assert.Contains("true", dialog.LastWarningConfirmRequest?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MacRawByNcRiskPrompt_ShouldPreferRuntimeLanguageCoordinator()
    {
        var dialog = new RecordingDialogService(DialogReturnSemantic.Cancel);
        var languageCoordinator = new StubUiLanguageCoordinator("zh-cn");
        var service = new MacRawByNcRiskConnectionPromptService(dialog, languageCoordinator);

        var decision = await service.ConfirmAsync(new MacRawByNcRiskConnectionPrompt(
            "Test.Connect",
            "127.0.0.1:5555",
            "General",
            "minitouch",
            ConfiguredAdbLiteEnabled: false,
            RecommendedTouchMode: "MaaFwAdb",
            RecommendedAdbLiteEnabled: true,
            "en-us"));

        Assert.Equal(MacRawByNcRiskConnectionDecision.ForceRun, decision);
        Assert.Equal("连接配置存在风险", dialog.LastWarningConfirmRequest?.Title);
        Assert.Equal("一键修改", dialog.LastWarningConfirmRequest?.ConfirmText);
        Assert.Equal("强行运行", dialog.LastWarningConfirmRequest?.CancelText);
        Assert.Contains("组合存在 macOS RawByNc/POSIX 连接问题", dialog.LastWarningConfirmRequest?.Message, StringComparison.Ordinal);
        Assert.Equal("zh-cn", dialog.LastWarningConfirmRequest?.Language);
    }

    [Theory]
    [InlineData(
        "ja-jp",
        PostActionType.Shutdown,
        "シャットダウンの確認",
        "キャンセルしない場合、MAA は 15 秒後にこのコンピューターをシャットダウンします。",
        "今すぐ実行")]
    [InlineData(
        "ko-kr",
        PostActionType.Sleep,
        "절전 모드 확인",
        "취소하지 않으면 MAA가 15초 후에 이 컴퓨터를 절전 모드로 전환합니다.",
        "지금 실행")]
    public void PostActionDialogTexts_ShouldUseLocalizedResources(
        string language,
        PostActionType action,
        string expectedTitle,
        string expectedMessage,
        string expectedConfirmText)
    {
        Assert.Equal(expectedTitle, InvokeBuildTitle(action, language));
        Assert.Equal(expectedMessage, InvokeBuildMessage(action, 15, language));
        Assert.Equal(expectedConfirmText, InvokeBuildConfirmText(language));
    }

    private static string InvokeBuildTitle(PostActionType action, string language)
    {
        return (string)typeof(AvaloniaPostActionPromptService)
            .GetMethod("BuildTitle", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [action, language])!;
    }

    private static string InvokeBuildMessage(PostActionType action, int seconds, string language)
    {
        return (string)typeof(AvaloniaPostActionPromptService)
            .GetMethod("BuildMessage", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [action, seconds, language])!;
    }

    private static string InvokeBuildConfirmText(string language)
    {
        return (string)typeof(AvaloniaPostActionPromptService)
            .GetMethod("BuildConfirmText", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [language])!;
    }

    private sealed class RecordingDialogService(DialogReturnSemantic warningReturn) : IAppDialogService
    {
        public WarningConfirmDialogRequest? LastWarningConfirmRequest { get; private set; }

        public Task<DialogCompletion<AnnouncementDialogPayload>> ShowAnnouncementAsync(
            AnnouncementDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DialogCompletion<VersionUpdateDialogPayload>> ShowVersionUpdateAsync(
            VersionUpdateDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DialogCompletion<ProcessPickerDialogPayload>> ShowProcessPickerAsync(
            ProcessPickerDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DialogCompletion<EmulatorPathDialogPayload>> ShowEmulatorPathAsync(
            EmulatorPathDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DialogCompletion<ErrorDialogPayload>> ShowErrorAsync(
            ErrorDialogRequest request,
            string sourceScope,
            Func<CancellationToken, Task<UiOperationResult>>? openIssueReportAsync = null,
            Func<CancellationToken, Task<UiOperationResult>>? openSettingsAsync = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DialogCompletion<AchievementListDialogPayload>> ShowAchievementListAsync(
            AchievementListDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DialogCompletion<TextDialogPayload>> ShowTextAsync(
            TextDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DialogCompletion<WarningConfirmDialogPayload>> ShowWarningConfirmAsync(
            WarningConfirmDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
        {
            LastWarningConfirmRequest = request;
            return Task.FromResult(new DialogCompletion<WarningConfirmDialogPayload>(
                warningReturn,
                warningReturn == DialogReturnSemantic.Confirm ? new WarningConfirmDialogPayload(true) : null,
                "test"));
        }
    }

    private sealed class StubUiLanguageCoordinator(string language) : IUiLanguageCoordinator
    {
        public string CurrentLanguage { get; private set; } = UiLanguageCatalog.Normalize(language);

        public event EventHandler<UiLanguageChangedEventArgs>? LanguageChanged;

        public Task<UiOperationResult<string>> ChangeLanguageAsync(
            string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            var previous = CurrentLanguage;
            CurrentLanguage = UiLanguageCatalog.Normalize(targetLanguage);
            LanguageChanged?.Invoke(this, new UiLanguageChangedEventArgs(previous, CurrentLanguage));
            return Task.FromResult(UiOperationResult<string>.Ok(CurrentLanguage, $"Language switched to {CurrentLanguage}."));
        }
    }
}
