using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services.TaskParams;

namespace MAAUnified.Tests;

public sealed class InfrastParityTests
{
    private static readonly string[] DefaultFacilities =
    [
        "Mfg", "Trade", "Control", "Power", "Reception", "Office", "Dorm", "Processing", "Training",
    ];

    [Fact]
    public void CompileTask_PartialParams_FillsWpfDefaultsForCore()
    {
        var task = new UnifiedTaskItem
        {
            Type = TaskModuleTypes.Infrast,
            Params = new JsonObject
            {
                ["mode"] = 0,
                ["future_option"] = "preserved",
            },
        };

        var compiled = TaskParamCompiler.CompileTask(
            task,
            new UnifiedProfile(),
            new UnifiedConfig(),
            strict: true);
        var runtime = TaskParamCompiler.BuildCoreParams(TaskModuleTypes.Infrast, compiled.Params);

        Assert.False(compiled.HasBlockingIssues);
        Assert.Equal(DefaultFacilities, ReadStrings(runtime, "facility"));
        Assert.Equal("Money", runtime["drones"]?.GetValue<string>());
        Assert.False(runtime["continue_training"]?.GetValue<bool>());
        Assert.Equal(0.3, runtime["threshold"]?.GetValue<double>());
        Assert.True(runtime["dorm_notstationed_enabled"]?.GetValue<bool>());
        Assert.True(runtime["dorm_trust_enabled"]?.GetValue<bool>());
        Assert.True(runtime["replenish"]?.GetValue<bool>());
        Assert.True(runtime["reception_message_board"]?.GetValue<bool>());
        Assert.True(runtime["reception_clue_exchange"]?.GetValue<bool>());
        Assert.True(runtime["reception_send_clue"]?.GetValue<bool>());
        Assert.Equal("preserved", runtime["future_option"]?.GetValue<string>());
        Assert.Null(runtime["_ui_facility_order"]);
    }

    [Fact]
    public void BuildCoreParams_PartialFacility_FillsMissingDefaultsAndPreservesExplicitValues()
    {
        var runtime = TaskParamCompiler.BuildCoreParams(
            TaskModuleTypes.Infrast,
            new JsonObject
            {
                ["mode"] = 0,
                ["facility"] = new JsonArray("Mfg", "Dorm"),
                ["dorm_trust_enabled"] = false,
            });

        Assert.Equal(["Mfg", "Dorm"], ReadStrings(runtime, "facility"));
        Assert.Equal("Money", runtime["drones"]?.GetValue<string>());
        Assert.True(runtime["dorm_notstationed_enabled"]?.GetValue<bool>());
        Assert.False(runtime["dorm_trust_enabled"]?.GetValue<bool>());
        Assert.True(runtime["replenish"]?.GetValue<bool>());
    }

