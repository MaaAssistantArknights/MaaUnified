using System.Text.RegularExpressions;
using MAAUnified.App.ViewModels.Settings;

namespace MAAUnified.Tests;

public sealed class SessionActivityLeaseRegressionTests
{
    [Fact]
    public void SessionActivityLease_ShouldReferenceCountAndDisposeIdempotently()
    {
        var shared = new ConnectionGameSharedStateViewModel();
        var transitions = new List<bool>();
        shared.PropertyChanged += (_, args) =>
        {
            if (string.Equals(
                    args.PropertyName,
                    nameof(ConnectionGameSharedStateViewModel.IsSessionOperationActive),
                    StringComparison.Ordinal))
            {
                transitions.Add(shared.IsSessionOperationActive);
            }
        };

        Assert.False(shared.IsSessionOperationActive);
        using var first = shared.BeginSessionOperation("Test.First");
        using var second = shared.BeginSessionOperation("Test.Second");
        Assert.True(shared.IsSessionOperationActive);

        first.Dispose();
        first.Dispose();
        Assert.True(shared.IsSessionOperationActive);
        second.Dispose();
        second.Dispose();
        Assert.False(shared.IsSessionOperationActive);
        Assert.Equal([true, false], transitions);
    }

    [Fact]
    public void CompatibilityActivityFlags_ShouldShareTheSingleLeaseSource()
    {
        var shared = new ConnectionGameSharedStateViewModel();

        shared.IsTaskStartRequestActive = true;
        Assert.True(shared.IsTaskStartRequestActive);
        Assert.True(shared.IsSessionOperationActive);
        Assert.False(shared.TryBeginScreenshotTestActivity());
        Assert.False(shared.IsScreenshotTestActive);

        shared.IsTaskStartRequestActive = false;
        Assert.False(shared.IsTaskStartRequestActive);
        Assert.False(shared.IsSessionOperationActive);

        Assert.True(shared.TryBeginScreenshotTestActivity());
        Assert.True(shared.IsScreenshotTestActive);
        Assert.True(shared.IsSessionOperationActive);

        shared.IsScreenshotTestActive = false;
        Assert.False(shared.IsScreenshotTestActive);
        Assert.False(shared.IsSessionOperationActive);
    }

    [Fact]
    public void StartEntrancesAndSettingsSurfaces_ShouldUseTheSingleActivitySource()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var taskQueue = File.ReadAllText(Path.Combine(root, "App", "ViewModels", "TaskQueue", "TaskQueuePageViewModel.cs"));
        var copilot = File.ReadAllText(Path.Combine(root, "App", "ViewModels", "Copilot", "CopilotPageViewModel.cs"));
        var toolbox = File.ReadAllText(Path.Combine(root, "App", "ViewModels", "Toolbox", "ToolboxPageViewModel.cs"));
        var shell = File.ReadAllText(Path.Combine(root, "App", "ViewModels", "MainShellViewModel.cs"));
        var settings = File.ReadAllText(Path.Combine(root, "App", "ViewModels", "Settings", "SettingsPageViewModel.cs"));
        var settingsView = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "SettingsView.axaml"));
        var configurationView = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConfigurationManagerView.axaml"));
        var guiView = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "GuiSettingsView.axaml"));

        Assert.Contains("_connectionGameSharedState.TryBeginTaskStartRequest()", taskQueue, StringComparison.Ordinal);
        Assert.Contains("_connectionGameSharedState.EndTaskStartRequest()", taskQueue, StringComparison.Ordinal);
        Assert.Matches(
            new Regex("TryBeginSessionOperation\\(\\s*\"TaskQueue\\.RunOnce\",\\s*requireIdle: true\\)"),
            taskQueue);
        Assert.Matches(
            new Regex("TryBeginSessionOperation\\(\\s*\"TaskQueue\\.AccountSwitch\",\\s*requireIdle: true\\)"),
            taskQueue);
        Assert.Matches(
            new Regex("TryBeginSessionOperation\\(\\s*\"Copilot\\.Start\",\\s*requireIdle: true\\)"),
            copilot);
        Assert.Contains("TryBeginSessionOperation(\"Toolbox.Peep\")", toolbox, StringComparison.Ordinal);
        Assert.Contains("AcquireExclusiveSessionOperationAsync(", shell, StringComparison.Ordinal);
        Assert.Contains("TryBeginExclusiveSessionOperation(", shell, StringComparison.Ordinal);
        Assert.Contains("ForcedScheduledStartActivityWaitTimeout = TimeSpan.FromSeconds(30)", shell, StringComparison.Ordinal);
        Assert.Contains("ConnectionGameSharedState.IsSessionOperationActive", settings, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding IsSessionIdle}\"", settingsView, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding IsSessionIdle}\"", configurationView, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ConfigurationProfiles}\"", configurationView, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding IsSessionIdle}\"", guiView, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding InverseClearModeOptions}\"", guiView, StringComparison.Ordinal);
    }
}
