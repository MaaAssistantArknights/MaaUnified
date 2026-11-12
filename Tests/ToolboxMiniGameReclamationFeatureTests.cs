using System.Text.Json.Nodes;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Tests;

/// <summary>
/// WPF dev-v2 66e003377c / a105eccd05 / 8a6ca5f276 / 1bc83ba6e6: Reclamation moved from the
/// task queue into the toolbox mini-game page, the mini-game list is categorized, and the
/// AutoRaisePotential / EventShop entries carry their own parameters.
/// </summary>
public sealed class ToolboxMiniGameReclamationFeatureTests
{
    [Fact]
    public async Task ToolboxPage_ShouldLoadMigratedReclamationSettings()
    {
        var globalSeeds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ToolboxSettingKeys.ReclamationTheme] = "RelaunchAnchor",
            [ToolboxSettingKeys.ReclamationMode] = "32",
            [ToolboxSettingKeys.ReclamationIncrementMode] = "1",
            [ToolboxSettingKeys.ReclamationMaxCraftCountPerRound] = "8",
            [ToolboxSettingKeys.ReclamationToolToCraft] = "荧光棒;工具A",
            [ToolboxSettingKeys.ReclamationClearStore] = "False",
            [LegacyConfigurationKeys.MiniGameTaskName] = "Reclamation",
        };

        await using var fixture = await ToolboxTestFixture.CreateAsync(globalSeeds);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        Assert.True(vm.IsMiniGameReclamation);
        Assert.Equal("RelaunchAnchor", vm.ReclamationTheme);
        Assert.True(vm.IsReclamationRelaunchAnchor);
        Assert.Equal(32, vm.ReclamationMode);
        Assert.Equal(1, vm.ReclamationIncrementMode);
        Assert.Equal(8, vm.ReclamationMaxCraftCountPerRound);
        Assert.Equal("荧光棒;工具A", vm.ReclamationToolToCraft);
        Assert.False(vm.ReclamationClearStore);
        Assert.Equal(new[] { "16", "48", "32" }, vm.ReclamationModeOptions.Select(option => option.Value).ToArray());

        // The mini-game selector lists the Reclamation entry with its permanent category.
        var entry = Assert.Single(vm.MiniGameTaskList, item => item.Value == "Reclamation");
        Assert.Equal("生息演算", entry.Display);
        Assert.Equal("常驻活动", entry.Category);
    }

    [Fact]
    public async Task StartReclamation_ShouldDispatchReclamationCoreTaskWithToolboxSettings()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        vm.MiniGameTaskName = "Reclamation";
        vm.ReclamationTheme = "Tales";
        vm.ReclamationMode = 1;
        vm.ReclamationIncrementMode = 1;
        vm.ReclamationMaxCraftCountPerRound = 4;
        vm.ReclamationToolToCraft = "荧光棒;工具A";
        vm.ReclamationClearStore = false;

        await vm.StartMiniGameAsync();

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("Reclamation", appended.Type);
        var parameters = JsonNode.Parse(appended.ParamsJson)!.AsObject();
        Assert.Equal("Tales", parameters["theme"]!.GetValue<string>());
        Assert.Equal(1, parameters["mode"]!.GetValue<int>());
        Assert.Equal(1, parameters["increment_mode"]!.GetValue<int>());
        Assert.Equal(4, parameters["num_craft_batches"]!.GetValue<int>());
        Assert.Equal(
            new[] { "荧光棒", "工具A" },
            parameters["tools_to_craft"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray());
        Assert.False(parameters["clear_store"]!.GetValue<bool>());
    }

    [Fact]
    public async Task StartAutoRaisePotential_ShouldSendNormalTokenParameter()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        vm.MiniGameTaskName = "MiniGame@AutoRaisePotential@Begin";
        vm.AutoRaisePotentialUseNormalToken = true;

        await vm.StartMiniGameAsync();

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("Custom", appended.Type);
        var payload = JsonNode.Parse(appended.ParamsJson)!.AsObject();
        Assert.Equal("MiniGame@AutoRaisePotential@Begin", payload["task_names"]![0]!.GetValue<string>());
        Assert.True(payload["params"]!["auto_raise_potential"]!["use_normal_token"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("Official", "龙门币")]
    [InlineData("YoStarEN", "LMD")]
    [InlineData("YoStarJP", "龍門幣")]
    public async Task StartEventShop_ShouldSendClientLanguageBlacklist(string clientType, string expectedKeyword)
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        fixture.ConnectionState.ClientType = clientType;
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        vm.MiniGameTaskName = "SS@Store@Begin";
        vm.EventShopBlackListLmd = true;
        vm.EventShopBlackListOther = true;
        vm.EventShopBlackList = "自定义商品;自定义商品; ;";

        await vm.StartMiniGameAsync();

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("Custom", appended.Type);
        var payload = JsonNode.Parse(appended.ParamsJson)!.AsObject();
        var blacklist = payload["params"]!["event_shop"]!["blacklist"]!.AsArray()
            .Select(item => item!.GetValue<string>())
            .ToArray();
        Assert.Contains(expectedKeyword, blacklist);
        Assert.Equal(1, blacklist.Count(keyword => keyword == "自定义商品"));
    }

    [Fact]
    public async Task StartEventShop_WithoutBlacklist_ShouldKeepPlainMiniGamePayload()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        vm.MiniGameTaskName = "SS@Store@Begin";
        vm.EventShopBlackListLmd = false;
        vm.EventShopBlackListOther = false;

        await vm.StartMiniGameAsync();

        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var payload = JsonNode.Parse(appended.ParamsJson)!.AsObject();
        Assert.Equal("SS@Store@Begin", payload["task_names"]![0]!.GetValue<string>());
        Assert.False(payload.ContainsKey("params"));
    }

    [Fact]
    public void MiniGameCatalog_ShouldListCurrentEventBeforePermanentCategories()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-minigame-catalog", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "gui"));
        File.WriteAllText(
            Path.Combine(root, "gui", "StageActivityV2.json"),
            """
            {
              "Official": {
                "miniGame": [
                  {
                    "Display": "当期小游戏",
                    "Value": "MiniGame@Current@Begin",
                    "Tip": "当期提示",
                    "Activity": {
                      "UtcStartTime": "2020/01/01 00:00:00",
                      "UtcExpireTime": "2099/01/01 00:00:00",
                      "TimeZone": 0
                    }
                  }
                ]
              }
            }
            """);

        using var _ = ToolboxAssetCatalog.PushTestBaseDirectoriesForTests(root);
        var entries = ToolboxAssetCatalog.GetMiniGameEntries("Official", "zh-cn", coreVersion: null);

        Assert.Equal("MiniGame@Current@Begin", entries[0].Value);
        Assert.True(entries[0].IsCurrentEvent);
        Assert.Equal("当期活动", entries[0].Category);

        var secretFront = Assert.Single(entries, entry => entry.Value == "MiniGame@SecretFront");
        Assert.Equal("常驻活动", secretFront.Category);
        Assert.False(secretFront.IsCurrentEvent);

        var reclamation = Assert.Single(entries, entry => entry.Value == "Reclamation");
        Assert.Equal("生息演算", reclamation.Display);
        Assert.Equal("常驻活动", reclamation.Category);

        var eventShop = Assert.Single(entries, entry => entry.Value == "SS@Store@Begin");
        Assert.Equal("常驻功能", eventShop.Category);

        Directory.Delete(root, recursive: true);
    }
}
