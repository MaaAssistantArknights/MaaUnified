using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia.Input;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class SettingsHotkeyFeedbackFeatureTests
{
    [Fact]
    public async Task RegisterHotkeysAsync_ShowGuiFails_LinkStartStillRegisters_AndOnlySuccessfulBindingPersists()
    {
        var hotkeyService = new ScriptedHotkeyService();
        hotkeyService.EnqueueRegisterResult(
            "ShowGui",
            PlatformOperation.Failed("test", "conflict", PlatformErrorCodes.HotkeyConflict, "hotkey.register"));

        await using var fixture = await RuntimeFixture.CreateAsync(hotkeyService: hotkeyService);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var before = ReadGlobalString(fixture.Config, ConfigurationKeys.HotKeys);
        vm.HotkeyShowGui = "Ctrl+Alt+S";
        vm.HotkeyLinkStart = "Ctrl+Alt+L";

        await vm.RegisterHotkeysAsync();

        Assert.Equal(2, hotkeyService.RegisterCalls.Count);
        Assert.Equal("ShowGui", hotkeyService.RegisterCalls[0].Name);
        Assert.Equal("LinkStart", hotkeyService.RegisterCalls[1].Name);

        var hotkeys = ParseHotkeys(ReadGlobalString(fixture.Config, ConfigurationKeys.HotKeys));
        Assert.Equal("Ctrl+Alt+L", hotkeys["LinkStart"]);
        Assert.Equal(HotkeyConfigurationCodec.Parse(before).ShowGui, hotkeys["ShowGui"]);
        Assert.Contains(
            PlatformCapabilityTextMap.FormatErrorCode("zh-cn", PlatformErrorCodes.HotkeyConflict, "ignored"),
            vm.HotkeyErrorMessage,
            StringComparison.Ordinal);
        Assert.Contains("1/2", vm.HotkeyStatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterHotkeysAsync_ShowGuiSuccess_LinkStartFails_KeepsShowGuiAndPersistsOnlyShowGui()
    {
        var hotkeyService = new ScriptedHotkeyService();
        hotkeyService.EnqueueRegisterResult(
            "ShowGui",
            PlatformOperation.NativeSuccess("test", "show registered", "hotkey.register"));
        hotkeyService.EnqueueRegisterResult(
            "LinkStart",
            PlatformOperation.Failed("test", "invalid", PlatformErrorCodes.HotkeyInvalidGesture, "hotkey.register"));

        await using var fixture = await RuntimeFixture.CreateAsync(hotkeyService: hotkeyService);
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.HotKeys] = JsonValue.Create("ShowGui=Ctrl+1;LinkStart=Ctrl+2");
        fixture.Config.CurrentConfig.GlobalValues[HotkeyConfigurationCodec.MacDefaultsVersionKey] = JsonValue.Create("1");
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.HotkeyShowGui = "Ctrl+Shift+Alt+M";
        vm.HotkeyLinkStart = "Ctrl+Shift+Alt+L";

        await vm.RegisterHotkeysAsync();

        Assert.Equal(2, hotkeyService.RegisterCalls.Count);
        Assert.Equal("ShowGui", hotkeyService.RegisterCalls[0].Name);
        Assert.Equal("LinkStart", hotkeyService.RegisterCalls[1].Name);

        var hotkeys = ParseHotkeys(ReadGlobalString(fixture.Config, ConfigurationKeys.HotKeys));
        Assert.Equal("Ctrl+Shift+Alt+M", hotkeys["ShowGui"]);
        Assert.Equal("Ctrl+2", hotkeys["LinkStart"]);
        Assert.Contains(
            PlatformCapabilityTextMap.FormatErrorCode("zh-cn", PlatformErrorCodes.HotkeyInvalidGesture, "ignored"),
            vm.HotkeyErrorMessage,
            StringComparison.Ordinal);
        Assert.Contains("1/2", vm.HotkeyStatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterHotkeysAsync_MapsHotkeyConflictMessage()
    {
        await ValidateMappedHotkeyErrorAsync(PlatformErrorCodes.HotkeyConflict);
    }

    [Fact]
    public async Task RegisterHotkeysAsync_MapsHotkeyInvalidGestureMessage()
    {
        await ValidateMappedHotkeyErrorAsync(PlatformErrorCodes.HotkeyInvalidGesture);
    }

    [Fact]
    public async Task RegisterHotkeysAsync_MapsHotkeyNotFoundMessage()
    {
        await ValidateMappedHotkeyErrorAsync(PlatformErrorCodes.HotkeyNotFound);
    }

    [Fact]
    public async Task RegisterHotkeysAsync_FallbackPlatform_ShowsVisibleWarningAndLogsFallbackScope()
    {
        var fallbackCapability = new PlatformCapabilityStatus(
            Supported: false,
            Message: "Global hotkeys are unavailable, fallback to window-scoped hotkeys.",
            Provider: "window-scoped",
            HasFallback: true,
            FallbackMode: "window-scoped");
        var hotkeyService = new ScriptedHotkeyService(fallbackCapability);
        hotkeyService.EnqueueRegisterResult(
            "ShowGui",
            PlatformOperation.FallbackSuccess(
                "window-scoped",
                "Window-scoped hotkey registered: ShowGui",
                "hotkey.register",
                PlatformErrorCodes.HotkeyFallback));
        hotkeyService.EnqueueRegisterResult(
            "LinkStart",
            PlatformOperation.FallbackSuccess(
                "window-scoped",
                "Window-scoped hotkey registered: LinkStart",
                "hotkey.register",
                PlatformErrorCodes.HotkeyFallback));

        await using var fixture = await RuntimeFixture.CreateAsync(hotkeyService: hotkeyService);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.RegisterHotkeysAsync();

        Assert.True(vm.HasHotkeyWarningMessage);
        Assert.Contains(
            PlatformCapabilityTextMap.FormatErrorCode("zh-cn", PlatformErrorCodes.HotkeyFallback, fallbackCapability.Message),
            vm.HotkeyWarningMessage,
            StringComparison.Ordinal);
        var eventLog = await File.ReadAllTextAsync(fixture.Diagnostics.EventLogPath);
        Assert.Contains("Settings.Hotkey.Fallback", eventLog, StringComparison.Ordinal);
        var platformLog = await File.ReadAllTextAsync(fixture.Diagnostics.PlatformEventLogPath);
        Assert.Contains("hotkey.register", platformLog, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("window-scoped", platformLog, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadHotkeysFromConfig_RoundTripStable()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var firstHotkeyService = new ScriptedHotkeyService();
            await using (var first = await RuntimeFixture.CreateAsync(root, cleanupRoot: false, hotkeyService: firstHotkeyService))
            {
                first.Config.CurrentConfig.GlobalValues[ConfigurationKeys.HotKeys] = JsonValue.Create("ShowGui=Ctrl+1;LinkStart=Ctrl+2");
                first.Config.CurrentConfig.GlobalValues[HotkeyConfigurationCodec.MacDefaultsVersionKey] = JsonValue.Create("1");
                await first.Config.SaveAsync();

                var vm = new SettingsPageViewModel(first.Runtime, new ConnectionGameSharedStateViewModel());
                await vm.InitializeAsync();
                Assert.Equal("Ctrl+1", vm.HotkeyShowGui);
                Assert.Equal("Ctrl+2", vm.HotkeyLinkStart);

                vm.HotkeyShowGui = "Ctrl+Shift+Alt+M";
                vm.HotkeyLinkStart = "Ctrl+Shift+Alt+L";
                await vm.RegisterHotkeysAsync();

                var serialized = ReadGlobalString(first.Config, ConfigurationKeys.HotKeys);
                Assert.Equal("ShowGui=Ctrl+Shift+Alt+M;LinkStart=Ctrl+Shift+Alt+L", serialized);
            }

            await using var second = await RuntimeFixture.CreateAsync(root, cleanupRoot: false, hotkeyService: new ScriptedHotkeyService());
            var reloadedVm = new SettingsPageViewModel(second.Runtime, new ConnectionGameSharedStateViewModel());
            await reloadedVm.InitializeAsync();
            Assert.Equal("Ctrl+Shift+Alt+M", reloadedVm.HotkeyShowGui);
            Assert.Equal("Ctrl+Shift+Alt+L", reloadedVm.HotkeyLinkStart);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temporary test directories
            }
        }
    }

    [Fact]
    public async Task LoadHotkeysFromLegacyWpfJson_ShouldNormalizeToSemicolonFormat()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.HotKeys] = JsonValue.Create(
            "{\"ShowGui\":{\"Key\":50,\"Modifiers\":7},\"LinkStart\":{\"Key\":61,\"Modifiers\":7}}");
        fixture.Config.CurrentConfig.GlobalValues[HotkeyConfigurationCodec.MacDefaultsVersionKey] = JsonValue.Create("1");
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.Equal("Ctrl+Shift+Alt+G", vm.HotkeyShowGui);
        Assert.Equal("Ctrl+Shift+Alt+R", vm.HotkeyLinkStart);

        await vm.RegisterHotkeysAsync();

        Assert.Equal(
            "ShowGui=Ctrl+Shift+Alt+G;LinkStart=Ctrl+Shift+Alt+R",
            ReadGlobalString(fixture.Config, ConfigurationKeys.HotKeys));
    }

    [Fact]
    public void LoadHotkeysFromLegacyWpfJson_EnumStringsNumericKeyStringsAndNumericActions_AreNormalized()
    {
        var snapshot = HotkeyConfigurationCodec.Parse(
            "{\"0\":{\"Key\":\"50\",\"Modifiers\":\"Control, Shift, Alt\"},\"1\":{\"Key\":\"Decimal\",\"Modifiers\":\"Windows, Control\"}}");

        Assert.Equal("Ctrl+Shift+Alt+G", snapshot.ShowGui);
        Assert.Equal("Ctrl+Meta+Decimal", snapshot.LinkStart);
        Assert.Empty(snapshot.Warnings);

        var oemSnapshot = HotkeyConfigurationCodec.Parse(
            "{\"ShowGui\":{\"Key\":\"OemComma\",\"Modifiers\":\"Control, Shift\"},\"LinkStart\":null}");

        Assert.Equal("Ctrl+Shift+OemComma", oemSnapshot.ShowGui);
        Assert.Equal(string.Empty, oemSnapshot.LinkStart);
        Assert.Empty(oemSnapshot.Warnings);

        var pauseSnapshot = HotkeyConfigurationCodec.Parse(
            "{\"ShowGui\":{\"Key\":7,\"Modifiers\":2},\"LinkStart\":{\"Key\":\"Pause\",\"Modifiers\":\"Control\"}}");

        Assert.Equal("Ctrl+Pause", pauseSnapshot.ShowGui);
        Assert.Equal("Ctrl+Pause", pauseSnapshot.LinkStart);
        Assert.Empty(pauseSnapshot.Warnings);
    }

    [Theory]
    [InlineData(Key.Escape)]
    [InlineData(Key.Back)]
    [InlineData(Key.Delete)]
    [InlineData(Key.A)]
    [InlineData(Key.Apps)]
    public void HotkeyCapture_WithoutModifiers_ClearsLikeWpf(Key key)
    {
        // WPF parity (HotKeyEditorUserControl.HotKeyTextBox_PreviewKeyDown): the
        // modifiers == None -> HotKey = null clear runs before the WPF exclusion
        // list, so even the excluded Apps (Menu) key clears the binding when
        // pressed without modifiers.
        var result = HotkeyGestureCodec.Capture(key, KeyModifiers.None);

        Assert.Equal(HotkeyCaptureResultKind.Cleared, result.Kind);
    }

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightAlt)]
    [InlineData(Key.LWin)]
    public void HotkeyCapture_ModifierKeys_StayPending(Key key)
    {
        var result = HotkeyGestureCodec.Capture(key, KeyModifiers.Control);

        Assert.Equal(HotkeyCaptureResultKind.Pending, result.Kind);
    }

    [Fact]
    public void HotkeyCapture_AppsWithModifier_KeepsBindingUnchangedLikeWpf()
    {
        // WPF parity: with a modifier held, WPF hits the exclusion list and
        // silently ignores Apps, leaving the binding unchanged. The unified
        // capture reports the unmappable chord instead of binding it; the
        // binding outcome matches WPF.
        var capture = HotkeyGestureCodec.Capture(Key.Apps, KeyModifiers.Control);

        Assert.Equal(HotkeyCaptureResultKind.Rejected, capture.Kind);
        Assert.Null(capture.Gesture);
    }

    [Fact]
    public void HotkeyCapture_AndWindowsReservation_SupportWpfPunctuationKeys()
    {
        var capture = HotkeyGestureCodec.Capture(Key.OemComma, KeyModifiers.Control);

        Assert.Equal(HotkeyCaptureResultKind.Captured, capture.Kind);
        Assert.Equal("OemComma", capture.Gesture!.Key);
        Assert.True(HotkeyGestureCodec.TryGetWindowsVirtualKey("OemComma", out var comma));
        Assert.Equal(0xBCu, comma);
        Assert.True(HotkeyGestureCodec.TryGetWindowsVirtualKey("Decimal", out var decimalKey));
        Assert.Equal(0x6Eu, decimalKey);
    }

    [Fact]
    public void HotkeyCapture_AndWindowsReservation_SupportWpfPauseKey()
    {
        var capture = HotkeyGestureCodec.Capture(Key.Pause, KeyModifiers.Control);

        Assert.Equal(HotkeyCaptureResultKind.Captured, capture.Kind);
        Assert.Equal("Pause", capture.Gesture!.Key);
        Assert.True(HotkeyGestureCodec.TryNormalize("Ctrl+Pause", out var normalized));
        Assert.Equal("Ctrl+Pause", normalized);
        Assert.True(HotkeyGestureCodec.TryGetWindowsVirtualKey("Pause", out var pause));
        Assert.Equal(0x13u, pause);
    }

    // Numeric legacy keys are System.Windows.Input.Key enum values from the
    // frozen net10.0-windows WPF runtime: browser keys are 122-128, volume keys
    // 129-131, media keys 132-135, and launch keys 136-139. Avalonia.Key 11.3.14
    // uses the identical numbering (verified against both assemblies).
    [Theory]
    [InlineData(1, "Ctrl+Cancel")]
    [InlineData(8, "Ctrl+CapsLock")]
    [InlineData(30, "Ctrl+PrintScreen")]
    [InlineData(33, "Ctrl+Help")]
    [InlineData(73, "Ctrl+Sleep")]
    [InlineData(114, "Ctrl+NumLock")]
    [InlineData(115, "Ctrl+ScrollLock")]
    [InlineData(122, "Ctrl+BrowserBack")]
    [InlineData(128, "Ctrl+BrowserHome")]
    [InlineData(129, "Ctrl+VolumeMute")]
    [InlineData(132, "Ctrl+MediaNextTrack")]
    [InlineData(135, "Ctrl+MediaPlayPause")]
    [InlineData(136, "Ctrl+LaunchMail")]
    [InlineData(137, "Ctrl+SelectMedia")]
    [InlineData(138, "Ctrl+LaunchApplication1")]
    [InlineData(139, "Ctrl+LaunchApplication2")]
    public void LoadHotkeysFromLegacyWpfJson_ExtendedWpfNumericKeys_AreNormalized(int key, string expected)
    {
        var snapshot = HotkeyConfigurationCodec.Parse(
            $"{{\"ShowGui\":{{\"Key\":{key},\"Modifiers\":2}},\"LinkStart\":null}}");

        Assert.Equal(expected, snapshot.ShowGui);
        Assert.Equal(string.Empty, snapshot.LinkStart);
        Assert.Empty(snapshot.Warnings);
    }

    [Theory]
    [InlineData("Capital", "Ctrl+CapsLock")]
    [InlineData("Snapshot", "Ctrl+PrintScreen")]
    [InlineData("Scroll", "Ctrl+ScrollLock")]
    [InlineData("BrowserBack", "Ctrl+BrowserBack")]
    [InlineData("MediaPlayPause", "Ctrl+MediaPlayPause")]
    [InlineData("LaunchApplication2", "Ctrl+LaunchApplication2")]
    public void LoadHotkeysFromLegacyWpfJson_ExtendedWpfKeyNames_AreNormalized(string keyName, string expected)
    {
        var snapshot = HotkeyConfigurationCodec.Parse(
            $"{{\"ShowGui\":{{\"Key\":\"{keyName}\",\"Modifiers\":\"Control\"}},\"LinkStart\":null}}");

        Assert.Equal(expected, snapshot.ShowGui);
        Assert.Empty(snapshot.Warnings);
    }

    [Theory]
    [InlineData(140, "Oem1")]
    [InlineData(141, "OemPlus")]
    [InlineData(142, "OemComma")]
    [InlineData(143, "OemMinus")]
    [InlineData(144, "OemPeriod")]
    [InlineData(145, "OemQuestion")]
    [InlineData(146, "OemTilde")]
    [InlineData(149, "OemOpenBrackets")]
    [InlineData(150, "OemPipe")]
    [InlineData(151, "Oem6")]
    [InlineData(152, "OemQuotes")]
    [InlineData(154, "Oem102")]
    public void LoadHotkeysFromLegacyWpfJson_OemNumericKeys_UseWpfEnumOffsets(int key, string expectedToken)
    {
        var snapshot = HotkeyConfigurationCodec.Parse(
            $"{{\"ShowGui\":{{\"Key\":{key},\"Modifiers\":2}},\"LinkStart\":null}}");

        Assert.Equal($"Ctrl+{expectedToken}", snapshot.ShowGui);
        Assert.Empty(snapshot.Warnings);
    }

    [Theory]
    [InlineData(27)] // Select: WPF accepts it, but no SharpHook trigger code exists.
    [InlineData(28)] // Print: no SharpHook trigger code.
    [InlineData(153)] // Oem8: no SharpHook trigger code.
    [InlineData(147)] // AbntC1: no portable trigger token.
    [InlineData(148)] // AbntC2: no portable trigger token.
    [InlineData(14)] // ImeConvert: IME key without a portable trigger code.
    public void LoadHotkeysFromLegacyWpfJson_KeysWithoutTriggerSupport_MigrateToUnboundWithWarning(int key)
    {
        // WPF registers these keys successfully; without a SharpHook trigger code the
        // unified app cannot fire them, so the explicitly listed action migrates to
        // unbound with a warning rather than being replaced by the default chord.
        var snapshot = HotkeyConfigurationCodec.Parse(
            $"{{\"ShowGui\":{{\"Key\":{key},\"Modifiers\":2}},\"LinkStart\":null}}");

        Assert.Equal(string.Empty, snapshot.ShowGui);
        Assert.NotEmpty(snapshot.Warnings);
    }

    [Fact]
    public void LoadHotkeysFromLegacyWpfJson_ExplicitEntryWithoutModifiers_MigratesToUnboundWithWarning()
    {
        // WPF would register the bare F1 (RegisterHotKey accepts fsModifiers=0), but
        // the portable gesture format requires a modifier, so the explicitly listed
        // action ends unbound instead of being replaced by the default chord.
        var snapshot = HotkeyConfigurationCodec.Parse(
            "{\"ShowGui\":{\"Key\":90,\"Modifiers\":0},\"LinkStart\":{\"Key\":55,\"Modifiers\":7}}");

        Assert.Equal(string.Empty, snapshot.ShowGui);
        Assert.Equal("Ctrl+Shift+Alt+L", snapshot.LinkStart);
        Assert.Contains(snapshot.Warnings, warning => warning.Contains("ShowGui", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadHotkeysFromLegacyWpfJson_MissingAction_StaysUnbound()
    {
        // WPF parity (MaaHotKeyManager.GetPersistentHotKeys): a persisted legacy
        // payload that omits an action simply never registers it; only a completely
        // absent config value yields the initial defaults.
        var snapshot = HotkeyConfigurationCodec.Parse(
            "{\"LinkStart\":{\"Key\":55,\"Modifiers\":7}}");

        Assert.Equal(string.Empty, snapshot.ShowGui);
        Assert.Equal("Ctrl+Shift+Alt+L", snapshot.LinkStart);

        var emptyObjectSnapshot = HotkeyConfigurationCodec.Parse("{}");
        Assert.Equal(string.Empty, emptyObjectSnapshot.ShowGui);
        Assert.Equal(string.Empty, emptyObjectSnapshot.LinkStart);

        var unsetSnapshot = HotkeyConfigurationCodec.Parse(null);
        Assert.Equal(HotkeyConfigurationCodec.DefaultHotkeyShowGui, unsetSnapshot.ShowGui);
        Assert.Equal(HotkeyConfigurationCodec.DefaultHotkeyLinkStart, unsetSnapshot.LinkStart);
    }

    [Fact]
    public void LoadHotkeysFromLegacyWpfJson_MalformedJson_FallsBackToDefaults()
    {
        // A payload that cannot be parsed at all is treated like an unreadable
        // config value: the initial defaults still apply.
        var snapshot = HotkeyConfigurationCodec.Parse("{not-json");

        Assert.Equal(HotkeyConfigurationCodec.DefaultHotkeyShowGui, snapshot.ShowGui);
        Assert.Equal(HotkeyConfigurationCodec.DefaultHotkeyLinkStart, snapshot.LinkStart);
        Assert.NotEmpty(snapshot.Warnings);
    }

    [Theory]
    [InlineData(Key.Cancel, "Cancel", 0x03u)]
    [InlineData(Key.CapsLock, "CapsLock", 0x14u)]
    [InlineData(Key.PrintScreen, "PrintScreen", 0x2Cu)]
    [InlineData(Key.NumLock, "NumLock", 0x90u)]
    [InlineData(Key.Scroll, "ScrollLock", 0x91u)]
    [InlineData(Key.Sleep, "Sleep", 0x5Fu)]
    [InlineData(Key.Help, "Help", 0x2Fu)]
    [InlineData(Key.BrowserBack, "BrowserBack", 0xA6u)]
    [InlineData(Key.BrowserHome, "BrowserHome", 0xACu)]
    [InlineData(Key.VolumeMute, "VolumeMute", 0xADu)]
    [InlineData(Key.MediaNextTrack, "MediaNextTrack", 0xB0u)]
    [InlineData(Key.MediaPlayPause, "MediaPlayPause", 0xB3u)]
    [InlineData(Key.LaunchMail, "LaunchMail", 0xB4u)]
    [InlineData(Key.SelectMedia, "SelectMedia", 0xB5u)]
    [InlineData(Key.LaunchApplication1, "LaunchApplication1", 0xB6u)]
    public void HotkeyCapture_AndWindowsReservation_SupportExtendedWpfKeys(
        Key key,
        string expectedToken,
        uint expectedVirtualKey)
    {
        var capture = HotkeyGestureCodec.Capture(key, KeyModifiers.Control);

        Assert.Equal(HotkeyCaptureResultKind.Captured, capture.Kind);
        Assert.Equal(expectedToken, capture.Gesture!.Key);
        Assert.True(HotkeyGestureCodec.TryGetWindowsVirtualKey(capture.Gesture.Key, out var virtualKey));
        Assert.Equal(expectedVirtualKey, virtualKey);
    }

    [Theory]
    [InlineData(Key.Oem1, "Oem1", 0xBAu)]
    [InlineData(Key.Oem6, "Oem6", 0xDDu)]
    [InlineData(Key.Oem102, "Oem102", 0xE2u)]
    public void HotkeyCapture_OemKeysWithDuplicatedAvaloniaNames_CaptureByPhysicalKey(
        Key key,
        string expectedToken,
        uint expectedVirtualKey)
    {
        // Avalonia.Key declares duplicate names for these values (OemSemicolon,
        // OemCloseBrackets, OemBackslash); capture must still resolve the
        // canonical WPF-compatible token.
        var capture = HotkeyGestureCodec.Capture(key, KeyModifiers.Control);

        Assert.Equal(HotkeyCaptureResultKind.Captured, capture.Kind);
        Assert.Equal(expectedToken, capture.Gesture!.Key);
        Assert.True(HotkeyGestureCodec.TryGetWindowsVirtualKey(capture.Gesture.Key, out var virtualKey));
        Assert.Equal(expectedVirtualKey, virtualKey);
    }

    [Fact]
    public void HotkeyCapture_AltModifiedLetter_CapturesRealKey()
    {
        // Avalonia reports Alt+M as Key.M with the Alt modifier: its Win32
        // KeyInterop table has no Key.System entry, so the WPF Key.System
        // masking never occurs here and the physical key is captured directly.
        var capture = HotkeyGestureCodec.Capture(Key.M, KeyModifiers.Alt);

        Assert.Equal(HotkeyCaptureResultKind.Captured, capture.Kind);
        Assert.Equal("M", capture.Gesture!.Key);
        Assert.True(capture.Gesture.Alt);
        Assert.Equal("Alt+M", capture.Gesture.ToStorageString());
    }

    [Fact]
    public void HotkeyCapture_SystemMaskingKey_IsRejectedInsteadOfMisbound()
    {
        // Key.System is a WPF masking key that no Avalonia 11.x backend produces
        // from real input. If it ever arrives, reject it instead of binding an
        // unmappable token.
        var capture = HotkeyGestureCodec.Capture(Key.System, KeyModifiers.Alt);

        Assert.Equal(HotkeyCaptureResultKind.Rejected, capture.Kind);
    }

    [Theory]
    [InlineData(Key.NumPad0, "NumPad0", 0x60u)]
    [InlineData(Key.NumPad1, "NumPad1", 0x61u)]
    [InlineData(Key.NumPad9, "NumPad9", 0x69u)]
    [InlineData(Key.Add, "Add", 0x6Bu)]
    public void HotkeyCapture_AndWindowsReservation_PreserveKeypadKeys(
        Key key,
        string expectedToken,
        uint expectedVirtualKey)
    {
        var capture = HotkeyGestureCodec.Capture(key, KeyModifiers.Control);

        Assert.Equal(HotkeyCaptureResultKind.Captured, capture.Kind);
        Assert.Equal(expectedToken, capture.Gesture!.Key);
        Assert.True(HotkeyGestureCodec.TryGetWindowsVirtualKey(capture.Gesture.Key, out var virtualKey));
        Assert.Equal(expectedVirtualKey, virtualKey);
    }

    [Fact]
    public async Task ImportLegacyConfigurationsAsync_ReplacesRuntimeHotkeys()
    {
        var hotkeyService = new ScriptedHotkeyService();
        await using var fixture = await RuntimeFixture.CreateAsync(hotkeyService: hotkeyService);
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.HotKeys] = JsonValue.Create(
            "ShowGui=Ctrl+1;LinkStart=Ctrl+2");
        fixture.Config.CurrentConfig.GlobalValues[HotkeyConfigurationCodec.MacDefaultsVersionKey] = JsonValue.Create("1");
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        await vm.RegisterHotkeysAsync();

        var legacyGuiPath = Path.Combine(fixture.Root, "config", "gui.json");
        await File.WriteAllTextAsync(
            legacyGuiPath,
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {}
              },
              "Global": {
                "HotKeys": "ShowGui=Ctrl+Alt+N;LinkStart="
              }
            }
            """);

        var report = await vm.ImportLegacyConfigurationsAsync(
            new LegacyImportRequest(
                LegacyConfigSnapshot.FromPaths(null, legacyGuiPath),
                ImportSource.GuiOnly,
                ManualImport: true,
                AllowPartialImport: true));

        Assert.True(report.AppliedConfig);
        Assert.Equal("Ctrl+Alt+N", vm.HotkeyShowGui);
        Assert.Equal(string.Empty, vm.HotkeyLinkStart);
        Assert.Collection(
            hotkeyService.RegisterCalls,
            call => Assert.Equal(("ShowGui", "Ctrl+1"), call),
            call => Assert.Equal(("LinkStart", "Ctrl+2"), call),
            call => Assert.Equal(("ShowGui", "Ctrl+Alt+N"), call));
        Assert.Contains("LinkStart", hotkeyService.UnregisterCalls);
        Assert.True(hotkeyService.TryGetRegisteredHotkey("ShowGui", out var showGui));
        Assert.Equal("Ctrl+Alt+N", showGui.Gesture);
        Assert.False(hotkeyService.TryGetRegisteredHotkey("LinkStart", out _));
    }

    [Fact]
    public async Task ImportLegacyConfigurationsAsync_RegistrationConflict_RestoresPersistedActiveHotkey()
    {
        var hotkeyService = new ScriptedHotkeyService();
        await using var fixture = await RuntimeFixture.CreateAsync(hotkeyService: hotkeyService);
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.HotKeys] = JsonValue.Create(
            "ShowGui=Ctrl+1;LinkStart=Ctrl+2");
        fixture.Config.CurrentConfig.GlobalValues[HotkeyConfigurationCodec.MacDefaultsVersionKey] = JsonValue.Create("1");
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        await vm.RegisterHotkeysAsync();
        hotkeyService.EnqueueRegisterResult(
            "ShowGui",
            PlatformOperation.Failed("test", "conflict", PlatformErrorCodes.HotkeyConflict, "hotkey.register"));

        var legacyGuiPath = Path.Combine(fixture.Root, "config", "gui.json");
        await File.WriteAllTextAsync(
            legacyGuiPath,
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {}
              },
              "Global": {
                "HotKeys": "ShowGui=Ctrl+Alt+N;LinkStart=Ctrl+Alt+L"
              }
            }
            """);

        var report = await vm.ImportLegacyConfigurationsAsync(
            new LegacyImportRequest(
                LegacyConfigSnapshot.FromPaths(null, legacyGuiPath),
                ImportSource.GuiOnly,
                ManualImport: true,
                AllowPartialImport: true));

        Assert.True(report.AppliedConfig);
        Assert.Equal("Ctrl+1", vm.HotkeyShowGui);
        Assert.Equal("Ctrl+1", vm.ShowGuiHotkeyState.GestureStorage);
        Assert.Equal("Ctrl+Alt+L", vm.HotkeyLinkStart);
        Assert.True(hotkeyService.TryGetRegisteredHotkey("ShowGui", out var showGui));
        Assert.Equal("Ctrl+1", showGui.Gesture);
        Assert.True(hotkeyService.TryGetRegisteredHotkey("LinkStart", out var linkStart));
        Assert.Equal("Ctrl+Alt+L", linkStart.Gesture);

        var persisted = ParseHotkeys(ReadGlobalString(fixture.Config, ConfigurationKeys.HotKeys));
        Assert.Equal("Ctrl+1", persisted["ShowGui"]);
        Assert.Equal("Ctrl+Alt+L", persisted["LinkStart"]);
        Assert.Contains(
            PlatformCapabilityTextMap.FormatErrorCode("zh-cn", PlatformErrorCodes.HotkeyConflict, "ignored"),
            vm.HotkeyErrorMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterHotkeysAsync_ClearBinding_UnregistersAndPersistsEmptyGesture()
    {
        var hotkeyService = new ScriptedHotkeyService();
        await using var fixture = await RuntimeFixture.CreateAsync(hotkeyService: hotkeyService);
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.HotKeys] = JsonValue.Create("ShowGui=Ctrl+1;LinkStart=Ctrl+2");
        fixture.Config.CurrentConfig.GlobalValues[HotkeyConfigurationCodec.MacDefaultsVersionKey] = JsonValue.Create("1");
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.ClearHotkeyBinding("ShowGui");

        await vm.RegisterHotkeysAsync();

        Assert.Contains("ShowGui", hotkeyService.UnregisterCalls);
        Assert.Equal("ShowGui=;LinkStart=Ctrl+2", ReadGlobalString(fixture.Config, ConfigurationKeys.HotKeys));
    }

    [Fact]
    public void ApplyPlatformDefaultsMigration_OnMac_ResetsHotkeysAndWritesVersion()
    {
        var config = new UnifiedConfig();
        config.GlobalValues[ConfigurationKeys.HotKeys] = JsonValue.Create("ShowGui=Ctrl+1;LinkStart=Ctrl+2");

        var changed = HotkeyConfigurationCodec.ApplyPlatformDefaultsMigration(config, isMacOS: true);

        Assert.True(changed);
        Assert.Equal("ShowGui=Meta+Shift+M;LinkStart=Meta+Shift+L", ReadGlobalString(config, ConfigurationKeys.HotKeys));
        Assert.Equal("1", ReadGlobalString(config, HotkeyConfigurationCodec.MacDefaultsVersionKey));
    }

    [Fact]
    public void ApplyPlatformDefaultsMigration_WhenAlreadyVersioned_DoesNotOverwriteCustomBindings()
    {
        var config = new UnifiedConfig();
        config.GlobalValues[ConfigurationKeys.HotKeys] = JsonValue.Create("ShowGui=Meta+Alt+G;LinkStart=Meta+Alt+R");
        config.GlobalValues[HotkeyConfigurationCodec.MacDefaultsVersionKey] = JsonValue.Create("1");

        var changed = HotkeyConfigurationCodec.ApplyPlatformDefaultsMigration(config, isMacOS: true);

        Assert.False(changed);
        Assert.Equal("ShowGui=Meta+Alt+G;LinkStart=Meta+Alt+R", ReadGlobalString(config, ConfigurationKeys.HotKeys));
    }

    [Fact]
    public void ApplyPlatformDefaultsMigration_OnNonMac_DoesNothing()
    {
        var config = new UnifiedConfig();
        config.GlobalValues[ConfigurationKeys.HotKeys] = JsonValue.Create("ShowGui=Ctrl+1;LinkStart=Ctrl+2");

        var changed = HotkeyConfigurationCodec.ApplyPlatformDefaultsMigration(config, isMacOS: false);

        Assert.False(changed);
        Assert.Equal("ShowGui=Ctrl+1;LinkStart=Ctrl+2", ReadGlobalString(config, ConfigurationKeys.HotKeys));
        Assert.Equal(string.Empty, ReadGlobalString(config, HotkeyConfigurationCodec.MacDefaultsVersionKey));
    }

    [Fact]
    public async Task HotkeyCaptureTexts_ShouldRefreshWithLanguageSwitch_AndUseLocalizedCaptureErrors()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.Language = "zh-cn";
        Assert.Equal("[热键] 显示/收起 MAA", vm.ShowGuiHotkeyState.Title);
        Assert.Equal("录入规则：至少按下一个修饰键与一个普通键才会提交；无修饰键会清空绑定。", vm.HotkeyCaptureGuideText);
        Assert.Equal("全局", vm.ShowGuiHotkeyState.ScopeLabel);
        vm.HandleHotkeyCapture(
            "ShowGui",
            new HotkeyCaptureResult(
                HotkeyCaptureResultKind.Rejected,
                Message: "At least one modifier key is required."));
        Assert.Equal("至少需要一个修饰键。", vm.ShowGuiHotkeyState.WarningMessage);

        vm.Language = "en-us";
        Assert.Equal("[HotKey] Show/collapse MAA", vm.ShowGuiHotkeyState.Title);
        Assert.Equal("Recording rules: press at least one modifier plus one non-modifier key. Any unmodified key clears the binding.", vm.HotkeyCaptureGuideText);
        Assert.Equal("Global", vm.ShowGuiHotkeyState.ScopeLabel);
        vm.HandleHotkeyCapture(
            "ShowGui",
            new HotkeyCaptureResult(
                HotkeyCaptureResultKind.Rejected,
                Message: "At least one modifier key is required."));
        Assert.Equal("At least one modifier key is required.", vm.ShowGuiHotkeyState.WarningMessage);
    }

    private static async Task ValidateMappedHotkeyErrorAsync(string errorCode)
    {
        var hotkeyService = new ScriptedHotkeyService();
        hotkeyService.EnqueueRegisterResult(
            "ShowGui",
            PlatformOperation.Failed("test", "failed", errorCode, "hotkey.register"));

        await using var fixture = await RuntimeFixture.CreateAsync(hotkeyService: hotkeyService);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.RegisterHotkeysAsync();

        Assert.Contains(
            PlatformCapabilityTextMap.FormatErrorCode("zh-cn", errorCode, "failed"),
            vm.HotkeyErrorMessage,
            StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> ParseHotkeys(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (var segment in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = segment.IndexOf('=');
            if (index <= 0 || index >= segment.Length - 1)
            {
                continue;
            }

            var key = segment[..index].Trim();
            var value = segment[(index + 1)..].Trim();
            result[key] = value;
        }

        return result;
    }

    private static string ReadGlobalString(UnifiedConfigurationService config, string key)
        => ReadGlobalString(config.CurrentConfig, key);

    private static string ReadGlobalString(UnifiedConfig config, string key)
    {
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value && value.TryGetValue(out string? text) && text is not null)
        {
            return text;
        }

        return node.ToString();
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly bool _cleanupRoot;

        private RuntimeFixture(
            string root,
            MAAUnifiedRuntime runtime,
            UnifiedConfigurationService config,
            UiDiagnosticsService diagnostics,
            bool cleanupRoot)
        {
            Root = root;
            Runtime = runtime;
            Config = config;
            Diagnostics = diagnostics;
            _cleanupRoot = cleanupRoot;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public UnifiedConfigurationService Config { get; }

        public UiDiagnosticsService Diagnostics { get; }

        public static async Task<RuntimeFixture> CreateAsync(
            string? root = null,
            bool cleanupRoot = true,
            IGlobalHotkeyService? hotkeyService = null)
        {
            root ??= Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));

            var log = new UiLogService();
            var diagnostics = new UiDiagnosticsService(root, log);
            var config = new UnifiedConfigurationService(
                new AvaloniaJsonConfigStore(root),
                new GuiNewJsonConfigImporter(),
                new GuiJsonConfigImporter(),
                log,
                root);
            await config.LoadOrBootstrapAsync();

            var bridge = new FakeBridge();
            var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
            hotkeyService ??= new ScriptedHotkeyService();
            var platform = new PlatformServiceBundle
            {
                TrayService = new NoOpTrayService(),
                NotificationService = new NoOpNotificationService(),
                HotkeyService = hotkeyService,
                AutostartService = new NoOpAutostartService(),
                FileDialogService = new NoOpFileDialogService(),
                OverlayService = new NoOpOverlayCapabilityService(),
                PostActionExecutorService = new NoOpPostActionExecutorService(),
            };

            var capability = new PlatformCapabilityFeatureService(platform, diagnostics);
            var connect = new ConnectFeatureService(session, config);
            var runtime = new MAAUnifiedRuntime
            {
                CoreBridge = bridge,
                ConfigurationService = config,
                ResourceWorkflowService = new ResourceWorkflowService(root, bridge, log),
                SessionService = session,
                Platform = platform,
                LogService = log,
                DiagnosticsService = diagnostics,
                ConnectFeatureService = connect,
                ShellFeatureService = new ShellFeatureService(connect),
                TaskQueueFeatureService = new TaskQueueFeatureService(session, config),
                CopilotFeatureService = new CopilotFeatureService(),
                ToolboxFeatureService = new ToolboxFeatureService(),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = capability,
                OverlayFeatureService = new OverlayFeatureService(capability),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(
                    config,
                    diagnostics,
                    platform.PostActionExecutorService),
            };

            return new RuntimeFixture(root, runtime, config, diagnostics, cleanupRoot);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            if (!_cleanupRoot)
            {
                return;
            }

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temporary test directories
            }
        }
    }

    private sealed class ScriptedHotkeyService : IGlobalHotkeyService
    {
        private readonly Dictionary<string, Queue<PlatformOperationResult>> _registerResults = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RegisteredHotkeyState> _registered = new(StringComparer.OrdinalIgnoreCase);

        public ScriptedHotkeyService(PlatformCapabilityStatus? capability = null)
        {
            Capability = capability ?? new PlatformCapabilityStatus(
                Supported: true,
                Message: "hotkey test service",
                Provider: "test");
        }

        public PlatformCapabilityStatus Capability { get; }

        public event EventHandler<GlobalHotkeyTriggeredEvent>? Triggered;

        public List<(string Name, string Gesture)> RegisterCalls { get; } = [];

        public List<string> UnregisterCalls { get; } = [];

        public void EnqueueRegisterResult(string name, PlatformOperationResult result)
        {
            if (!_registerResults.TryGetValue(name, out var queue))
            {
                queue = new Queue<PlatformOperationResult>();
                _registerResults[name] = queue;
            }

            queue.Enqueue(result);
        }

        public Task<PlatformOperationResult> RegisterAsync(string name, string gesture, CancellationToken cancellationToken = default)
        {
            RegisterCalls.Add((name, gesture));
            PlatformOperationResult result;
            if (_registerResults.TryGetValue(name, out var queue) && queue.Count > 0)
            {
                result = queue.Dequeue();
            }
            else
            {
                result = PlatformOperation.NativeSuccess(Capability.Provider, $"Registered {name}", "hotkey.register");
            }

            if (result.Success)
            {
                _registered[name] = new RegisteredHotkeyState(
                    name,
                    gesture,
                    HotkeyGestureCodec.FormatDisplay(gesture),
                    result.Provider,
                    result.ExecutionMode);
            }

            return Task.FromResult(result);
        }

        public Task<PlatformOperationResult> UnregisterAsync(string name, CancellationToken cancellationToken = default)
        {
            UnregisterCalls.Add(name);
            _registered.Remove(name);
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, $"Unregistered {name}", "hotkey.unregister"));
        }

        public bool TryGetRegisteredHotkey(string name, out RegisteredHotkeyState state)
        {
            return _registered.TryGetValue(name, out state!);
        }
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(
            CoreInitializeRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));
        }

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<int>.Ok(1));
        }

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));
        }

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));
        }

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));
        }

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
