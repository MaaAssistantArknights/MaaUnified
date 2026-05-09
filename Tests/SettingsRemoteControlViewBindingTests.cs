namespace MAAUnified.Tests;

public sealed class SettingsRemoteControlViewBindingTests
{
    [Fact]
    public void RemoteControlSettingsView_ShouldBindUserIdentityAndReadonlyDeviceIdentity()
    {
        var root = BaselineTestSupport.GetMaaUnifiedRoot();
        var path = Path.Combine(root, "App", "Features", "Settings", "RemoteControlSettingsView.axaml");
        var xaml = File.ReadAllText(path);

        Assert.Matches(
            "Text=\"\\{Binding RemoteUserIdentity(?:,\\s*UpdateSourceTrigger=LostFocus)?\\}\"",
            xaml);
        Assert.Matches(
            "Text=\"\\{Binding RemoteDeviceIdentity(?:,\\s*Mode=OneWay)?\\}\"",
            xaml);
        Assert.Contains("IsReadOnly=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnRegenerateRemoteDeviceIdentityClick\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteControlSettingsView_ShouldNotContainLegacyUnboundIdentityPlaceholder()
    {
        var root = BaselineTestSupport.GetMaaUnifiedRoot();
        var path = Path.Combine(root, "App", "Features", "Settings", "RemoteControlSettingsView.axaml");
        var xaml = File.ReadAllText(path);

        Assert.DoesNotContain("userId-deviceId", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteControlSettingsViewCodeBehind_ShouldUseRegenerateHandlerAndRemoveDeadSaveHandler()
    {
        var root = BaselineTestSupport.GetMaaUnifiedRoot();
        var path = Path.Combine(root, "App", "Features", "Settings", "RemoteControlSettingsView.axaml.cs");
        var code = File.ReadAllText(path);

        Assert.Contains("OnRegenerateRemoteDeviceIdentityClick", code, StringComparison.Ordinal);
        Assert.Contains("Guid.NewGuid().ToString(\"N\")", code, StringComparison.Ordinal);
        Assert.Contains("await VM.SaveRemoteControlAsync();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OnSaveRemoteClick", code, StringComparison.Ordinal);
    }
}
