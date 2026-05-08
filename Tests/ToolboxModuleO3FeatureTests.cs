using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MAAUnified.App.Features.Advanced;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.CoreBridge;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Tests;

public sealed class ToolboxModuleO3FeatureTests
{
    [Fact]
    public async Task ApplyRuntimeCallback_RecruitCallbacks_ShouldProjectResultsAndCompleteRun()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartRecruitAsync();

        vm.ApplyRuntimeCallback(CreateCallback(
            "SubTaskExtraInfo",
            new JsonObject
            {
                ["taskchain"] = "Recruit",
                ["what"] = "RecruitTagsDetected",
                ["details"] = new JsonObject
                {
                    ["tags"] = new JsonArray("先锋干员", "费用回复"),
                },
            }));
        vm.ApplyRuntimeCallback(CreateCallback(
            "SubTaskExtraInfo",
            new JsonObject
            {
                ["taskchain"] = "Recruit",
                ["what"] = "RecruitResult",
                ["details"] = new JsonObject
                {
                    ["result"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["level"] = 4,
                            ["tags"] = new JsonArray("先锋干员", "费用回复"),
                            ["opers"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["level"] = 5,
                                    ["id"] = "char_102_texas",
                                    ["name"] = "德克萨斯",
                                },
                                new JsonObject
                                {
                                    ["level"] = 3,
                                    ["id"] = "char_123_fang",
                                    ["name"] = "芬",
                                },
                            },
                        },
                    },
                },
            }));
        Assert.Equal(ToolboxExecutionState.Succeeded, vm.ExecutionState);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.Contains("先锋干员", vm.RecruitInfo, StringComparison.Ordinal);
        Assert.True(vm.RecruitResultLines.Count >= 2);

        var operatorLine = Assert.Single(
            vm.RecruitResultLines,
            line => line.Text.Contains("德克萨斯", StringComparison.Ordinal));
        Assert.Equal(2, operatorLine.Segments.Count);
        var texasSegment = Assert.Single(
            operatorLine.Segments,
            segment => segment.Text.Contains("德克萨斯", StringComparison.Ordinal));
        var texasBrush = Assert.IsAssignableFrom<ISolidColorBrush>(texasSegment.Foreground);
        Assert.Equal(Colors.Orange, texasBrush.Color);

        var history = Assert.Single(vm.ExecutionHistory);
        Assert.True(history.Success);
        Assert.Equal("招募识别", history.ToolName);
        await WaitForSettingAsync(fixture, "Toolbox.ExecutionHistory");
    }

    [Fact]
    public async Task SetLanguage_AfterRecruitResult_ShouldRelocalizeRecruitOperatorText()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartRecruitAsync();
        vm.ApplyRuntimeCallback(CreateCallback(
            "SubTaskExtraInfo",
            new JsonObject
            {
                ["taskchain"] = "Recruit",
                ["what"] = "RecruitResult",
                ["details"] = new JsonObject
                {
                    ["result"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["level"] = 4,
                            ["tags"] = new JsonArray("先锋干员", "费用回复"),
                            ["opers"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["level"] = 5,
                                    ["id"] = "char_102_texas",
                                    ["name"] = "德克萨斯",
                                },
                            },
                        },
                    },
                },
            }));

        var before = string.Join('\n', vm.RecruitResultLines.Select(line => line.Text));
        vm.SetLanguage("en-us");
        var after = string.Join('\n', vm.RecruitResultLines.Select(line => line.Text));

        Assert.Equal("en-us", vm.RootTexts.Language);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task LanguageSwitch_ShouldKeepExecutionHistorySnapshotWhileRelocalizingLiveRecruitResult()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();
        vm.SetLanguage("zh-cn");

        await vm.StartRecruitAsync();
        vm.ApplyRuntimeCallback(CreateCallback(
            "SubTaskExtraInfo",
            new JsonObject
            {
                ["taskchain"] = "Recruit",
                ["what"] = "RecruitResult",
                ["details"] = new JsonObject
                {
                    ["result"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["level"] = 4,
                            ["tags"] = new JsonArray("先锋干员", "费用回复"),
                            ["opers"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["level"] = 5,
                                    ["id"] = "char_102_texas",
                                    ["name"] = "德克萨斯",
                                },
                            },
                        },
                    },
                },
            }));
        vm.ApplyRuntimeCallback(CreateCallback("TaskChainCompleted"));

        var historyBefore = Assert.Single(vm.ExecutionHistory);
        var recruitResultBefore = string.Join('\n', vm.RecruitResultLines.Select(line => line.Text));

        vm.SetLanguage("en-us");

        var historyAfter = Assert.Single(vm.ExecutionHistory);
        var recruitResultAfter = string.Join('\n', vm.RecruitResultLines.Select(line => line.Text));

        Assert.Same(historyBefore, historyAfter);
        Assert.Equal("招募识别", historyAfter.ToolName);
        Assert.Equal("en-us", vm.RootTexts.Language);
        Assert.Equal("Recruit Recognition", vm.Texts["Toolbox.ToolName.Recruit"]);
        Assert.NotEqual(historyAfter.ToolName, vm.Texts["Toolbox.ToolName.Recruit"]);
        Assert.NotEqual(recruitResultBefore, recruitResultAfter);
    }

    [Fact]
    public async Task ApplyRuntimeCallback_OperBoxDone_ShouldPersistLegacyBoxData()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartOperBoxAsync();

        vm.ApplyRuntimeCallback(CreateCallback(
            "SubTaskExtraInfo",
            new JsonObject
            {
                ["taskchain"] = "OperBox",
                ["what"] = "OperBoxResult",
                ["details"] = new JsonObject
                {
                    ["own_opers"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "char_003_kalts",
                            ["name"] = "凯尔希",
                            ["rarity"] = 6,
                            ["elite"] = 2,
                            ["level"] = 90,
                            ["potential"] = 6,
                        },
                    },
                    ["done"] = true,
                },
            }));
        Assert.Equal(ToolboxExecutionState.Succeeded, vm.ExecutionState);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.Single(vm.OperBoxHaveList);
        Assert.Equal("char_003_kalts", vm.OperBoxHaveList[0].Id);

        await WaitForSettingAsync(fixture, LegacyConfigurationKeys.OperBoxData, expectedSubstring: "char_003_kalts");
    }

    [Fact]
    public async Task ApplyRuntimeCallback_DepotDone_ShouldCompleteRunWithoutTopLevelCallback()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartDepotAsync();

        vm.ApplyRuntimeCallback(CreateCallback(
            "SubTaskExtraInfo",
            new JsonObject
            {
                ["taskchain"] = "Depot",
                ["what"] = "DepotResult",
                ["details"] = new JsonObject
                {
                    ["done"] = true,
                    ["data"] = "{\"2001\":7}",
                },
            }));

        Assert.Equal(ToolboxExecutionState.Succeeded, vm.ExecutionState);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.Equal("2001", Assert.Single(vm.DepotResult).Id);
        await WaitForSettingAsync(fixture, LegacyConfigurationKeys.DepotResult);
    }

    [Fact]
    public async Task ApplyRuntimeCallback_StageDrops_ShouldUpdateDepotAndPersistLegacyResult()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        vm.ApplyRuntimeCallback(CreateCallback(
            "SubTaskExtraInfo",
            new JsonObject
            {
                ["taskchain"] = "Fight",
                ["what"] = "StageDrops",
                ["details"] = new JsonObject
                {
                    ["stats"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["itemId"] = "2001",
                            ["itemName"] = "源岩",
                            ["addQuantity"] = 7,
                        },
                    },
                },
            }));

        var depotItem = Assert.Single(vm.DepotResult);
        Assert.Equal("2001", depotItem.Id);
        Assert.Equal(7, depotItem.Count);
        await WaitForSettingAsync(fixture, LegacyConfigurationKeys.DepotResult);
        Assert.Equal(7, ReadPersistedDepotCount(fixture, "2001"));
    }

    [Fact]
    public async Task ApplyRuntimeCallback_StageDrops_ShouldRemainCompatibleWithFightTaskHints()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        vm.ApplyRuntimeCallback(CreateCallback(
            "SubTaskExtraInfo",
            new JsonObject
            {
                ["taskchain"] = "Fight",
                ["what"] = "StageDrops",
                ["details"] = new JsonObject
                {
                    ["stats"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["itemId"] = "3231",
                            ["itemName"] = "重装芯片",
                            ["addQuantity"] = 7,
                        },
                    },
                },
            }));

        await WaitForSettingAsync(fixture, LegacyConfigurationKeys.DepotResult);
        var hint = FightTaskModuleViewModel.BuildDailyResourceHint(
            "zh-cn",
            "Official",
            fixture.Config.CurrentConfig,
            new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains("PR-A-1/2", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("(库存", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolboxView_ShouldKeepRealSixToolStructure_AndExcludeAdvancedInjection()
    {
        var root = GetMaaUnifiedRoot();
        var toolboxXaml = ReadAdvancedView(root, "ToolboxView.axaml");
        var recruitXaml = ReadAdvancedView(root, "ToolboxRecruitView.axaml");
        var operBoxXaml = ReadAdvancedView(root, "ToolboxOperBoxView.axaml");
        var depotXaml = ReadAdvancedView(root, "ToolboxDepotView.axaml");
        var gachaXaml = ReadAdvancedView(root, "ToolboxGachaView.axaml");
        var peepXaml = ReadAdvancedView(root, "ToolboxPeepView.axaml");
        var miniGameXaml = ReadAdvancedView(root, "ToolboxMiniGameView.axaml");

        Assert.Contains("Classes=\"toolbox-nav\"", toolboxXaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"{Binding RecruitTabTitle}\"", toolboxXaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"{Binding OperBoxTabTitle}\"", toolboxXaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"{Binding DepotTabTitle}\"", toolboxXaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"{Binding GachaTabTitle}\"", toolboxXaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"{Binding PeepTabTitle}\"", toolboxXaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"{Binding MiniGameTabTitle}\"", toolboxXaml, StringComparison.Ordinal);
        Assert.Contains("<views:ToolboxRecruitView />", toolboxXaml, StringComparison.Ordinal);
        Assert.Contains("<views:ToolboxOperBoxView />", toolboxXaml, StringComparison.Ordinal);
        Assert.Contains("<views:ToolboxDepotView />", toolboxXaml, StringComparison.Ordinal);
        Assert.Contains("<views:ToolboxGachaView />", toolboxXaml, StringComparison.Ordinal);
        Assert.Contains("<views:ToolboxPeepView />", toolboxXaml, StringComparison.Ordinal);
        Assert.Contains("<views:ToolboxMiniGameView />", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"{Binding Texts[Toolbox.Tab.Advanced]}\"", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<views:StageManagerView", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<views:OverlayView", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<views:TrayIntegrationView", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<views:RemoteControlCenterView", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<views:ExternalNotificationProvidersView", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<views:WebApiView", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionReview", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding ResultText}\"", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding CurrentToolParameters}\"", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding ExecutionHistory}\"", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Classes.status-success=\"{Binding Success}\"", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Classes.status-error=\"{Binding HasErrorCode}\"", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Legacy structure contract anchors", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("执行成功示例", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("执行失败示例", toolboxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("toolbox-page-title", recruitXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("toolbox-page-title", operBoxXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("toolbox-page-title", depotXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("toolbox-page-title", gachaXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("toolbox-page-title", peepXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("toolbox-minigame-title", miniGameXaml, StringComparison.Ordinal);

        Assert.Contains("ItemsSource=\"{Binding Segments}\"", recruitXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionReviewResultLabelText", recruitXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding RecruitFixedSixStarText}\"", recruitXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding StartRecognitionText}\"", recruitXaml, StringComparison.Ordinal);

        Assert.DoesNotContain("ExecutionReviewResultLabelText", operBoxXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding OperBoxCopyToClipboardText}\"", operBoxXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding StartRecognitionText}\"", operBoxXaml, StringComparison.Ordinal);
        Assert.Contains("Source=\"{Binding EliteIconImage}\"", operBoxXaml, StringComparison.Ordinal);
        Assert.Contains("Source=\"{Binding PotentialIconImage}\"", operBoxXaml, StringComparison.Ordinal);
        Assert.Contains("DropShadowDirectionEffect", operBoxXaml, StringComparison.Ordinal);

        Assert.DoesNotContain("ExecutionReviewResultLabelText", depotXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DepotExportArkPlannerText}\"", depotXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DepotExportLoliconText}\"", depotXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding StartRecognitionText}\"", depotXaml, StringComparison.Ordinal);
        Assert.Contains("Source=\"{Binding ItemImage}\"", depotXaml, StringComparison.Ordinal);

        Assert.Contains("Content=\"{Binding GachaDrawOnceText}\"", gachaXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding GachaDrawTenText}\"", gachaXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding GachaDisclaimerAcknowledgeText}\"", gachaXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding GachaDisclaimerNoMoreText}\"", gachaXaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding GachaShowDisclaimerNoMore}\"", gachaXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GachaDisclaimerEmphasisText\"", gachaXaml, StringComparison.Ordinal);
        Assert.Contains("SpreadMethod=\"Repeat\"", gachaXaml, StringComparison.Ordinal);

        Assert.Contains("Content=\"{Binding PeepCommandText}\"", peepXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding PeepCommandText}\"", peepXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PeepDisplayArea\"", peepXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PeepControlPanel\"", peepXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PeepCommandControlGroup\"", peepXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PeepFpsControlGroup\"", peepXaml, StringComparison.Ordinal);
        Assert.Contains("ColumnDefinitions=\"*,Auto,*\"", peepXaml, StringComparison.Ordinal);
        Assert.Contains("Orientation=\"Horizontal\"", peepXaml, StringComparison.Ordinal);
        Assert.Contains("SizeChanged=\"OnPeepLayoutSizeChanged\"", peepXaml, StringComparison.Ordinal);
        Assert.Contains("PeepPreviewAspectRatio = 16d / 9d", ReadAdvancedView(root, "ToolboxPeepView.axaml.cs"), StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding MiniGameCommandText}\"", miniGameXaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolboxView_ShouldInstantiateAndMeasureDefaultTab()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        EnsureAvaloniaApplication();
        var view = new ToolboxView
        {
            DataContext = vm,
        };

        view.Measure(new Size(1280, 900));
        view.Arrange(new Rect(0, 0, 1280, 900));
        Dispatcher.UIThread.RunJobs(null);

        Assert.Contains(
            view.GetLogicalDescendants().OfType<TabControl>(),
            tab => tab.Classes.Contains("toolbox-nav"));
    }

    private static CoreCallbackEvent CreateCallback(string msgName, JsonObject? payload = null)
    {
        return new CoreCallbackEvent(0, msgName, (payload ?? new JsonObject()).ToJsonString(), DateTimeOffset.Now);
    }

    private static string ReadAdvancedView(string root, string fileName)
    {
        return File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", fileName));
    }

    private static void EnsureAvaloniaApplication()
    {
        if (global::Avalonia.Application.Current is not null)
        {
            return;
        }

        var app = new MAAUnified.App.App();
        app.Initialize();
    }

    private static async Task WaitForSettingAsync(ToolboxTestFixture fixture, string key, string? expectedSubstring = null)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var value = ReadGlobalString(fixture, key);
            if (!string.IsNullOrWhiteSpace(value)
                && (string.IsNullOrWhiteSpace(expectedSubstring) || value.Contains(expectedSubstring, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(20);
        }

        var current = ReadGlobalString(fixture, key);
        Assert.True(
            !string.IsNullOrWhiteSpace(current)
            && (string.IsNullOrWhiteSpace(expectedSubstring) || current.Contains(expectedSubstring, StringComparison.Ordinal)),
            $"Expected setting `{key}` to contain `{expectedSubstring}`, but got `{current}`.");
    }

    private static string ReadGlobalString(ToolboxTestFixture fixture, string key)
    {
        if (fixture.Config.CurrentConfig.GlobalValues.TryGetValue(key, out var node) && node is not null)
        {
            return node.ToString();
        }

        return string.Empty;
    }

    private static int ReadPersistedDepotCount(ToolboxTestFixture fixture, string itemId)
    {
        var payload = ReadGlobalString(fixture, LegacyConfigurationKeys.DepotResult);
        var root = JsonNode.Parse(payload) as JsonObject;
        var dataPayload = root?["data"]?.ToString();
        var data = string.IsNullOrWhiteSpace(dataPayload) ? null : JsonNode.Parse(dataPayload) as JsonObject;
        var countText = data?[itemId]?.ToString();
        return int.TryParse(countText, out var count) ? count : 0;
    }

    private static string GetMaaUnifiedRoot()
    {
        return TestRepoLayout.GetMaaUnifiedRoot();
    }
}
