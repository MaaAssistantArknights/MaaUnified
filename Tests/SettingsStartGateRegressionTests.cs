using System.Reflection;
using MAAUnified.App.Services;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.App.Views;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Security;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.Tests;

public sealed class SettingsStartGateRegressionTests
{
    private const string IssueReportTipKey = "Settings.IssueReport.ClearImageCacheTip";
    private const string ConfigurationManagerTitleKey = "Settings.ConfigurationManager.Title";

    [Fact]
    public async Task SettingsGate_ShouldStayClosedForTheWholeTaskStartRequest()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var shared = new ConnectionGameSharedStateViewModel();
        using var settings = new SettingsPageViewModel(fixture.Runtime, shared);
        var stateMachine = GetSessionStateMachine(fixture.Runtime.SessionService);
        var gateChanges = new List<bool>();
        settings.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(SettingsPageViewModel.IsSessionIdle), StringComparison.Ordinal))
            {
                gateChanges.Add(settings.IsSessionIdle);
            }
        };

        Assert.True(settings.IsSessionIdle);
        Assert.True(stateMachine.TryMoveTo(SessionState.Connecting));
        Assert.False(settings.IsSessionIdle);
        Assert.True(stateMachine.TryMoveTo(SessionState.Idle));
        Assert.True(settings.IsSessionIdle);
        var firstActivityLease = shared.BeginSessionOperation("TaskQueue.LinkStart");
        Assert.True(shared.IsSessionOperationActive);
        Assert.False(settings.IsSessionIdle);

        Assert.True(stateMachine.TryMoveTo(SessionState.Connecting));
        Assert.False(settings.IsSessionIdle);
        Assert.True(stateMachine.TryMoveTo(SessionState.Connected));
        Assert.False(settings.IsSessionIdle);

        firstActivityLease.Dispose();
        Assert.True(settings.IsSessionIdle);

        var secondActivityLease = shared.BeginSessionOperation("TaskQueue.LinkStart");
        Assert.True(stateMachine.TryMoveTo(SessionState.Running));
        Assert.False(settings.IsSessionIdle);
        secondActivityLease.Dispose();
        Assert.False(settings.IsSessionIdle);

        Assert.True(stateMachine.TryMoveTo(SessionState.Stopping));
        Assert.False(settings.IsSessionIdle);
        Assert.True(stateMachine.TryMoveTo(SessionState.Idle));
        Assert.True(settings.IsSessionIdle);
        Assert.Contains(false, gateChanges);
        Assert.Contains(true, gateChanges);
    }

    [Fact]
    public async Task SettingsGate_ShouldCoverScreenshotTestAndRejectProfileSwitchWithRollback()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var config = fixture.Runtime.ConfigurationService;
        var addResult = await fixture.Runtime.ConfigurationProfileFeatureService.AddProfileAsync(
            "Second",
            config.CurrentConfig.CurrentProfile);
        Assert.True(addResult.Success);

        var shared = new ConnectionGameSharedStateViewModel();
        using var settings = new SettingsPageViewModel(fixture.Runtime, shared);
        await settings.InitializeAsync();
        var stateMachine = GetSessionStateMachine(fixture.Runtime.SessionService);

        shared.IsScreenshotTestActive = true;
        Assert.False(settings.IsSessionIdle);
        Assert.True(stateMachine.TryMoveTo(SessionState.Connecting));
        Assert.False(settings.IsSessionIdle);
        Assert.True(stateMachine.TryMoveTo(SessionState.Connected));
        Assert.False(settings.IsSessionIdle);

        settings.ConfigurationManagerSelectedProfile = "Second";
        await settings.SwitchConfigurationProfileAsync();
        Assert.Equal("Default", config.CurrentConfig.CurrentProfile);
        Assert.Equal("Default", settings.ConfigurationManagerSelectedProfile);
        Assert.Equal(
            settings.RootTexts["Settings.ConfigurationManager.Status.SwitchBlockedWhileRunning"],
            settings.ConfigurationManagerErrorMessage);

        shared.IsScreenshotTestActive = false;
        Assert.True(settings.IsSessionIdle);
        settings.ConfigurationManagerSelectedProfile = "Second";
        await settings.SwitchConfigurationProfileAsync();
        Assert.Equal("Second", config.CurrentConfig.CurrentProfile);
        Assert.Equal("Second", settings.ConfigurationManagerSelectedProfile);
    }

    [Fact]
    public void SettingsGate_ShouldWireTaskStartAndScreenshotBusyLifetimes()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var taskQueue = File.ReadAllText(Path.Combine(root, "App", "ViewModels", "TaskQueue", "TaskQueuePageViewModel.cs"));
        var connect = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConnectSettingsView.axaml.cs"));
        var settings = File.ReadAllText(Path.Combine(root, "App", "ViewModels", "Settings", "SettingsPageViewModel.cs"));
        var settingsView = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "SettingsView.axaml"));

        Assert.Contains("_connectionGameSharedState.TryBeginTaskStartRequest()", taskQueue, StringComparison.Ordinal);
        Assert.Contains("_connectionGameSharedState.EndTaskStartRequest()", taskQueue, StringComparison.Ordinal);
        Assert.Contains("vm.TryBeginScreenshotTestActivity()", connect, StringComparison.Ordinal);
        Assert.Contains("vm.EndScreenshotTestActivity()", connect, StringComparison.Ordinal);
        Assert.Contains("ConnectionGameSharedState.IsSessionOperationActive", settings, StringComparison.Ordinal);
        Assert.Contains("{Binding IsSessionIdle}", settingsView, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("zh-cn", "正常情况下无需手动清理。\nDEBUG 目录下保存的图片有数量限制，超出后会自动清理旧图片。\n手动清理会删除所有图片，可能导致问题排查时缺少截图依据。\n缓存文件（如地图、干员头像等）清理后需要重新生成，可能影响使用体验。")]
    [InlineData("zh-tw", "正常情況下無需手動清理。\nDEBUG 目錄下儲存的圖片有數量限制，超出後會自動清理舊圖片。\n手動清理會刪除所有圖片，可能導致問題排查時缺少截圖依據。\n快取檔案（如地圖、幹員頭像等）清理後需要重新產生，可能影響使用體驗。")]
    [InlineData("en-us", "Manual cleanup is usually unnecessary.\nImages in the DEBUG directory have a quantity limit, and old images are automatically cleaned when exceeded.\nManual cleanup deletes all images, which may result in missing screenshots for troubleshooting.\nCache files (such as maps, operator avatars, etc.) need to be regenerated after clearing, which may affect user experience.")]
    [InlineData("ja-jp", "通常、手動でクリアする必要はありません。\nDEBUGディレクトリに保存される画像には数量制限があり、超過すると古い画像が自動的にクリアされます。\n手動クリアはすべての画像を削除するため、問題調査時にスクリーンショットが不足する可能性があります。\nキャッシュファイル（マップ、オペレーターアイコンなど）はクリア後に再生成が必要で、使用体験に影響する可能性があります。")]
    [InlineData("ko-kr", "일반적으로 수동 정리가 필요하지 않습니다.\nDEBUG 디렉토리에 저장된 이미지는 수량 제한이 있으며 초과 시 오래된 이미지가 자동으로 정리됩니다.\n수동 정리는 모든 이미지를 삭제하므로 문제 해결 시 스크린샷이 부족할 수 있습니다.\n캐시 파일(지도, 오퍼레이터 아바타 등)은 정리 후 재생성이 필요하며 사용 경험에 영향을 줄 수 있습니다.")]
    public void IssueReportClearImageCacheTip_ShouldPreserveTheFullWpfWarning(string language, string expected)
    {
        var texts = new RootLocalizationTextMap("Root.Localization.Tests")
        {
            Language = language,
        };

        Assert.Equal(expected, texts[IssueReportTipKey]);
        Assert.False(UiLocalizationResourceManager.Shared.Lookup(language, IssueReportTipKey).IsFallback);
    }

    [Fact]
    public void IssueReportClearImageCacheTip_PallasShouldBePseudoLocalized()
    {
        var texts = new RootLocalizationTextMap("Root.Localization.Tests")
        {
            Language = "pallas",
        };

        var tip = texts[IssueReportTipKey];
        Assert.StartsWith("[!! ", tip, StringComparison.Ordinal);
        Assert.NotEqual(
            new RootLocalizationTextMap("Root.Localization.Tests") { Language = "zh-cn" }[IssueReportTipKey],
            tip);
        Assert.False(UiLocalizationResourceManager.Shared.Lookup("pallas", IssueReportTipKey).IsFallback);
    }

    [Theory]
    [InlineData("zh-cn", "配置管理")]
    [InlineData("zh-tw", "設定管理")]
    [InlineData("en-us", "Configuration Manager")]
    [InlineData("ja-jp", "設定マネージャー")]
    [InlineData("ko-kr", "구성 관리자")]
    public void ConfigurationManagerTitle_ShouldBeLocalized(string language, string expected)
    {
        var texts = new RootLocalizationTextMap("Root.Localization.Tests")
        {
            Language = language,
        };

        Assert.Equal(expected, texts[ConfigurationManagerTitleKey]);
        Assert.False(UiLocalizationResourceManager.Shared.Lookup(language, ConfigurationManagerTitleKey).IsFallback);
    }

    [Fact]
    public async Task CloseSaveGate_ShouldUseTheLocalizedConfigurationManagerNameOnFailureAndTimeout()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var tracker = ConfigurationSaveTracker.Instance;
        var languages = new[] { "zh-cn", "zh-tw", "en-us", "ja-jp", "ko-kr", "pallas" };

        foreach (var language in languages)
        {
            var texts = new RootLocalizationTextMap("Root.Localization.Tests")
            {
                Language = language,
            };
            var title = texts[ConfigurationManagerTitleKey];
            Assert.False(string.IsNullOrWhiteSpace(title));
            if (language == "pallas")
            {
                Assert.StartsWith("[!! ", title, StringComparison.Ordinal);
            }
            else
            {
                Assert.False(UiLocalizationResourceManager.Shared.Lookup(language, ConfigurationManagerTitleKey).IsFallback);
            }

            var failureKey = $"M28Round14.Failure.{language}.{Guid.NewGuid():N}";
            var failed = await tracker.RunTrackedAsync(
                failureKey,
                title,
                "M28Round14.CloseGate",
                fixture.Runtime.DiagnosticsService,
                static _ => Task.FromResult(false));
            Assert.False(failed);
            Assert.Contains(title, tracker.GetFailedDisplayNames(fixture.Runtime.DiagnosticsService));

            var timeoutKey = $"M28Round14.Timeout.{language}.{Guid.NewGuid():N}";
            var timedOut = await tracker.RunTrackedAsync(
                timeoutKey,
                title,
                "M28Round14.CloseGate",
                fixture.Runtime.DiagnosticsService,
                static _ => Task.FromException<bool>(new OperationCanceledException()));
            Assert.False(timedOut);
            Assert.Contains(title, tracker.GetFailedDisplayNames(fixture.Runtime.DiagnosticsService));

            var message = MainWindow.BuildConfigurationSaveFailureMessage([title], texts);
            Assert.Contains(title, message, StringComparison.Ordinal);
            if (!string.Equals(language, "zh-cn", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("配置管理", message, StringComparison.Ordinal);
                Assert.DoesNotContain("保存失败", message, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmulatorPathStatus_ShouldClearAfterCorrectionAndRefreshAfterLanguageSwitch(bool mumu)
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-m28round14", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = new ConnectionGameSharedStateViewModel
            {
                ConnectConfig = mumu ? "MuMuEmulator12" : "LDPlayer",
            };
            state.SetLanguage("zh-cn");
            if (mumu)
            {
                state.MuMu12ExtrasEnabled = true;
            }
            else
            {
                state.LdPlayerExtrasEnabled = true;
            }

            var invalidPath = Path.Combine(root, "missing");
            SetPath(state, mumu, invalidPath);
            Assert.False(string.IsNullOrWhiteSpace(state.TestLinkInfo));
            var simplifiedMessage = state.TestLinkInfo;

            state.SetLanguage("en-us");
            Assert.NotEqual(simplifiedMessage, state.TestLinkInfo);
            Assert.DoesNotContain("模拟器路径", state.TestLinkInfo, StringComparison.Ordinal);
            state.SetLanguage("pallas");
            Assert.StartsWith("[!! ", state.TestLinkInfo, StringComparison.Ordinal);

            var firstValidPath = mumu ? Path.Combine(root, "mumu-valid") : Path.Combine(root, "ld-valid");
            CreateValidPath(firstValidPath, mumu);
            SetPath(state, mumu, firstValidPath);
            Assert.True(string.IsNullOrEmpty(state.TestLinkInfo));

            state.SetLanguage("zh-cn");
            if (mumu)
            {
                state.ShowMuMu12AutoDetectedPathStatus();
            }
            else
            {
                state.ShowLdPlayerAutoDetectedPathStatus();
            }
            Assert.Contains("自动检测", state.TestLinkInfo, StringComparison.Ordinal);

            state.SetLanguage("en-us");
            Assert.Contains("auto-detected", state.TestLinkInfo, StringComparison.OrdinalIgnoreCase);

            var secondValidPath = mumu ? Path.Combine(root, "mumu-valid-2") : Path.Combine(root, "ld-valid-2");
            CreateValidPath(secondValidPath, mumu);
            SetPath(state, mumu, secondValidPath);
            Assert.True(string.IsNullOrEmpty(state.TestLinkInfo));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void SetPath(ConnectionGameSharedStateViewModel state, bool mumu, string path)
    {
        if (mumu)
        {
            state.MuMu12EmulatorPath = path;
        }
        else
        {
            state.LdPlayerEmulatorPath = path;
        }
    }

    private static void CreateValidPath(string path, bool mumu)
    {
        if (mumu)
        {
            Directory.CreateDirectory(Path.Combine(path, "shell", "sdk"));
            File.WriteAllText(Path.Combine(path, "shell", "sdk", "external_renderer_ipc.dll"), "test");
            return;
        }

        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "ldopengl64.dll"), "test");
    }

    private static SessionStateMachine GetSessionStateMachine(UnifiedSessionService sessionService)
    {
        var field = typeof(UnifiedSessionService).GetField("_stateMachine", BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<SessionStateMachine>(field?.GetValue(sessionService));
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private RuntimeFixture(string root, MAAUnifiedRuntime runtime)
        {
            Root = root;
            Runtime = runtime;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public static async Task<RuntimeFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var runtime = MAAUnifiedRuntimeFactory.Create(root, SensitiveSettingsService.CreateEphemeralForTests());
            await runtime.ConfigurationService.LoadOrBootstrapAsync();
            return new RuntimeFixture(root, runtime);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
