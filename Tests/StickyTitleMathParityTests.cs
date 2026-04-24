using System.Reflection;
using MAAUnified.App.Controls;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.Features.Root;
using MAAUnified.App.ViewModels.Settings;

namespace MAAUnified.Tests;

public sealed class StickyTitleMathParityTests
{
    [Fact]
    public void AnnouncementAndSettings_ShouldAgreeOnSectionActivationMath()
    {
        const double activationLineY = 36d;
        const double targetHeaderTop = 240d;
        var headerContentTops = new[] { 120d, targetHeaderTop, 420d };

        Assert.Equal(204d, AnnouncementDialogView.ComputeSectionTargetOffset(targetHeaderTop, activationLineY));
        Assert.Equal(204d, InvokeSettingsComputeSectionTargetOffset(targetHeaderTop, activationLineY));

        Assert.Equal(
            1,
            AnnouncementDialogView.ResolveActiveSectionIndex(
                204d,
                activationLineY,
                headerContentTops));
        Assert.Equal(
            1,
            InvokeSettingsResolveActiveSectionIndex(
                204d,
                activationLineY,
                headerContentTops));

        Assert.Equal(
            -1,
            AnnouncementDialogView.ResolveActiveSectionIndex(
                20d,
                activationLineY,
                headerContentTops));
        Assert.Equal(
            -1,
            InvokeSettingsResolveActiveSectionIndex(
                20d,
                activationLineY,
                headerContentTops));
    }

    [Theory]
    [InlineData(120d, 60d, 0d)]
    [InlineData(60d, 60d, 0d)]
    [InlineData(30d, 60d, 30d)]
    [InlineData(-40d, 60d, 60d)]
    [InlineData(30d, 0d, 0d)]
    public void AnnouncementAndSettings_ShouldClampStickyPushOffsetTheSameWay(double nextViewportTop, double stickyHeight, double expected)
    {
        Assert.Equal(expected, AnnouncementDialogView.ComputeStickyPushOffset(nextViewportTop, stickyHeight));
        Assert.Equal(expected, InvokeSettingsCalculatePushOffset(nextViewportTop, stickyHeight));
    }

    [Fact]
    public void SettingsStickyMathSource_ShouldStillMatchAnnouncementClampFormula()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var settingsCode = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "SettingsView.axaml.cs"));
        var announcementCode = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "AnnouncementDialogView.axaml.cs"));

        Assert.Equal(204d, StickyTitleMath.ComputeSectionTargetOffset(240d, 36d));
        Assert.Equal(1, StickyTitleMath.ResolveActiveSectionIndex(204d, 36d, [120d, 240d, 420d]));
        Assert.Equal(-1, StickyTitleMath.ResolvePinnedHeaderIndex([12d, 30d], 0d));
        Assert.Equal(0, StickyTitleMath.ResolvePinnedHeaderIndex([-1d, 30d], 0d));
        Assert.Equal(30d, StickyTitleMath.ComputePushOffset(30d, 60d));
        Assert.Equal(0d, StickyTitleMath.ComputePushOffset(30d, 0d));

        Assert.Contains("return StickyTitleMath.ComputeSectionTargetOffset(headerContentTop, activationLineY);", settingsCode, StringComparison.Ordinal);
        Assert.Contains("return StickyTitleMath.ResolveActiveSectionIndex(offsetY, activationLineY, headerContentTops);", settingsCode, StringComparison.Ordinal);
        Assert.Contains("return StickyTitleMath.ComputePushOffset(nextLayout.Value.ViewportTop, stickyHeight);", settingsCode, StringComparison.Ordinal);
        Assert.Contains("return StickyTitleMath.ComputeSectionTargetOffset(headerContentTop, activationLineY);", announcementCode, StringComparison.Ordinal);
        Assert.Contains("return StickyTitleMath.ResolveActiveSectionIndex(offsetY, activationLineY, headerContentTops);", announcementCode, StringComparison.Ordinal);
        Assert.Contains("return StickyTitleMath.ComputePushOffset(nextViewportTop, stickyHeight);", announcementCode, StringComparison.Ordinal);
        Assert.Contains("AppStickyTitleState.Hidden", settingsCode, StringComparison.Ordinal);
        Assert.Contains("AppStickyTitleState.Hidden", announcementCode, StringComparison.Ordinal);
    }

    private static double InvokeSettingsComputeSectionTargetOffset(double headerContentTop, double activationLineY)
    {
        var method = GetSettingsMethod("ComputeSectionTargetOffset");
        return Assert.IsType<double>(method.Invoke(null, [headerContentTop, activationLineY]));
    }

    private static int InvokeSettingsResolveActiveSectionIndex(double offsetY, double activationLineY, IReadOnlyList<double> headerContentTops)
    {
        var method = GetSettingsMethod("ResolveActiveSectionIndex");
        return Assert.IsType<int>(method.Invoke(null, [offsetY, activationLineY, headerContentTops]));
    }

    private static double InvokeSettingsCalculatePushOffset(double nextViewportTop, double stickyHeight)
    {
        var layoutType = typeof(SettingsView).GetNestedType("SectionHeaderLayout", BindingFlags.NonPublic);
        Assert.NotNull(layoutType);

        var layout = Activator.CreateInstance(
            layoutType!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                new SettingsSectionViewModel("About", "About"),
                0d,
                nextViewportTop,
                18d,
            ],
            culture: null);

        Assert.NotNull(layout);

        var method = GetSettingsMethod("CalculatePushOffset");
        return Assert.IsType<double>(method.Invoke(null, [layout, stickyHeight]));
    }

    private static MethodInfo GetSettingsMethod(string name)
    {
        var method = typeof(SettingsView).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        return Assert.IsAssignableFrom<MethodInfo>(method);
    }
}
