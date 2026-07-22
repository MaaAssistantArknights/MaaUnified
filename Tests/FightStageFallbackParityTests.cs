using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Services.TaskParams;

namespace MAAUnified.Tests;

public sealed class FightStageFallbackParityTests
{
    private static readonly IReadOnlyList<(string Stage, IReadOnlySet<DayOfWeek> OpenDays)> WeeklyStageFixtures =
    [
        ("CE-6", new HashSet<DayOfWeek> { DayOfWeek.Tuesday, DayOfWeek.Thursday, DayOfWeek.Saturday, DayOfWeek.Sunday }),
        ("AP-5", new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday, DayOfWeek.Sunday }),
        ("CA-5", new HashSet<DayOfWeek> { DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Friday, DayOfWeek.Sunday }),
        ("SK-5", new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday, DayOfWeek.Saturday }),
    ];

    [Fact]
    public void CompileFight_ClosedKnownStage_ShouldSkipInsteadOfFallingBackToCurrentOrLast()
    {
        var closedStage = ResolveClosedWeeklyStage();

        var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto
        {
            Stage = closedStage,
            StagePlan = [closedStage],
            HideUnavailableStage = true,
            StageResetMode = "Current",
        }, new UnifiedProfile(), new UnifiedConfig());

        Assert.Contains(compiled.Issues, issue => issue.Code == "TaskCompileSkipAppend" && issue.Field == "fight.stage_plan");
        Assert.Equal(closedStage, compiled.Params["stage"]?.GetValue<string>());
    }

    [Fact]
    public void CompileFight_AlternativePlanWithNoOpenStage_ShouldSkipAppend()
    {
        var closedStage = ResolveClosedWeeklyStage();

        var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto
        {
            Stage = closedStage,
            StagePlan = [closedStage],
            UseAlternateStage = true,
            HideUnavailableStage = false,
            StageResetMode = "Ignore",
        }, new UnifiedProfile(), new UnifiedConfig());

        Assert.Contains(compiled.Issues, issue => issue.Code == "TaskCompileSkipAppend" && issue.Field == "fight.stage_plan");
    }

    [Fact]
    public void CompileFight_ExpiredActivityStageWithIgnore_ShouldSkipAppend()
    {
        const string expiredStage = "ZZ-99";

        var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto
        {
            Stage = expiredStage,
            StagePlan = [expiredStage],
            HideUnavailableStage = false,
            StageResetMode = "Ignore",
        }, new UnifiedProfile(), new UnifiedConfig());

        Assert.Contains(compiled.Issues, issue => issue.Code == "TaskCompileSkipAppend" && issue.Field == "fight.stage_plan");
    }

    [Fact]
    public void CompileFight_HistoricalStageInResourceStages_ShouldSkipLikeWpf()
    {
        // SV-1 remains in resource/stages.json but is not in the current StageActivityV2 state.
        // WPF treats it as an expired two-letter activity stage rather than a permanent stage.
        const string historicalStage = "SV-1";

        var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto
        {
            Stage = historicalStage,
            StagePlan = [historicalStage],
            HideUnavailableStage = false,
            StageResetMode = "Ignore",
        }, new UnifiedProfile(), new UnifiedConfig());

        Assert.Contains(compiled.Issues, issue => issue.Code == "TaskCompileSkipAppend" && issue.Field == "fight.stage_plan");
    }

    [Fact]
    public void CompileFight_ManualExpiredActivityStage_ShouldSkipAppend()
    {
        const string expiredStage = "ZZ-99";

        var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto
        {
            Stage = expiredStage,
            StagePlan = [expiredStage],
            IsStageManually = true,
            HideUnavailableStage = false,
            StageResetMode = "Ignore",
        }, new UnifiedProfile(), new UnifiedConfig());

        Assert.Contains(compiled.Issues, issue => issue.Code == "TaskCompileSkipAppend" && issue.Field == "fight.stage_plan");
    }

    [Fact]
    public void CompileTask_MissingStageResetModeWithVisibleClosedStages_ShouldUseIgnore()
    {
        var task = new UnifiedTaskItem
        {
            Type = "Fight",
            Params = new JsonObject
            {
                ["stage"] = "ZZ-99",
                ["medicine"] = 0,
                ["stone"] = 0,
                ["times"] = 1,
                ["series"] = 1,
                ["_ui_stage_plan"] = new JsonArray("ZZ-99"),
                ["_ui_hide_unavailable_stage"] = false,
            },
        };

        var compiled = TaskParamCompiler.CompileTask(task, new UnifiedProfile(), new UnifiedConfig(), strict: true);

        Assert.Equal("Ignore", compiled.Params["_ui_stage_reset_mode"]?.GetValue<string>());
        Assert.Contains(compiled.Issues, issue => issue.Code == "TaskCompileSkipAppend" && issue.Field == "fight.stage_plan");
    }

    [Fact]
    public void CompileFight_InvalidStageResetModeWithVisibleClosedStages_ShouldUseIgnore()
    {
        const string expiredStage = "ZZ-99";

        var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto
        {
            Stage = expiredStage,
            StagePlan = [expiredStage],
            HideUnavailableStage = false,
            StageResetMode = "Invalid",
        }, new UnifiedProfile(), new UnifiedConfig());

        Assert.Equal("Ignore", compiled.Params["_ui_stage_reset_mode"]?.GetValue<string>());
        Assert.Contains(compiled.Issues, issue => issue.Code == "TaskCompileSkipAppend" && issue.Field == "fight.stage_plan");
    }

    [Fact]
    public void CompileFight_ExplicitCurrentOrLastAfterClosedCandidate_ShouldRemainRunnable()
    {
        var closedStage = ResolveClosedWeeklyStage();

        var compiled = TaskParamCompiler.CompileFight(new FightTaskParamsDto
        {
            Stage = closedStage,
            StagePlan = [closedStage, FightStageSelection.CurrentOrLast],
            UseAlternateStage = true,
            HideUnavailableStage = false,
            StageResetMode = "Ignore",
        }, new UnifiedProfile(), new UnifiedConfig());

        Assert.DoesNotContain(compiled.Issues, issue => issue.Code == "TaskCompileSkipAppend" && issue.Field == "fight.stage_plan");
        var coreParameters = TaskParamCompiler.BuildCoreParams("Fight", compiled.Params);
        Assert.Equal(string.Empty, coreParameters["stage"]?.GetValue<string>());
    }

    private static string ResolveClosedWeeklyStage()
    {
        var dayOfWeek = MallDailyResetHelper.GetYjDate(DateTime.UtcNow, "Official").DayOfWeek;
        return WeeklyStageFixtures.First(candidate => !candidate.OpenDays.Contains(dayOfWeek)).Stage;
    }
}
