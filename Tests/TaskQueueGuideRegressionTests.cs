using System.Text.RegularExpressions;
using MAAUnified.App;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.Views;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.Tests;

public sealed class TaskQueueGuideRegressionTests
{
    [Fact]
    public void TaskQueueGuide_ShouldBeWiredToRuntimeVisibilityAndNavigation()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var taskQueueXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml"));
        var guideXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueGuideView.axaml"));
        var guideCode = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueGuideView.axaml.cs"));

        Assert.Contains("<root:TaskQueueGuideView", taskQueueXaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsGuideVisible}\"", taskQueueXaml, StringComparison.Ordinal);
        Assert.Equal(
            3,
            Regex.Matches(taskQueueXaml, "IsVisible=\"\\{Binding IsTaskQueueContentVisible\\}\"").Count);
        Assert.Contains("Content=\"{DynamicResource PrevGuide}\"", guideXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{DynamicResource NextGuide}\"", guideXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{DynamicResource DoneGuide}\"", guideXaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsGuideNextVisible}\"", guideXaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsGuideDoneVisible}\"", guideXaml, StringComparison.Ordinal);
        Assert.Contains("OnPreviousGuideClick", guideCode, StringComparison.Ordinal);
        Assert.Contains("OnNextGuideClick", guideCode, StringComparison.Ordinal);
        Assert.Contains("OnDoneGuideClick", guideCode, StringComparison.Ordinal);
        Assert.Equal(7, Regex.Matches(guideXaml, "Classes=\"guide-step-marker\"").Count);
    }

    [Theory]
    [InlineData("zh-cn", "敏感设置存储警告", "取消", "使用明文存储", "明文")]
    [InlineData("zh-tw", "敏感設定儲存警告", "取消", "使用明文儲存", "明文")]
    [InlineData("en-us", "Sensitive storage warning", "Cancel", "Use plaintext", "Plaintext")]
    [InlineData("ja-jp", "セキュアストレージの警告", "キャンセル", "平文で保存", "平文")]
    [InlineData("ko-kr", "민감한 설정 저장소 경고", "취소", "일반 텍스트로 저장", "일반 텍스트")]
    public void PlaintextConsent_ShouldUseLocalizedSecurityCopy(
        string language,
        string expectedTitle,
        string expectedCancel,
        string expectedConfirm,
        string expectedPlaintextTerm)
    {
        var texts = SensitiveStorageComposition.AvaloniaPlaintextConsent.GetDialogTexts(language);
        var manager = UiLocalizationResourceManager.Shared;

        Assert.Equal(expectedTitle, texts.Title);
        Assert.Equal(expectedCancel, texts.CancelButton);
        Assert.Equal(expectedConfirm, texts.ConfirmButton);
        Assert.Contains(expectedPlaintextTerm, texts.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(manager.Lookup(language, "Settings.SensitiveStorage.PlaintextConsent.Title").IsFallback);
        Assert.False(manager.Lookup(language, "Settings.SensitiveStorage.PlaintextConsent.Message").IsFallback);
        Assert.False(manager.Lookup(language, "Settings.SensitiveStorage.PlaintextConsent.Cancel").IsFallback);
        Assert.False(manager.Lookup(language, "Settings.SensitiveStorage.PlaintextConsent.UsePlaintext").IsFallback);
    }

    [Fact]
    public void PlaintextConsent_PallasShouldBePseudoLocalized()
    {
        var texts = SensitiveStorageComposition.AvaloniaPlaintextConsent.GetDialogTexts("pallas");

        Assert.StartsWith("[!! ", texts.Title, StringComparison.Ordinal);
        Assert.StartsWith("[!! ", texts.Message, StringComparison.Ordinal);
        Assert.StartsWith("[!! ", texts.CancelButton, StringComparison.Ordinal);
        Assert.StartsWith("[!! ", texts.ConfirmButton, StringComparison.Ordinal);
        Assert.NotEqual("敏感设置存储警告", texts.Title);
    }

    [Fact]
    public void PlaintextConsentDialog_ShouldMakeCancelTheDefaultAndEscapeAction()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var source = File.ReadAllText(Path.Combine(root, "App", "SensitiveStorageComposition.cs"));
        var cancelBlock = Regex.Match(
            source,
            "var cancel = new Button\\s*\\{(?<body>.*?)\\};",
            RegexOptions.Singleline).Groups["body"].Value;
        var confirmBlock = Regex.Match(
            source,
            "var confirm = new Button\\s*\\{(?<body>.*?)\\};",
            RegexOptions.Singleline).Groups["body"].Value;

        Assert.Contains("IsCancel = true", cancelBlock, StringComparison.Ordinal);
        Assert.Contains("IsDefault = true", cancelBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCancel = true", confirmBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("IsDefault = true", confirmBlock, StringComparison.Ordinal);
        Assert.Contains("cancel.Click += (_, _) => dialog.Close(false);", source, StringComparison.Ordinal);
        Assert.Contains("dialog.Opened += (_, _) => cancel.Focus();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PallasLegacyDynamicResources_ShouldPseudoLocalizeEveryAllowlistedKey()
    {
        var zhResources = MAAUnified.App.App.ResolveLegacyLocalizationResources("zh-cn");
        var enResources = MAAUnified.App.App.ResolveLegacyLocalizationResources("en-us");
        var pallasResources = MAAUnified.App.App.ResolveLegacyLocalizationResources("pallas");

        Assert.Equal(MAAUnified.App.App.LegacyLocalizationResourceKeys.Length, pallasResources.Count);
        foreach (var key in MAAUnified.App.App.LegacyLocalizationResourceKeys)
        {
            Assert.True(zhResources.ContainsKey(key), $"Missing zh-cn legacy resource: {key}");
            Assert.True(enResources.ContainsKey(key), $"Missing en-us legacy resource: {key}");
            Assert.True(pallasResources.TryGetValue(key, out var pallasValue), $"Missing pallas legacy resource: {key}");
            Assert.StartsWith("[!! ", pallasValue, StringComparison.Ordinal);
            Assert.NotEqual(zhResources[key], pallasValue);
            Assert.Equal(ExtractFormatTokens(enResources[key]), ExtractFormatTokens(pallasValue));
        }

        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var externalNotifications = File.ReadAllText(
            Path.Combine(root, "App", "Features", "Settings", "ExternalNotificationSettingsView.axaml"));
        var guiSettings = File.ReadAllText(
            Path.Combine(root, "App", "Features", "Settings", "GuiSettingsView.axaml"));
        Assert.Contains("{DynamicResource ExternalNotificationBarkSendKey}", externalNotifications, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource SystemNotificationInfo}", guiSettings, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "zh-cn",
        "自动战斗、小工具、干员识别和仓库识别保存失败")]
    [InlineData(
        "zh-tw",
        "自動戰鬥、小工具、幹員辨識與倉庫辨識儲存失敗")]
    [InlineData(
        "en-us",
        "Failed to save Copilot, Toolbox, Operator and Depot.")]
    [InlineData(
        "ja-jp",
        "自動戦闘、ツールボックス、オペレーターと倉庫アイテム認識の保存に失敗しました。")]
    [InlineData(
        "ko-kr",
        "자동지휘, 툴박스, 오퍼레이터 인식 및 창고 인식 저장에 실패했습니다.")]
    public void CloseSaveFailureMessage_ShouldBeFullyLocalized(
        string language,
        string expectedMessage)
    {
        var texts = new RootLocalizationTextMap("Root.Localization.Tests")
        {
            Language = language,
        };
        var names = new[]
        {
            texts["Copilot.Title"],
            texts["Toolbox.Title"],
            texts["Toolbox.OperBox.Title"],
            texts["Toolbox.Depot.Title"],
        };

        Assert.Equal(expectedMessage, MainWindow.BuildConfigurationSaveFailureMessage(names, texts));
    }

    [Fact]
    public void CloseSaveFailureMessage_PallasShouldNotFallBackToChinese()
    {
        var texts = new RootLocalizationTextMap("Root.Localization.Tests")
        {
            Language = "pallas",
        };
        var message = MainWindow.BuildConfigurationSaveFailureMessage(
            new[]
            {
                texts["Copilot.Title"],
                texts["Toolbox.Title"],
                texts["Toolbox.OperBox.Title"],
                texts["Toolbox.Depot.Title"],
            },
            texts);

        Assert.StartsWith("[!! ", message, StringComparison.Ordinal);
        Assert.DoesNotContain("保存失败", message, StringComparison.Ordinal);
        Assert.DoesNotContain("自动战斗", message, StringComparison.Ordinal);
        Assert.False(UiLocalizationResourceManager.Shared.Lookup("pallas", "Copilot.Title").IsFallback);
        Assert.False(UiLocalizationResourceManager.Shared.Lookup("pallas", "Toolbox.Title").IsFallback);
        Assert.False(UiLocalizationResourceManager.Shared.Lookup("pallas", "Toolbox.OperBox.Title").IsFallback);
        Assert.False(UiLocalizationResourceManager.Shared.Lookup("pallas", "Toolbox.Depot.Title").IsFallback);
    }

    [Theory]
    [InlineData("en-us")]
    [InlineData("ko-kr")]
    [InlineData("pallas")]
    public void CloseSaveFailureMessage_NonChineseLocalesShouldNotExposeChineseFallback(string language)
    {
        var texts = new RootLocalizationTextMap("Root.Localization.Tests")
        {
            Language = language,
        };
        var message = MainWindow.BuildConfigurationSaveFailureMessage(
            new[]
            {
                texts["Copilot.Title"],
                texts["Toolbox.Title"],
                texts["Toolbox.OperBox.Title"],
                texts["Toolbox.Depot.Title"],
            },
            texts);

        Assert.DoesNotContain("保存失败", message, StringComparison.Ordinal);
        Assert.DoesNotContain("自动战斗", message, StringComparison.Ordinal);
        Assert.DoesNotContain("干员识别", message, StringComparison.Ordinal);
        Assert.DoesNotContain("仓库识别", message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> ExtractFormatTokens(string value)
    {
        return Regex.Matches(value, "\\{\\d+(?::[^}]*)?\\}")
            .Select(match => match.Value)
            .ToArray();
    }
}
