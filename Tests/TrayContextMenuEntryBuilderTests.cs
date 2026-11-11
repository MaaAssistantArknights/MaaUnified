using MAAUnified.App.Controls;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.Views;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

// WPF reference: src/MaaWpfGui/Views/UI/NotifyIcon.xaml defines the tray menu as
// Start, Stop, SwitchLanguage (submenu), ForceShow, HideTray, ToggleOverlay, Restart,
// Exit with Start/Stop never disabled, and MainWindowManager collapses HideTray while
// the main window is hidden. The Avalonia fallback popup must mirror that layout.
public sealed class TrayContextMenuEntryBuilderTests
{
    [Fact]
    public void Build_ShouldMatchWpfMenuLayoutAndKeepStartStopEnabled()
    {
        var text = PlatformCapabilityTextMap.CreateTrayMenuText("zh-cn");

        var entries = TrayContextMenuEntryBuilder.Build(text, mainWindowVisible: true);

        Assert.Collection(
            entries,
            entry =>
            {
                var item = Assert.IsType<TrayContextMenuItemEntry>(entry);
                Assert.Equal(TrayCommandId.Start, item.Command);
                Assert.True(item.IsEnabled);
            },
            entry =>
            {
                var item = Assert.IsType<TrayContextMenuItemEntry>(entry);
                Assert.Equal(TrayCommandId.Stop, item.Command);
                Assert.True(item.IsEnabled);
            },
            entry =>
            {
                var submenu = Assert.IsType<TrayContextMenuSubmenuEntry>(entry);
                Assert.Equal(text.SwitchLanguage, submenu.Header);
                Assert.NotEmpty(submenu.Items);
            },
            entry => Assert.Equal(TrayCommandId.ForceShow, Assert.IsType<TrayContextMenuItemEntry>(entry).Command),
            entry => Assert.Equal(TrayCommandId.HideTray, Assert.IsType<TrayContextMenuItemEntry>(entry).Command),
            entry => Assert.Equal(TrayCommandId.ToggleOverlay, Assert.IsType<TrayContextMenuItemEntry>(entry).Command),
            entry => Assert.Equal(TrayCommandId.Restart, Assert.IsType<TrayContextMenuItemEntry>(entry).Command),
            entry => Assert.Equal(TrayCommandId.Exit, Assert.IsType<TrayContextMenuItemEntry>(entry).Command));
    }

    [Fact]
    public void Build_ShouldOmitHideTrayEntry_WhenMainWindowHidden()
    {
        var text = PlatformCapabilityTextMap.CreateTrayMenuText("zh-cn");

        var entries = TrayContextMenuEntryBuilder.Build(text, mainWindowVisible: false);

        Assert.DoesNotContain(
            entries,
            entry => entry is TrayContextMenuItemEntry { Command: TrayCommandId.HideTray });
        Assert.Contains(
            entries,
            entry => entry is TrayContextMenuItemEntry { Command: TrayCommandId.ForceShow });
    }

    [Fact]
    public void Build_LanguageSubmenu_ShouldExcludePallasAndCarryTargetLanguage()
    {
        var text = PlatformCapabilityTextMap.CreateTrayMenuText("zh-cn");

        var entries = TrayContextMenuEntryBuilder.Build(text, mainWindowVisible: true);

        var submenu = entries.OfType<TrayContextMenuSubmenuEntry>().Single();
        Assert.DoesNotContain(
            submenu.Items,
            item => string.Equals(item.TargetLanguage, "pallas", StringComparison.OrdinalIgnoreCase));
        Assert.All(
            submenu.Items,
            item =>
            {
                Assert.Equal(TrayCommandId.SwitchLanguage, item.Command);
                Assert.True(item.IsEnabled);
                Assert.False(string.IsNullOrWhiteSpace(item.TargetLanguage));
            });
    }

    [Fact]
    public void ToAppMenuEntry_ShouldMapSubmenuAndLanguageParameter()
    {
        var entry = new TrayContextMenuSubmenuEntry(
            "Switch Language",
            [new TrayContextMenuItemEntry("English", TrayCommandId.SwitchLanguage, IsEnabled: true, TargetLanguage: "en-us")]);

        var mapped = Assert.IsType<AppMenuSubmenuItem>(TrayContextMenuEntryBuilder.ToAppMenuEntry(entry));

        Assert.Equal("Switch Language", mapped.Header);
        var child = Assert.IsType<AppMenuActionItem>(Assert.Single(mapped.Items));
        Assert.Equal(TrayCommandId.SwitchLanguage, child.Command);
        Assert.Equal("en-us", child.Parameter);
        Assert.True(child.IsEnabled);
    }
}
