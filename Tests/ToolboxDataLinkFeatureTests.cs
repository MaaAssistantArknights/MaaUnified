using System.Text.Json.Nodes;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Models;
using MAAUnified.CoreBridge;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Tests;

/// <summary>
/// Toolbox data-link parity (WPF dev-v2):
/// P1-29 1dda1ec985 — depot recognition/drop tracking of the five base materials;
/// P1-30 0302381585 — empty inventory protection on both recognition and drop paths.
/// </summary>
public sealed class ToolboxDataLinkFeatureTests
{
    [Fact]
    public async Task Toolbox_ResourceBroken_ShouldBlockEveryCoreToolAndSurfaceRepairGuidance()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        fixture.Runtime.MarkResourceBroken("TaskQueue.SubTaskError.UnknownTask:MissingTaskA");
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();
        var blockedText = vm.RootTexts.GetOrDefault("ResourceBrokenTaskBlocked", "ResourceBrokenTaskBlocked");

        // WPF a413ff2cfd: every toolbox Core entry shares the unified start gate and reports
        // the localized reason instead of silently failing after a connect attempt.
        await vm.StartDepotAsync();
        Assert.Equal(UiErrorCode.ResourceBroken, vm.LastExecutionErrorCode);
        Assert.Contains(blockedText, vm.ResultText, StringComparison.Ordinal);
        Assert.Contains(blockedText, vm.LastErrorMessage, StringComparison.Ordinal);

        await vm.StartRecruitAsync();
        Assert.Equal(UiErrorCode.ResourceBroken, vm.LastExecutionErrorCode);

