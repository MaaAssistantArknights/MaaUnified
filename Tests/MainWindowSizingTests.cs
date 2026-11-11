using MAAUnified.App.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using MAAUnified.Platform;
using System.Text.Json.Nodes;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Tests;

public sealed class MainWindowSizingTests
{
    [Theory]
    [InlineData(InAppNotificationSeverity.Information, NotificationType.Information)]
    [InlineData(InAppNotificationSeverity.Warning, NotificationType.Warning)]
    [InlineData(InAppNotificationSeverity.Error, NotificationType.Error)]
    public void ResolveInAppNotificationType_ShouldPreservePlatformSeverity(
        InAppNotificationSeverity severity,
        NotificationType expected)
    {
        Assert.Equal(expected, MainWindow.ResolveInAppNotificationType(severity));
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(90, true)]
    public void ShouldUnlockAfkWatcherOnRestore_ShouldRequireAtLeastOneHourMinimized(
        int minimizedMinutes,
        bool expected)
    {
        var minimizedSince = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Local);

        var actual = MainWindow.ShouldUnlockAfkWatcherOnRestore(
            minimizedSince,
            minimizedSince.AddMinutes(minimizedMinutes));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveAfkWatcherMinimizedSince_ShouldStartClockWhenNotTracked()
    {
        var hiddenAt = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Local);

        var actual = MainWindow.ResolveAfkWatcherMinimizedSince(null, hiddenAt);

