using MAAUnified.App.ViewModels.TaskQueue;

namespace MAAUnified.Tests;

public sealed class SettingsGuideStateMachineTests
{
    [Fact]
    public void StepOrder_ShouldMatchDevV2SettingsSections()
    {
        var expected = new (int Step, string? Section)[]
        {
            (0, "GUI"),
            (1, "About"),
            (2, "Game"),
            (3, "Connect"),
            (4, "Performance"),
            (5, null),
            (6, "VersionUpdate"),
        };

        foreach (var (step, section) in expected)
        {
            Assert.Equal(section, SettingsGuideStateMachine.GetSettingsSectionKey(step));
        }

        Assert.Equal(7, SettingsGuideStateMachine.StepCount);
        Assert.Equal(1, SettingsGuideStateMachine.AgreementStepIndex);
        Assert.Equal(5, SettingsGuideStateMachine.TaskDemoStepIndex);
    }

    [Fact]
    public void AgreementStep_ShouldKeepNextEnabledAndShowTipUntilConfirmed()
    {
        var guide = CreateGuide(SettingsGuideStateMachine.AgreementStepIndex);

        Assert.True(guide.IsAgreementStep);
        Assert.True(guide.CanGoToNext);
        Assert.True(guide.AgreementBadgeVisible);
        Assert.True(guide.TryGoNext());
        Assert.Equal(2, guide.CurrentStep);
    }

    [Fact]
    public void AgreementConfirmation_ShouldOnlyBeAllowedOnAgreementStep_AndClearBadge()
    {
        var guide = CreateGuide(0);

        Assert.False(guide.ConfirmAgreement());
        Assert.False(guide.AgreementConfirmed);
        Assert.True(guide.TryGoNext());
        Assert.True(guide.ConfirmAgreement());
        Assert.True(guide.AgreementConfirmed);
        Assert.False(guide.AgreementBadgeVisible);
        Assert.False(guide.ConfirmAgreement());
    }

    [Fact]
    public void TaskDemo_ShouldBeLockedUntilAgreementConfirmed()
    {
        var guide = CreateGuide(SettingsGuideStateMachine.TaskDemoStepIndex);

        Assert.True(guide.IsTaskDemoStep);
        Assert.False(guide.AgreementConfirmed);
        Assert.True(guide.IsNextBlocked);
        Assert.False(guide.CanGoToNext);
        Assert.False(guide.CompleteDemoAction(SettingsGuideDemoAction.AddFight));
        Assert.All(guide.DemoSteps, step => Assert.False(step.IsEnabled));
        Assert.All(guide.DemoSteps, step => Assert.False(step.IsBadgeVisible));
    }

    [Fact]
    public void TaskDemoActions_ShouldCompleteInOrderAndMoveTheBadge()
    {
        var guide = CreateGuide(SettingsGuideStateMachine.AgreementStepIndex, agreementConfirmed: true);
        Assert.True(guide.TryGoNext());
        Assert.True(guide.TryGoNext());
        Assert.True(guide.TryGoNext());
        Assert.True(guide.TryGoNext());
        Assert.Equal(SettingsGuideStateMachine.TaskDemoStepIndex, guide.CurrentStep);

        var orderedActions = Enum.GetValues<SettingsGuideDemoAction>();
        Assert.True(guide.DemoSteps[0].IsBadgeVisible);
        Assert.False(guide.CompleteDemoAction(orderedActions[2]));

        for (var i = 0; i < orderedActions.Length; i++)
        {
            Assert.Equal(orderedActions[i], guide.NextDemoAction!.Action);
            Assert.True(guide.DemoSteps[i].IsBadgeVisible);
            Assert.True(guide.CompleteDemoAction(orderedActions[i]));
            Assert.True(guide.DemoSteps[i].IsCompleted);
            Assert.False(guide.DemoSteps[i].IsBadgeVisible);
            Assert.Equal(i + 1, guide.CompletedDemoActionCount);
            if (i < orderedActions.Length - 1)
            {
                Assert.True(guide.DemoSteps[i + 1].IsBadgeVisible);
            }
        }

        Assert.True(guide.AllDemoActionsComplete);
        Assert.All(guide.DemoSteps, step => Assert.False(step.IsBadgeVisible));
        Assert.False(guide.IsNextBlocked);
        Assert.True(guide.CanGoToNext);
    }

    [Fact]
    public void TaskDemoProgress_ShouldSurviveStepNavigation()
    {
        var guide = CreateGuide(SettingsGuideStateMachine.AgreementStepIndex, agreementConfirmed: true);
        for (var i = 0; i < 4; i++)
        {
            Assert.True(guide.TryGoNext());
        }

        Assert.True(guide.CompleteDemoAction(SettingsGuideDemoAction.AddStartUp));
        Assert.True(guide.TryGoPrevious());
        Assert.Equal(4, guide.CurrentStep);
        Assert.True(guide.TryGoNext());
        Assert.Equal(5, guide.CurrentStep);
        Assert.Equal(1, guide.CompletedDemoActionCount);
        Assert.Equal(SettingsGuideDemoAction.AddFight, guide.NextDemoAction!.Action);
        Assert.True(guide.DemoSteps[1].IsBadgeVisible);
    }

    [Fact]
    public void Skip_ShouldRecordCurrentStepAndCompleteGuide()
    {
        var guide = CreateGuide(3);

        Assert.True(guide.Skip());
        Assert.True(guide.WasSkipped);
        Assert.Equal(3, guide.SkippedAtStep);
        Assert.Equal(SettingsGuideStateMachine.CompletedStep, guide.CurrentStep);
        Assert.False(guide.IsVisible);
        Assert.False(guide.Skip());
    }

    [Fact]
    public void Restart_ShouldKeepAgreementAndResetDemoProgress()
    {
        var guide = CreateGuide(SettingsGuideStateMachine.AgreementStepIndex, agreementConfirmed: true);
        Assert.True(guide.TryGoNext());
        Assert.True(guide.TryGoNext());
        Assert.True(guide.TryGoNext());
        Assert.True(guide.TryGoNext());
        Assert.True(guide.CompleteDemoAction(SettingsGuideDemoAction.AddStartUp));
        Assert.Equal(1, guide.CompletedDemoActionCount);

        Assert.True(guide.Restart());
        Assert.Equal(0, guide.CurrentStep);
        Assert.True(guide.AgreementConfirmed);
        Assert.Equal(0, guide.CompletedDemoActionCount);
        Assert.False(guide.WasSkipped);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 5)]
    [InlineData(4, 4)]
    [InlineData(5, 6)]
    [InlineData(6, 1)]
    [InlineData(7, 7)]
    public void LegacyStepIndex_ShouldMapToDevV2Order(int legacyStep, int expectedStep)
    {
        Assert.Equal(
            expectedStep,
            SettingsGuideStateMachine.ResolvePersistedStep(legacyStep, persistedStepOrderVersion: 0));
        Assert.Equal(
            legacyStep,
            SettingsGuideStateMachine.ResolvePersistedStep(legacyStep, SettingsGuideStateMachine.StepOrderVersion));
    }

    private static SettingsGuideStateMachine CreateGuide(int step, bool agreementConfirmed = false)
        => new(step, agreementConfirmed, skipped: false, skippedAtStep: 0, key => key);
}