    [Fact]
    public void BuildCoreParams_CustomTimeRotation_ResolvesNonNegativeIndexWithoutMutatingPersistence()
    {
        var path = WritePlanFile("{\"plans\":[{\"name\":\"A\",\"period\":[]}]}");
        try
        {
            var persisted = new InfrastParams
            {
                Mode = 10000,
                Filename = path,
                PlanIndex = -1,
                Facility = ["Mfg"],
                FacilityOrder = ["Mfg", "Dorm"],
            }.ToJson();

            var compiled = TaskParamCompiler.CompileTask(
                new UnifiedTaskItem { Type = TaskModuleTypes.Infrast, Params = persisted },
                new UnifiedProfile(),
                new UnifiedConfig(),
                strict: true);
            var runtime = TaskParamCompiler.BuildCoreParams(TaskModuleTypes.Infrast, compiled.Params);

            Assert.False(compiled.HasBlockingIssues);
            Assert.Equal(-1, persisted["plan_index"]?.GetValue<int>());
            Assert.Equal(0, compiled.Params["plan_index"]?.GetValue<int>());
            Assert.Equal(0, runtime["plan_index"]?.GetValue<int>());
            Assert.Null(runtime["_ui_facility_order"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CompileTask_CustomOutOfRangePlanIndex_ResetsToFirstPlanWithoutBlocking()
    {
        var path = WritePlanFile("{\"plans\":[{\"name\":\"Only\",\"period\":[]}]}");
        try
        {
            var persisted = new InfrastParams
            {
                Mode = 10000,
                Filename = path,
                PlanIndex = 5,
                Facility = ["Mfg"],
            }.ToJson();

            var compiled = TaskParamCompiler.CompileTask(
                new UnifiedTaskItem { Type = TaskModuleTypes.Infrast, Params = persisted },
                new UnifiedProfile(),
                new UnifiedConfig(),
                strict: true);
            var runtime = TaskParamCompiler.BuildCoreParams(TaskModuleTypes.Infrast, compiled.Params);

            Assert.False(compiled.HasBlockingIssues);
            Assert.Contains(
                compiled.Issues,
                issue => issue.Code == "InfrastCustomPlanIndexClamped" && !issue.Blocking);
            Assert.Equal(0, compiled.Params["plan_index"]?.GetValue<int>());
            Assert.Equal(0, runtime["plan_index"]?.GetValue<int>());
            Assert.Equal(5, persisted["plan_index"]?.GetValue<int>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{\"plans\":[{\"period\":[\"00:00-12:00\"]}]}")]
    [InlineData("{\"plans\":[{\"period\":[[\"00:00\",\"12:00\",\"18:00\"]]}]}")]
    [InlineData("{\"plans\":[{\"period\":[[\"not-a-time\",\"12:00\"]]}]}")]
    public void CompileTask_InvalidCustomFile_BlocksExecution(string json)
    {
        var path = WritePlanFile(json);
        try
        {
            var output = TaskParamCompiler.CompileTask(
                new UnifiedTaskItem
                {
                    Type = TaskModuleTypes.Infrast,
                    Params = new InfrastParams { Mode = 10000, Filename = path, PlanIndex = -1 }.ToJson(),
                },
                new UnifiedProfile(),
                new UnifiedConfig(),
                strict: true);

            Assert.True(output.HasBlockingIssues);
            Assert.Contains(output.Issues, issue => issue.Code == "InfrastCustomConfigInvalid");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CompileTask_MissingCustomFile_BlocksExecution()
    {
        var output = TaskParamCompiler.CompileTask(
            new UnifiedTaskItem
            {
                Type = TaskModuleTypes.Infrast,
                Params = new InfrastParams
                {
                    Mode = 10000,
                    Filename = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"),
                }.ToJson(),
            },
            new UnifiedProfile(),
            new UnifiedConfig(),
            strict: true);

        Assert.True(output.HasBlockingIssues);
    }

    [Fact]
    public void InfrastParams_ExplicitEmptyFacility_RemainsEmptyAndPreservesFullOrder()
    {
        var source = new JsonObject
        {
            ["facility"] = new JsonArray(),
            ["_ui_facility_order"] = new JsonArray("Mfg", "Dorm", "Trade"),
        };

        var model = InfrastParams.FromJson(source);
        var roundTrip = model.ToJson();
        var runtime = TaskParamCompiler.BuildCoreParams(TaskModuleTypes.Infrast, roundTrip);

        Assert.Empty(model.Facility);
        Assert.Equal(["Mfg", "Dorm", "Trade"], model.FacilityOrder);
        Assert.Empty(Assert.IsType<JsonArray>(roundTrip["facility"]));
        Assert.Empty(Assert.IsType<JsonArray>(runtime["facility"]));
        Assert.Null(runtime["_ui_facility_order"]);
        Assert.Equal(
            ["Mfg", "Dorm", "Trade"],
            Assert.IsType<JsonArray>(roundTrip["_ui_facility_order"]).Select(node => node!.GetValue<string>()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20000)]
    public void BuildCoreParams_NonCustom_RemovesCustomAndUiOnlyFields(int mode)
    {
        var runtime = TaskParamCompiler.BuildCoreParams(
            TaskModuleTypes.Infrast,
            new JsonObject
            {
                ["mode"] = mode,
                ["facility"] = new JsonArray("Mfg"),
                ["filename"] = "unused.json",
                ["plan_index"] = 7,
                ["_ui_facility_order"] = new JsonArray("Dorm", "Mfg"),
            });

        Assert.Null(runtime["filename"]);
        Assert.Null(runtime["plan_index"]);
        Assert.Null(runtime["_ui_facility_order"]);
        Assert.Equal("Mfg", runtime["facility"]?[0]?.GetValue<string>());
    }

    [Fact]
    public void ResolvePlanIndex_UsesWpfInclusiveNonWrappingPeriodSemantics()
    {
        var plans = InfrastPlanParser.Parse(
            "{\"plans\":["
            + "{\"name\":\"Day\",\"period\":[[\"08:00\",\"16:00\"]]},"
            + "{\"name\":\"CrossMidnight\",\"period\":[[\"22:00\",\"02:00\"]]}]}");

        Assert.Equal(0, InfrastPlanParser.ResolvePlanIndex(plans, -1, new TimeOnly(12, 0)));
        Assert.Equal(0, InfrastPlanParser.ResolvePlanIndex(plans, -1, new TimeOnly(23, 0)));
    }

    private static string WritePlanFile(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"maa-infrast-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string[] ReadStrings(JsonObject obj, string property)
        => Assert.IsType<JsonArray>(obj[property])
            .Select(node => node!.GetValue<string>())
            .ToArray();
}
