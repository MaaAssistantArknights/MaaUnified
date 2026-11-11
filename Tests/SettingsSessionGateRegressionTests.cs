using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using MAAUnified.App.Features.Settings;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Security;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.Tests;

public sealed class SettingsSessionGateRegressionTests
{
    private static readonly string[] EmulatorPathLocalizationKeys =
    [
        "Settings.Connect.Hint.MuMuPathNotFound",
        "Settings.Connect.Hint.MuMuExternalRendererMissing",
        "Settings.Connect.Hint.LdPlayerPathNotFound",
        "Settings.Connect.Hint.LdPlayerOpenGlMissing",
        "Settings.Connect.Status.MuMuPathAutoDetected",
        "Settings.Connect.Status.LdPlayerPathAutoDetected",
    ];

    [Fact]
    public async Task SettingsSessionGate_ShouldTrackRunningStoppingAndIdle()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        using var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        var isSessionIdleChanges = new List<bool>();
        vm.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(SettingsPageViewModel.IsSessionIdle), StringComparison.Ordinal))
            {
                isSessionIdleChanges.Add(vm.IsSessionIdle);
            }
        };

        var stateMachine = GetSessionStateMachine(fixture.Runtime.SessionService);
        Assert.True(vm.IsSessionIdle);

        Assert.True(stateMachine.TryMoveTo(SessionState.Running));
        Assert.False(vm.IsSessionIdle);

        Assert.True(stateMachine.TryMoveTo(SessionState.Stopping));
        Assert.False(vm.IsSessionIdle);

        Assert.True(stateMachine.TryMoveTo(SessionState.Idle));
        Assert.True(vm.IsSessionIdle);

        Assert.Equal([false, false, true], isSessionIdleChanges);
    }

    [Fact]
    public async Task SettingsSessionGate_ShouldRejectProfileSwitchUntilIdle()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var config = fixture.Runtime.ConfigurationService;
        var addResult = await fixture.Runtime.ConfigurationProfileFeatureService.AddProfileAsync(
            "Second",
            config.CurrentConfig.CurrentProfile);
        Assert.True(addResult.Success);

        using var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        Assert.Equal("Default", config.CurrentConfig.CurrentProfile);
        Assert.Contains("Second", vm.ConfigurationProfiles, StringComparer.Ordinal);

        var stateMachine = GetSessionStateMachine(fixture.Runtime.SessionService);
        Assert.True(stateMachine.TryMoveTo(SessionState.Running));
        vm.ConfigurationManagerSelectedProfile = "Second";
        await vm.SwitchConfigurationProfileAsync();

        Assert.Equal("Default", config.CurrentConfig.CurrentProfile);
        Assert.Equal("Default", vm.ConfigurationManagerSelectedProfile);
        Assert.Equal(
            vm.RootTexts["Settings.ConfigurationManager.Status.SwitchBlockedWhileRunning"],
            vm.ConfigurationManagerErrorMessage);
        Assert.Equal(
            vm.RootTexts["Settings.ConfigurationManager.Status.SwitchFailed"],
            vm.ConfigurationManagerStatusMessage);
        Assert.Equal(vm.ConfigurationManagerErrorMessage, vm.StatusMessage);

        Assert.True(stateMachine.TryMoveTo(SessionState.Stopping));
        vm.ConfigurationManagerSelectedProfile = "Second";
        await vm.SwitchConfigurationProfileAsync();

        Assert.Equal("Default", config.CurrentConfig.CurrentProfile);
        Assert.Equal("Default", vm.ConfigurationManagerSelectedProfile);

        Assert.True(stateMachine.TryMoveTo(SessionState.Idle));
        vm.ConfigurationManagerSelectedProfile = "Second";
        await vm.SwitchConfigurationProfileAsync();

        Assert.Equal("Second", config.CurrentConfig.CurrentProfile);
        Assert.Equal("Second", vm.ConfigurationManagerSelectedProfile);
    }

    [Fact]
    public void SettingsSessionGate_ShouldBeWiredToTheWpfGatedSurfaces()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var settingsXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "SettingsView.axaml"));
        var configurationManagerXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConfigurationManagerView.axaml"));
        var guiXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "GuiSettingsView.axaml"));
        var settingsCode = File.ReadAllText(Path.Combine(root, "App", "ViewModels", "Settings", "SettingsPageViewModel.cs"));

        AssertSectionHasSessionGate(settingsXaml, "SectionPerformance");
        AssertSectionHasSessionGate(settingsXaml, "SectionConnect");
        Assert.Matches(
            new Regex(
                "<controls:AppSelect[^>]*IsEnabled=\"\\{Binding IsSessionIdle\\}\"[^>]*ItemsSource=\"\\{Binding ConfigurationProfiles\\}\"",
                RegexOptions.Singleline),
            configurationManagerXaml);
        Assert.Matches(
            new Regex(
                "<controls:AppSelect[^>]*IsEnabled=\"\\{Binding IsSessionIdle\\}\"[^>]*ItemsSource=\"\\{Binding InverseClearModeOptions\\}\"",
                RegexOptions.Singleline),
            guiXaml);
        Assert.Contains("SessionStateChanged += OnSessionStateChanged", settingsCode, StringComparison.Ordinal);
        Assert.Contains("RejectConfigurationProfileSwitchDuringActiveSession", settingsCode, StringComparison.Ordinal);
    }

    private static void AssertSectionHasSessionGate(string xaml, string sectionName)
    {
        Assert.Matches(
            new Regex(
                $"<Border x:Name=\"{Regex.Escape(sectionName)}\"\\s+Classes=\"settings-section-anchor\"\\s+IsEnabled=\"\\{{Binding IsSessionIdle\\}}\"\\s*/>",
                RegexOptions.Singleline),
            xaml);
    }

    [Fact]
    public async Task ConfigurationManager_EmptyName_ShouldCreateOneTimestampProfile()
    {
        Assert.Equal(
            new DateTime(2026, 11, 6, 7, 8, 9).ToString("yy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture),
            SettingsPageViewModel.CreateDefaultConfigurationProfileName(new DateTime(2026, 11, 6, 7, 8, 9)));

        await using var fixture = await RuntimeFixture.CreateAsync();
        using var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        var before = fixture.Runtime.ConfigurationService.CurrentConfig.Profiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        vm.ConfigurationManagerNewProfileName = string.Empty;

        await vm.AddConfigurationProfileAsync();

        var createdProfiles = fixture.Runtime.ConfigurationService.CurrentConfig.Profiles.Keys
            .Where(profile => !before.Contains(profile))
            .ToArray();
        var created = Assert.Single(createdProfiles);
        var dateSeparator = Regex.Escape(CultureInfo.CurrentCulture.DateTimeFormat.DateSeparator);
        var timeSeparator = Regex.Escape(CultureInfo.CurrentCulture.DateTimeFormat.TimeSeparator);
        Assert.Matches($@"^\d{{2}}{dateSeparator}\d{{2}}{dateSeparator}\d{{2}} \d{{2}}{timeSeparator}\d{{2}}{timeSeparator}\d{{2}}$", created);
        Assert.Equal(string.Empty, vm.ConfigurationManagerNewProfileName);

        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConfigurationManagerView.axaml"));
        var code = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConfigurationManagerView.axaml.cs"));
        Assert.Contains("EditorKeyDown=\"OnNewProfileNameEditorKeyDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ActionClick=\"OnCreateProfileClick\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(code, "await AddConfigurationProfileOnceAsync\\(\\);").Count);
        Assert.Contains("TryBeginConfigurationProfileSubmission", code, StringComparison.Ordinal);
    }

    [Fact]
    public void EmulatorPathFeedback_ShouldUseAllSixLocalesWithoutFallbackLeakage()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var missingDirectory = Path.Combine(root, "missing");
            var emptyMuMuDirectory = Path.Combine(root, "mumu-empty");
            var emptyLdPlayerDirectory = Path.Combine(root, "ld-empty");
            Directory.CreateDirectory(emptyMuMuDirectory);
            Directory.CreateDirectory(emptyLdPlayerDirectory);

            var manager = UiLocalizationResourceManager.Shared;
            foreach (var language in UiLanguageCatalog.Ordered)
            {
                var switchBlocked = manager.Lookup(language, "Settings.ConfigurationManager.Status.SwitchBlockedWhileRunning");
                Assert.False(switchBlocked.IsFallback, $"{language}:switch-blocked fell back to {switchBlocked.FallbackSource}.");
                Assert.False(string.IsNullOrWhiteSpace(switchBlocked.Value));
                if (string.Equals(language, "pallas", StringComparison.Ordinal))
                {
                    Assert.StartsWith("[!! ", switchBlocked.Value, StringComparison.Ordinal);
                }
                AssertNotFallbackToNormalCatalog(language, switchBlocked.Value, "Settings.ConfigurationManager.Status.SwitchBlockedWhileRunning");

                foreach (var key in EmulatorPathLocalizationKeys)
                {
                    var resource = manager.Lookup(language, key);
                    Assert.False(resource.IsFallback, $"{language}:{key} fell back to {resource.FallbackSource}.");
                    Assert.Contains("{0}", resource.Value, StringComparison.Ordinal);
                    if (string.Equals(language, "pallas", StringComparison.Ordinal))
                    {
                        Assert.StartsWith("[!! ", resource.Value, StringComparison.Ordinal);
                    }
                }

                AssertMuMuMissingPath(language, missingDirectory);
                AssertMuMuMissingRenderer(language, emptyMuMuDirectory);
                AssertLdPlayerMissingPath(language, missingDirectory);
                AssertLdPlayerMissingOpenGl(language, emptyLdPlayerDirectory);

                var mumuAutoDetected = CreateEmulatorState(language, isMuMu: true, emptyMuMuDirectory)
                    .BuildMuMu12AutoDetectedPathMessage();
                var ldPlayerAutoDetected = CreateEmulatorState(language, isMuMu: false, emptyLdPlayerDirectory)
                    .BuildLdPlayerAutoDetectedPathMessage();
                Assert.Equal(
                    string.Format(CultureInfo.CurrentCulture, manager.Lookup(language, "Settings.Connect.Status.MuMuPathAutoDetected").Value, emptyMuMuDirectory),
                    mumuAutoDetected);
                Assert.Equal(
                    string.Format(CultureInfo.CurrentCulture, manager.Lookup(language, "Settings.Connect.Status.LdPlayerPathAutoDetected").Value, emptyLdPlayerDirectory),
                    ldPlayerAutoDetected);
                AssertNotFallbackToNormalCatalog(
                    language,
                    mumuAutoDetected,
                    "Settings.Connect.Status.MuMuPathAutoDetected",
                    emptyMuMuDirectory);
                AssertNotFallbackToNormalCatalog(
                    language,
                    ldPlayerAutoDetected,
                    "Settings.Connect.Status.LdPlayerPathAutoDetected",
                    emptyLdPlayerDirectory);

                if (!string.Equals(language, "en-us", StringComparison.Ordinal)
                    && !string.Equals(language, "pallas", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain("emulator path does not exist", mumuAutoDetected, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("emulator path does not exist", ldPlayerAutoDetected, StringComparison.OrdinalIgnoreCase);
                }

                if (!string.Equals(language, "zh-cn", StringComparison.Ordinal)
                    && !string.Equals(language, "pallas", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain("模拟器路径不存在", mumuAutoDetected, StringComparison.Ordinal);
                    Assert.DoesNotContain("模拟器路径不存在", ldPlayerAutoDetected, StringComparison.Ordinal);
                }
            }

            var switchMessage = manager.Lookup("zh-tw", "Settings.ConfigurationManager.Status.SwitchBlockedWhileRunning");
            Assert.False(switchMessage.IsFallback);
            Assert.DoesNotContain("任务运行", switchMessage.Value, StringComparison.Ordinal);
            Assert.DoesNotContain("配置切换", switchMessage.Value, StringComparison.Ordinal);

            var source = File.ReadAllText(Path.Combine(TestRepoLayout.GetMaaUnifiedRoot(), "App", "Features", "Settings", "ConnectSettingsView.axaml.cs"));
            Assert.DoesNotContain("MuMu path auto-detected", source, StringComparison.Ordinal);
            Assert.DoesNotContain("LDPlayer path auto-detected", source, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static void AssertMuMuMissingPath(string language, string missingDirectory)
    {
        var vm = CreateEmulatorState(language, isMuMu: true, missingDirectory);
        Assert.False(vm.ValidateMuMu12EmulatorPath(out var error));
        var key = "Settings.Connect.Hint.MuMuPathNotFound";
        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture,
                UiLocalizationResourceManager.Shared.Lookup(language, key).Value,
                missingDirectory),
            error);
        AssertNotFallbackToNormalCatalog(language, error, key, missingDirectory);
        AssertNoFallbackLanguageLeakage(language, error);
    }

    private static void AssertMuMuMissingRenderer(string language, string directory)
    {
        var vm = CreateEmulatorState(language, isMuMu: true, directory);
        Assert.False(vm.ValidateMuMu12EmulatorPath(out var error));
        var key = "Settings.Connect.Hint.MuMuExternalRendererMissing";
        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture,
                UiLocalizationResourceManager.Shared.Lookup(language, key).Value,
                directory),
            error);
        AssertNotFallbackToNormalCatalog(language, error, key, directory);
        AssertNoFallbackLanguageLeakage(language, error);
    }

    private static void AssertLdPlayerMissingPath(string language, string missingDirectory)
    {
        var vm = CreateEmulatorState(language, isMuMu: false, missingDirectory);
        Assert.False(vm.ValidateLdPlayerEmulatorPath(out var error));
        var key = "Settings.Connect.Hint.LdPlayerPathNotFound";
        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture,
                UiLocalizationResourceManager.Shared.Lookup(language, key).Value,
                missingDirectory),
            error);
        AssertNotFallbackToNormalCatalog(language, error, key, missingDirectory);
        AssertNoFallbackLanguageLeakage(language, error);
    }

    private static void AssertLdPlayerMissingOpenGl(string language, string directory)
    {
        var vm = CreateEmulatorState(language, isMuMu: false, directory);
        Assert.False(vm.ValidateLdPlayerEmulatorPath(out var error));
        var key = "Settings.Connect.Hint.LdPlayerOpenGlMissing";
        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture,
                UiLocalizationResourceManager.Shared.Lookup(language, key).Value,
                directory),
            error);
        AssertNotFallbackToNormalCatalog(language, error, key, directory);
        AssertNoFallbackLanguageLeakage(language, error);
    }

    private static void AssertNotFallbackToNormalCatalog(string language, string? value, string key, params object[] args)
    {
        Assert.False(string.IsNullOrWhiteSpace(value));
        var manager = UiLocalizationResourceManager.Shared;
        var english = string.Format(CultureInfo.CurrentCulture, manager.Lookup("en-us", key).Value, args);
        var simplifiedChinese = string.Format(CultureInfo.CurrentCulture, manager.Lookup("zh-cn", key).Value, args);
        if (!string.Equals(language, "en-us", StringComparison.Ordinal))
        {
            Assert.NotEqual(english, value);
        }

        if (!string.Equals(language, "zh-cn", StringComparison.Ordinal))
        {
            Assert.NotEqual(simplifiedChinese, value);
        }
    }

    private static void AssertNoFallbackLanguageLeakage(string language, string? message)
    {
        Assert.False(string.IsNullOrWhiteSpace(message));
        if (string.Equals(language, "pallas", StringComparison.Ordinal))
        {
            Assert.StartsWith("[!! ", message, StringComparison.Ordinal);
            return;
        }

        if (!string.Equals(language, "en-us", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("emulator path", message, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.Equals(language, "zh-cn", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("模拟器路径", message, StringComparison.Ordinal);
        }
    }

    private static ConnectionGameSharedStateViewModel CreateEmulatorState(string language, bool isMuMu, string path)
    {
        var vm = new ConnectionGameSharedStateViewModel();
        vm.SetLanguage(language);
        vm.ConnectConfig = isMuMu ? "MuMuEmulator12" : "LDPlayer";
        if (isMuMu)
        {
            vm.MuMu12EmulatorPath = path;
            vm.MuMu12ExtrasEnabled = true;
        }
        else
        {
            vm.LdPlayerEmulatorPath = path;
            vm.LdPlayerExtrasEnabled = true;
        }

        return vm;
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
