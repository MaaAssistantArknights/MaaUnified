namespace MAAUnified.Tests;

public sealed class TaskQueueViewActionRegressionTests
{
    [Fact]
    public void TaskQueueView_ShouldWireImmediateStopAndVisibleWaitAndStopAction()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var xaml = BaselineTestSupport.NormalizeLineEndings(
            File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml")));
        var codeBehind = File.ReadAllText(
            Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml.cs"));

        Assert.Contains("Content=\"{Binding RunButtonText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsRunButtonVisible}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnToggleRunClick\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding WaitAndStopButtonText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowWaitAndStopButton}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanWaitAndStop}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnWaitAndStopClick\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OnWaitAndStopClick", codeBehind, StringComparison.Ordinal);
        Assert.Contains("VM.WaitAndStopAsync", codeBehind, StringComparison.Ordinal);
    }
}