        Assert.Equal(hiddenAt, actual);
    }

    [Fact]
    public void ResolveAfkWatcherMinimizedSince_ShouldKeepWindowStatePipelineTimestamp()
    {
        var minimizedSince = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Local);
        var hiddenAt = minimizedSince.AddMinutes(5);

        var actual = MainWindow.ResolveAfkWatcherMinimizedSince(minimizedSince, hiddenAt);

        Assert.Equal(minimizedSince, actual);
    }

    [Theory]
    [InlineData(false, 1380)]
    [InlineData(true, 1104)]
    public void ComputeDefaultWindowWidth_ShouldMatchMacOSDefault(bool isMacOS, double expected)
    {
        var actual = MainWindow.ComputeDefaultWindowWidth(isMacOS);

        Assert.Equal(expected, actual, precision: 6);
    }

    [Theory]
    [InlineData(false, 900)]
    [InlineData(true, 720)]
    public void ComputeDefaultWindowHeight_ShouldMatchMacOSDefault(bool isMacOS, double expected)
    {
        var actual = MainWindow.ComputeDefaultWindowHeight(isMacOS);

        Assert.Equal(expected, actual, precision: 6);
    }

    [Theory]
    [InlineData(false, 1080, 620)]
    [InlineData(true, 1056, 596)]
    public void ComputeMinimumWindowSize_ShouldCompensateMacOSNativeWindowShadow(
        bool isMacOS,
        double expectedWidth,
        double expectedHeight)
    {
        Assert.Equal(expectedWidth, MainWindow.ComputeMinimumWindowWidth(isMacOS), precision: 6);
        Assert.Equal(expectedHeight, MainWindow.ComputeMinimumWindowHeight(isMacOS), precision: 6);
    }

    [Theory]
    [InlineData(0.9, 0.0, true, 0.9)]
    [InlineData(0.9, 1.0, true, 0.9)]
    [InlineData(0.9, 2.0, true, 1.008)]
    [InlineData(1.0, 2.5, true, 1.18)]
    [InlineData(0.9, 2.0, false, 0.9)]
    public void ComputeWindowHeightScale_ShouldApplyMacHiDpiBoost(
        double effectiveUiScaleFactor,
        double renderScaling,
        bool isMacOS,
        double expected)
    {
        var actual = MainWindow.ComputeWindowHeightScale(effectiveUiScaleFactor, renderScaling, isMacOS);

        Assert.Equal(expected, actual, precision: 6);
    }

    [Theory]
    [InlineData(0.9, 0.9)]
    [InlineData(0.0, 1.0)]
    [InlineData(-1.0, 1.0)]
    public void ComputeWindowWidthScale_ShouldFollowUiScaleOnly(double effectiveUiScaleFactor, double expected)
    {
        var actual = MainWindow.ComputeWindowWidthScale(effectiveUiScaleFactor);

        Assert.Equal(expected, actual, precision: 6);
    }

    [Theory]
    [InlineData(1380, 1.0, 0.9, 1380, 972, false, false, 1242)]
    [InlineData(1104, 1.0, 0.81, 1104, 855.36, false, true, 1104)]
    [InlineData(1117.8, 0.81, 1.0, 1380, 1080, true, false, 1380)]
    public void ResolveWindowSizeTarget_ShouldRespectMacPlatformDefaultWhenRequested(
        double currentSize,
        double previousScale,
        double nextScale,
        double defaultSize,
        double minSize,
        bool preserveLogicalSize,
        bool keepMacPlatformDefaultSize,
        double expected)
    {
        var actual = MainWindow.ResolveWindowSizeTarget(
            currentSize,
            previousScale,
            nextScale,
            defaultSize,
            minSize,
            preserveLogicalSize,
            keepMacPlatformDefaultSize);

        Assert.Equal(expected, actual, precision: 6);
    }

    [Fact]
    public void PersistedWindowSize_ShouldRoundTripPerPlatform()
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);

        MainWindow.WritePersistedWindowSize(values, "macOS", new Size(1104.123, 720.456));
        MainWindow.WritePersistedWindowSize(values, "Windows", new Size(1242, 810));

        Assert.True(MainWindow.TryReadPersistedWindowSize(values, "macOS", out var macSize));
        Assert.True(MainWindow.TryReadPersistedWindowSize(values, "Windows", out var windowsSize));
        Assert.Equal(1104.12, macSize.Width, precision: 6);
        Assert.Equal(720.46, macSize.Height, precision: 6);
        Assert.Equal(1242, windowsSize.Width, precision: 6);
        Assert.Equal(810, windowsSize.Height, precision: 6);
    }

    [Fact]
    public void PersistedWindowSize_ShouldHonorLegacyLoadAndSaveSwitches()
    {
        var defaults = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        Assert.True(MainWindow.ShouldLoadPersistedWindowSize(defaults));
        Assert.True(MainWindow.ShouldSavePersistedWindowSize(defaults));

        var disabled = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
        {
            [LegacyConfigurationKeys.LoadWindowPlacement] = JsonValue.Create("false"),
            [LegacyConfigurationKeys.SaveWindowPlacement] = JsonValue.Create(false),
        };

        Assert.False(MainWindow.ShouldLoadPersistedWindowSize(disabled));
        Assert.False(MainWindow.ShouldSavePersistedWindowSize(disabled));
    }

    [Fact]
    public void PersistedWindowSize_ShouldRejectInvalidPlacementNode()
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
        {
            [LegacyConfigurationKeys.WindowPlacement] = new JsonObject
            {
                ["Platforms"] = new JsonObject
                {
                    ["macOS"] = new JsonObject
                    {
                        ["Width"] = JsonValue.Create("NaN"),
                        ["Height"] = JsonValue.Create(720),
                    },
                },
            },
        };

        Assert.False(MainWindow.TryReadPersistedWindowSize(values, "macOS", out _));
    }

    [Fact]
    public void PersistedWindowPlacement_ShouldReadFrozenWpfSerializedValue()
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
        {
            [LegacyConfigurationKeys.WindowPlacement] = JsonValue.Create(
                "{\"Length\":44,\"Flags\":0,\"ShowCmd\":3,\"MinPosition\":{\"X\":-1,\"Y\":-1},\"MaxPosition\":{\"X\":-1,\"Y\":-1},\"NormalPosition\":{\"Left\":120,\"Top\":80,\"Right\":1320,\"Bottom\":880}}"),
        };

        Assert.True(MainWindow.TryReadPersistedWindowPlacement(values, "Windows", out var placement));
        Assert.Equal(new Size(1200, 800), placement.Size);
        Assert.Equal(new PixelPoint(120, 80), placement.Position);
        Assert.Equal(WindowState.Maximized, placement.State);
    }

    [Fact]
    public void PersistedWindowPlacement_ShouldWritePositionAndStateWithoutWpfStringLoss()
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
        {
            [LegacyConfigurationKeys.WindowPlacement] = JsonValue.Create(
                "{\"ShowCmd\":1,\"NormalPosition\":{\"Left\":10,\"Top\":20,\"Right\":810,\"Bottom\":620}}"),
        };

        MainWindow.WritePersistedWindowPlacement(
            values,
            "Windows",
            new MainWindow.PersistedWindowPlacement(
                new Size(1000, 700),
                new PixelPoint(240, 160),
                WindowState.Maximized));

        Assert.True(MainWindow.TryReadPersistedWindowPlacement(values, "Windows", out var placement));
        Assert.Equal(new Size(1000, 700), placement.Size);
        Assert.Equal(new PixelPoint(240, 160), placement.Position);
        Assert.Equal(WindowState.Maximized, placement.State);
    }

    [Theory]
    [InlineData(3024, 2.0, 1512)]
    [InlineData(1512, 1.0, 1512)]
    [InlineData(200, 2.0, 320)]
    [InlineData(3024, 0.0, 3024)]
    public void ConvertScreenPixelsToWindowUnits_ShouldUseDesktopScaling(
        double pixelLength,
        double desktopScaling,
        double expected)
    {
        var actual = MainWindow.ConvertScreenPixelsToWindowUnits(pixelLength, desktopScaling);

        Assert.Equal(expected, actual, precision: 6);
    }

    [Fact]
    public void OverlayPreviewBounds_ShouldKeepLogicalWidthOnHiDpiScreens()
    {
        var workingArea = new PixelRect(0, 0, 3024, 1964);
        var margin = OverlayHostWindow.ResolvePreviewMarginPixels(2.0);

        var size = OverlayHostWindow.ResolvePreviewPixelSize(workingArea, 2.0, margin);

        Assert.Equal(48, margin);
        Assert.Equal(960, size.Width);
        Assert.Equal(520, size.Height);
    }

    [Fact]
    public void OverlayPreviewPosition_WhenAnchored_ShouldUseTargetTopLeft()
    {
        var workingArea = new PixelRect(0, 0, 3024, 1964);
        var target = new PixelRect(100, 200, 1280, 720);
        var size = new PixelSize(960, 520);

        var position = OverlayHostWindow.ResolvePreviewPosition(workingArea, size, target, marginPixels: 48);

        Assert.Equal(new PixelPoint(148, 248), position);
    }

    [Theory]
    [InlineData(WindowResizeReason.User, true)]
    [InlineData(WindowResizeReason.Application, false)]
    [InlineData(WindowResizeReason.Layout, false)]
    [InlineData(WindowResizeReason.DpiChange, false)]
    [InlineData(WindowResizeReason.Unspecified, false)]
    public void ShouldTreatResizeAsLiveInteraction_ShouldOnlyTrackUserResize(
        WindowResizeReason reason,
        bool expected)
    {
        var actual = MainWindow.ShouldTreatResizeAsLiveInteraction(reason);

        Assert.Equal(expected, actual);
    }
}
