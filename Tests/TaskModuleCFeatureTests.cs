using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.CoreBridge;

namespace MAAUnified.Tests;

public sealed class TaskModuleCFeatureTests
{
    [Fact]
    public async Task AddTask_RoguelikeReclamationCustom_InjectsBaselineDefaults()
    {
        await using var fixture = await TestFixture.CreateAsync();

        Assert.True((await fixture.TaskQueue.AddTaskAsync("RoguelikeTask", "rogue")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("ReclamationTask", "reclamation")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("CustomTask", "custom")).Success);

        var queue = await fixture.TaskQueue.GetCurrentTaskQueueAsync();
        Assert.True(queue.Success);
        var tasks = Assert.IsAssignableFrom<IReadOnlyList<UnifiedTaskItem>>(queue.Value);
        Assert.Equal(3, tasks.Count);

        Assert.Equal(TaskModuleTypes.Roguelike, tasks[0].Type);
        Assert.Equal(0, tasks[0].Params["mode"]?.GetValue<int>());
        Assert.Equal("JieGarden", tasks[0].Params["theme"]?.GetValue<string>());
        Assert.Equal(int.MaxValue, tasks[0].Params["difficulty"]?.GetValue<int>());
        Assert.Equal(999999, tasks[0].Params["starts_count"]?.GetValue<int>());
        Assert.True(tasks[0].Params["investment_enabled"]?.GetValue<bool>());

        Assert.Equal(TaskModuleTypes.Reclamation, tasks[1].Type);
        Assert.Equal("Tales", tasks[1].Params["theme"]?.GetValue<string>());
        Assert.Equal(1, tasks[1].Params["mode"]?.GetValue<int>());
        Assert.Equal(0, tasks[1].Params["increment_mode"]?.GetValue<int>());
        Assert.Equal(16, tasks[1].Params["num_craft_batches"]?.GetValue<int>());
        Assert.Equal(["荧光棒"], ((JsonArray)tasks[1].Params["tools_to_craft"]!).Select(node => node!.GetValue<string>()));
        Assert.True(tasks[1].Params["clear_store"]?.GetValue<bool>());

        Assert.Equal(TaskModuleTypes.Custom, tasks[2].Type);
        var taskNames = Assert.IsType<JsonArray>(tasks[2].Params["task_names"]);
        Assert.Empty(taskNames);
    }

