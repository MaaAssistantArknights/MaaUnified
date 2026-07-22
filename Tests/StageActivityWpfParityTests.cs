using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services.Features;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Tests;

public sealed class StageActivityWpfParityTests
{
    [Fact]
    public void CachedActivityState_ShouldApplyResourceCollectionAndFutureActivityRules()
    {
        var root = CreateTempRoot();
        try
        {
            WriteActivityCache(root, """
                {
                  "Official": {
                    "resourceCollection": {
                      "Tip": "All resource stages open",
                      "UtcStartTime": "2026/07/01 00:00:00",
                      "UtcExpireTime": "2026/07/03 00:00:00",
                      "TimeZone": 0
                    },
                    "sideStoryStage": {
                      "event": {
                        "MinimumRequired": "v1.0.0",
                        "Activity": {
                          "StageName": "Future event",
                          "UtcStartTime": "2026/07/02 00:00:00",
                          "UtcExpireTime": "2026/07/04 00:00:00",
                          "TimeZone": 0
                        },
                        "Stages": [ { "Display": "EV-8", "Value": "EV-8", "Drop": "30011" } ]
                      }
                    }
                  }
                }
                """);

            var service = new StageManagerFeatureService(
                configService: null,
                baseDirectory: root,
                coreVersionResolver: () => "v1.0.0");
            var state = service.GetStageActivityState("Official", forceReload: true);
            var now = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(state.Find("CE-6")!.IsOpen(now, DayOfWeek.Wednesday));
            Assert.True(state.Find("EV-8")!.IsOpenOrWillOpen(now));
            Assert.False(state.Find("EV-8")!.IsOpen(now, DayOfWeek.Wednesday));
            Assert.True(
                state.Stages.ToList().FindIndex(stage => stage.Value == "EV-8")
                < state.Stages.ToList().FindIndex(stage => stage.Value == "1-7"));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void CachedActivityState_ShouldMatchWpfMinimumCoreVersionRules()
    {
        var root = CreateTempRoot();
        try
        {
            WriteActivityCache(root, """
                {
                  "Official": {
                    "sideStoryStage": {
                      "supported": {
                        "MinimumRequired": "v1.0.0",
                        "Activity": { "UtcStartTime": "2026/07/01 00:00:00", "UtcExpireTime": "2026/07/03 00:00:00", "TimeZone": 0 },
                        "Stages": [ { "Display": "EV-1", "Value": "EV-1", "Drop": "30011" } ]
                      },
                      "unsupported": {
                        "Activity": { "UtcStartTime": "2026/07/01 00:00:00", "UtcExpireTime": "2026/07/03 00:00:00", "TimeZone": 0 },
                        "Stages": [ { "Display": "EV-2", "Value": "EV-2", "Drop": "30012", "MinimumRequired": "v2.0.0" } ]
                      },
                      "invalid": {
                        "Activity": { "UtcStartTime": "2026/07/01 00:00:00", "UtcExpireTime": "2026/07/03 00:00:00", "TimeZone": 0 },
                        "Stages": [ { "Display": "EV-3", "Value": "EV-3", "Drop": "30013" } ]
                      }
                    }
                  }
                }
                """);

            var service = new StageManagerFeatureService(
                configService: null,
                baseDirectory: root,
                coreVersionResolver: () => "v1.5.0");
            var state = service.GetStageActivityState("Official", forceReload: true);

            Assert.True(state.Find("EV-1")!.IsCoreVersionSupported);
            Assert.False(state.Find("EV-2")!.IsCoreVersionSupported);
            Assert.Equal("v2.0.0", state.Find("EV-2")!.MinimumRequired);
            Assert.Null(state.Find("EV-3"));

            var hint = FightTaskModuleViewModel.BuildDailyResourceHint(
                "zh-cn",
                "Official",
                config: null,
                stageState: state,
                nowUtc: new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc));
            Assert.Contains("EV-2: 版本过低", hint);
            Assert.Contains("最低需求: v2.0.0", hint);
            Assert.DoesNotContain("30012", hint);

            var unavailableCore = new StageManagerFeatureService(
                configService: null,
                baseDirectory: root,
                coreVersionResolver: () => null);
            Assert.Null(unavailableCore.GetStageActivityState("Official", forceReload: true).Find("EV-1"));

            var debugCore = new StageManagerFeatureService(
                configService: null,
                baseDirectory: root,
                coreVersionResolver: () => "DEBUG_VERSION");
            var debugState = debugCore.GetStageActivityState("Official", forceReload: true);
            Assert.NotNull(debugState.Find("EV-1"));
            Assert.True(debugState.Find("EV-2")!.IsCoreVersionSupported);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void CachedActivityState_ShouldPreferActivityStageOverPermanentFallbackWithSameDisplay()
    {
        var root = CreateTempRoot();
        try
        {
            WriteActivityCache(root, """
                {
                  "Official": {
                    "sideStoryStage": {
                      "event": {
                        "MinimumRequired": "v1.0.0",
                        "Activity": { "UtcStartTime": "2026/07/01 00:00:00", "UtcExpireTime": "2026/07/03 00:00:00", "TimeZone": 0 },
                        "Stages": [ { "Display": "OF-1", "Value": "OF-1", "Drop": "30011" } ]
                      }
                    }
                  }
                }
                """);

            var service = new StageManagerFeatureService(
                configService: null,
                baseDirectory: root,
                coreVersionResolver: () => "v1.0.0");
            var state = service.GetStageActivityState("Official", forceReload: true);

            var stage = Assert.Single(state.Stages, stage => stage.Value == "OF-1");
            Assert.False(stage.IsHidden);
            Assert.NotNull(stage.Activity);
            Assert.Equal("30011", stage.Drop);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Theory]
    [InlineData("V1.0.0")]
    [InlineData("1.0")]
    public void CachedActivityState_ShouldRejectCoreVersionsWpfDoesNotParse(string coreVersion)
    {
        var root = CreateTempRoot();
        try
        {
            WriteActivityCache(root, """
                {
                  "Official": {
                    "sideStoryStage": {
                      "event": {
                        "MinimumRequired": "v1.0.0",
                        "Activity": { "UtcStartTime": "2026/07/01 00:00:00", "UtcExpireTime": "2026/07/03 00:00:00", "TimeZone": 0 },
                        "Stages": [ { "Display": "EV-1", "Value": "EV-1" } ]
                      }
                    }
                  }
                }
                """);

            var service = new StageManagerFeatureService(
                configService: null,
                baseDirectory: root,
                coreVersionResolver: () => coreVersion);

            Assert.Null(service.GetStageActivityState("Official", forceReload: true).Find("EV-1"));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void DailyHint_ShouldIncludeActivityAndUsePartialInventoryPlaceholders()
    {
        var config = new UnifiedConfig();
        config.GlobalValues[LegacyConfigurationKeys.DepotResult] = JsonValue.Create("""{"data":"{\"3231\":8}"}""");
        var state = new MAAUnified.Application.Models.StageActivityState(
            "Official",
            MAAUnified.Application.Models.StageActivityStage.CreatePermanentStages()
                .Select(stage => stage.Value is "CE-6" or "PR-A-1"
                    ? stage with
                    {
                        Activity = new MAAUnified.Application.Models.StageActivityWindow(
                            "All resource stages open",
                            string.Empty,
                            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                            new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc),
                            IsResourceCollection: true),
                    }
                    : stage)
                .ToArray(),
            DateTimeOffset.UtcNow,
            ResourceTasksUpdated: false);

        var hint = FightTaskModuleViewModel.BuildDailyResourceHint(
            "zh-cn",
            "Official",
            config,
            state,
            new DateTime(2026, 7, 1, 12, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains("｢All resource stages open｣", hint);
        Assert.Contains("(库存 -- & 8 / -- & --)", hint);
    }

    [Fact]
    public void DailyHint_ShouldRenderActivityDropsAndAnnihilationRemindersLikeWpf()
    {
        var root = CreateTempRoot();
        try
        {
            var resourceDirectory = Path.Combine(root, "resource");
            Directory.CreateDirectory(resourceDirectory);
            File.WriteAllText(
                Path.Combine(resourceDirectory, "item_index.json"),
                """
                { "30011": { "name": "活动材料" } }
                """);
            using var _ = ToolboxAssetCatalog.PushTestBaseDirectoriesForTests(root);

            var config = new UnifiedConfig();
            config.GlobalValues[LegacyConfigurationKeys.DepotResult] = JsonValue.Create("""{"data":"{\"30011\":23}"}""");
            var now = new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc);
            var state = new StageActivityState(
                "Official",
                [
                    .. StageActivityStage.CreatePermanentStages(),
                    new StageActivityStage(
                        "EV-8",
                        "EV-8",
                        Activity: new StageActivityWindow(
                            string.Empty,
                            "测试活动",
                            now.AddDays(-1),
                            now.AddDays(2)),
                        Drop: "30011"),
                ],
                DateTimeOffset.UtcNow,
                ResourceTasksUpdated: false);

            var hint = FightTaskModuleViewModel.BuildDailyResourceHint("zh-cn", "Official", config, state, now);

            Assert.Contains("周一了，可以打剿灭了~", hint);
            Assert.Contains("｢测试活动｣ 剩余天数: 2", hint);
            Assert.Contains("EV-8: 活动材料 (库存 23)", hint);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task RefreshStageActivityWebAsync_ShouldCacheApiActivityAndTasksForCoreOverlay()
    {
        var root = CreateTempRoot();
        try
        {
            using var client = new HttpClient(new ScriptedHandler(), disposeHandler: true);
            var service = new StageManagerFeatureService(
                configService: null,
                baseDirectory: root,
                httpClient: client,
                coreVersionResolver: () => "v1.0.0");

            var result = await service.RefreshStageActivityWebAsync("Official");

            Assert.True(result.Success);
            Assert.True(result.Value!.ResourceTasksUpdated);
            Assert.NotNull(result.Value.Find("EV-8"));
            Assert.True(File.Exists(Path.Combine(root, "cache", "gui", "StageActivityV2.json")));
            Assert.True(File.Exists(Path.Combine(root, "cache", "resource", "tasks", "tasks.json")));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task RefreshStageActivityWebAsync_ShouldFailWhenTasksAreUnavailable()
    {
        var root = CreateTempRoot();
        try
        {
            using var client = new HttpClient(new ScriptedHandler(tasksAvailable: false), disposeHandler: true);
            var service = new StageManagerFeatureService(
                configService: null,
                baseDirectory: root,
                httpClient: client,
                coreVersionResolver: () => "v1.0.0");

            var result = await service.RefreshStageActivityWebAsync("Official");

            Assert.False(result.Success);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task RefreshStageActivityWebAsync_ShouldNotUseStaleCacheWhenApiRefreshFails()
    {
        var root = CreateTempRoot();
        try
        {
            var handler = new ScriptedHandler();
            using var client = new HttpClient(handler, disposeHandler: true);
            var service = new StageManagerFeatureService(
                configService: null,
                baseDirectory: root,
                httpClient: client,
                coreVersionResolver: () => "v1.0.0");

            Assert.True((await service.RefreshStageActivityWebAsync("Official")).Success);
            handler.TasksAvailable = false;

            var result = await service.RefreshStageActivityWebAsync("Official");

            Assert.False(result.Success);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static void WriteActivityCache(string root, string content)
    {
        var path = Path.Combine(root, "cache", "gui");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "StageActivityV2.json"), content);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "maaunified-stage-activity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Ignore temporary file cleanup failures.
        }
    }

    private sealed class ScriptedHandler(bool tasksAvailable = true) : HttpMessageHandler
    {
        public bool TasksAvailable { get; set; } = tasksAvailable;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var content = path.EndsWith("gui/StageActivityV2.json", StringComparison.Ordinal)
                ? """
                    { "Official": { "sideStoryStage": { "event": { "MinimumRequired": "v1.0.0", "Activity": { "UtcStartTime": "2026/07/01 00:00:00", "UtcExpireTime": "2026/07/03 00:00:00", "TimeZone": 0 }, "Stages": [ { "Display": "EV-8", "Value": "EV-8" } ] } } } }
                    """
                : "{ \"SideStoryStage\": { \"text\": [] } }";
            var response = new HttpResponseMessage(
                path.EndsWith("gui/StageActivityV2.json", StringComparison.Ordinal) || TasksAvailable
                    ? HttpStatusCode.OK
                    : HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"test\"");
            return Task.FromResult(response);
        }
    }
}
