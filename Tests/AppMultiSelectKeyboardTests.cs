using Avalonia.Input;
using MAAUnified.App.Controls;

namespace MAAUnified.Tests;

[Collection("MainShellSerial")]
public sealed class AppMultiSelectKeyboardTests
{
    [Fact]
    public void KeyboardInput_MapsSupportedKeysToOpenAndCloseActions()
    {
        Assert.Equal(AppMultiSelectKeyboardAction.Open, AppMultiSelect.GetKeyboardAction(true, false, Key.Space));
        Assert.Equal(AppMultiSelectKeyboardAction.Open, AppMultiSelect.GetKeyboardAction(true, false, Key.Enter));
        Assert.Equal(AppMultiSelectKeyboardAction.Open, AppMultiSelect.GetKeyboardAction(true, false, Key.F4));
        Assert.Equal(AppMultiSelectKeyboardAction.Open, AppMultiSelect.GetKeyboardAction(true, false, Key.Down));
        Assert.Equal(AppMultiSelectKeyboardAction.Close, AppMultiSelect.GetKeyboardAction(true, true, Key.Escape));
        Assert.Equal(AppMultiSelectKeyboardAction.None, AppMultiSelect.GetKeyboardAction(true, false, Key.Escape));
    }

    [Fact]
    public void KeyboardInput_DoesNotOpenDisabledMultiSelect_AndIsWiredToFocusableRoot()
    {
        Assert.Equal(AppMultiSelectKeyboardAction.None, AppMultiSelect.GetKeyboardAction(false, false, Key.Enter));

        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppMultiSelect.axaml"));
        var code = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppMultiSelect.axaml.cs"));

        Assert.Contains("Focusable=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsTabStop=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("KeyDown=\"OnKeyDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("KeyDown=\"OnPopupKeyDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("GetKeyboardAction(IsEnabled, IsDropDownOpen, key)", code, StringComparison.Ordinal);
    }
}