    [Fact]
    public async Task SaveAndGet_RoundTripPreservesModuleCValues()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Roguelike", "rogue")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Reclamation", "reclamation")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Custom", "custom")).Success);

        var roguelikeSave = await fixture.TaskQueue.SaveRoguelikeParamsAsync(0, new RoguelikeTaskParamsDto
        {
            Mode = 4,
            Theme = "Mizuki",
            Difficulty = 12,
            StartsCount = 128,
            InvestmentEnabled = true,
            InvestmentWithMoreScore = true,
            InvestmentsCount = 77,
            StopWhenInvestmentFull = true,
            Squad = "Leader",
            Roles = "Pioneer",
            CoreChar = "Amiya",
            UseSupport = true,
            UseNonfriendSupport = true,
            RefreshTraderWithDice = true,
            CollectibleModeShopping = true,
            CollectibleModeSquad = "Collectors",
            StartWithEliteTwo = true,
            OnlyStartWithEliteTwo = false,
            CollectibleModeStartList = new RoguelikeCollectibleStartListDto
            {
                HotWater = true,
                Dice = true,
                Ticket = true,
            },
            StartFoldartalList = ["a", "b"],
            StartWithSeed = "seeda,rogue_1,3",
        });
        Assert.True(roguelikeSave.Success, roguelikeSave.Message);

        var reclamationSave = await fixture.TaskQueue.SaveReclamationParamsAsync(1, new ReclamationTaskParamsDto
        {
            Theme = "Fire",
            Mode = 1,
            IncrementMode = 1,
            NumCraftBatches = 9,
            ToolsToCraft = ["tool-a", "tool-b"],
            ClearStore = false,
        });
        Assert.True(reclamationSave.Success, reclamationSave.Message);

        var customSave = await fixture.TaskQueue.SaveCustomParamsAsync(2, new CustomTaskParamsDto
        {
            TaskNames = ["Fight", "Mall", "Award"],
        });
        Assert.True(customSave.Success, customSave.Message);

        var rogue = await fixture.TaskQueue.GetRoguelikeParamsAsync(0);
        Assert.True(rogue.Success);
        Assert.NotNull(rogue.Value);
        Assert.Equal(4, rogue.Value!.Mode);
        Assert.Equal("Mizuki", rogue.Value.Theme);
        Assert.True(rogue.Value.RefreshTraderWithDice);
        Assert.True(rogue.Value.CollectibleModeStartList.HotWater);
        Assert.True(rogue.Value.CollectibleModeStartList.Dice);
        Assert.False(rogue.Value.CollectibleModeStartList.Ticket);
        Assert.Equal("seeda,rogue_1,3", rogue.Value.StartWithSeed);

        var reclamation = await fixture.TaskQueue.GetReclamationParamsAsync(1);
        Assert.True(reclamation.Success);
        Assert.NotNull(reclamation.Value);
        Assert.Equal("Fire", reclamation.Value!.Theme);
        Assert.Equal(1, reclamation.Value.Mode);
        Assert.Equal(1, reclamation.Value.IncrementMode);
        Assert.Equal(9, reclamation.Value.NumCraftBatches);
        Assert.Equal(new[] { "tool-a", "tool-b" }, reclamation.Value.ToolsToCraft);
        Assert.False(reclamation.Value.ClearStore);

        var custom = await fixture.TaskQueue.GetCustomParamsAsync(2);
        Assert.True(custom.Success);
        Assert.NotNull(custom.Value);
        Assert.Equal(new[] { "Fight", "Mall", "Award" }, custom.Value!.TaskNames);
    }

    [Fact]
    public async Task GetModuleCParamsAsync_LocalizesLoadedMessage_AndMapsLegacyDefaultTaskNamesToComponentTitles()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Config.CurrentConfig.GlobalValues["GUI.Localization"] = JsonValue.Create("ja-jp");

        Assert.True((await fixture.TaskQueue.AddTaskAsync("Roguelike", "自动肉鸽")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Reclamation", "生息演算")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Custom", "自定义任务")).Success);

        var localizer = UiLocalizer.Create("ja-jp");
        var template = localizer.GetOrDefault("TaskQueue.Status.ParamsLoaded", "Loaded params for `{0}`.", "TaskQueue.Status");

        var roguelike = await fixture.TaskQueue.GetRoguelikeParamsAsync(0);
        Assert.True(roguelike.Success);
        Assert.Equal(string.Format(template, localizer["Roguelike.Title"]), roguelike.Message);
        Assert.DoesNotContain("自动肉鸽", roguelike.Message, StringComparison.Ordinal);

        var reclamation = await fixture.TaskQueue.GetReclamationParamsAsync(1);
        Assert.True(reclamation.Success);
        Assert.Equal(string.Format(template, localizer["Reclamation.Title"]), reclamation.Message);

        var custom = await fixture.TaskQueue.GetCustomParamsAsync(2);
        Assert.True(custom.Success);
        Assert.Equal(string.Format(template, localizer["Custom.Title"]), custom.Message);
        Assert.DoesNotContain("自定义任务", custom.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateTask_HandlesModuleC_AndReturnsIssueShape()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Roguelike", "rogue")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Reclamation", "reclamation")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Custom", "custom")).Success);

        var roguelikeParams = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        roguelikeParams.Remove("theme");
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(0, roguelikeParams)).Success);

        var validateRogue = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.True(validateRogue.Success);
        Assert.NotNull(validateRogue.Value);
        Assert.Contains(validateRogue.Value!.Issues, issue => issue.Code == "TaskFieldMissing" && issue.Field == "roguelike.theme");

        var validateReclamation = await fixture.TaskQueue.ValidateTaskAsync(1);
        Assert.True(validateReclamation.Success);
        Assert.NotNull(validateReclamation.Value);
        Assert.Empty(validateReclamation.Value!.Issues);

        var validateCustom = await fixture.TaskQueue.ValidateTaskAsync(2);
        Assert.True(validateCustom.Success);
        Assert.NotNull(validateCustom.Value);
        Assert.Contains(validateCustom.Value!.Issues, issue => issue.Code == "CustomTaskNamesEmpty" && !issue.Blocking);
    }

    [Fact]
    public async Task ValidateTaskCompilation_ReturnsCompiledSnapshot_WithoutMutatingTaskParams()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Roguelike", "rogue")).Success);

        var rawParams = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        rawParams["theme"] = "UnknownTheme";
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(0, rawParams)).Success);

        var validate = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.True(validate.Success, validate.Message);
        Assert.NotNull(validate.Value);
        Assert.Equal(0, validate.Value!.TaskIndex);
        Assert.Equal("rogue", validate.Value.TaskName);
        Assert.Equal(TaskModuleTypes.Roguelike, validate.Value.NormalizedType);
        Assert.Equal("JieGarden", validate.Value.CompiledParams["theme"]?.GetValue<string>());
        Assert.Contains(validate.Value.Issues, issue => issue.Code == "RoguelikeThemeUnknown" && !issue.Blocking);

        var storedAfterValidate = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Equal("UnknownTheme", storedAfterValidate["theme"]?.GetValue<string>());
    }

    [Fact]
    public async Task ValidateTaskCompilation_BlockingIssue_DoesNotRewriteOriginalTaskParams()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Reclamation", "reclamation")).Success);

        var rawParams = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        rawParams["mode"] = new JsonObject { ["bad"] = true };
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(0, rawParams)).Success);
        var beforeValidate = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!.ToJsonString();

        var validate = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.True(validate.Success, validate.Message);
        Assert.NotNull(validate.Value);
        Assert.True(validate.Value!.HasBlockingIssues);
        Assert.Contains(validate.Value.Issues, issue => issue.Code == "TaskFieldTypeInvalid" && issue.Blocking);
        Assert.Equal(1, validate.Value.CompiledParams["mode"]?.GetValue<int>());

        var afterValidate = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!.ToJsonString();
        Assert.Equal(beforeValidate, afterValidate);
    }

    [Fact]
    public async Task SaveAndReload_RoundTripKeepsModuleCParams()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Roguelike", "rogue")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Reclamation", "reclamation")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Custom", "custom")).Success);

        Assert.True((await fixture.TaskQueue.SaveRoguelikeParamsAsync(0, new RoguelikeTaskParamsDto
        {
            Mode = 7,
            Theme = "Sarkaz",
            Difficulty = 5,
            StartsCount = 32,
            InvestmentEnabled = false,
            DeepExplorationAutoIterate = false,
            StartWithSeed = "persist,rogue_2,9",
        })).Success);
        Assert.True((await fixture.TaskQueue.SaveReclamationParamsAsync(1, new ReclamationTaskParamsDto
        {
            Theme = "Tales",
            Mode = 1,
            IncrementMode = 1,
            NumCraftBatches = 4,
            ToolsToCraft = ["alpha", "beta"],
            ClearStore = false,
        })).Success);
        Assert.True((await fixture.TaskQueue.SaveCustomParamsAsync(2, new CustomTaskParamsDto
        {
            TaskNames = ["Roguelike", "Reclamation"],
        })).Success);
        Assert.True((await fixture.TaskQueue.SaveAsync()).Success);

        var log = new UiLogService();
        var config = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(fixture.Root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            log,
            fixture.Root);
        var load = await config.LoadOrBootstrapAsync();
        Assert.True(load.LoadedFromExistingConfig);

        var bridge = new FakeBridge();
        var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
        var queue = new TaskQueueFeatureService(session, config);

        var rogue = await queue.GetRoguelikeParamsAsync(0);
        Assert.True(rogue.Success);
        Assert.Equal(7, rogue.Value!.Mode);
        Assert.Equal("Sarkaz", rogue.Value.Theme);
        Assert.False(rogue.Value.InvestmentEnabled);
        Assert.False(rogue.Value.DeepExplorationAutoIterate);
        Assert.Equal("persist,rogue_2,9", rogue.Value.StartWithSeed);

        var reclamation = await queue.GetReclamationParamsAsync(1);
        Assert.True(reclamation.Success);
        Assert.Equal(1, reclamation.Value!.Mode);
        Assert.Equal(1, reclamation.Value.IncrementMode);
        Assert.Equal(4, reclamation.Value.NumCraftBatches);
        Assert.Equal(new[] { "alpha", "beta" }, reclamation.Value.ToolsToCraft);

        var custom = await queue.GetCustomParamsAsync(2);
        Assert.True(custom.Success);
        Assert.Equal(new[] { "Roguelike", "Reclamation" }, custom.Value!.TaskNames);

        await bridge.DisposeAsync();
    }

    [Fact]
    public async Task SaveRoguelike_InvalidSeed_BlocksAndPreservesOriginalParams()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Roguelike", "rogue")).Success);

        var before = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!.ToJsonString();
        var save = await fixture.TaskQueue.SaveRoguelikeParamsAsync(0, new RoguelikeTaskParamsDto
        {
            Mode = 0,
            Theme = "JieGarden",
            StartWithSeedEnabled = true,
            StartWithSeed = "invalid-seed-format",
        });

        Assert.False(save.Success);
        var after = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!.ToJsonString();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task SaveReclamation_DegradesRiskyFields_ButPreservesWpfClearStore()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Reclamation", "reclamation")).Success);

        var save = await fixture.TaskQueue.SaveReclamationParamsAsync(0, new ReclamationTaskParamsDto
        {
            Theme = "UnknownTheme",
            Mode = 1,
            IncrementMode = 77,
            NumCraftBatches = 200000,
            ToolsToCraft = ["tool-a", "tool-b"],
            ClearStore = true,
        });
        Assert.True(save.Success, save.Message);

        var parameters = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Equal("Tales", parameters["theme"]?.GetValue<string>());
        Assert.Equal(1, parameters["mode"]?.GetValue<int>());
        Assert.Equal(0, parameters["increment_mode"]?.GetValue<int>());
        // Frozen WPF sets no upper bound on the NumericUpDown, so large values persist.
        Assert.Equal(200000, parameters["num_craft_batches"]?.GetValue<int>());
        Assert.True(parameters["clear_store"]?.GetValue<bool>());

        parameters["clear_store"] = JsonValue.Create(true);
        parameters["mode"] = JsonValue.Create(1);
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(0, parameters)).Success);

        var validate = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.True(validate.Success, validate.Message);
        Assert.NotNull(validate.Value);
        Assert.DoesNotContain(
            validate.Value!.Issues,
            issue => issue.Code == "ReclamationClearStoreIgnoredInArchive");
        Assert.True(validate.Value.CompiledParams["clear_store"]?.GetValue<bool>());
    }

    [Fact]
    public async Task SaveReclamation_RelaunchAnchorModes_ShouldCompileCoreParams()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Reclamation", "reclamation")).Success);

        var save = await fixture.TaskQueue.SaveReclamationParamsAsync(0, new ReclamationTaskParamsDto
        {
            Theme = "RelaunchAnchor",
            Mode = 32,
            IncrementMode = 1,
            NumCraftBatches = 12,
            ToolsToCraft = ["tool-a"],
            ClearStore = true,
        });
        Assert.True(save.Success, save.Message);

        var parameters = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Equal("RelaunchAnchor", parameters["theme"]?.GetValue<string>());
        Assert.Equal(32, parameters["mode"]?.GetValue<int>());
        Assert.Equal(1, parameters["increment_mode"]?.GetValue<int>());
        Assert.Equal(12, parameters["num_craft_batches"]?.GetValue<int>());
        Assert.False(parameters["clear_store"]?.GetValue<bool>());
        Assert.Empty((JsonArray)parameters["tools_to_craft"]!);

        var compiled = TaskParamCompiler.CompileReclamation(
            new ReclamationTaskParamsDto { Theme = "RelaunchAnchor", Mode = 48 },
            fixture.Config.CurrentConfig.Profiles["Default"],
            fixture.Config.CurrentConfig);

        Assert.Equal("RelaunchAnchor", compiled.Params["theme"]?.GetValue<string>());
        Assert.Equal(48, compiled.Params["mode"]?.GetValue<int>());
        Assert.DoesNotContain(compiled.Issues, issue => issue.Code == "ReclamationModeInvalid");

        var fallback = TaskParamCompiler.CompileReclamation(
            new ReclamationTaskParamsDto { Theme = "RelaunchAnchor", Mode = 1 },
            fixture.Config.CurrentConfig.Profiles["Default"],
            fixture.Config.CurrentConfig);

        Assert.Equal(16, fallback.Params["mode"]?.GetValue<int>());
        Assert.Contains(fallback.Issues, issue => issue.Code == "ReclamationModeInvalid" && !issue.Blocking);
    }

    [Fact]
    public async Task SaveConfig_NormalizesReclamationParamsAcrossProfiles()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var config = fixture.Config.CurrentConfig;
        config.CurrentProfile = "Current";
        config.Profiles["Current"] = new UnifiedProfile();
        config.Profiles["Default"].TaskQueue.Add(new UnifiedTaskItem
        {
            Type = TaskModuleTypes.Reclamation,
            Name = "Reclamation",
            Params = new JsonObject
            {
                ["theme"] = "Tales",
                ["mode"] = 1,
                ["increment_mode"] = 0,
                ["num_craft_batches"] = 16,
                ["tools_to_craft"] = new JsonArray(),
                ["clear_store"] = true,
            },
        });

        await fixture.Config.SaveAsync();

        var task = Assert.Single(config.Profiles["Default"].TaskQueue);
        Assert.True(task.Params["clear_store"]?.GetValue<bool>());
        Assert.Equal(["荧光棒"], ((JsonArray)task.Params["tools_to_craft"]!).Select(node => node!.GetValue<string>()));
        Assert.DoesNotContain(
            fixture.Config.CurrentValidationIssues,
            issue => issue.Code == "ReclamationClearStoreIgnoredInArchive");
    }

    [Fact]
    public async Task SaveReclamation_ZeroCraftBatches_IsPreservedForWpfParity()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Reclamation", "reclamation")).Success);

        var save = await fixture.TaskQueue.SaveReclamationParamsAsync(0, new ReclamationTaskParamsDto
        {
            Theme = "Tales",
            Mode = 1,
            IncrementMode = 0,
            NumCraftBatches = 0,
            ToolsToCraft = ["tool-a"],
            ClearStore = false,
        });
        Assert.True(save.Success, save.Message);

        var parameters = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Equal(0, parameters["num_craft_batches"]?.GetValue<int>());

        var validate = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.True(validate.Success, validate.Message);
        Assert.NotNull(validate.Value);
        Assert.DoesNotContain(validate.Value!.Issues, issue => issue.Code == "ReclamationNumCraftBatchesOutOfRange");
    }

    [Fact]
    public async Task Reclamation_WpfSemicolonLists_PreserveDuplicatesAndLiteralCommas()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var compiled = TaskParamCompiler.CompileReclamation(
            new ReclamationTaskParamsDto
            {
                Theme = "Tales",
                Mode = 1,
                ToolsToCraft = ["工具A", "工具A", "工具B,工具C", "工具D\n工具E"],
            },
            fixture.Config.CurrentConfig.Profiles["Default"],
            fixture.Config.CurrentConfig);

        Assert.Equal(
            ["工具A", "工具A", "工具B,工具C", "工具D\n工具E"],
            ((JsonArray)compiled.Params["tools_to_craft"]!).Select(node => node!.GetValue<string>()));
        Assert.True(compiled.Params["clear_store"]?.GetValue<bool>());

        fixture.Config.CurrentConfig.Profiles["Default"].Values["ClientType"] = "YoStarEN";
        var localizedDefault = TaskParamCompiler.CompileReclamation(
            new ReclamationTaskParamsDto(),
            fixture.Config.CurrentConfig.Profiles["Default"],
            fixture.Config.CurrentConfig);
        Assert.Equal(
            ["Glow Stick"],
            ((JsonArray)localizedDefault.Params["tools_to_craft"]!).Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public async Task Roguelike_WpfDefaultsAndThemeRules_AreReflectedInCoreJson()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];

        var defaultCollectible = TaskParamCompiler.CompileRoguelike(
            new RoguelikeTaskParamsDto { Mode = 4, Theme = "JieGarden" },
            profile,
            fixture.Config.CurrentConfig);
        var defaultRewards = Assert.IsType<JsonObject>(defaultCollectible.Params["collectible_mode_start_list"]);
        Assert.True(defaultRewards["hot_water"]?.GetValue<bool>());
        Assert.False(defaultRewards["hope"]?.GetValue<bool>());
        Assert.False(defaultRewards["ideas"]?.GetValue<bool>());

        var onlyEliteTwo = TaskParamCompiler.CompileRoguelike(
            new RoguelikeTaskParamsDto
            {
                Mode = 4,
                Theme = "Mizuki",
                Squad = "突击战术分队",
                StartWithEliteTwo = true,
                OnlyStartWithEliteTwo = true,
            },
            profile,
            fixture.Config.CurrentConfig);
        Assert.Empty(Assert.IsType<JsonObject>(onlyEliteTwo.Params["collectible_mode_start_list"]));

        var invalidOnlyEliteTwo = TaskParamCompiler.CompileRoguelike(
            new RoguelikeTaskParamsDto
            {
                Mode = 4,
                Theme = "Mizuki",
                OnlyStartWithEliteTwo = true,
            },
            profile,
            fixture.Config.CurrentConfig);
        Assert.False(invalidOnlyEliteTwo.Params["only_start_with_elite_two"]?.GetValue<bool>());
        Assert.Equal(9, Assert.IsType<JsonObject>(invalidOnlyEliteTwo.Params["collectible_mode_start_list"]).Count);
        Assert.Contains(invalidOnlyEliteTwo.Issues, issue => issue.Code == "RoguelikeOnlyEliteTwoRequiresEliteTwo");
    }

    [Fact]
    public async Task Roguelike_CompileEmitsCoreCharListAndDropsLegacySingleFields()
    {
        // WPF dev-v2 (f04fb1645b) sends core_char_list[{name,use_support}] and no
        // longer emits core_char / top-level use_support.
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];

        var compiled = TaskParamCompiler.CompileRoguelike(
            new RoguelikeTaskParamsDto
            {
                Mode = 4,
                Theme = "JieGarden",
                CoreCharList =
                [
                    new RoguelikeStartingOperDto { Name = "Amiya", UseSupport = true },
                    new RoguelikeStartingOperDto { Name = "Kal'tsit", UseSupport = false },
                    new RoguelikeStartingOperDto { Name = "Exusiai", UseSupport = true },
                ],
            },
            profile,
            fixture.Config.CurrentConfig);

        Assert.DoesNotContain("core_char", compiled.Params.Select(pair => pair.Key), StringComparer.Ordinal);
        Assert.DoesNotContain("use_support", compiled.Params.Select(pair => pair.Key), StringComparer.Ordinal);
        var opers = Assert.IsType<JsonArray>(compiled.Params["core_char_list"]);
        Assert.Equal(3, opers.Count);
        Assert.Equal("Amiya", Assert.IsType<JsonObject>(opers[0])["name"]?.GetValue<string>());
        Assert.True(Assert.IsType<JsonObject>(opers[0])["use_support"]?.GetValue<bool>());
        Assert.Equal("Kal'tsit", Assert.IsType<JsonObject>(opers[1])["name"]?.GetValue<string>());
        Assert.False(Assert.IsType<JsonObject>(opers[1])["use_support"]?.GetValue<bool>());
        Assert.Equal("Exusiai", Assert.IsType<JsonObject>(opers[2])["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task Roguelike_CompileFallsBackToLegacySingleCoreCharAsFirstPosition()
    {
        // DTO callers that only fill the pre-dev-v2 CoreChar/UseSupport pair still
        // map to the first starting position.
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];

        var compiled = TaskParamCompiler.CompileRoguelike(
            new RoguelikeTaskParamsDto
            {
                Mode = 0,
                Theme = "JieGarden",
                CoreChar = "Amiya",
                UseSupport = true,
            },
            profile,
            fixture.Config.CurrentConfig);

        var opers = Assert.IsType<JsonArray>(compiled.Params["core_char_list"]);
        var first = Assert.IsType<JsonObject>(Assert.Single(opers));
        Assert.Equal("Amiya", first["name"]?.GetValue<string>());
        Assert.True(first["use_support"]?.GetValue<bool>());
    }

    [Fact]
    public async Task Roguelike_ReadKeepsAllPositions_AndExposesFirstAsLegacyAlias()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var task = new UnifiedTaskItem
        {
            Type = "Roguelike",
            Name = "Roguelike",
            IsEnabled = true,
            Params = new JsonObject
            {
                ["mode"] = 0,
                ["theme"] = "JieGarden",
                ["core_char_list"] = new JsonArray
                {
                    new JsonObject { ["name"] = "Amiya", ["use_support"] = true },
                    new JsonObject { ["name"] = "Kal'tsit", ["use_support"] = false },
                    new JsonObject { ["name"] = "Exusiai", ["use_support"] = true },
                },
            },
        };

        var (dto, _) = TaskParamCompiler.ReadRoguelike(task, strict: false);

        Assert.Equal(3, dto.CoreCharList.Count);
        Assert.Equal("Amiya", dto.CoreChar);
        Assert.True(dto.UseSupport);
        Assert.Equal("Kal'tsit", dto.CoreCharList[1].Name);
        Assert.True(dto.CoreCharList[2].UseSupport);
    }

    [Fact]
    public async Task Roguelike_PersistenceKeepsImportedStartingPositions()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];
        var dto = new RoguelikeTaskParamsDto
        {
            Mode = 0,
            Theme = "JieGarden",
            CoreCharList =
            [
                new RoguelikeStartingOperDto { Name = "Amiya", UseSupport = true },
                new RoguelikeStartingOperDto { Name = "Kal'tsit", UseSupport = false },
                new RoguelikeStartingOperDto { Name = "Exusiai", UseSupport = true },
            ],
        };

        var compiled = TaskParamCompiler.CompileRoguelike(dto, profile, fixture.Config.CurrentConfig);
        var persisted = TaskParamCompiler.BuildRoguelikePersistenceParams(dto, compiled.Params);

        var opers = Assert.IsType<JsonArray>(persisted["core_char_list"]);
        Assert.Equal(3, opers.Count);
        Assert.DoesNotContain("core_char", persisted.Select(pair => pair.Key), StringComparer.Ordinal);
        Assert.DoesNotContain("use_support", persisted.Select(pair => pair.Key), StringComparer.Ordinal);
    }

    [Fact]
    public async Task Roguelike_CollapseModeWithoutSami_FallsBackToExpLikeFrozenWpf()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];

        var sami = TaskParamCompiler.CompileRoguelike(
            new RoguelikeTaskParamsDto { Mode = 5, Theme = "Sami", ExpectedCollapsalParadigms = ["范式A"] },
            profile,
            fixture.Config.CurrentConfig);
        Assert.Equal(5, sami.Params["mode"]?.GetValue<int>());
        Assert.True(sami.Params.ContainsKey("expected_collapsal_paradigms"));
        Assert.DoesNotContain(sami.Issues, issue => issue.Code == "RoguelikeCollapseModeThemeMismatch");

        var sarkaz = TaskParamCompiler.CompileRoguelike(
            new RoguelikeTaskParamsDto { Mode = 5, Theme = "Sarkaz", ExpectedCollapsalParadigms = ["范式A"] },
            profile,
            fixture.Config.CurrentConfig);
        Assert.Equal(0, sarkaz.Params["mode"]?.GetValue<int>());
        Assert.False(sarkaz.Params.ContainsKey("expected_collapsal_paradigms"));
        var mismatch = Assert.Single(sarkaz.Issues, issue => issue.Code == "RoguelikeCollapseModeThemeMismatch");
        Assert.False(mismatch.Blocking);
    }

    [Fact]
    public async Task Roguelike_WpfSemicolonLists_PreserveDuplicatesCommasAndWhitespace()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];

        var foldartal = TaskParamCompiler.CompileRoguelike(
            new RoguelikeTaskParamsDto
            {
                Mode = 4,
                Theme = "Sami",
                Squad = "生活至上分队",
                StartFoldartalListEnabled = true,
                StartFoldartalList = ["板子A", "板子A", "板子B,板子C"],
            },
            profile,
            fixture.Config.CurrentConfig);
        Assert.Equal(
            ["板子A", "板子A", "板子B,板子C"],
            ((JsonArray)foldartal.Params["start_foldartal_list"]!).Select(node => node!.GetValue<string>()));

        var paradigms = TaskParamCompiler.CompileRoguelike(
            new RoguelikeTaskParamsDto
            {
                Mode = 5,
                Theme = "Sami",
                ExpectedCollapsalParadigms = ["范式A", " 范式B", "范式A"],
            },
            profile,
            fixture.Config.CurrentConfig);
        Assert.Equal(
            ["范式A", " 范式B", "范式A"],
            ((JsonArray)paradigms.Params["expected_collapsal_paradigms"]!).Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public async Task Roguelike_CollapseModeEmptyParadigms_OmitsKeySoCoreKeepsRareDefaults()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];

        var empty = TaskParamCompiler.CompileRoguelike(
            new RoguelikeTaskParamsDto { Mode = 5, Theme = "Sami" },
            profile,
            fixture.Config.CurrentConfig);
        Assert.Equal(5, empty.Params["mode"]?.GetValue<int>());
        Assert.False(empty.Params.ContainsKey("expected_collapsal_paradigms"));

        var emptyEntriesOnly = TaskParamCompiler.CompileRoguelike(
            new RoguelikeTaskParamsDto { Mode = 5, Theme = "Sami", ExpectedCollapsalParadigms = ["", ""] },
            profile,
            fixture.Config.CurrentConfig);
        Assert.False(emptyEntriesOnly.Params.ContainsKey("expected_collapsal_paradigms"));

        var filled = TaskParamCompiler.CompileRoguelike(
            new RoguelikeTaskParamsDto { Mode = 5, Theme = "Sami", ExpectedCollapsalParadigms = ["范式A"] },
            profile,
            fixture.Config.CurrentConfig);
        Assert.Equal(
            ["范式A"],
            ((JsonArray)filled.Params["expected_collapsal_paradigms"]!).Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public async Task Roguelike_FindPlaytimeWithoutJieGarden_ClampsModeAndKeepsThemeLikeFrozenWpf()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];

        var compiled = TaskParamCompiler.CompileRoguelike(
            new RoguelikeTaskParamsDto { Mode = 20001, Theme = "Sami", FindPlayTimeTarget = 3 },
            profile,
            fixture.Config.CurrentConfig);
        Assert.Equal(0, compiled.Params["mode"]?.GetValue<int>());
        Assert.Equal("Sami", compiled.Params["theme"]?.GetValue<string>());
        Assert.False(compiled.Params.ContainsKey("find_playTime_target"));
        var mismatch = Assert.Single(compiled.Issues, issue => issue.Code == "RoguelikeFindPlaytimeThemeMismatch");
        Assert.False(mismatch.Blocking);
    }

    [Fact]
    public async Task Roguelike_StoredOnlyEliteTwoOnNonEliteTwoTheme_EmitsEmptyStartListLikeFrozenWpf()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var profile = fixture.Config.CurrentConfig.Profiles["Default"];

        // Frozen WPF keys the reward skip on the stored flags for any non-Phantom
        // professional squad, so Sarkaz/JieGarden also emit an empty start list.
        var sarkaz = TaskParamCompiler.CompileRoguelike(
            new RoguelikeTaskParamsDto
            {
                Mode = 4,
                Theme = "Sarkaz",
                Squad = "突击战术分队",
                StartWithEliteTwo = true,
                OnlyStartWithEliteTwo = true,
                CollectibleModeStartList = new RoguelikeCollectibleStartListDto { HotWater = true, Ideas = true },
            },
            profile,
            fixture.Config.CurrentConfig);
        Assert.False(sarkaz.Params["start_with_elite_two"]?.GetValue<bool>());
        Assert.False(sarkaz.Params["only_start_with_elite_two"]?.GetValue<bool>());
        Assert.Empty(Assert.IsType<JsonObject>(sarkaz.Params["collectible_mode_start_list"]));

        // Phantom is excluded from the frozen WPF predicate and keeps the full list.
        var phantom = TaskParamCompiler.CompileRoguelike(
            new RoguelikeTaskParamsDto
            {
                Mode = 4,
                Theme = "Phantom",
                Squad = "突击战术分队",
                StartWithEliteTwo = true,
                OnlyStartWithEliteTwo = true,
            },
            profile,
            fixture.Config.CurrentConfig);
        Assert.Equal(9, Assert.IsType<JsonObject>(phantom.Params["collectible_mode_start_list"]).Count);
    }

    [Fact]
    public async Task SaveRoguelike_PersistsModeAndInvestmentGatedSettingsLikeFrozenWpf()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Roguelike", "rogue")).Success);

        var collectibleSave = await fixture.TaskQueue.SaveRoguelikeParamsAsync(0, new RoguelikeTaskParamsDto
        {
            Mode = 4,
            Theme = "Sami",
            Squad = "突击战术分队",
            InvestmentEnabled = true,
            InvestmentWithMoreScore = true,
            InvestmentsCount = 77,
            StopWhenInvestmentFull = true,
            CollectibleModeShopping = true,
            CollectibleModeSquad = "生活至上分队",
            StartWithEliteTwo = true,
            OnlyStartWithEliteTwo = true,
            CollectibleModeStartList = new RoguelikeCollectibleStartListDto { HotWater = true, Shield = true, Hope = false, Ideas = false },
            MonthlySquadAutoIterate = false,
            MonthlySquadCheckComms = false,
            DeepExplorationAutoIterate = false,
            FindPlayTimeTarget = 2,
            StopAtFinalBoss = true,
            StopAtMaxLevel = true,
        });
        Assert.True(collectibleSave.Success, collectibleSave.Message);

        // Simulate the VM round-trip when the user switches to Exp mode and disables
        // investment: hidden controls keep their values in the DTO like frozen WPF.
        var switched = (await fixture.TaskQueue.GetRoguelikeParamsAsync(0)).Value!;
        switched.Mode = 0;
        switched.InvestmentEnabled = false;
        var switchedSave = await fixture.TaskQueue.SaveRoguelikeParamsAsync(0, switched);
        Assert.True(switchedSave.Success, switchedSave.Message);

        var reloaded = (await fixture.TaskQueue.GetRoguelikeParamsAsync(0)).Value!;
        Assert.Equal(0, reloaded.Mode);
        Assert.False(reloaded.InvestmentEnabled);
        Assert.True(reloaded.InvestmentWithMoreScore);
        Assert.Equal(77, reloaded.InvestmentsCount);
        Assert.True(reloaded.StopWhenInvestmentFull);
        Assert.True(reloaded.StartWithEliteTwo);
        Assert.True(reloaded.OnlyStartWithEliteTwo);
        Assert.True(reloaded.CollectibleModeShopping);
        Assert.Equal("生活至上分队", reloaded.CollectibleModeSquad);
        Assert.True(reloaded.CollectibleModeStartList.Shield);
        Assert.False(reloaded.CollectibleModeStartList.Hope);
        Assert.False(reloaded.MonthlySquadAutoIterate);
        Assert.False(reloaded.MonthlySquadCheckComms);
        Assert.False(reloaded.DeepExplorationAutoIterate);
        Assert.Equal(2, reloaded.FindPlayTimeTarget);
        Assert.True(reloaded.StopAtFinalBoss);

        // The Core-facing compiled form stays gated while mode 0 is active.
        var validate = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.True(validate.Success, validate.Message);
        var core = validate.Value!.CompiledParams;
        Assert.False(core.ContainsKey("collectible_mode_start_list"));
        Assert.False(core.ContainsKey("start_with_elite_two"));
        Assert.False(core.ContainsKey("monthly_squad_auto_iterate"));
        Assert.False(core.ContainsKey("deep_exploration_auto_iterate"));
        Assert.False(core.ContainsKey("find_playTime_target"));
        Assert.False(core.ContainsKey("investments_count"));
        Assert.True(core["stop_at_final_boss"]?.GetValue<bool>());

        // Switching back to collectible mode restores the preserved intent.
        reloaded.Mode = 4;
        reloaded.InvestmentEnabled = true;
        var backSave = await fixture.TaskQueue.SaveRoguelikeParamsAsync(0, reloaded);
        Assert.True(backSave.Success, backSave.Message);

        var restored = await fixture.TaskQueue.ValidateTaskAsync(0);
        var coreBack = restored.Value!.CompiledParams;
        Assert.True(coreBack["start_with_elite_two"]?.GetValue<bool>());
        Assert.True(coreBack["only_start_with_elite_two"]?.GetValue<bool>());
        Assert.Empty(Assert.IsType<JsonObject>(coreBack["collectible_mode_start_list"]));
        Assert.Equal(77, coreBack["investments_count"]?.GetValue<int>());
        Assert.True(coreBack["stop_when_investment_full"]?.GetValue<bool>());
        Assert.False(coreBack["investment_with_more_score"]?.GetValue<bool>());
    }

    [Fact]
    public async Task SaveRoguelike_PreservesFoldartalAcrossThemeSwitchLikeFrozenWpf()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Roguelike", "rogue")).Success);

        var save = await fixture.TaskQueue.SaveRoguelikeParamsAsync(0, new RoguelikeTaskParamsDto
        {
            Mode = 4,
            Theme = "Sami",
            Squad = "生活至上分队",
            FirstFloorFoldartalEnabled = true,
            FirstFloorFoldartal = "号角",
            StartFoldartalListEnabled = true,
            StartFoldartalList = ["火炬", "涟漪"],
        });
        Assert.True(save.Success, save.Message);

        var samiValidate = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.Equal("号角", samiValidate.Value!.CompiledParams["first_floor_foldartal"]?.GetValue<string>());

        var switched = (await fixture.TaskQueue.GetRoguelikeParamsAsync(0)).Value!;
        switched.Theme = "Sarkaz";
        switched.Squad = "指挥分队";
        var sarkazSave = await fixture.TaskQueue.SaveRoguelikeParamsAsync(0, switched);
        Assert.True(sarkazSave.Success, sarkazSave.Message);

        // The stored form keeps the hidden Sami foldartal intent...
        var sarkazParams = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.Equal("号角", sarkazParams["first_floor_foldartal"]?.GetValue<string>());
        Assert.Equal(
            ["火炬", "涟漪"],
            ((JsonArray)sarkazParams["start_foldartal_list"]!).Select(node => node!.GetValue<string>()));

        // ...while the Core-facing compiled form gates emission like frozen WPF.
        var sarkazValidate = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.False(sarkazValidate.Value!.CompiledParams.ContainsKey("first_floor_foldartal"));
        Assert.False(sarkazValidate.Value.CompiledParams.ContainsKey("start_foldartal_list"));

        var restored = (await fixture.TaskQueue.GetRoguelikeParamsAsync(0)).Value!;
        Assert.Equal("号角", restored.FirstFloorFoldartal);
        Assert.Equal(["火炬", "涟漪"], restored.StartFoldartalList);

        restored.Theme = "Sami";
        restored.Squad = "生活至上分队";
        Assert.True((await fixture.TaskQueue.SaveRoguelikeParamsAsync(0, restored)).Success);
        var samiAgain = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.Equal("号角", samiAgain.Value!.CompiledParams["first_floor_foldartal"]?.GetValue<string>());
        Assert.Equal(
            ["火炬", "涟漪"],
            ((JsonArray)samiAgain.Value.CompiledParams["start_foldartal_list"]!).Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public async Task SaveRoguelike_DisabledFoldartalAndSeedToggles_PreserveContentLikeFrozenWpf()
    {
        // Frozen WPF stores each foldartal/seed toggle independently of its content
        // (RoguelikeTask.cs), so disabling a toggle must not destroy the text.
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Roguelike", "rogue")).Success);

        var save = await fixture.TaskQueue.SaveRoguelikeParamsAsync(0, new RoguelikeTaskParamsDto
        {
            Mode = 4,
            Theme = "Sami",
            Squad = "生活至上分队",
            FirstFloorFoldartalEnabled = true,
            FirstFloorFoldartal = "号角",
            StartFoldartalListEnabled = true,
            StartFoldartalList = ["火炬"],
            StartWithSeedEnabled = true,
            StartWithSeed = "seed,rogue_1,3",
        });
        Assert.True(save.Success, save.Message);

        var enabled = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.Equal("号角", enabled.Value!.CompiledParams["first_floor_foldartal"]?.GetValue<string>());
        Assert.Equal("seed,rogue_1,3", enabled.Value.CompiledParams["start_with_seed"]?.GetValue<string>());

        var loaded = (await fixture.TaskQueue.GetRoguelikeParamsAsync(0)).Value!;
        loaded.FirstFloorFoldartalEnabled = false;
        loaded.StartFoldartalListEnabled = false;
        loaded.StartWithSeedEnabled = false;
        Assert.True((await fixture.TaskQueue.SaveRoguelikeParamsAsync(0, loaded)).Success);

        // Stored form keeps toggle and content independently; Core JSON omits both.
        var stored = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.False(stored["first_floor_foldartal_enabled"]?.GetValue<bool>());
        Assert.Equal("号角", stored["first_floor_foldartal"]?.GetValue<string>());
        Assert.False(stored["start_foldartal_list_enabled"]?.GetValue<bool>());
        Assert.Equal(["火炬"], ((JsonArray)stored["start_foldartal_list"]!).Select(node => node!.GetValue<string>()));
        Assert.False(stored["start_with_seed_enabled"]?.GetValue<bool>());
        Assert.Equal("seed,rogue_1,3", stored["start_with_seed"]?.GetValue<string>());

        var disabled = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.False(disabled.Value!.CompiledParams.ContainsKey("first_floor_foldartal"));
        Assert.False(disabled.Value.CompiledParams.ContainsKey("start_foldartal_list"));
        Assert.False(disabled.Value.CompiledParams.ContainsKey("start_with_seed"));

        // Re-enabling the toggles restores the stored content like frozen WPF.
        var restored = (await fixture.TaskQueue.GetRoguelikeParamsAsync(0)).Value!;
        Assert.False(restored.FirstFloorFoldartalEnabled);
        Assert.Equal("号角", restored.FirstFloorFoldartal);
        restored.FirstFloorFoldartalEnabled = true;
        restored.StartFoldartalListEnabled = true;
        restored.StartWithSeedEnabled = true;
        Assert.True((await fixture.TaskQueue.SaveRoguelikeParamsAsync(0, restored)).Success);
        var reenabled = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.Equal("号角", reenabled.Value!.CompiledParams["first_floor_foldartal"]?.GetValue<string>());
        Assert.Equal("seed,rogue_1,3", reenabled.Value.CompiledParams["start_with_seed"]?.GetValue<string>());
    }

    [Fact]
    public async Task SaveRoguelike_DisabledSeedToggle_AllowsPreservedInvalidSeedLikeFrozenWpf()
    {
        // A disabled seed is never emitted, so its stored text must not block saves.
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Roguelike", "rogue")).Success);

        var save = await fixture.TaskQueue.SaveRoguelikeParamsAsync(0, new RoguelikeTaskParamsDto
        {
            Mode = 0,
            Theme = "JieGarden",
            StartWithSeedEnabled = false,
            StartWithSeed = "invalid-seed-format",
        });
        Assert.True(save.Success, save.Message);

        var stored = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        Assert.False(stored["start_with_seed_enabled"]?.GetValue<bool>());
        Assert.Equal("invalid-seed-format", stored["start_with_seed"]?.GetValue<string>());

        var validated = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.False(validated.Value!.CompiledParams.ContainsKey("start_with_seed"));
    }

    public async Task SaveCustom_PreservesWpfTaskNamesAndOrder()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Custom", "custom")).Success);

        var save = await fixture.TaskQueue.SaveCustomParamsAsync(0, new CustomTaskParamsDto
        {
            TaskNames = ["FightTask", "Mall", "Mall", "  [Core]: \"literal\"  "],
        });
        Assert.True(save.Success, save.Message);

        var parameters = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        var taskNames = Assert.IsType<JsonArray>(parameters["task_names"]);
        Assert.Equal(
            new[] { "FightTask", "Mall", "Mall", "[Core]: \"literal\"" },
            taskNames.Select(node => node?.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task ValidateTask_ModuleC_CatchesTypeCorruptionAsBlocking()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Roguelike", "rogue")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Reclamation", "reclamation")).Success);
        Assert.True((await fixture.TaskQueue.AddTaskAsync("Custom", "custom")).Success);

        var rogueParams = (await fixture.TaskQueue.GetTaskParamsAsync(0)).Value!;
        rogueParams["collectible_mode_start_list"] = new JsonArray("invalid");
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(0, rogueParams)).Success);

        var reclamationParams = (await fixture.TaskQueue.GetTaskParamsAsync(1)).Value!;
        reclamationParams["tools_to_craft"] = new JsonObject { ["bad"] = true };
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(1, reclamationParams)).Success);

        var customParams = (await fixture.TaskQueue.GetTaskParamsAsync(2)).Value!;
        customParams["task_names"] = new JsonObject { ["bad"] = "Fight" };
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(2, customParams)).Success);

        var rogueValidate = await fixture.TaskQueue.ValidateTaskAsync(0);
        Assert.True(rogueValidate.Success);
        Assert.NotNull(rogueValidate.Value);
        Assert.Contains(rogueValidate.Value!.Issues, issue => issue.Code == "TaskFieldTypeInvalid" && issue.Blocking);

        var reclamationValidate = await fixture.TaskQueue.ValidateTaskAsync(1);
        Assert.True(reclamationValidate.Success);
        Assert.NotNull(reclamationValidate.Value);
        Assert.Contains(reclamationValidate.Value!.Issues, issue => issue.Code == "TaskFieldTypeInvalid" && issue.Blocking);

        var customValidate = await fixture.TaskQueue.ValidateTaskAsync(2);
        Assert.True(customValidate.Success);
        Assert.NotNull(customValidate.Value);
        Assert.Contains(customValidate.Value!.Issues, issue => issue.Code == "TaskFieldTypeInvalid" && issue.Blocking);
    }

    [Fact]
    public void TaskModuleCViews_AreBindingBased_NoStaticBusinessDefaults()
    {
        var root = ResolveRepoRoot();
        var roguelikeView = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", "RoguelikeSettingsView.axaml"));
        var reclamationView = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", "ReclamationSettingsView.axaml"));
        var customView = File.ReadAllText(Path.Combine(root, "App", "Features", "TaskQueue", "CustomSettingsView.axaml"));

        Assert.Contains("SelectedValue=\"{Binding Theme}\"", roguelikeView);
        Assert.Contains("SelectedValue=\"{Binding Mode}\"", roguelikeView);
        Assert.Contains("{Binding DelayAbortUntilCombatComplete}", roguelikeView);
        Assert.Contains("{Binding StatusMessage}", roguelikeView);
        Assert.Contains("CheckBoxIsEnabled=\"{Binding CanUseSupport}\"", roguelikeView);
        Assert.DoesNotContain("<CheckBox Content=", roguelikeView, StringComparison.Ordinal);
        Assert.DoesNotContain("傀影与猩红孤钻", roguelikeView);
        Assert.DoesNotContain("SelectedIndex=\"0\"", roguelikeView);

        Assert.Contains("SelectedValue=\"{Binding Theme}\"", reclamationView);
        Assert.Contains("{Binding NumCraftBatches}", reclamationView);
        Assert.Contains("{Binding StatusMessage}", reclamationView);
        Assert.DoesNotContain("萨尔贡沙洲遗闻", reclamationView);

        Assert.Contains("{Binding TaskNamesText", customView);
        Assert.Contains("{Binding TaskNamesPreview}", customView);
        Assert.Contains("{Binding StatusMessage}", customView);
        Assert.DoesNotContain("自定义任务名规则", customView);
    }

    [Fact]
    public void Localization_HasModuleCKeys_ForAllSupportedLanguages()
    {
        var text = new LocalizedTextMap();
        var languages = new[] { "zh-cn", "zh-tw", "en-us", "ja-jp", "ko-kr", "pallas" };
        var requiredKeys = new[]
        {
            "Roguelike.Title",
            "Roguelike.Theme",
            "Roguelike.Mode",
            "Roguelike.InvestmentEnabled",
            "Roguelike.Collectible.Dice",
            "Roguelike.DelayAbortUntilCombatComplete",
            "RoguelikeThemePhantom",
            "Reclamation.Title",
            "Reclamation.ToolsToCraft",
            "Reclamation.Option.Mode.Archive",
            "Reclamation.Option.Mode.RA1",
            "Reclamation.Option.Theme.RelaunchAnchor",
            "Custom.Title",
            "Custom.TaskNamesPreview",
            "Common.LoadingTaskHint",
            "Common.ValidationIssues",
            "TaskQueue.Validation.Clean",
            "TaskQueue.Validation.BlockingCount",
            "TaskQueue.Error.BlockingValidation",
            "TaskQueue.Status.ParamsLoaded",
            "Issue.TaskFieldTypeInvalid",
            "Issue.DelimitedInputParseFailed",
            "Issue.RoguelikeModeInvalid",
            "Issue.CustomTaskNameInvalid",
            "Issue.ReclamationClearStoreIgnoredInArchive",
        };

        foreach (var language in languages)
        {
            text.Language = language;
            foreach (var key in requiredKeys)
            {
                var value = text[key];
                Assert.False(string.Equals(value, key, StringComparison.Ordinal), $"Missing `{key}` for `{language}`");
            }
        }
    }

    private static string ResolveRepoRoot()
    {
        return TestRepoLayout.GetMaaUnifiedRoot();
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(
            string root,
            UnifiedConfigurationService config,
            TaskQueueFeatureService taskQueue,
            FakeBridge bridge)
        {
            Root = root;
            Config = config;
            TaskQueue = taskQueue;
            Bridge = bridge;
        }

        public string Root { get; }

        public UnifiedConfigurationService Config { get; }

        public TaskQueueFeatureService TaskQueue { get; }

        public FakeBridge Bridge { get; }

        public static async Task<TestFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));

            var log = new UiLogService();
            var config = new UnifiedConfigurationService(
                new AvaloniaJsonConfigStore(root),
                new GuiNewJsonConfigImporter(),
                new GuiJsonConfigImporter(),
                log,
                root);
            await config.LoadOrBootstrapAsync();

            var bridge = new FakeBridge();
            var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
            var taskQueue = new TaskQueueFeatureService(session, config);
            return new TestFixture(root, config, taskQueue, bridge);
        }

        public async ValueTask DisposeAsync()
        {
            await Bridge.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temporary folder.
            }
        }
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _channel = Channel.CreateUnbounded<CoreCallbackEvent>();
        private int _taskId;

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<int>.Ok(Interlocked.Increment(ref _taskId)));

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

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
}