        await vm.TogglePeepAsync();
        Assert.Equal(UiErrorCode.ResourceBroken, vm.LastExecutionErrorCode);
        Assert.False(vm.Peeping);

        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Equal(0, fixture.Bridge.ConnectCallCount);
        Assert.Equal(0, fixture.Bridge.StartCallCount);
    }

    [Fact]
    public async Task ApplyRuntimeCallback_DepotDone_ShouldReportBaseMaterials()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartDepotAsync();
        vm.ApplyRuntimeCallback(CreateDepotResultCallback(
            "{\"4001\":123,\"4002\":3,\"4003\":45,\"4006\":7,\"3003\":9,\"2001\":1}"));

        // WPF parity (1dda1ec985): recognition output is not filtered, so the five basic
        // materials are visible with their counts.
        Assert.Contains(vm.DepotResult, item => item.Id == "4001" && item.Count == 123);
        Assert.Contains(vm.DepotResult, item => item.Id == "4002" && item.Count == 3);
        Assert.Contains(vm.DepotResult, item => item.Id == "4003" && item.Count == 45);
        Assert.Contains(vm.DepotResult, item => item.Id == "4006" && item.Count == 7);
        Assert.Contains(vm.DepotResult, item => item.Id == "3003" && item.Count == 9);

        await WaitForSettingAsync(fixture, LegacyConfigurationKeys.DepotResult, "4001");
        Assert.Contains("4001", ReadGlobalString(fixture, LegacyConfigurationKeys.DepotResult), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyRuntimeCallback_StageDrops_ShouldAccumulateBaseMaterials()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LegacyConfigurationKeys.DepotResult] = "{\"done\":true,\"data\":\"{\\\"2001\\\":5}\",\"syncTime\":\"2026-03-12T08:00:00.0000000+00:00\"}",
            });
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        vm.ApplyRuntimeCallback(CreateStageDropsCallback(
            ("4001", "龙门币", 100),
            ("4003", "合成玉", 50),
            ("4006", "采购凭证", 7),
            ("4002", "源石", 3),
            ("3003", "赤金", 2)));

        // WPF parity (1dda1ec985): 4001/4003/4006 were removed from the exclusion list so drops
        // update them instead of leaving recognition and drop tracking disagreeing.
        Assert.Contains(vm.DepotResult, item => item.Id == "4001" && item.Count == 100);
        Assert.Contains(vm.DepotResult, item => item.Id == "4003" && item.Count == 50);
        Assert.Contains(vm.DepotResult, item => item.Id == "4006" && item.Count == 7);
        Assert.Contains(vm.DepotResult, item => item.Id == "4002" && item.Count == 3);
        Assert.Contains(vm.DepotResult, item => item.Id == "3003" && item.Count == 2);
        Assert.Contains(vm.DepotResult, item => item.Id == "2001" && item.Count == 5);

        await WaitForSettingAsync(fixture, LegacyConfigurationKeys.DepotResult, "4001");
    }

    [Fact]
    public async Task ApplyRuntimeCallback_StageDrops_ShouldStillExcludeFurnitureCarbonAndExp()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LegacyConfigurationKeys.DepotResult] = "{\"done\":true,\"data\":\"{\\\"2001\\\":5}\"}",
            });
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        vm.ApplyRuntimeCallback(CreateStageDropsCallback(
            ("3401", "furni", 4),
            ("3112", "碳素", 8),
            ("5001", "经验", 2),
            ("furni", "furni", 1)));

        Assert.DoesNotContain(vm.DepotResult, item => item.Id is "3401" or "3112" or "5001" or "furni");
        Assert.Equal("2001", Assert.Single(vm.DepotResult).Id);
    }

    [Fact]
    public async Task ApplyRuntimeCallback_DepotDone_WhenResultEmpty_ShouldKeepCacheAndSyncTime()
    {
        const string seededPayload = "{\"done\":true,\"data\":\"{\\\"2001\\\":123}\",\"syncTime\":\"2026-03-12T08:00:00.0000000+00:00\"}";
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LegacyConfigurationKeys.DepotResult] = seededPayload,
            });
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();
        var seededSyncTime = vm.LastDepotSyncTime;
        Assert.NotNull(seededSyncTime);

        await vm.StartDepotAsync();
        vm.ApplyRuntimeCallback(CreateDepotResultCallback("{}"));

        // WPF parity (0302381585): an empty recognition result means the flow failed (wrong
        // screen etc.). Keep both the in-memory inventory and the persisted cache untouched.
        var item = Assert.Single(vm.DepotResult);
        Assert.Equal("2001", item.Id);
        Assert.Equal(123, item.Count);
        Assert.Equal(seededSyncTime, vm.LastDepotSyncTime);

        await Task.Delay(120);
        var persisted = ReadGlobalString(fixture, LegacyConfigurationKeys.DepotResult);
        Assert.Contains("2001", persisted, StringComparison.Ordinal);
        Assert.Contains("2026-03-12T08:00:00", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyRuntimeCallback_DepotDone_WhenNoCache_ShouldNotPersistEmptyResult()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartDepotAsync();
        vm.ApplyRuntimeCallback(CreateDepotResultCallback("{}"));

        Assert.Empty(vm.DepotResult);
        Assert.False(vm.HasDepotResult);
        Assert.False(
            fixture.Config.CurrentConfig.GlobalValues.ContainsKey(LegacyConfigurationKeys.DepotResult));
        Assert.False(string.IsNullOrWhiteSpace(vm.DepotInfo));
    }

    [Fact]
    public async Task ApplyRuntimeCallback_StageDrops_WhenNoDepotBaseline_ShouldNotCreateEntries()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        vm.ApplyRuntimeCallback(CreateStageDropsCallback(("2001", "源岩", 4), ("4001", "龙门币", 100)));

        // WPF parity (0302381585): incrementing an empty baseline would fabricate a partial
        // inventory, so the depot stays "no data".
        Assert.Empty(vm.DepotResult);
        Assert.False(
            fixture.Config.CurrentConfig.GlobalValues.ContainsKey(LegacyConfigurationKeys.DepotResult));
    }

    [Fact]
    public async Task OperBoxExport_ShouldProduceJsonMarkdownAndCsvFromSelectedFormat()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LegacyConfigurationKeys.OperBoxData] = BuildSeededOperBoxPayload(),
            });
        using var catalogScope = PushOperBoxCatalog(fixture.Root);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();
        Assert.True(vm.CanExportOperBox);
        Assert.Equal("0", vm.SelectedOperBoxExportFormatOption?.Value);
        Assert.Equal("复制到剪切板", vm.OperBoxExportActionText);

        // Default (clipboard) format stays the WPF JSON payload and keeps `own` flags.
        var json = vm.OperBoxExportText;
        Assert.Contains("\"Id\": \"char_102_texas\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Own\": false", json, StringComparison.Ordinal);

        SelectOperBoxExportFormat(vm, "2");
        Assert.True(vm.IsOperBoxExportFileFormat);
        Assert.Equal("导出到文件", vm.OperBoxExportActionText);
        var markdown = vm.OperBoxExportText;
        Assert.Contains("| 干员 | 干员 ID | 星级 | 精英化等级 | 等级 | 是否拥有 | 潜能 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 德克萨斯 | char_102_texas | 5 | 2 | 80 | 是 | 2 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 凯尔希 | char_003_kalts | 6 | 0 | 0 | 否 | 0 |", markdown, StringComparison.Ordinal);
        // WPF 131f2d248d keeps cell layout intact; pipes are escaped.
        Assert.Contains("测试\\|干员", markdown, StringComparison.Ordinal);
        Assert.Equal("Arknights_OperBox_Export.md", vm.OperBoxExportSuggestedFileName);

        SelectOperBoxExportFormat(vm, "3");
        var csv = vm.OperBoxExportText;
        Assert.StartsWith("干员,干员 ID,星级,精英化等级,等级,是否拥有,潜能", csv, StringComparison.Ordinal);
        Assert.Contains("德克萨斯,char_102_texas,5,2,80,是,2", csv, StringComparison.Ordinal);
        // Names with commas or quotes are quoted with doubled inner quotes.
        Assert.Contains("\"测试|干员,副手\"", csv, StringComparison.Ordinal);
        Assert.Equal("Arknights_OperBox_Export.csv", vm.OperBoxExportSuggestedFileName);

        SelectOperBoxExportFormat(vm, "1");
        Assert.Equal("Arknights_OperBox_Export.json", vm.OperBoxExportSuggestedFileName);
    }

    [Fact]
    public async Task OperBoxExportFormat_ShouldPersistAndReloadFromWpfConfigurationKey()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LegacyConfigurationKeys.OperBoxData] = BuildSeededOperBoxPayload(),
            });
        using var catalogScope = PushOperBoxCatalog(fixture.Root);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        SelectOperBoxExportFormat(vm, "3");
        Assert.Equal(3, vm.OperBoxExportFormatValue);
        await WaitForSettingAsync(fixture, "OperBox.SelectedExportValue", "3");

        // WPF parity (131f2d248d): the selected export value is persisted in the global config
        // slot, so a restart keeps the same export action.
        var reloaded = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await reloaded.InitializeAsync();
        Assert.Equal(3, reloaded.OperBoxExportFormatValue);
        Assert.StartsWith("干员,干员 ID", reloaded.OperBoxExportText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperBoxExportFormat_ShouldReadLegacyWpfSelection()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LegacyConfigurationKeys.OperBoxData] = BuildSeededOperBoxPayload(),
                ["OperBox.SelectedExportValue"] = "2",
            });
        using var catalogScope = PushOperBoxCatalog(fixture.Root);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        Assert.Equal(2, vm.OperBoxExportFormatValue);
        Assert.Contains("| 干员 |", vm.OperBoxExportText, StringComparison.Ordinal);
    }

    private static void SelectOperBoxExportFormat(ToolboxPageViewModel vm, string value)
    {
        var option = Assert.Single(vm.OperBoxExportFormatOptions, candidate => candidate.Value == value);
        vm.SelectedOperBoxExportFormatOption = option;
    }

    private static IDisposable PushOperBoxCatalog(string root)
    {
        var resourceDirectory = Path.Combine(root, "resource");
        Directory.CreateDirectory(resourceDirectory);
        File.WriteAllText(
            Path.Combine(resourceDirectory, "battle_data.json"),
            """
            {
              "chars": {
                "char_102_texas": { "name": "德克萨斯", "rarity": 5, "profession": "PIONEER" },
                "char_003_kalts": { "name": "凯尔希", "rarity": 6, "profession": "MEDIC" },
                "char_000_test": { "name": "测试|干员,副手", "rarity": 4, "profession": "SNIPER" }
              }
            }
            """);
        return ToolboxAssetCatalog.PushTestBaseDirectoriesForTests(root);
    }

    private static string BuildSeededOperBoxPayload()
    {
        return """
            {
              "done": true,
              "own_opers": [
                { "id": "char_102_texas", "name": "德克萨斯", "rarity": 5, "elite": 2, "level": 80, "own": true, "potential": 2 }
              ]
            }
            """;
    }

    private static CoreCallbackEvent CreateDepotResultCallback(string data)
    {
        return CreateCallback(
            "SubTaskExtraInfo",
            new JsonObject
            {
                ["taskchain"] = "Depot",
                ["what"] = "DepotResult",
                ["details"] = new JsonObject
                {
                    ["done"] = true,
                    ["data"] = data,
                },
            });
    }

    private static CoreCallbackEvent CreateStageDropsCallback(params (string ItemId, string ItemName, int Add)[] stats)
    {
        var nodes = new JsonArray();
        foreach (var (itemId, itemName, add) in stats)
        {
            nodes.Add(new JsonObject
            {
                ["itemId"] = itemId,
                ["itemName"] = itemName,
                ["addQuantity"] = add,
                ["count"] = add,
            });
        }

        return CreateCallback(
            "SubTaskExtraInfo",
            new JsonObject
            {
                ["taskchain"] = "Fight",
                ["what"] = "StageDrops",
                ["details"] = new JsonObject
                {
                    ["stats"] = nodes,
                },
            });
    }

    private static CoreCallbackEvent CreateCallback(string msgName, JsonObject payload)
    {
        return new CoreCallbackEvent(0, msgName, payload.ToJsonString(), DateTimeOffset.Now);
    }

    private static async Task WaitForSettingAsync(ToolboxTestFixture fixture, string key, string expectedSubstring)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var value = ReadGlobalString(fixture, key);
            if (value.Contains(expectedSubstring, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Contains(
            expectedSubstring,
            ReadGlobalString(fixture, key),
            StringComparison.Ordinal);
    }

    private static string ReadGlobalString(ToolboxTestFixture fixture, string key)
    {
        if (fixture.Config.CurrentConfig.GlobalValues.TryGetValue(key, out var node) && node is not null)
        {
            if (node is JsonValue value && value.TryGetValue(out string? text))
            {
                return text ?? string.Empty;
            }

            return node.ToString();
        }

        return string.Empty;
    }
}
