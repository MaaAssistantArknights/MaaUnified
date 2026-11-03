using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Compat.Constants;

namespace MAAUnified.Tests;

[Collection("MainShellSerial")]
public sealed class SettingsSystemProxyUiTests
{
    [Fact]
    public async Task NewSettings_DefaultToSystemProxy_WithFourAvailableModes()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, fixture.ConnectionState);

        Assert.Equal("system", vm.SelectedVersionUpdateProxyTypeOption?.Value);
        Assert.Equal(new[] { "system", "none", "http", "socks5" },
            vm.VersionUpdateProxyTypeOptions.Select(option => option.Value));
        Assert.False(vm.IsVersionUpdateManualProxy);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("none")]
    public async Task SelectingAutomaticMode_DisablesManualAddress_AndRestoresItWhenSwitchingBack(string mode)
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, fixture.ConnectionState);
        vm.VersionUpdateProxyType = "http";
        vm.VersionUpdateProxy = "127.0.0.1:7890";
        var changes = new List<string?>();
        vm.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        vm.SelectedVersionUpdateProxyTypeOption =
            Assert.Single(vm.VersionUpdateProxyTypeOptions, option => option.Value == mode);

        Assert.Equal(mode, vm.VersionUpdateProxyType);
        Assert.Equal(mode == "system", vm.IsVersionUpdateSystemProxy);
        Assert.Equal(mode == "none", vm.IsVersionUpdateNoProxy);
        Assert.False(vm.IsVersionUpdateManualProxy);
        Assert.Equal("127.0.0.1:7890", vm.VersionUpdateProxy);
        Assert.Contains(nameof(SettingsPageViewModel.IsVersionUpdateSystemProxy), changes);
        Assert.Contains(nameof(SettingsPageViewModel.IsVersionUpdateNoProxy), changes);
        Assert.Contains(nameof(SettingsPageViewModel.IsVersionUpdateManualProxy), changes);

        vm.SelectedVersionUpdateProxyTypeOption =
            Assert.Single(vm.VersionUpdateProxyTypeOptions, option => option.Value == "socks5");

        Assert.False(vm.IsVersionUpdateSystemProxy);
        Assert.True(vm.IsVersionUpdateManualProxy);
        Assert.Equal("127.0.0.1:7890", vm.VersionUpdateProxy);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("none")]
    public async Task ProxyMode_SaveAndReload_PreservesModeAndManualAddress(string mode)
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            new Dictionary<string, string>
            {
                [ConfigurationKeys.StartupUpdateCheck] = "False",
                [ConfigurationKeys.UpdateProxy] = "127.0.0.1:7890",
                [ConfigurationKeys.ProxyType] = "http",
            });
        var runtime = CreateSettingsRuntime(fixture);
        var vm = new SettingsPageViewModel(runtime, fixture.ConnectionState);
        await vm.InitializeAsync();
        vm.VersionUpdateProxyType = mode;

        await vm.SaveVersionUpdateProxyAsync();

        Assert.False(vm.HasVersionUpdateErrorMessage);
        Assert.Equal(mode, ReadGlobalString(fixture, ConfigurationKeys.ProxyType));
        Assert.Equal("127.0.0.1:7890", ReadGlobalString(fixture, ConfigurationKeys.UpdateProxy));

        var reloaded = new SettingsPageViewModel(runtime, fixture.ConnectionState);
        await reloaded.InitializeAsync();

        Assert.Equal(mode, reloaded.SelectedVersionUpdateProxyTypeOption?.Value);
        Assert.False(reloaded.IsVersionUpdateManualProxy);
        Assert.Equal("127.0.0.1:7890", reloaded.VersionUpdateProxy);

        reloaded.VersionUpdateProxyType = "socks5";
        await reloaded.SaveVersionUpdateProxyAsync();

        Assert.Equal("socks5", ReadGlobalString(fixture, ConfigurationKeys.ProxyType));
        Assert.True(reloaded.IsVersionUpdateManualProxy);
        Assert.Equal("127.0.0.1:7890", reloaded.VersionUpdateProxy);
    }

    [Fact]
    public async Task NoneMode_SavesDespiteInvalidRetainedManualAddress()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            new Dictionary<string, string> { [ConfigurationKeys.StartupUpdateCheck] = "False" });
        var runtime = CreateSettingsRuntime(fixture);
        var vm = new SettingsPageViewModel(runtime, fixture.ConnectionState);
        await vm.InitializeAsync();
        vm.VersionUpdateProxy = "not a valid proxy address !";
        vm.VersionUpdateProxyType = "NONE";

        await vm.SaveVersionUpdateProxyAsync();

        Assert.False(vm.HasVersionUpdateErrorMessage);
        Assert.Equal("none", ReadGlobalString(fixture, ConfigurationKeys.ProxyType));
        Assert.Equal("not a valid proxy address !", ReadGlobalString(fixture, ConfigurationKeys.UpdateProxy));
    }

    [Theory]
    [InlineData("zh-cn", "跟随系统代理")]
    [InlineData("zh-tw", "跟隨系統代理")]
    [InlineData("en-us", "Follow system proxy")]
    [InlineData("ja-jp", "システムのプロキシを使用")]
    [InlineData("ko-kr", "시스템 프록시 사용")]
    public async Task LanguageChange_LocalizesSystemProxy_WithoutChangingSelection(
        string language,
        string expectedDisplay)
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, fixture.ConnectionState);
        vm.VersionUpdateProxy = "127.0.0.1:7890";
        vm.VersionUpdateProxyType = "SYSTEM";
        vm.Language = language;

        Assert.Equal(expectedDisplay, vm.SelectedVersionUpdateProxyTypeOption?.Display);
        Assert.Equal("system", vm.SelectedVersionUpdateProxyTypeOption?.Value);
        Assert.False(vm.IsVersionUpdateManualProxy);
        Assert.Equal("127.0.0.1:7890", vm.VersionUpdateProxy);
        Assert.DoesNotContain(
            "Settings.VersionUpdate.Proxy.",
            vm.RootTexts["Settings.VersionUpdate.Proxy.System.Tip"],
            StringComparison.Ordinal);

        vm.Language = language == "en-us" ? "zh-cn" : "en-us";

        Assert.NotEqual(expectedDisplay, vm.SelectedVersionUpdateProxyTypeOption?.Display);
        Assert.Equal("system", vm.SelectedVersionUpdateProxyTypeOption?.Value);
        Assert.False(vm.IsVersionUpdateManualProxy);
    }

    [Theory]
    [InlineData("zh-cn", "无代理")]
    [InlineData("zh-tw", "無代理")]
    [InlineData("en-us", "No proxy")]
    [InlineData("ja-jp", "プロキシなし")]
    [InlineData("ko-kr", "프록시 없음")]
    public async Task LanguageChange_LocalizesNoneMode_WithoutChangingSelection(
        string language,
        string expectedDisplay)
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, fixture.ConnectionState);
        vm.VersionUpdateProxyType = "NONE";
        vm.Language = language;

        Assert.Equal(expectedDisplay, vm.SelectedVersionUpdateProxyTypeOption?.Display);
        Assert.Equal("none", vm.SelectedVersionUpdateProxyTypeOption?.Value);
        Assert.True(vm.IsVersionUpdateNoProxy);
        Assert.False(vm.IsVersionUpdateManualProxy);
        Assert.DoesNotContain(
            "Settings.VersionUpdate.Proxy.",
            vm.RootTexts["Settings.VersionUpdate.Proxy.None.Tip"],
            StringComparison.Ordinal);

        vm.Language = language == "en-us" ? "zh-cn" : "en-us";

        Assert.NotEqual(expectedDisplay, vm.SelectedVersionUpdateProxyTypeOption?.Display);
        Assert.Equal("none", vm.SelectedVersionUpdateProxyTypeOption?.Value);
        Assert.True(vm.IsVersionUpdateNoProxy);
    }

    private static string ReadGlobalString(ToolboxTestFixture fixture, string key)
    {
        return fixture.Config.CurrentConfig.GlobalValues[key]!.GetValue<string>();
    }

    private static MAAUnifiedRuntime CreateSettingsRuntime(ToolboxTestFixture fixture)
    {
        var source = fixture.Runtime;
        return new MAAUnifiedRuntime
        {
            CoreBridge = source.CoreBridge,
            ConfigurationService = source.ConfigurationService,
            ResourceWorkflowService = source.ResourceWorkflowService,
            SessionService = source.SessionService,
            Platform = source.Platform,
            LogService = source.LogService,
            DiagnosticsService = source.DiagnosticsService,
            ConnectFeatureService = source.ConnectFeatureService,
            ShellFeatureService = source.ShellFeatureService,
            TaskQueueFeatureService = source.TaskQueueFeatureService,
            CopilotFeatureService = source.CopilotFeatureService,
            ToolboxFeatureService = source.ToolboxFeatureService,
            RemoteControlFeatureService = source.RemoteControlFeatureService,
            PlatformCapabilityService = source.PlatformCapabilityService,
            OverlayFeatureService = source.OverlayFeatureService,
            NotificationProviderFeatureService = source.NotificationProviderFeatureService,
            SettingsFeatureService = source.SettingsFeatureService,
            DialogFeatureService = source.DialogFeatureService,
            PostActionFeatureService = source.PostActionFeatureService,
            UiLanguageCoordinator = source.UiLanguageCoordinator,
            VersionUpdateFeatureService = new VersionUpdateFeatureService(
                source.ConfigurationService,
                runtimeBaseDirectory: fixture.Root),
        };
    }
}
