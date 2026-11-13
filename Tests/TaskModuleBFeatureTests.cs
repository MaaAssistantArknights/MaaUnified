using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class TaskModuleBFeatureTests
{
    [Theory]
    [InlineData("zh-cn", "招聘许可", "家具")]
    [InlineData("zh-tw", "招聘許可", "傢俱")]
    [InlineData("en-us", "Recruitment Permit", "Furniture Part")]
    [InlineData("ja-jp", "求人票", "家具")]
    [InlineData("ko-kr", "모집 허가증", "가구 부품")]
    public void CreateMallDefaults_UsesLocalizedWpfItemNames(string language, string firstItem, string blacklistItem)
    {
        var parameters = TaskModuleParameterDefaults.CreateMallDefaults(language);

        Assert.Contains(firstItem, Assert.IsType<JsonArray>(parameters["buy_first"]).Select(item => item?.GetValue<string>()));
        Assert.Contains(blacklistItem, Assert.IsType<JsonArray>(parameters["blacklist"]).Select(item => item?.GetValue<string>()));
    }

    [Fact]
    public async Task AddTask_InfrastMallAward_InjectsExpectedDefaults()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");

        Assert.True((await fixture.TaskQueue.AddTaskAsync("InfrastTask", "infra")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("MallTask", "mall")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("AwardTask", "award")).Success);

        var queueResult = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queueResult.Success, $"{queueResult.Error?.Code}:{queueResult.Error?.Message}");
        var tasks = Assert.IsAssignableFrom<IReadOnlyList<UnifiedTaskItem>>(queueResult.Value);
        Assert.Equal(3, tasks.Count);

        Assert.Equal(TaskModuleTypes.Infrast, tasks[0].Type);
        Assert.Equal(0, tasks[0].Params["mode"]?.GetValue<int>());
        var threshold = tasks[0].Params["threshold"]?.GetValue<double>() ?? 0;
        Assert.InRange(threshold, 0.299, 0.301);

        Assert.Equal(TaskModuleTypes.Mall, tasks[1].Type);
        var buyFirst = tasks[1].Params["buy_first"] as JsonArray;
        Assert.NotNull(buyFirst);
        Assert.Contains("Recruitment Permit", buyFirst!.Select(item => item?.GetValue<string>() ?? string.Empty));

        Assert.Equal(TaskModuleTypes.Award, tasks[2].Type);
        Assert.True(tasks[2].Params["award"]?.GetValue<bool>());
        Assert.False(tasks[2].Params["mail"]?.GetValue<bool>());
    }

    [Fact]
    public async Task UpdateTaskParams_RoundTripPersistsByTaskIndex()
    {
        await using var fixture = await TestFixture.CreateAsync();
        await fixture.TaskQueue.AddTaskAsync("Mall", "mall");

        var paramsResult = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(paramsResult.Success);
        var parameters = paramsResult.Value ?? new JsonObject();
        parameters["credit_fight"] = true;
        parameters["buy_first"] = new JsonArray("A", "B");

        var updateResult = await fixture.TaskQueue.UpdateTaskParamsAsync(0, parameters, persistImmediately: true);
        Assert.True(updateResult.Success);

        var latest = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(latest.Success);
        Assert.True(latest.Value?["credit_fight"]?.GetValue<bool>());
        var latestBuyFirst = latest.Value?["buy_first"] as JsonArray;
        Assert.NotNull(latestBuyFirst);
        Assert.Equal(2, latestBuyFirst!.Count);
    }

    [Fact]
    public async Task QueueEnabledTasks_LegacyEmptyFightStage_NormalizesToCurrentOrLast_AndDisablesMallCreditFight()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Fight",
            Name = "fight",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["stage"] = string.Empty,
                ["medicine"] = 0,
                ["stone"] = 0,
                ["times"] = 1,
                ["series"] = 1,
            },
        });
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Mall",
            Name = "mall",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["credit_fight"] = true,
            },
        });

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(queueResult.Success);
        Assert.Equal(2, queueResult.Value);

        Assert.Equal(FightStageSelection.CurrentOrLast, profile.TaskQueue[0].Params["stage"]?.GetValue<string>());
        Assert.True(profile.TaskQueue[1].Params["credit_fight"]?.GetValue<bool>());

        var fightTask = Assert.IsType<JsonObject>(JsonNode.Parse(fixture.Bridge.AppendedTasks[0].ParamsJson));
        var mallTask = Assert.IsType<JsonObject>(JsonNode.Parse(fixture.Bridge.AppendedTasks[1].ParamsJson));
        Assert.Equal(string.Empty, fightTask["stage"]?.GetValue<string>());
        Assert.False(mallTask["credit_fight"]?.GetValue<bool>());

        var eventLog = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "debug", "avalonia-ui-events.log"));
        Assert.Contains("Disabled Mall credit fight", eventLog, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueueEnabledTasks_MallCreditFight_GuardFiresWhenPlanResolvesToCurrentOrLastOnAnyEnabledWeekday()
    {
        // Frozen WPF parity: the guard aggregates over the whole week, so a plan
        // [Pormpt1, current/last] downgrades credit_fight even when today's
        // persisted stage is specific, because Pormpt1 is closed on most enabled
        // days (it opens Monday only). Pormpt1 is a permanent non-resource stage,
        // so no activity window can make it open all week (CE-6 et al. can become
        // week-long open during resource-collection activities).
        await using var fixture = await TestFixture.CreateAsync("en-us");
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.Values["ClientType"] = "Official";
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Fight",
            Name = "fight",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["stage"] = "Pormpt1",
                ["_ui_stage_plan"] = new JsonArray("Pormpt1", FightStageSelection.CurrentOrLast),
                ["_ui_use_alternate_stage"] = true,
                ["medicine"] = 0,
                ["stone"] = 0,
                ["times"] = 1,
                ["series"] = 1,
            },
        });
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Mall",
            Name = "mall",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["credit_fight"] = true,
                ["credit_fight_once_a_day"] = false,
            },
        });

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(queueResult.Success);
        var mallTask = Assert.IsType<JsonObject>(JsonNode.Parse(fixture.Bridge.AppendedTasks.Single(t => t.Type == "Mall").ParamsJson));
        Assert.False(mallTask["credit_fight"]?.GetValue<bool>());
        // Runtime-only downgrade: the persisted user choice is untouched.
        Assert.True(profile.TaskQueue[1].Params["credit_fight"]?.GetValue<bool>());
    }

    [Fact]
    public async Task QueueEnabledTasks_MallCreditFight_GuardSkipsDaysDisabledByWeeklySchedule()
    {
        // Frozen WPF parity: only schedule-enabled days are evaluated. With the
        // plan [Pormpt1, current/last] and the weekly schedule limited to Monday
        // (Pormpt1's only open day), no enabled day resolves to current/last, so
        // credit_fight stays enabled even though today's persisted stage resolved
        // to current/last.
        await using var fixture = await TestFixture.CreateAsync("en-us");
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.Values["ClientType"] = "Official";
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Fight",
            Name = "fight",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["stage"] = FightStageSelection.CurrentOrLast,
                ["_ui_stage_plan"] = new JsonArray("Pormpt1", FightStageSelection.CurrentOrLast),
                ["_ui_use_alternate_stage"] = true,
                ["_ui_use_weekly_schedule"] = true,
                ["_ui_weekly_schedule_sunday"] = false,
                ["_ui_weekly_schedule_monday"] = true,
                ["_ui_weekly_schedule_tuesday"] = false,
                ["_ui_weekly_schedule_wednesday"] = false,
                ["_ui_weekly_schedule_thursday"] = false,
                ["_ui_weekly_schedule_friday"] = false,
                ["_ui_weekly_schedule_saturday"] = false,
                ["medicine"] = 0,
                ["stone"] = 0,
                ["times"] = 1,
                ["series"] = 1,
            },
        });
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Mall",
            Name = "mall",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["credit_fight"] = true,
                ["credit_fight_once_a_day"] = false,
            },
        });

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();

        // The weekly schedule may disable the Fight task today, so locate the
        // Mall append by type instead of queue position.
        Assert.True(queueResult.Success);
        var mallTask = Assert.IsType<JsonObject>(JsonNode.Parse(fixture.Bridge.AppendedTasks.Single(t => t.Type == "Mall").ParamsJson));
        Assert.True(mallTask["credit_fight"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GetStartPrecheckWarnings_ReportsMallCreditFightDowngrade_WhenPlanResolvesToCurrentOrLastOnAnyEnabledWeekday()
    {
        // The warning path must use the same week-aggregated guard as compilation.
        await using var fixture = await TestFixture.CreateAsync("en-us");
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.Values["ClientType"] = "Official";
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Fight",
            Name = "fight",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["stage"] = "Pormpt1",
                ["_ui_stage_plan"] = new JsonArray("Pormpt1", FightStageSelection.CurrentOrLast),
                ["_ui_use_alternate_stage"] = true,
                ["medicine"] = 0,
                ["stone"] = 0,
                ["times"] = 1,
                ["series"] = 1,
            },
        });
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Mall",
            Name = "mall",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["credit_fight"] = true,
                ["credit_fight_once_a_day"] = false,
            },
        });

        var warnings = await fixture.TaskQueue.GetStartPrecheckWarningsAsync();

        Assert.True(warnings.Success);
        Assert.Contains(warnings.Value ?? [], warning => warning.Message.Contains("credit fight", StringComparison.OrdinalIgnoreCase));
        Assert.True(profile.TaskQueue[1].Params["credit_fight"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GetStartPrecheckWarnings_ReportsMallCreditFightDowngrade_WithoutMutatingTaskParams()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Fight",
            Name = "fight",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["stage"] = FightStageSelection.CurrentOrLast,
                ["medicine"] = 0,
                ["stone"] = 0,
                ["times"] = 1,
                ["series"] = 1,
            },
        });
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Mall",
            Name = "mall",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["credit_fight"] = true,
            },
        });

        var warnings = await fixture.TaskQueue.GetStartPrecheckWarningsAsync();
        Assert.True(warnings.Success);
        var warning = Assert.Single(warnings.Value ?? []);
        Assert.Equal(UiErrorCode.MallCreditFightDowngraded, warning.Code);
        Assert.Contains("Disabled Mall credit fight", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Current/Last", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(warning.Blocking);
        Assert.True(profile.TaskQueue[1].Params["credit_fight"]?.GetValue<bool>());
    }

    [Fact]
    public async Task TaskQueuePage_StartAsync_CurrentOrLastFightStage_AppliesRuntimeDowngradeWithoutPersistingIt()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Fight",
            Name = "fight",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["stage"] = FightStageSelection.CurrentOrLast,
                ["medicine"] = 0,
                ["stone"] = 0,
                ["times"] = 1,
                ["series"] = 1,
            },
        });
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Mall",
            Name = "mall",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["credit_fight"] = true,
            },
        });

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        var readyAdbPath = await TestConnectionFixtureSupport.PrepareReadyRuntimeAsync(fixture.Root, fixture.Config, "taskmodule-b-warning-ready");
        Assert.True((await TestConnectionFixtureSupport.ConnectReadyAsync(fixture.Runtime.ConnectFeatureService, readyAdbPath)).Success);

        await vm.StartAsync();

        Assert.True(vm.IsRunning);
        Assert.True(profile.TaskQueue[1].Params["credit_fight"]?.GetValue<bool>());
        Assert.Equal(2, fixture.Bridge.AppendedTasks.Count);

        var eventLog = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "debug", "avalonia-ui-events.log"));
        Assert.Contains("TaskQueue.Start.PrecheckWarning", eventLog, StringComparison.Ordinal);
        Assert.Contains("Disabled Mall credit fight", eventLog, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Current/Last", eventLog, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mall_ListNormalization_PreservesDuplicatesAndCaseVariantsLikeFrozenWpf()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        await fixture.TaskQueue.AddTaskAsync("Mall", "mall");

        var vm = new MallModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        var taskParams = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(taskParams.Success);
        await vm.BindAsync(0, taskParams.Value!, CancellationToken.None);

        vm.BuyFirstText = "A; a ;B；B;; ";
        vm.BlacklistText = "X;x; y ;Y；";
        var flush = await vm.FlushPendingChangesAsync();
        Assert.True(flush);

        var latest = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(latest.Success);
        var buyFirst = latest.Value?["buy_first"] as JsonArray;
        var blacklist = latest.Value?["blacklist"] as JsonArray;
        Assert.NotNull(buyFirst);
        Assert.NotNull(blacklist);
        // Frozen WPF stores the Mall list text verbatim (the setter only replaces
        // fullwidth semicolons and trims the outer text), so duplicates and case
        // variants the user typed survive; only trimming and empty-entry removal
        // are applied.
        Assert.Equal(new[] { "A", "a", "B", "B" }, buyFirst!.Select(x => x?.GetValue<string>()).ToArray());
        Assert.Equal(new[] { "X", "x", "y", "Y" }, blacklist!.Select(x => x?.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task QueueEnabledTasks_MallLists_PreserveCaseVariantDuplicates_LikeFrozenWpf()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Mall", "mall")).Success);
        fixture.Config.CurrentConfig.Profiles["Default"].Values["ClientType"] = "YoStarEN";

        var update = await fixture.TaskQueue.UpdateTaskParamsAsync(
            0,
            new JsonObject
            {
                ["credit_fight"] = false,
                ["credit_fight_once_a_day"] = true,
                ["visit_friends"] = true,
                ["visit_friends_once_a_day"] = false,
                ["shopping"] = true,
                ["buy_first"] = new JsonArray("lmd", "LMD", "lmd"),
                ["blacklist"] = new JsonArray("gavial", "Gavial", "gavial"),
            },
            persistImmediately: true);
        Assert.True(update.Success);

        var result = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(result.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var parameters = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));

        // Core matches goods names with a case-sensitive substring test
        // (CreditShopImageAnalyzer.match_required_index; the OCR equivalence
        // classes never fold case generally), so case-variant duplicates are
        // distinct entries and must survive compilation like in frozen WPF.
        Assert.Equal(
            ["lmd", "LMD"],
            parameters["buy_first"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray());
        // The blacklist keeps the WPF Union semantics (f244924): exact
        // duplicates collapse through the ordinal Union with the client-type
        // operator blacklist, while case variants survive.
        Assert.Equal(
            ["gavial", "Gavial", "Courier", "Dur-nar"],
            parameters["blacklist"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray());

        // Accepted divergence (same class as the dropped empty split entries):
        // frozen WPF also emits exact duplicates in buy_first
        // ("lmd;LMD;lmd" -> ["lmd","LMD","lmd"]), while MAAUnified collapses
        // them at the Core boundary. Core match_required_index returns the first
        // matching index, so trailing exact duplicates never change the
        // white-list priority order, and blacklist membership is set semantics;
        // the compiled Core behavior is identical.

        // Compilation must not mutate the persisted profile lists.
        var persisted = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(persisted.Success);
        Assert.Equal(
            ["lmd", "LMD", "lmd"],
            persisted.Value!["buy_first"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray());
        Assert.Equal(
            ["gavial", "Gavial", "gavial"],
            persisted.Value!["blacklist"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task MallDuplicateLists_SurviveStartupAndSaveNormalization_LikeFrozenWpf()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Mall", "mall")).Success);

        var update = await fixture.TaskQueue.UpdateTaskParamsAsync(
            0,
            new JsonObject
            {
                ["credit_fight"] = false,
                ["credit_fight_once_a_day"] = true,
                ["visit_friends"] = true,
                ["visit_friends_once_a_day"] = false,
                ["shopping"] = true,
                ["buy_first"] = new JsonArray("Gavial", "gavial"),
                ["blacklist"] = new JsonArray("LMD", "LMD", "lmd"),
            },
            persistImmediately: true);
        Assert.True(update.Success);

        var log = new UiLogService();
        var reloaded = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(fixture.Root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            log,
            fixture.Root);
        await reloaded.LoadOrBootstrapAsync();

        var profile = reloaded.CurrentConfig.Profiles[reloaded.CurrentConfig.CurrentProfile];
        var mall = profile.TaskQueue[0];
        // Startup/save normalization must not fold the stored duplicates: frozen
        // WPF keeps the list text verbatim, and a case-sensitive Core match
        // could depend on the variant that an OrdinalIgnoreCase dedup dropped.
        Assert.Equal(
            ["Gavial", "gavial"],
            mall.Params!["buy_first"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray());
        Assert.Equal(
            ["LMD", "LMD", "lmd"],
            mall.Params!["blacklist"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task QueueEnabledTasks_MallDailyOnce_CompilesRuntimeFlags_WithoutMutatingProfileParams()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Mall", "mall")).Success);

        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.Values["ClientType"] = "Official";

        var today = MallDailyResetHelper.GetCurrentYjDateString(DateTime.UtcNow, "Official");
        var update = await fixture.TaskQueue.UpdateTaskParamsAsync(
            0,
            new JsonObject
            {
                ["credit_fight"] = true,
                ["credit_fight_once_a_day"] = true,
                ["_ui_mall_credit_fight_last_time"] = today,
                ["formation_index"] = 0,
                ["visit_friends"] = true,
                ["visit_friends_once_a_day"] = true,
                ["_ui_mall_visit_friends_last_time"] = today,
                ["shopping"] = true,
                ["buy_first"] = new JsonArray("Recruitment Permit"),
                ["blacklist"] = new JsonArray("Carbon"),
                ["force_shopping_if_credit_full"] = false,
                ["only_buy_discount"] = false,
                ["reserve_max_credit"] = false,
            },
            persistImmediately: true);
        Assert.True(update.Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(queueResult.Success);
        Assert.Equal(1, queueResult.Value);

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var appendedParams = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.False(appendedParams["credit_fight"]?.GetValue<bool>());
        Assert.False(appendedParams["visit_friends"]?.GetValue<bool>());
        Assert.False(appendedParams.ContainsKey("credit_fight_once_a_day"));
        Assert.False(appendedParams.ContainsKey("visit_friends_once_a_day"));
        Assert.False(appendedParams.ContainsKey("_ui_mall_credit_fight_last_time"));
        Assert.False(appendedParams.ContainsKey("_ui_mall_visit_friends_last_time"));

        var persisted = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(persisted.Success);
        Assert.True(persisted.Value?["credit_fight"]?.GetValue<bool>());
        Assert.True(persisted.Value?["visit_friends"]?.GetValue<bool>());
    }

    [Fact]
    public async Task QueueEnabledTasks_MallDailyOnce_LooseMarkerString_IsTreatedAsAvailableLikeFrozenWpf()
    {
        // Frozen WPF MallTask.IsCreditFightAvailable/IsVisitFriendsAvailable only accept
        // DateTime.ParseExact("yyyy/MM/dd HH:mm:ss"); any other string is a parse failure
        // and means "available today". A hand-edited date-only marker for the current YJ
        // date must therefore not downgrade credit_fight/visit_friends.
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Mall", "mall")).Success);

        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.Values["ClientType"] = "Official";

        // Date-only form of the current YJ date: parseable by DateTime.TryParse but not
        // by the frozen WPF ParseExact marker format.
        var looseToday = MallDailyResetHelper.GetCurrentYjDateString(DateTime.UtcNow, "Official")[..10];
        var update = await fixture.TaskQueue.UpdateTaskParamsAsync(
            0,
            new JsonObject
            {
                ["credit_fight"] = true,
                ["credit_fight_once_a_day"] = true,
                ["_ui_mall_credit_fight_last_time"] = looseToday,
                ["visit_friends"] = true,
                ["visit_friends_once_a_day"] = true,
                ["_ui_mall_visit_friends_last_time"] = looseToday,
                ["shopping"] = false,
            },
            persistImmediately: true);
        Assert.True(update.Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(queueResult.Success);

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var appendedParams = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.True(appendedParams["credit_fight"]?.GetValue<bool>());
        Assert.True(appendedParams["visit_friends"]?.GetValue<bool>());
    }

    [Theory]
    [InlineData("2026/10/02 00:00:00", false)] // exact marker for the current YJ day
    [InlineData("2026/10/02 03:04:05", false)] // exact marker, non-midnight time today
    [InlineData("2026-10-02 00:00:00", false)] // WPF normalizes '-' to '/' before ParseExact
    [InlineData("2026/10/01 00:00:00", true)] // previous YJ day
    [InlineData("2026/10/02", true)] // date-only: WPF ParseExact fails -> available
    [InlineData("10/02/2026 00:00:00", true)] // other layout: parse failure -> available
    [InlineData(" 2026/10/02 00:00:00 ", true)] // surrounding whitespace: parse failure -> available
    [InlineData("not a date", true)]
    [InlineData("", true)]
    public void MallDailyResetHelper_IsTaskAvailableToday_OnlyAcceptsExactWpfMarkerFormat(string marker, bool expectedAvailable)
    {
        // 2026-10-02T12:00:00Z with Official (+8, YJ day starts at 04:00) => YJ date 2026-10-02.
        var utcNow = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(expectedAvailable, MallDailyResetHelper.IsTaskAvailableToday(marker, "Official", utcNow));
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(99, 4)]
    [InlineData(-3, 0)]
    public async Task QueueEnabledTasks_MallFormationIndex_HandEditedOutOfRange_IsClampedToWpfUiDomain(int stored, int expected)
    {
        // Deliberate divergence from frozen WPF (which emits the raw CreditFightFormation):
        // a hand-edited out-of-range formation_index is clamped to the 0..4 domain the
        // frozen WPF combobox can produce. UI-reachable values pass through unchanged.
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Mall", "mall")).Success);

        var update = await fixture.TaskQueue.UpdateTaskParamsAsync(
            0,
            new JsonObject
            {
                ["credit_fight"] = false,
                ["visit_friends"] = true,
                ["visit_friends_once_a_day"] = false,
                ["formation_index"] = stored,
                ["shopping"] = false,
            },
            persistImmediately: true);
        Assert.True(update.Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(queueResult.Success);

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var appendedParams = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.Equal(expected, appendedParams["formation_index"]?.GetValue<int>());
    }

    [Fact]
    public async Task QueueEnabledTasks_MallBlacklist_UnionsClientTypeBlacklistLikeFrozenWpf()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Mall", "mall")).Success);
        fixture.Config.CurrentConfig.Profiles["Default"].Values["ClientType"] = "YoStarEN";

        var update = await fixture.TaskQueue.UpdateTaskParamsAsync(
            0,
            new JsonObject
            {
                ["credit_fight"] = false,
                ["credit_fight_once_a_day"] = true,
                ["visit_friends"] = true,
                ["visit_friends_once_a_day"] = false,
                ["shopping"] = true,
                ["buy_first"] = new JsonArray("Recruitment Permit"),
                ["blacklist"] = new JsonArray("My custom item", "Gavial"),
            },
            persistImmediately: true);
        Assert.True(update.Success);

        var result = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(result.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var parameters = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        // Frozen WPF MallSettingsUserControlModel.cs:239 unconditionally unions
        // the client-type operator blacklist; existing entries are not
        // duplicated and the names follow the client type, not the UI language.
        Assert.Equal(
            ["My custom item", "Gavial", "Courier", "Dur-nar"],
            parameters["blacklist"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray());

        // The runtime union must not mutate the persisted profile list.
        var persisted = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(persisted.Success);
        Assert.Equal(
            ["My custom item", "Gavial"],
            persisted.Value!["blacklist"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task QueueEnabledTasks_AwardEmitsExactlyFrozenWpfFields()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Award", "award")).Success);
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(
            0,
            new JsonObject
            {
                ["mail"] = true,
                ["recruit"] = true,
                ["unknown"] = "must-not-reach-core",
            })).Success);

        var result = await fixture.TaskQueue.QueueEnabledTasksAsync();

        Assert.True(result.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var parameters = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.Equal(
            ["award", "mail", "mining", "orundum", "recruit", "signinevent", "specialaccess"],
            parameters.Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.True(parameters["award"]?.GetValue<bool>());
        Assert.True(parameters["mail"]?.GetValue<bool>());
        Assert.True(parameters["recruit"]?.GetValue<bool>());
        Assert.False(parameters["signinevent"]?.GetValue<bool>());
    }

    [Fact]
    public async Task Award_SignInEvent_DefaultOffThenPersistsAndReachesCore()
    {
        // WPF dev-v2 (19699d8483) adds AsstAwardTask.SignInEvent; the checkbox lives in
        // AwardSettingsUserControl and the Core parameter is `signinevent`.
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Award", "award")).Success);

        var added = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.False(added["signinevent"]!.GetValue<bool>());

        var module = new AwardModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        await module.BindAsync(0, added);
        Assert.False(module.SignInEvent);

        module.SignInEvent = true;
        Assert.True(await module.FlushPendingChangesAsync());

        var persisted = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.True(persisted["signinevent"]!.GetValue<bool>());

        var result = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(result.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var parameters = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));
        Assert.True(parameters["signinevent"]!.GetValue<bool>());

        var rebound = new AwardModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        await rebound.BindAsync(0, persisted);
        Assert.True(rebound.SignInEvent);
    }

    [Fact]
    public async Task TaskQueuePage_SubTaskCompletedMall_UpdatesDailyExecutionMarkers()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Mall", "mall")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        await vm.ReloadTasksAsync();

        // Real Core shape: subtask is the C++ class name; the pipeline task name
        // lives in details.task (AbstractTask::basic_info / ProcessTask::run_task).
        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(
                22001,
                "SubTaskCompleted",
                """{"taskchain":"Mall","taskid":1,"class":"asst::ProcessTask","subtask":"ProcessTask","details":{"task":"EndOfActionThenStop","exec_times":1,"max_times":1,"action":"Stop","algorithm":"JustReturn","result":{}}}""",
                DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(
                22002,
                "SubTaskCompleted",
                """{"taskchain":"Mall","taskid":1,"class":"asst::ProcessTask","subtask":"ProcessTask","details":{"task":"VisitLimited","exec_times":2,"max_times":2,"action":"ClickSelf","algorithm":"OcrDetect","result":{}}}""",
                DateTimeOffset.UtcNow));

        var latest = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(latest.Success);

        var creditFightTime = latest.Value?["_ui_mall_credit_fight_last_time"]?.GetValue<string>() ?? string.Empty;
        var visitFriendsTime = latest.Value?["_ui_mall_visit_friends_last_time"]?.GetValue<string>() ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(creditFightTime));
        Assert.False(string.IsNullOrWhiteSpace(visitFriendsTime));

        Assert.True(DateTime.TryParseExact(
            creditFightTime,
            "yyyy/MM/dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out _));
        Assert.True(DateTime.TryParseExact(
            visitFriendsTime,
            "yyyy/MM/dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out _));
    }

    [Fact]
    public async Task TaskQueuePage_MallDailyMarker_UpdatesOnlyCurrentProfile()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Mall", "default-mall")).Success);
        var config = fixture.Config.CurrentConfig;
        config.Profiles["Second"] = new UnifiedProfile();
        config.Profiles["Second"].TaskQueue.Add(new UnifiedTaskItem
        {
            Type = TaskModuleTypes.Mall,
            Name = "second-mall",
            Params = TaskModuleParameterDefaults.CreateMallDefaults("en-us"),
        });

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        await vm.ReloadTasksAsync();
        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(
                22003,
                "SubTaskCompleted",
                """{"taskchain":"Mall","taskid":1,"class":"asst::ProcessTask","subtask":"ProcessTask","details":{"task":"EndOfActionThenStop","exec_times":1,"max_times":1,"action":"Stop","algorithm":"JustReturn","result":{}}}""",
                DateTimeOffset.UtcNow));

        Assert.NotNull(config.Profiles["Default"].TaskQueue[0].Params["_ui_mall_credit_fight_last_time"]);
        Assert.Null(config.Profiles["Second"].TaskQueue[0].Params["_ui_mall_credit_fight_last_time"]);
    }

    [Fact]
    public async Task TaskQueuePage_SubTaskCompletedMall_SyntheticTopLevelMarkerShape_DoesNotWriteMarkers()
    {
        // Core never reports the pipeline task name at the top-level subtask field;
        // such a payload must not be mistaken for a completion callback (frozen WPF
        // AsstProxy.ProcSubTaskCompleted requires subtask == "ProcessTask").
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Mall", "mall")).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        await vm.ReloadTasksAsync();

        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(
                22004,
                "SubTaskCompleted",
                """{"taskchain":"Mall","taskid":1,"subtask":"EndOfActionThenStop","details":{"task":"EndOfActionThenStop"}}""",
                DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(
                22005,
                "SubTaskCompleted",
                """{"taskchain":"Mall","taskid":1,"subtask":"ProcessTask","details":{"task":"SomeOtherPipelineTask"}}""",
                DateTimeOffset.UtcNow));

        var latest = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(latest.Success);
        Assert.Null(latest.Value?["_ui_mall_credit_fight_last_time"]);
        Assert.Null(latest.Value?["_ui_mall_visit_friends_last_time"]);
    }

    [Fact]
    public async Task TaskQueuePage_MallDailyMarker_RealCoreShape_SkipsSameDayAndRunsAgainNextYjDay()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Mall", "mall")).Success);

        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.Values["ClientType"] = "Official";

        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(
            0,
            new JsonObject
            {
                ["credit_fight"] = true,
                ["credit_fight_once_a_day"] = true,
                ["formation_index"] = 0,
                ["visit_friends"] = true,
                ["visit_friends_once_a_day"] = true,
                ["shopping"] = true,
                ["buy_first"] = new JsonArray("Recruitment Permit"),
                ["blacklist"] = new JsonArray("Carbon"),
                ["force_shopping_if_credit_full"] = false,
                ["only_buy_discount"] = false,
                ["reserve_max_credit"] = false,
            },
            persistImmediately: true)).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        await vm.ReloadTasksAsync();

        // Real Core SubTaskCompleted callbacks for the credit fight and visit chains.
        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(
                22006,
                "SubTaskCompleted",
                """{"taskchain":"Mall","taskid":1,"class":"asst::ProcessTask","subtask":"ProcessTask","details":{"task":"EndOfActionThenStop","exec_times":1,"max_times":1,"action":"Stop","algorithm":"JustReturn","result":{}}}""",
                DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(
                22007,
                "SubTaskCompleted",
                """{"taskchain":"Mall","taskid":1,"class":"asst::ProcessTask","subtask":"ProcessTask","details":{"task":"VisitNextBlack","exec_times":3,"max_times":3,"action":"ClickSelf","algorithm":"JustReturn","result":{}}}""",
                DateTimeOffset.UtcNow));

        var queueToday = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(queueToday.Success);
        var todayParams = Assert.IsType<JsonObject>(JsonNode.Parse(Assert.Single(fixture.Bridge.AppendedTasks).ParamsJson));
        Assert.False(todayParams["credit_fight"]?.GetValue<bool>());
        Assert.False(todayParams["visit_friends"]?.GetValue<bool>());
        Assert.False(todayParams.ContainsKey("_ui_mall_credit_fight_last_time"));
        Assert.False(todayParams.ContainsKey("_ui_mall_visit_friends_last_time"));

        // The runtime downgrade must not overwrite the stored user intent.
        var persisted = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(persisted.Value?["credit_fight"]?.GetValue<bool>());
        Assert.True(persisted.Value?["visit_friends"]?.GetValue<bool>());

        // A marker from a previous YJ day makes both available again.
        var previousDay = MallDailyResetHelper.GetPreviousYjDateString(DateTime.UtcNow, "Official");
        profile.TaskQueue[0].Params!["_ui_mall_credit_fight_last_time"] = previousDay;
        profile.TaskQueue[0].Params!["_ui_mall_visit_friends_last_time"] = previousDay;

        fixture.Bridge.AppendedTasks.Clear();
        var queueNextDay = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(queueNextDay.Success);
        var nextDayParams = Assert.IsType<JsonObject>(JsonNode.Parse(Assert.Single(fixture.Bridge.AppendedTasks).ParamsJson));
        Assert.True(nextDayParams["credit_fight"]?.GetValue<bool>());
        Assert.True(nextDayParams["visit_friends"]?.GetValue<bool>());
    }

    [Fact]
    public async Task PostAction_Load_MigratesLegacyBitmaskToProfileStructuredConfig()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.PostActions] = 136; // ExitSelf + Sleep

        var load = await fixture.PostAction.LoadAsync();
        Assert.True(load.Success);
        Assert.NotNull(load.Value);
        Assert.True(load.Value!.ExitSelf);
        Assert.True(load.Value.Sleep);
        Assert.False(load.Value.Shutdown);

        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        Assert.True(profile.Values.ContainsKey("TaskQueue.PostAction"));
        Assert.False(profile.Values.ContainsKey(ConfigurationKeys.PostActions));
        Assert.False(fixture.Config.CurrentConfig.GlobalValues.ContainsKey("TaskQueue.PostAction"));
        Assert.False(fixture.Config.CurrentConfig.GlobalValues.ContainsKey(ConfigurationKeys.PostActions));
    }

    [Fact]
    public async Task PostAction_Save_WritesOnlyStructuredConfigKeyToCurrentProfile()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues[ConfigurationKeys.PostActions] = 64;

        var save = await fixture.PostAction.SaveAsync(new PostActionConfig
        {
            ExitSelf = true,
            Shutdown = true,
        });

        Assert.True(save.Success);
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        Assert.True(profile.Values.ContainsKey("TaskQueue.PostAction"));
        Assert.False(profile.Values.ContainsKey(ConfigurationKeys.PostActions));
        Assert.False(fixture.Config.CurrentConfig.GlobalValues.ContainsKey("TaskQueue.PostAction"));
        Assert.False(fixture.Config.CurrentConfig.GlobalValues.ContainsKey(ConfigurationKeys.PostActions));
    }

    [Fact]
    public async Task PostAction_LoadSave_RoundTrip_WithCommands_ProfileScoped()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var save = await fixture.PostAction.SaveAsync(new PostActionConfig
        {
            ExitArknights = true,
            ExitSelf = true,
            Commands = new PostActionCommandConfig
            {
                ExitArknights = "adb shell am force-stop com.hypergryph.arknights",
                ExitSelf = "pkill MAAUnified",
            },
        });

        Assert.True(save.Success);

        var load = await fixture.PostAction.LoadAsync();
        Assert.True(load.Success);
        Assert.NotNull(load.Value);
        Assert.Equal("adb shell am force-stop com.hypergryph.arknights", load.Value!.Commands.ExitArknights);
        Assert.Equal("pkill MAAUnified", load.Value.Commands.ExitSelf);
        Assert.True(load.Value.ExitArknights);
        Assert.True(load.Value.ExitSelf);

        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        Assert.True(profile.Values.ContainsKey("TaskQueue.PostAction"));
        Assert.False(fixture.Config.CurrentConfig.GlobalValues.ContainsKey("TaskQueue.PostAction"));
    }

    [Fact]
    public async Task PostAction_Load_ParsesLegacyStructuredProfileConfig_WithHistoricalPropertyNames()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        profile.Values["TaskQueue.PostAction"] = JsonNode.Parse(
            """
            {
              "ExitSelf": true,
              "sleep": true,
              "Commands": {
                "ExitSelf": "pkill MAAUnified"
              }
            }
            """);

        var load = await fixture.PostAction.LoadAsync();

        Assert.True(load.Success);
        Assert.NotNull(load.Value);
        Assert.True(load.Value!.ExitSelf);
        Assert.True(load.Value.Sleep);
        Assert.Equal("pkill MAAUnified", load.Value.Commands.ExitSelf);
    }

    [Fact]
    public async Task PostAction_Load_MigratesGlobalStructuredConfig_WithHistoricalPropertyNames_ToCurrentProfile()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues["TaskQueue.PostAction"] = JsonNode.Parse(
            """
            {
              "ExitEmulator": true,
              "Commands": {
                "ExitEmulator": "echo close-emulator"
              }
            }
            """);

        var load = await fixture.PostAction.LoadAsync();

        Assert.True(load.Success);
        Assert.NotNull(load.Value);
        Assert.True(load.Value!.ExitEmulator);
        Assert.Equal("echo close-emulator", load.Value.Commands.ExitEmulator);

        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        Assert.True(profile.Values.ContainsKey("TaskQueue.PostAction"));
        Assert.False(fixture.Config.CurrentConfig.GlobalValues.ContainsKey("TaskQueue.PostAction"));
        var persisted = PostActionConfig.FromJson(profile.Values["TaskQueue.PostAction"]);
        Assert.True(persisted.ExitEmulator);
    }

    [Fact]
    public async Task PostAction_ValidateSelection_NativeActionsDoNotDependOnFallbackExecutorCapabilities()
    {
        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        var executor = new TestPostActionExecutorService(new PostActionCapabilityMatrix(
            ExitArknights: new PlatformCapabilityStatus(false, "requires native provider", Provider: "test"),
            BackToAndroidHome: new PlatformCapabilityStatus(false, "requires native provider", Provider: "test"),
            ExitEmulator: supported,
            ExitSelf: new PlatformCapabilityStatus(false, "requires native provider", Provider: "test"),
            Hibernate: supported,
            Shutdown: supported,
            Sleep: supported));
        await using var fixture = await TestFixture.CreateAsync(
            executor: executor,
            bridge: new FakeBridge
            {
                SupportsNativeBackToHome = true,
                SupportsNativeCloseDown = true,
            },
            appLifecycleService: new TestAppLifecycleService(supportsExit: true));

        var preview = await fixture.PostAction.ValidateSelectionAsync(new PostActionConfig
        {
            ExitArknights = true,
            BackToAndroidHome = true,
            ExitSelf = true,
        });
        Assert.True(preview.Success);
        Assert.DoesNotContain(nameof(PostActionType.ExitArknights), preview.Value!.UnsupportedActions);
        Assert.DoesNotContain(nameof(PostActionType.BackToAndroidHome), preview.Value.UnsupportedActions);
        Assert.DoesNotContain(nameof(PostActionType.ExitSelf), preview.Value.UnsupportedActions);
    }

    [Fact]
    public async Task PostAction_ValidateSelection_CommandFields_DoNotEnableLegacyCommandLineFallback()
    {
        var unsupported = new PlatformCapabilityStatus(false, "unsupported", Provider: "test");
        PostActionExecutorRequest? capturedRequest = null;
        var executor = new TestPostActionExecutorService(
            new PostActionCapabilityMatrix(
                ExitArknights: unsupported,
                BackToAndroidHome: unsupported,
                ExitEmulator: unsupported,
                ExitSelf: unsupported,
                Hibernate: unsupported,
                Shutdown: unsupported,
                Sleep: unsupported),
            matrixSelector: request =>
            {
                capturedRequest = request;
                var hasLegacyCommand = !string.IsNullOrWhiteSpace(request?.CommandLine);
                return new PostActionCapabilityMatrix(
                    ExitArknights: new PlatformCapabilityStatus(hasLegacyCommand, "command fallback", Provider: "command"),
                    BackToAndroidHome: new PlatformCapabilityStatus(hasLegacyCommand, "command fallback", Provider: "command"),
                    ExitEmulator: new PlatformCapabilityStatus(hasLegacyCommand, "command fallback", Provider: "command"),
                    ExitSelf: new PlatformCapabilityStatus(hasLegacyCommand, "command fallback", Provider: "command"),
                    Hibernate: unsupported,
                    Shutdown: unsupported,
                    Sleep: unsupported);
            });

        await using var fixture = await TestFixture.CreateAsync(executor: executor);

        var preview = await fixture.PostAction.ValidateSelectionAsync(new PostActionConfig
        {
            ExitSelf = true,
            Commands = new PostActionCommandConfig
            {
                ExitSelf = "echo close-maa",
            },
        });

        Assert.True(preview.Success);
        Assert.NotNull(capturedRequest);
        Assert.True(string.IsNullOrWhiteSpace(capturedRequest!.CommandLine));
        Assert.Contains(nameof(PostActionType.ExitSelf), preview.Value!.UnsupportedActions);
    }

    [Fact]
    public async Task PostAction_GetCapabilityPreview_ReportsUnsupportedActionsOnNoOpPlatform()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var preview = await fixture.PostAction.GetCapabilityPreviewAsync(new PostActionConfig
        {
            ExitSelf = true,
            Shutdown = true,
        });

        Assert.True(preview.Success);
        Assert.NotNull(preview.Value);
        Assert.Contains(nameof(PostActionType.ExitSelf), preview.Value!.UnsupportedActions);
        Assert.Contains(nameof(PostActionType.Shutdown), preview.Value.UnsupportedActions);
    }

    [Fact]
    public async Task PostAction_GetCapabilityPreview_ExitEmulatorSelection_IsWindowsOnlyAfterNormalization()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var preview = await fixture.PostAction.GetCapabilityPreviewAsync(new PostActionConfig
        {
            ExitEmulator = true,
        });

        Assert.True(preview.Success);
        Assert.NotNull(preview.Value);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(nameof(PostActionType.ExitEmulator), preview.Value!.UnsupportedActions);
            return;
        }

        Assert.DoesNotContain(nameof(PostActionType.ExitEmulator), preview.Value!.UnsupportedActions);
    }

    [Fact]
    public async Task PostAction_GetCapabilityPreview_HibernateSelection_FollowsPlatformNormalization()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var preview = await fixture.PostAction.GetCapabilityPreviewAsync(new PostActionConfig
        {
            Hibernate = true,
        });

        Assert.True(preview.Success);
        Assert.NotNull(preview.Value);
        if (OperatingSystem.IsMacOS())
        {
            Assert.DoesNotContain(nameof(PostActionType.Hibernate), preview.Value!.UnsupportedActions);
            Assert.Contains(nameof(PostActionType.Sleep), preview.Value.UnsupportedActions);
            return;
        }

        Assert.Contains(nameof(PostActionType.Hibernate), preview.Value!.UnsupportedActions);
        Assert.DoesNotContain(nameof(PostActionType.Sleep), preview.Value.UnsupportedActions);
    }

    [Fact]
    public async Task PostAction_Execute_UsesNativeHandlers_AndConfirmedPowerAction()
    {
        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        var executor = new TestPostActionExecutorService(new PostActionCapabilityMatrix(
            ExitArknights: new PlatformCapabilityStatus(false, "requires native provider", Provider: "test"),
            BackToAndroidHome: new PlatformCapabilityStatus(false, "requires native provider", Provider: "test"),
            ExitEmulator: supported,
            ExitSelf: new PlatformCapabilityStatus(false, "requires native provider", Provider: "test"),
            Hibernate: supported,
            Shutdown: supported,
            Sleep: supported));
        var bridge = new FakeBridge
        {
            SupportsNativeBackToHome = true,
            SupportsNativeCloseDown = true,
        };
        var appLifecycle = new TestAppLifecycleService(supportsExit: true);

        await using var fixture = await TestFixture.CreateAsync(
            executor: executor,
            bridge: bridge,
            appLifecycleService: appLifecycle,
            promptService: new TestPostActionPromptService(UiOperationResult.Ok("confirmed")));

        var execute = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext("AllTasksCompleted", true, RunId: "run-b1", TaskIndex: 2),
            new PostActionConfig
            {
                ExitArknights = true,
                BackToAndroidHome = true,
                ExitSelf = true,
                Sleep = true,
            });

        Assert.True(execute.Success, execute.Message);
        Assert.Equal(1, bridge.BackToHomeCallCount);
        Assert.Equal(1, bridge.StartCloseDownCallCount);
        // WPF parity (CheckAfterCompleted): ExitSelf is gated on !(Hibernate || Shutdown || Sleep),
        // so the selected Sleep action suppresses ExitSelf even though both are configured.
        Assert.Equal(0, appLifecycle.ExitCallCount);
        Assert.Contains(PostActionType.Sleep, executor.ExecutedActions);
        Assert.DoesNotContain(executor.ExecutedActions, action => action == PostActionType.ExitSelf);
        Assert.DoesNotContain(executor.ExecutedActions, action => action == PostActionType.ExitArknights);
        Assert.DoesNotContain(executor.ExecutedActions, action => action == PostActionType.BackToAndroidHome);

        var eventLog = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "debug", "avalonia-ui-events.log"));
        Assert.Contains("runId=run-b1", eventLog);
        Assert.Contains("action=ExitArknights", eventLog);
        Assert.Contains("action=BackToAndroidHome", eventLog);
        Assert.DoesNotContain("action=ExitSelf", eventLog, StringComparison.Ordinal);
        Assert.Contains("action=Sleep", eventLog);
    }

    [Fact]
    public async Task PostAction_Execute_PowerActionCancelled_SkipsOnlyPowerAction()
    {
        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        var executor = new TestPostActionExecutorService(new PostActionCapabilityMatrix(
            ExitArknights: supported,
            BackToAndroidHome: supported,
            ExitEmulator: supported,
            ExitSelf: supported,
            Hibernate: supported,
            Shutdown: supported,
            Sleep: supported));
        await using var fixture = await TestFixture.CreateAsync(
            executor: executor,
            promptService: new TestPostActionPromptService(UiOperationResult.Cancelled("cancelled")));

        var execute = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext("AllTasksCompleted", true, RunId: "run-b1-cancel"),
            new PostActionConfig
            {
                Sleep = true,
                BackToAndroidHome = true,
            });

        Assert.True(execute.Success, execute.Message);
        Assert.DoesNotContain(executor.ExecutedActions, action => action == PostActionType.Sleep);
        Assert.Contains(PostActionType.BackToAndroidHome, executor.ExecutedActions);

        var eventLog = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "debug", "avalonia-ui-events.log"));
        Assert.Contains("action=Sleep", eventLog);
        Assert.Contains($"errorCode={UiErrorCode.PostActionCancelled}", eventLog);
        Assert.Contains("action=BackToAndroidHome", eventLog);
    }

    [Fact]
    public async Task PostAction_Execute_ExitSelfWithSystemAction_DoesNotExitSelf_WpfParity()
    {
        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        var executor = new TestPostActionExecutorService(new PostActionCapabilityMatrix(
            ExitArknights: supported,
            BackToAndroidHome: supported,
            ExitEmulator: supported,
            ExitSelf: supported,
            Hibernate: supported,
            Shutdown: supported,
            Sleep: supported));
        var appLifecycle = new TestAppLifecycleService(supportsExit: true);
        var promptService = new TestPostActionPromptService(UiOperationResult.Ok("confirmed"));

        await using var fixture = await TestFixture.CreateAsync(
            executor: executor,
            appLifecycleService: appLifecycle,
            promptService: promptService);

        // WPF CheckAfterCompleted gates ExitSelf on !(Hibernate || Shutdown || Sleep).
        var execute = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext("AllTasksCompleted", true, RunId: "run-b-exitself-gate"),
            new PostActionConfig
            {
                ExitSelf = true,
                Sleep = true,
            });

        Assert.True(execute.Success, execute.Message);
        Assert.Equal(0, appLifecycle.ExitCallCount);
        Assert.Single(promptService.Requests);
        Assert.Contains(PostActionType.Sleep, executor.ExecutedActions);
        Assert.DoesNotContain(executor.ExecutedActions, action => action == PostActionType.ExitSelf);
    }

    [Fact]
    public async Task PostAction_Execute_IfNoOtherMaaWithOtherMaaProcess_ExitsSelfInsteadOfSystemAction()
    {
        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        var executor = new TestPostActionExecutorService(new PostActionCapabilityMatrix(
            ExitArknights: supported,
            BackToAndroidHome: supported,
            ExitEmulator: supported,
            ExitSelf: supported,
            Hibernate: supported,
            Shutdown: supported,
            Sleep: supported));
        var appLifecycle = new TestAppLifecycleService(supportsExit: true);
        var promptService = new TestPostActionPromptService(UiOperationResult.Ok("confirmed"));

        await using var fixture = await TestFixture.CreateAsync(
            executor: executor,
            appLifecycleService: appLifecycle,
            promptService: promptService,
            hasOtherMaaProcess: () => true);

        // WPF CheckAfterCompleted: IfNoOtherMaa + another MAA process replaces each configured
        // system-level action with Bootstrapper.Shutdown(), regardless of the ExitSelf checkbox.
        var execute = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext("AllTasksCompleted", true, RunId: "run-b-ifnoother-substitute"),
            new PostActionConfig
            {
                IfNoOtherMaa = true,
                Shutdown = true,
            });

        Assert.True(execute.Success, execute.Message);
        Assert.Equal(1, appLifecycle.ExitCallCount);
        Assert.DoesNotContain(executor.ExecutedActions, action => action == PostActionType.Shutdown);
        // The skipped system-level action must not trigger the power-action prompt.
        Assert.Empty(promptService.Requests);

        var eventLog = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "debug", "avalonia-ui-events.log"));
        Assert.Contains("action=Shutdown", eventLog);
        Assert.Contains("action=ExitSelf", eventLog);
    }

    [Fact]
    public async Task PostAction_Execute_IfNoOtherMaaWithoutOtherMaaProcess_RunsSystemAction()
    {
        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        var executor = new TestPostActionExecutorService(new PostActionCapabilityMatrix(
            ExitArknights: supported,
            BackToAndroidHome: supported,
            ExitEmulator: supported,
            ExitSelf: supported,
            Hibernate: supported,
            Shutdown: supported,
            Sleep: supported));
        var appLifecycle = new TestAppLifecycleService(supportsExit: true);
        var promptService = new TestPostActionPromptService(UiOperationResult.Ok("confirmed"));

        await using var fixture = await TestFixture.CreateAsync(
            executor: executor,
            appLifecycleService: appLifecycle,
            promptService: promptService,
            hasOtherMaaProcess: () => false);

        var execute = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext("AllTasksCompleted", true, RunId: "run-b-ifnoother-normal"),
            new PostActionConfig
            {
                IfNoOtherMaa = true,
                Shutdown = true,
            });

        Assert.True(execute.Success, execute.Message);
        Assert.Equal(0, appLifecycle.ExitCallCount);
        Assert.Single(promptService.Requests);
        Assert.Contains(PostActionType.Shutdown, executor.ExecutedActions);
    }

    [Fact]
    public async Task PostAction_Execute_IfNoOtherMaaWithoutPowerAction_DoesNotBlockExitSelf()
    {
        var appLifecycle = new TestAppLifecycleService(supportsExit: true);
        await using var fixture = await TestFixture.CreateAsync(
            appLifecycleService: appLifecycle,
            hasOtherMaaProcess: () => false);

        var execute = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext("AllTasksCompleted", true, RunId: "run-b1-ifnoother"),
            new PostActionConfig
            {
                IfNoOtherMaa = true,
                ExitSelf = true,
            });

        Assert.True(execute.Success, execute.Message);
        Assert.Equal(1, appLifecycle.ExitCallCount);

        var eventLog = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "debug", "avalonia-ui-events.log"));
        Assert.Contains("action=ExitSelf", eventLog);
        Assert.DoesNotContain("Skipped by IfNoOtherMaa", eventLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostAction_Execute_ExecutionFailure_RecordsError_NoCrash()
    {
        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        var failAction = OperatingSystem.IsWindows() ? PostActionType.ExitEmulator : PostActionType.ExitSelf;
        var executor = new TestPostActionExecutorService(
            new PostActionCapabilityMatrix(
                ExitArknights: supported,
                BackToAndroidHome: supported,
                ExitEmulator: supported,
                ExitSelf: supported,
                Hibernate: supported,
                Shutdown: supported,
                Sleep: supported),
            (action, _) => action == failAction
                ? PlatformOperation.Failed("test", "forced failure", PlatformErrorCodes.PostActionExecutionFailed)
                : PlatformOperation.NativeSuccess("test", "ok"));

        await using var fixture = await TestFixture.CreateAsync(executor: executor);
        var execute = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext("AllTasksCompleted", true, RunId: "run-b3", TaskIndex: 1),
            failAction == PostActionType.ExitEmulator
                ? new PostActionConfig { ExitEmulator = true }
                : new PostActionConfig { ExitSelf = true });

        Assert.False(execute.Success);
        Assert.Contains(UiErrorCode.PostActionExecutionFailed, execute.Error?.Code ?? string.Empty);

        var errorLog = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "debug", "avalonia-ui-errors.log"));
        Assert.Contains("runId=run-b3", errorLog);
        Assert.Contains($"action={failAction}", errorLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostAction_Execute_UnsupportedSelection_LogsDegradeReason()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var execute = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext("AllTasksCompleted", true, RunId: "run-b2"),
            new PostActionConfig
            {
                Shutdown = true,
            });

        Assert.True(execute.Success);
        Assert.Contains("Skipped", execute.Message);

        var eventLog = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "debug", "avalonia-ui-events.log"));
        Assert.Contains("runId=run-b2", eventLog);
        Assert.Contains("action=Shutdown", eventLog);
        Assert.Contains($"errorCode={UiErrorCode.PostActionUnsupported}", eventLog);
    }

    [Fact]
    public async Task PostActionModule_OnceMode_DoesNotPersistActionEdits_ButPersistsCommandConfig()
    {
        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        var executor = new TestPostActionExecutorService(new PostActionCapabilityMatrix(
            ExitArknights: supported,
            BackToAndroidHome: supported,
            ExitEmulator: supported,
            ExitSelf: supported,
            Hibernate: supported,
            Shutdown: supported,
            Sleep: supported));

        await using var fixture = await TestFixture.CreateAsync(executor: executor);
        await fixture.PostAction.SaveAsync(new PostActionConfig
        {
            Shutdown = true,
            ExitSelf = false,
            Commands = new PostActionCommandConfig
            {
                ExitSelf = "echo persist-old",
            },
        });

        var vm = new PostActionModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        await vm.InitializeAsync();
        Assert.True(vm.Shutdown);
        Assert.False(vm.ExitSelf);

        vm.Once = true;
        vm.Shutdown = false;
        vm.ExitSelf = true;
        vm.ExitSelfCommand = "echo persist-new";
        var flush = await vm.FlushPendingChangesAsync();
        Assert.True(flush);

        var persisted = await fixture.PostAction.LoadAsync();
        Assert.True(persisted.Success);
        Assert.True(persisted.Value!.Shutdown);
        Assert.False(persisted.Value.ExitSelf);
        Assert.Equal("echo persist-new", persisted.Value.Commands.ExitSelf);

        await vm.ReloadPersistentConfigAsync();
        Assert.False(vm.Once);
        Assert.True(vm.Shutdown);
        Assert.False(vm.ExitSelf);
        Assert.Equal("echo persist-new", vm.ExitSelfCommand);
    }

    [Fact]
    public async Task PostActionModule_IfNoOtherMaa_RuntimeConfig_OnlyAppliesWithPowerActions_AndDoesNotEnableExitEmulator()
    {
        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        var executor = new TestPostActionExecutorService(new PostActionCapabilityMatrix(
            ExitArknights: supported,
            BackToAndroidHome: supported,
            ExitEmulator: supported,
            ExitSelf: supported,
            Hibernate: supported,
            Shutdown: supported,
            Sleep: supported));

        await using var fixture = await TestFixture.CreateAsync(executor: executor);
        var vm = new PostActionModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        await vm.InitializeAsync();

        vm.ClearActions();
        vm.IfNoOtherMaa = true;
        var withoutPower = vm.BuildRuntimeConfig();
        Assert.False(withoutPower.IfNoOtherMaa);
        Assert.False(withoutPower.ExitSelf);
        Assert.False(withoutPower.ExitEmulator);

        vm.Sleep = true;
        vm.IfNoOtherMaa = true;
        Assert.False(vm.ExitEmulator);
        var withPower = vm.BuildRuntimeConfig();
        Assert.True(withPower.IfNoOtherMaa);
        Assert.True(withPower.ExitSelf);
        Assert.False(withPower.ExitEmulator);
    }

    [Fact]
    public async Task PostActionModule_OnNonWindows_PreservesStoredExitEmulator_WhenSavingOtherFields()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var fixture = await TestFixture.CreateAsync();
        var save = await fixture.PostAction.SaveAsync(new PostActionConfig
        {
            ExitEmulator = true,
            ExitSelf = true,
            Commands = new PostActionCommandConfig
            {
                ExitEmulator = "echo close-emulator",
            },
        });
        Assert.True(save.Success);

        var vm = new PostActionModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        await vm.InitializeAsync();

        Assert.False(vm.ShowExitEmulator);
        Assert.False(vm.ExitEmulator);
        Assert.Equal("echo close-emulator", vm.ExitEmulatorCommand);

        vm.Shutdown = true;
        vm.ExitSelf = true;
        var flush = await vm.FlushPendingChangesAsync();
        Assert.True(flush);

        var load = await fixture.PostAction.LoadAsync();
        Assert.True(load.Success);
        Assert.True(load.Value!.ExitEmulator);
        Assert.True(load.Value.Shutdown);
        Assert.Equal("echo close-emulator", load.Value.Commands.ExitEmulator);

        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        var persisted = PostActionConfig.FromJson(profile.Values["TaskQueue.PostAction"]);
        Assert.True(persisted.ExitEmulator);
        Assert.True(persisted.Shutdown);
    }

    [Fact]
    public async Task TaskQueuePage_CallbackRuntimeMapping_SubTaskStartCompleted()
    {
        await using var fixture = await TestFixture.CreateAsync();
        await fixture.TaskQueue.AddTaskAsync("Fight", "fight");

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        await vm.ReloadTasksAsync();

        var callbackStart = new CoreCallbackEvent(
            20001,
            "SubTaskStart",
            """{"task_chain":"Fight","sub_task":"Stage","run_id":"run-callback","task_index":0}""",
            DateTimeOffset.UtcNow);
        await InvokeCallbackAsync(vm, callbackStart);

        Assert.NotNull(vm.LastRuntimeStatus);
        Assert.Equal("SubTaskStart", vm.LastRuntimeStatus!.Action);
        Assert.Equal("run-callback", vm.LastRuntimeStatus.RunId);
        Assert.Equal(0, vm.LastRuntimeStatus.TaskIndex);
        Assert.Equal("Fight", vm.LastRuntimeStatus.Module);
        Assert.Equal("Running", vm.LastRuntimeStatus.Status);

        var callbackCompleted = new CoreCallbackEvent(
            20002,
            "SubTaskCompleted",
            """{"task_chain":"Fight","sub_task":"Stage","run_id":"run-callback","task_index":0}""",
            DateTimeOffset.UtcNow);
        await InvokeCallbackAsync(vm, callbackCompleted);

        Assert.NotNull(vm.LastRuntimeStatus);
        Assert.Equal("SubTaskCompleted", vm.LastRuntimeStatus!.Action);
        Assert.Equal("Running", vm.LastRuntimeStatus.Status);
        Assert.Equal("Running", vm.Tasks[0].Status);

        var eventLog = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "debug", "avalonia-ui-events.log"));
        Assert.Contains("runId=run-callback", eventLog);
        Assert.Contains("taskIndex=0", eventLog);
        Assert.Contains("module=Fight", eventLog);
        Assert.Contains("action=SubTaskCompleted", eventLog);
        Assert.Contains("errorCode=-", eventLog);
    }

    [Fact]
    public async Task TaskQueuePage_TaskChainCompleted_ShouldAppendElapsedTimeToLog()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(
                10002,
                "TaskChainStart",
                """{"task_chain":"Fight","run_id":"run-time-1","task_index":0}""",
                DateTimeOffset.UtcNow));
        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(
                10002,
                "TaskChainCompleted",
                """{"task_chain":"Fight","run_id":"run-time-1","task_index":0}""",
                DateTimeOffset.UtcNow));

        Assert.Contains(
            vm.OverlayLogs,
            log => log.Content.Contains("(in ", StringComparison.Ordinal)
                   && log.Content.Contains("Complete task", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TaskQueuePage_InfrastCompleted_AdvancesCustomPlanAndPersistsUiState()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");

        var customPlanPath = Path.Combine(fixture.Root, "config", "infrast.autonext.json");
        await File.WriteAllTextAsync(
            customPlanPath,
            """
            {
              "plans": [
                { "name": "Day", "period": [] },
                { "name": "Night", "period": [] }
              ]
            }
            """);

        Assert.True((await fixture.TaskQueue.AddTaskAsync("Infrast", "infra")).Success);
        var parameters = new InfrastParams
        {
            Mode = InfrastModuleViewModel.InfrastModeCustom,
            Facility = ["Mfg"],
            Filename = customPlanPath,
            PlanIndex = 5,
        }.ToJson();
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(0, parameters, persistImmediately: true)).Success);

        var vm = new TaskQueuePageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        await vm.ReloadTasksAsync();
        await vm.WaitForPendingBindingAsync();
        Assert.Equal(0, vm.InfrastModule.SelectedPlanIndex);
        var normalized = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(normalized.Success);
        Assert.Equal(0, normalized.Value?["plan_index"]?.GetValue<int>());

        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(
                10002,
                "TaskChainCompleted",
                """{"task_chain":"Infrast","run_id":"run-infra-1","task_index":0}""",
                DateTimeOffset.UtcNow));

        var advanced = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(advanced.Success);
        Assert.Equal(1, advanced.Value?["plan_index"]?.GetValue<int>());
        Assert.Equal(1, vm.InfrastModule.SelectedPlanIndex);
        Assert.Equal("Night", vm.InfrastModule.SelectedPlan?.Display);

        await InvokeCallbackAsync(
            vm,
            new CoreCallbackEvent(
                10002,
                "TaskChainCompleted",
                """{"task_chain":"Infrast","run_id":"run-infra-2","task_index":0}""",
                DateTimeOffset.UtcNow));

        var wrapped = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(wrapped.Success);
        Assert.Equal(0, wrapped.Value?["plan_index"]?.GetValue<int>());
        Assert.Equal(0, vm.InfrastModule.SelectedPlanIndex);
        Assert.Equal("Day", vm.InfrastModule.SelectedPlan?.Display);

        var log = new UiLogService();
        var reloaded = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(fixture.Root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            log,
            fixture.Root);
        await reloaded.LoadOrBootstrapAsync();
        var profile = reloaded.CurrentConfig.Profiles[reloaded.CurrentConfig.CurrentProfile];
        Assert.Equal(0, profile.TaskQueue[0].Params["plan_index"]?.GetValue<int>());
    }

    [Fact]
    public async Task AdvanceInfrastCustomPlan_NormalizesBoundaryIndexesBeforeAdvancing()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");

        var onePlanPath = Path.Combine(fixture.Root, "config", "infrast.one-plan.json");
        await File.WriteAllTextAsync(
            onePlanPath,
            """{"plans":[{"name":"Only","period":[]}]}""");
        var twoPlanPath = Path.Combine(fixture.Root, "config", "infrast.two-plan.json");
        await File.WriteAllTextAsync(
            twoPlanPath,
            """
            {
              "plans": [
                { "name": "Day", "period": [] },
                { "name": "Night", "period": [] }
              ]
            }
            """);

        Assert.True((await fixture.TaskQueue.AddTaskAsync("Infrast", "one-plan-overflow")).Success);
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(
            0,
            new InfrastParams
            {
                Mode = InfrastModuleViewModel.InfrastModeCustom,
                Filename = onePlanPath,
                PlanIndex = 5,
            }.ToJson(),
            persistImmediately: true)).Success);

        var onePlanAdvance = await fixture.TaskQueue.AdvanceInfrastCustomPlanAsync(0);
        Assert.True(onePlanAdvance.Success);
        Assert.Equal(0, onePlanAdvance.Value);
        var onePlanParams = await fixture.TaskQueue.GetTaskParamsAsync(0);
        Assert.True(onePlanParams.Success);
        Assert.Equal(0, onePlanParams.Value?["plan_index"]?.GetValue<int>());

        Assert.True((await fixture.TaskQueue.AddTaskAsync("Infrast", "negative-overflow")).Success);
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(
            1,
            new InfrastParams
            {
                Mode = InfrastModuleViewModel.InfrastModeCustom,
                Filename = twoPlanPath,
                PlanIndex = -2,
            }.ToJson(),
            persistImmediately: true)).Success);

        var negativeAdvance = await fixture.TaskQueue.AdvanceInfrastCustomPlanAsync(1);
        Assert.True(negativeAdvance.Success);
        Assert.Null(negativeAdvance.Value);
        var negativeParams = await fixture.TaskQueue.GetTaskParamsAsync(1);
        Assert.True(negativeParams.Success);
        Assert.Equal(-1, negativeParams.Value?["plan_index"]?.GetValue<int>());

        Assert.True((await fixture.TaskQueue.AddTaskAsync("Infrast", "time-rotation")).Success);
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(
            2,
            new InfrastParams
            {
                Mode = InfrastModuleViewModel.InfrastModeCustom,
                Filename = twoPlanPath,
                PlanIndex = -1,
            }.ToJson(),
            persistImmediately: true)).Success);

        var rotationAdvance = await fixture.TaskQueue.AdvanceInfrastCustomPlanAsync(2);
        Assert.True(rotationAdvance.Success);
        Assert.Null(rotationAdvance.Value);
        var rotationParams = await fixture.TaskQueue.GetTaskParamsAsync(2);
        Assert.True(rotationParams.Success);
        Assert.Equal(-1, rotationParams.Value?["plan_index"]?.GetValue<int>());

        Assert.True((await fixture.TaskQueue.AddTaskAsync("Infrast", "normal-mode")).Success);
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(
            3,
            new InfrastParams
            {
                Mode = InfrastModuleViewModel.InfrastModeNormal,
                Filename = twoPlanPath,
                PlanIndex = 0,
            }.ToJson(),
            persistImmediately: true)).Success);

        var normalModeAdvance = await fixture.TaskQueue.AdvanceInfrastCustomPlanAsync(3);
        Assert.True(normalModeAdvance.Success);
        Assert.Null(normalModeAdvance.Value);
        var normalModeParams = await fixture.TaskQueue.GetTaskParamsAsync(3);
        Assert.True(normalModeParams.Success);
        Assert.Equal(0, normalModeParams.Value?["plan_index"]?.GetValue<int>());
    }

    [Fact]
    public async Task Infrast_Parse_SuccessAndRotation()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        var module = new InfrastModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });

        var customPlanPath = Path.Combine(fixture.Root, "config", "infrast.rotation.json");
        await File.WriteAllTextAsync(
            customPlanPath,
            """
            {
              "plans": [
                { "name": "Day", "period": [["00:00", "12:00"]] },
                { "name": "Night", "period": [] }
              ]
            }
            """);

        module.Mode = 10000;
        module.CustomFilePath = customPlanPath;
        await module.ReloadPlansAsync();

        Assert.Equal(3, module.PlanOptions.Count); // Auto + 2 plans
        Assert.Equal(-1, module.PlanOptions[0].Index);
        Assert.Contains("Loaded", module.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, module.LastErrorMessage);
    }

    [Fact]
    public async Task Infrast_ReloadPlans_ShouldCacheUnchangedFileAndRefreshOnDemandOrFileChange()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        var module = new InfrastModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });

        var customPlanPath = Path.Combine(fixture.Root, "config", "infrast.cache.json");
        await File.WriteAllTextAsync(
            customPlanPath,
            "{\"plans\":[{\"name\":\"PlanA\",\"period\":[[\"00:00\",\"12:00\"]]}]}");

        module.Mode = 10000;
        module.CustomFilePath = customPlanPath;
        await WaitUntilAsync(() => module.PlanOptions.Any(option => option.Display == "PlanA"));

        Assert.Equal(1, module.PlanFileReadCount);
        Assert.Contains(module.PlanOptions, option => option.Display == "PlanA");

        await module.ReloadPlansAsync();

        Assert.Equal(1, module.PlanFileReadCount);

        await module.ReloadPlansAsync(forceReload: true);

        Assert.Equal(2, module.PlanFileReadCount);

        await File.WriteAllTextAsync(
            customPlanPath,
            "{\"plans\":[{\"name\":\"PlanB\",\"period\":[[\"00:00\",\"12:00\"]]}]}");
        File.SetLastWriteTimeUtc(customPlanPath, DateTime.UtcNow.AddMinutes(1));

        await module.ReloadPlansAsync();

        Assert.Equal(3, module.PlanFileReadCount);
        Assert.Contains(module.PlanOptions, option => option.Display == "PlanB");
    }

    [Fact]
    public async Task Infrast_CustomFilePathChange_AutomaticallyReloadsAndReportsInvalidFile()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        var module = new InfrastModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        var firstPath = Path.Combine(fixture.Root, "config", "infrast.auto-a.json");
        var secondPath = Path.Combine(fixture.Root, "config", "infrast.auto-b.json");
        await File.WriteAllTextAsync(firstPath, "{\"plans\":[{\"name\":\"AutoA\",\"period\":[]}]}");
        await File.WriteAllTextAsync(secondPath, "{\"plans\":[{\"name\":\"AutoB\",\"period\":[[\"00:00\",\"12:00\"]]}]}");

        module.Mode = InfrastModuleViewModel.InfrastModeCustom;
        module.CustomFilePath = firstPath;
        await module.WaitForPendingPlanReloadAsync();
        Assert.Contains(module.PlanOptions, option => option.Display == "AutoA");
        Assert.Equal(0, module.SelectedPlanIndex);

        module.CustomFilePath = secondPath;
        await module.WaitForPendingPlanReloadAsync();
        Assert.Contains(module.PlanOptions, option => option.Display == "AutoB");
        Assert.Equal(-1, module.SelectedPlanIndex);

        module.CustomFilePath = Path.Combine(fixture.Root, "config", "missing.json");
        await module.WaitForPendingPlanReloadAsync();
        Assert.Contains("not found", module.LastErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Infrast_BindCustomParameters_PreservesPersistedPlanSelection()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Infrast, "infra")).Success);
        var path = Path.Combine(fixture.Root, "config", "infrast.bind.json");
        await File.WriteAllTextAsync(
            path,
            "{\"plans\":[{\"name\":\"A\",\"period\":[[\"00:00\",\"12:00\"]]},{\"name\":\"B\",\"period\":[]}]}");
        var parameters = new InfrastParams
        {
            Mode = InfrastModuleViewModel.InfrastModeCustom,
            Filename = path,
            PlanIndex = 1,
            Facility = ["Mfg"],
            FacilityOrder = ["Mfg", "Dorm"],
        }.ToJson();

        var module = new InfrastModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        await module.BindAsync(0, parameters);

        Assert.Equal(1, module.SelectedPlanIndex);
        Assert.Equal("B", module.SelectedPlan?.Display);
    }

    [Fact]
    public async Task Infrast_Append_UsesResolvedPlanIndexWithoutOverwritingTimeRotationSelection()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var path = Path.Combine(fixture.Root, "config", "infrast.append.json");
        await File.WriteAllTextAsync(path, "{\"plans\":[{\"name\":\"Only\",\"period\":[]}]}");
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = TaskModuleTypes.Infrast,
            Name = "infra",
            IsEnabled = true,
            Params = new InfrastParams
            {
                Mode = InfrastModuleViewModel.InfrastModeCustom,
                Filename = path,
                PlanIndex = -1,
                Facility = ["Mfg"],
                FacilityOrder = ["Mfg", "Dorm"],
            }.ToJson(),
        });

        var result = await fixture.Runtime.SessionService.AppendTasksFromCurrentProfileAsync();
        var appended = JsonNode.Parse(Assert.Single(fixture.Bridge.AppendedTasks).ParamsJson)!.AsObject();

        Assert.True(result.Success);
        Assert.Equal(0, appended["plan_index"]?.GetValue<int>());
        Assert.Null(appended["_ui_facility_order"]);
        Assert.Equal(-1, profile.TaskQueue.Single().Params["plan_index"]?.GetValue<int>());
    }

    [Fact]
    public async Task Infrast_Append_OutOfRangePlanIndex_RunsFirstPlanWithoutBlockingQueue()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var path = Path.Combine(fixture.Root, "config", "infrast.shrunk.json");
        await File.WriteAllTextAsync(path, "{\"plans\":[{\"name\":\"Only\",\"period\":[]}]}");
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = TaskModuleTypes.Infrast,
            Name = "infra",
            IsEnabled = true,
            Params = new InfrastParams
            {
                Mode = InfrastModuleViewModel.InfrastModeCustom,
                Filename = path,
                PlanIndex = 5,
                Facility = ["Mfg"],
                FacilityOrder = ["Mfg", "Dorm"],
            }.ToJson(),
        });
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = TaskModuleTypes.Award,
            Name = "award",
            IsEnabled = true,
            Params = new JsonObject(),
        });

        var result = await fixture.Runtime.SessionService.AppendTasksFromCurrentProfileAsync();

        Assert.True(result.Success);
        Assert.Equal(2, fixture.Bridge.AppendedTasks.Count);
        var infrastAppend = JsonNode.Parse(fixture.Bridge.AppendedTasks[0].ParamsJson)!.AsObject();
        Assert.Equal(0, infrastAppend["plan_index"]?.GetValue<int>());
        Assert.Null(infrastAppend["_ui_facility_order"]);
        Assert.Equal(5, profile.TaskQueue[0].Params["plan_index"]?.GetValue<int>());
    }

    [Fact]
    public async Task Infrast_Parse_FailureLogsAndOutOfRangeNormalizes()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        var module = new InfrastModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });

        var invalidPath = Path.Combine(fixture.Root, "config", "infrast.invalid.json");
        await File.WriteAllTextAsync(invalidPath, """{"plans":[{"name":"A"}""");
        module.Mode = 10000;
        module.CustomFilePath = invalidPath;
        module.SelectedPlanIndex = 0;
        await module.ReloadPlansAsync();
        Assert.Contains("parse", module.LastErrorMessage, StringComparison.OrdinalIgnoreCase);

        var validPath = Path.Combine(fixture.Root, "config", "infrast.valid.json");
        await File.WriteAllTextAsync(
            validPath,
            "{\"plans\":[{\"name\":\"PlanA\",\"period\":[[\"00:00\",\"12:00\"]]}]}");
        module.CustomFilePath = validPath;
        module.SelectedPlanIndex = 5;
        await module.ReloadPlansAsync();
        Assert.Equal(0, module.SelectedPlanIndex);
        Assert.Equal(string.Empty, module.LastErrorMessage);

        var errorLog = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "debug", "avalonia-ui-errors.log"));
        Assert.Contains(UiErrorCode.InfrastPlanParseFailed, errorLog);
    }

    [Fact]
    public async Task Infrast_ReloadPlans_OutOfRange_NormalizesToFirstPlan()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new InfrastModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });

        var customPlanPath = Path.Combine(fixture.Root, "config", "infrast.custom.json");
        await File.WriteAllTextAsync(
            customPlanPath,
            "{\"plans\":[{\"name\":\"PlanA\",\"period\":[[\"00:00\",\"12:00\"]]}]}");

        module.Mode = 10000;
        module.CustomFilePath = customPlanPath;
        module.SelectedPlanIndex = 5;
        await module.ReloadPlansAsync();

        Assert.Equal(0, module.SelectedPlanIndex);
        Assert.Equal("PlanA", module.SelectedPlan?.Display);
        Assert.Equal(string.Empty, module.LastErrorMessage);
    }

    [Fact]
    public async Task Infrast_FacilitySelection_ReordersAndKeepsSelectionState()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new InfrastModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });

        var originalOrder = module.FacilityOptions.Select(option => option.Value).ToArray();
        module.FacilityOptions[0].IsSelected = true;
        module.MoveFacility(0, 2);

        Assert.Equal(originalOrder[0], module.FacilityOptions[2].Value);
        Assert.True(module.FacilityOptions[2].IsSelected);
    }

    [Fact]
    public async Task Infrast_FacilityPersistence_PreservesEmptySelectionAndDisabledRoomOrder()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync(TaskModuleTypes.Infrast, "infra")).Success);
        var parameters = new InfrastParams
        {
            Facility = ["Mfg", "Trade"],
            FacilityOrder = ["Mfg", "Dorm", "Trade", "Control", "Power", "Reception", "Office", "Processing", "Training"],
        }.ToJson();
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(0, parameters, persistImmediately: true)).Success);

        var module = new InfrastModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        await module.BindAsync(0, parameters);
        Assert.Equal(["Mfg", "Dorm", "Trade"], module.FacilityOptions.Take(3).Select(option => option.Value));
        Assert.False(module.FacilityOptions.Single(option => option.Value == "Dorm").IsSelected);

        module.FacilityOptions.Single(option => option.Value == "Dorm").IsSelected = true;
        Assert.True(await module.FlushPendingChangesAsync());
        var enabled = Assert.IsType<JsonArray>((await fixture.TaskQueue.GetTaskParamsAsync(0)).Value?["facility"]);
        Assert.Equal(["Mfg", "Dorm", "Trade"], enabled.Select(node => node!.GetValue<string>()));

        module.ClearFacility();
        Assert.True(await module.FlushPendingChangesAsync());
        var cleared = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Empty(Assert.IsType<JsonArray>(cleared["facility"]));

        var rebound = new InfrastModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });
        await rebound.BindAsync(0, cleared);
        Assert.DoesNotContain(rebound.FacilityOptions, option => option.IsSelected);
        Assert.Equal(["Mfg", "Dorm", "Trade"], rebound.FacilityOptions.Take(3).Select(option => option.Value));
    }

    [Fact]
    public async Task Infrast_FacilityOptions_DisplayLocalizedRoomNames()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var texts = new LocalizedTextMap { Language = "en-us" };
        var module = new InfrastModuleViewModel(fixture.Runtime, texts);

        var mfg = module.FacilityOptions.Single(option => option.Value == "Mfg");
        Assert.Equal("Factory", mfg.DisplayName);

        texts.Language = "zh-cn";
        Assert.Equal("制造站", mfg.DisplayName);
        Assert.Equal("Mfg", mfg.Value);
    }

    [Fact]
    public async Task Award_RecruitConfirmation_ConfirmAndCancelBranches_WorkAsExpected()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var module = new AwardModuleViewModel(fixture.Runtime, new LocalizedTextMap { Language = "en-us" });

        module.Recruit = true;
        Assert.True(module.PendingRecruitConfirmation);
        Assert.False(module.Recruit);

        module.CancelRecruitEnable();
        Assert.False(module.PendingRecruitConfirmation);
        Assert.False(module.Recruit);

        module.Recruit = true;
        Assert.True(module.PendingRecruitConfirmation);

        module.ConfirmRecruitEnable();
        Assert.False(module.PendingRecruitConfirmation);
        Assert.True(module.Recruit);
    }

    [Fact]
    public async Task PostAction_EndScript_Completion_ExecutesConfiguredScript()
    {
        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        var executor = new TestPostActionExecutorService(new PostActionCapabilityMatrix(
            ExitArknights: supported,
            BackToAndroidHome: supported,
            ExitEmulator: supported,
            ExitSelf: supported,
            Hibernate: supported,
            Shutdown: supported,
            Sleep: supported));
        await using var fixture = await TestFixture.CreateAsync(executor: executor);
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        profile.Values[ConfigurationKeys.EndsWithScript] = JsonValue.Create("/bin/echo done");

        var result = await fixture.PostAction.ExecuteEndScriptAsync(manualStop: false);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["/bin/echo done"], executor.ExecutedScripts);

        var eventLog = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "debug", "avalonia-ui-events.log"));
        Assert.Contains("action=EndsWithScript", eventLog);
    }

    [Fact]
    public async Task PostAction_EndScript_NotConfigured_SkipsExecution()
    {
        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        var executor = new TestPostActionExecutorService(new PostActionCapabilityMatrix(
            ExitArknights: supported,
            BackToAndroidHome: supported,
            ExitEmulator: supported,
            ExitSelf: supported,
            Hibernate: supported,
            Shutdown: supported,
            Sleep: supported));
        await using var fixture = await TestFixture.CreateAsync(executor: executor);

        var result = await fixture.PostAction.ExecuteEndScriptAsync(manualStop: false);

        Assert.True(result.Success, result.Message);
        Assert.Empty(executor.ExecutedScripts);
    }

    [Fact]
    public async Task PostAction_EndScript_ManualStop_RequiresManualStopWithScriptFlag()
    {
        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        var executor = new TestPostActionExecutorService(new PostActionCapabilityMatrix(
            ExitArknights: supported,
            BackToAndroidHome: supported,
            ExitEmulator: supported,
            ExitSelf: supported,
            Hibernate: supported,
            Shutdown: supported,
            Sleep: supported));
        await using var fixture = await TestFixture.CreateAsync(executor: executor);
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        profile.Values[ConfigurationKeys.EndsWithScript] = JsonValue.Create("/bin/echo stop");

        var disabled = await fixture.PostAction.ExecuteEndScriptAsync(manualStop: true);

        Assert.True(disabled.Success, disabled.Message);
        Assert.Empty(executor.ExecutedScripts);

        profile.Values[ConfigurationKeys.ManualStopWithScript] = JsonValue.Create("True");
        var enabled = await fixture.PostAction.ExecuteEndScriptAsync(manualStop: true);

        Assert.True(enabled.Success, enabled.Message);
        Assert.Equal(["/bin/echo stop"], executor.ExecutedScripts);
    }

    [Fact]
    public async Task PostAction_StartScript_ExecutesConfiguredScript()
    {
        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        var executor = new TestPostActionExecutorService(new PostActionCapabilityMatrix(
            ExitArknights: supported,
            BackToAndroidHome: supported,
            ExitEmulator: supported,
            ExitSelf: supported,
            Hibernate: supported,
            Shutdown: supported,
            Sleep: supported));
        await using var fixture = await TestFixture.CreateAsync(executor: executor);
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];

        var skipped = await fixture.PostAction.ExecuteStartScriptAsync();
        Assert.True(skipped.Success, skipped.Message);

        profile.Values[ConfigurationKeys.StartsWithScript] = JsonValue.Create("/bin/echo start");
        var result = await fixture.PostAction.ExecuteStartScriptAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(["/bin/echo start"], executor.ExecutedScripts);

        var eventLog = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "debug", "avalonia-ui-events.log"));
        Assert.Contains("action=StartsWithScript", eventLog);
    }

    [Fact]
    public async Task PostAction_Execute_OnceSleep_RestoresExitSelfFromPersistentConfig()
    {
        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        var executor = new TestPostActionExecutorService(new PostActionCapabilityMatrix(
            ExitArknights: supported,
            BackToAndroidHome: supported,
            ExitEmulator: supported,
            ExitSelf: supported,
            Hibernate: supported,
            Shutdown: supported,
            Sleep: supported));
        await using var fixture = await TestFixture.CreateAsync(
            executor: executor,
            promptService: new TestPostActionPromptService(UiOperationResult.Ok("confirmed")));

        var save = await fixture.PostAction.SaveAsync(new PostActionConfig { ExitSelf = true });
        Assert.True(save.Success, save.Message);

        var execute = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext("AllTasksCompleted", true, RunId: "run-once-sleep"),
            new PostActionConfig
            {
                Once = true,
                Sleep = true,
                ExitSelf = false,
            });

        Assert.True(execute.Success, execute.Message);
        Assert.Contains(PostActionType.Sleep, executor.ExecutedActions);
        Assert.Contains(PostActionType.ExitSelf, executor.ExecutedActions);
    }

    [Fact]
    public async Task SessionService_InvalidateConnectionAfterExternalExit_ResetsConnectionState()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var session = fixture.Runtime.SessionService;

        var connect = await session.ConnectAsync("127.0.0.1:5555", "General", string.Empty);
        Assert.True(connect.Success);
        Assert.Equal(SessionState.Connected, session.CurrentState);
        Assert.True(session.IsConnectedWith(new CoreConnectionInfo("127.0.0.1:5555", "General", string.Empty)));

        session.InvalidateConnectionAfterExternalExit();

        Assert.Equal(SessionState.Idle, session.CurrentState);
        Assert.False(session.IsConnectedWith(new CoreConnectionInfo("127.0.0.1:5555", "General", string.Empty)));
    }

    [Fact]
    public async Task PostAction_Execute_ExitEmulator_InvalidatesSessionConnection()
    {
        if (!OperatingSystem.IsWindows())
        {
            // ExitEmulator is normalized away on non-Windows runtimes (NormalizeForRuntime).
            return;
        }

        var supported = new PlatformCapabilityStatus(true, "supported", Provider: "test");
        var executor = new TestPostActionExecutorService(new PostActionCapabilityMatrix(
            ExitArknights: supported,
            BackToAndroidHome: supported,
            ExitEmulator: supported,
            ExitSelf: supported,
            Hibernate: supported,
            Shutdown: supported,
            Sleep: supported));
        await using var fixture = await TestFixture.CreateAsync(executor: executor);
        var session = fixture.Runtime.SessionService;

        var connect = await session.ConnectAsync("127.0.0.1:5555", "General", string.Empty);
        Assert.True(connect.Success);
        Assert.Equal(SessionState.Connected, session.CurrentState);

        var execute = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext("AllTasksCompleted", true, RunId: "run-exit-emulator"),
            new PostActionConfig { ExitEmulator = true });

        Assert.True(execute.Success, execute.Message);
        Assert.Contains(PostActionType.ExitEmulator, executor.ExecutedActions);
        Assert.Equal(SessionState.Idle, session.CurrentState);
    }

    private static async Task InvokeCallbackAsync(TaskQueuePageViewModel vm, CoreCallbackEvent callback)
    {
        var method = typeof(TaskQueuePageViewModel).GetMethod(
            "HandleCallbackCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = method!.Invoke(vm, new object?[] { callback, GetActiveRunLease(vm) }) as Task;
        if (task is null)
        {
            throw new InvalidOperationException("HandleCallbackAsync invocation returned null.");
        }

        await task;
    }

    [Fact]
    public async Task GetStartPrecheckWarnings_SkipsMallCreditFightWarning_WhenOnceADayCreditFightAlreadyRanToday()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.Values["ClientType"] = "Official";
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Fight",
            Name = "fight",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["stage"] = FightStageSelection.CurrentOrLast,
                ["medicine"] = 0,
                ["stone"] = 0,
                ["times"] = 1,
                ["series"] = 1,
            },
        });
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Mall",
            Name = "mall",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["credit_fight"] = true,
                ["credit_fight_once_a_day"] = true,
                ["_ui_mall_credit_fight_last_time"] = MallDailyResetHelper.GetCurrentYjDateString(DateTime.UtcNow, "Official"),
            },
        });

        var warnings = await fixture.TaskQueue.GetStartPrecheckWarningsAsync();
        Assert.True(warnings.Success);
        Assert.Empty(warnings.Value ?? []);
        Assert.True(profile.TaskQueue[1].Params["credit_fight"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GetStartPrecheckWarnings_ReportsMallCreditFightDowngrade_WhenOnceADayCreditFightIsAvailableToday()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.Values["ClientType"] = "Official";
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Fight",
            Name = "fight",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["stage"] = FightStageSelection.CurrentOrLast,
                ["medicine"] = 0,
                ["stone"] = 0,
                ["times"] = 1,
                ["series"] = 1,
            },
        });
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Mall",
            Name = "mall",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["credit_fight"] = true,
                ["credit_fight_once_a_day"] = true,
                ["_ui_mall_credit_fight_last_time"] = MallDailyResetHelper.GetPreviousYjDateString(DateTime.UtcNow, "Official"),
            },
        });

        var warnings = await fixture.TaskQueue.GetStartPrecheckWarningsAsync();
        Assert.True(warnings.Success);
        var warning = Assert.Single(warnings.Value ?? []);
        Assert.Equal(UiErrorCode.MallCreditFightDowngraded, warning.Code);
        Assert.True(profile.TaskQueue[1].Params["credit_fight"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GetStartPrecheckWarnings_ReportsMallCreditFightDowngrade_WhenOnceADayDisabledDespiteTodaysMarker()
    {
        await using var fixture = await TestFixture.CreateAsync("en-us");
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        profile.Values["ClientType"] = "Official";
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Fight",
            Name = "fight",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["stage"] = FightStageSelection.CurrentOrLast,
                ["medicine"] = 0,
                ["stone"] = 0,
                ["times"] = 1,
                ["series"] = 1,
            },
        });
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Mall",
            Name = "mall",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["credit_fight"] = true,
                ["credit_fight_once_a_day"] = false,
                ["_ui_mall_credit_fight_last_time"] = MallDailyResetHelper.GetCurrentYjDateString(DateTime.UtcNow, "Official"),
            },
        });

        var warnings = await fixture.TaskQueue.GetStartPrecheckWarningsAsync();
        Assert.True(warnings.Success);
        var warning = Assert.Single(warnings.Value ?? []);
        Assert.Equal(UiErrorCode.MallCreditFightDowngraded, warning.Code);
    }

    private static RunOwnershipLease? GetActiveRunLease(TaskQueuePageViewModel vm)
    {
        var field = typeof(TaskQueuePageViewModel).GetField(
            "_activeRunLease",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var snapshot = field!.GetValue(vm);
        if (snapshot is null)
        {
            return null;
        }

        var leaseProperty = snapshot.GetType().GetProperty("Lease", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(leaseProperty);
        return (RunOwnershipLease)leaseProperty!.GetValue(snapshot)!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("Condition was not satisfied within the timeout.");
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(
            string root,
            UnifiedConfigurationService config,
            TaskQueueFeatureService taskQueue,
            PostActionFeatureService postAction,
            MAAUnifiedRuntime runtime,
            FakeBridge bridge)
        {
            Root = root;
            Config = config;
            TaskQueue = taskQueue;
            PostAction = postAction;
            Runtime = runtime;
            Bridge = bridge;
        }

        public string Root { get; }

        public UnifiedConfigurationService Config { get; }

        public TaskQueueFeatureService TaskQueue { get; }

        public PostActionFeatureService PostAction { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public FakeBridge Bridge { get; }

        public static async Task<TestFixture> CreateAsync(
            string language = "zh-cn",
            IPostActionExecutorService? executor = null,
            FakeBridge? bridge = null,
            IAppLifecycleService? appLifecycleService = null,
            IPostActionPromptService? promptService = null,
            Func<bool>? hasOtherMaaProcess = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));

            var log = new UiLogService();
            var diagnostics = new UiDiagnosticsService(root, log);
            var config = new UnifiedConfigurationService(
                new AvaloniaJsonConfigStore(root),
                new GuiNewJsonConfigImporter(),
                new GuiJsonConfigImporter(),
                log,
                root);

            await config.LoadOrBootstrapAsync();
            config.CurrentConfig.GlobalValues["GUI.Localization"] = language;
            if (config.TryGetCurrentProfile(out var profile))
            {
                profile.Values[MacBundledAdbPolicy.ProfileUseBundledAdbKey] = JsonValue.Create(false);
            }
            _ = await TestConnectionFixtureSupport.PrepareReadyRuntimeAsync(root, config, "taskmodule-b-ready");

            bridge ??= new FakeBridge();
            var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
            var taskQueue = new TaskQueueFeatureService(session, config);

            var platform = new PlatformServiceBundle
            {
                TrayService = new NoOpTrayService(),
                NotificationService = new NoOpNotificationService(),
                HotkeyService = new NoOpGlobalHotkeyService(),
                AutostartService = new NoOpAutostartService(),
                FileDialogService = new NoOpFileDialogService(),
                OverlayService = new NoOpOverlayCapabilityService(),
                PostActionExecutorService = executor ?? new NoOpPostActionExecutorService(),
            };

            var capability = new PlatformCapabilityFeatureService(platform, diagnostics);
            var lifecycle = appLifecycleService ?? new NoOpAppLifecycleService();
            var postAction = new PostActionFeatureService(
                config,
                diagnostics,
                platform.PostActionExecutorService,
                bridge,
                lifecycle,
                promptService ?? new NoOpPostActionPromptService(),
                session,
                hasOtherMaaProcess);
            var connectFeatureService = new ConnectFeatureService(session, config, log, bridge, root);
            var runtime = new MAAUnifiedRuntime
            {
                CoreBridge = bridge,
                ConfigurationService = config,
                ResourceWorkflowService = new ResourceWorkflowService(root, bridge, log),
                SessionService = session,
                Platform = platform,
                LogService = log,
                DiagnosticsService = diagnostics,
                ConnectFeatureService = connectFeatureService,
                ShellFeatureService = new ShellFeatureService(connectFeatureService),
                TaskQueueFeatureService = taskQueue,
                CopilotFeatureService = new CopilotFeatureService(),
                ToolboxFeatureService = new ToolboxFeatureService(),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = capability,
                OverlayFeatureService = new OverlayFeatureService(capability),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = postAction,
                AppLifecycleService = lifecycle,
            };

            return new TestFixture(root, config, taskQueue, postAction, runtime, bridge);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await Bridge.DisposeAsync();
            CleanupRoot();
        }

        private void CleanupRoot()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // keep temporary folder for inspection when file lock cleanup fails on CI hosts.
            }
        }
    }

    private sealed class TestPostActionExecutorService : IPostActionExecutorService
    {
        private readonly Func<PostActionType, PostActionExecutorRequest?, PlatformOperationResult>? _handler;
        private readonly Func<PostActionExecutorRequest?, PostActionCapabilityMatrix>? _matrixSelector;

        public TestPostActionExecutorService(
            PostActionCapabilityMatrix matrix,
            Func<PostActionType, PostActionExecutorRequest?, PlatformOperationResult>? handler = null,
            Func<PostActionExecutorRequest?, PostActionCapabilityMatrix>? matrixSelector = null)
        {
            CapabilityMatrix = matrix;
            _handler = handler;
            _matrixSelector = matrixSelector;
        }

        public PostActionCapabilityMatrix CapabilityMatrix { get; }

        public List<PostActionType> ExecutedActions { get; } = [];

        public List<(PostActionType Action, PostActionExecutorRequest? Request)> Requests { get; } = [];

        public PostActionCapabilityMatrix GetCapabilityMatrix(PostActionExecutorRequest? request = null)
            => _matrixSelector?.Invoke(request) ?? CapabilityMatrix;

        public List<string> ExecutedScripts { get; } = [];

        public Func<string, PlatformOperationResult>? ScriptHandler { get; set; }

        public Task<PlatformOperationResult> ExecuteUserScriptAsync(
            string scriptPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutedScripts.Add(scriptPath);
            if (ScriptHandler is not null)
            {
                return Task.FromResult(ScriptHandler(scriptPath));
            }

            return Task.FromResult(PlatformOperation.NativeSuccess(
                "user-script",
                $"Executed user script `{scriptPath}`.",
                "post-action.UserScript"));
        }

        public Task<PlatformOperationResult> ExecuteAsync(
            PostActionType action,
            PostActionExecutorRequest? request = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutedActions.Add(action);
            Requests.Add((action, request));
            if (_handler is not null)
            {
                return Task.FromResult(_handler(action, request));
            }

            return Task.FromResult(PlatformOperation.NativeSuccess(
                GetCapabilityMatrix(request).Get(action).Provider,
                $"Executed {action}.",
                $"post-action.{action}"));
        }
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _channel = Channel.CreateUnbounded<CoreCallbackEvent>();
        private int _taskId;

        public List<CoreTaskRequest> AppendedTasks { get; } = [];

        public bool SupportsNativeBackToHome { get; set; }

        public bool SupportsNativeCloseDown { get; set; }

        public int BackToHomeCallCount { get; private set; }

        public int StartCloseDownCallCount { get; private set; }

        public string? LastCloseDownClientType { get; private set; }

        public bool SupportsBackToHome => SupportsNativeBackToHome;

        public bool SupportsStartCloseDown => SupportsNativeCloseDown;

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            AppendedTasks.Add(task);
            return Task.FromResult(CoreResult<int>.Ok(Interlocked.Increment(ref _taskId)));
        }

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<bool>> BackToHomeAsync(CancellationToken cancellationToken = default)
        {
            BackToHomeCallCount += 1;
            return Task.FromResult(
                SupportsNativeBackToHome
                    ? CoreResult<bool>.Ok(true)
                    : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "back-to-home unsupported")));
        }

        public Task<CoreResult<bool>> StartCloseDownAsync(string clientType, CancellationToken cancellationToken = default)
        {
            StartCloseDownCallCount += 1;
            LastCloseDownClientType = clientType;
            return Task.FromResult(
                SupportsNativeCloseDown
                    ? CoreResult<bool>.Ok(true)
                    : CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "close-down unsupported")));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var callback in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return callback;
            }
        }

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestAppLifecycleService : IAppLifecycleService
    {
        private readonly UiOperationResult _exitResult;

        public TestAppLifecycleService(bool supportsExit, UiOperationResult? exitResult = null)
        {
            SupportsExit = supportsExit;
            _exitResult = exitResult ?? UiOperationResult.Ok("exit");
        }

        public bool SupportsExit { get; }

        public int ExitCallCount { get; private set; }

        public Task<UiOperationResult> RestartAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Ok("restart"));

        public Task<UiOperationResult> ExitAsync(CancellationToken cancellationToken = default)
        {
            ExitCallCount += 1;
            return Task.FromResult(_exitResult);
        }
    }

    private sealed class TestPostActionPromptService : IPostActionPromptService
    {
        private readonly UiOperationResult _result;

        public TestPostActionPromptService(UiOperationResult result)
        {
            _result = result;
        }

        public List<PostActionPromptRequest> Requests { get; } = [];

        public Task<UiOperationResult> ConfirmPowerActionAsync(
            PostActionPromptRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_result);
        }
    }
}
