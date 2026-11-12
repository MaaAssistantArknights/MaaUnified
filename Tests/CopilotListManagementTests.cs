using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels.Copilot;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Tests;

public sealed class CopilotListManagementTests
{
    [Fact]
    public async Task AddEmptyTaskAsync_ShouldPersistListAndUpdateFeedback()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();

        Assert.Single(vm.Items);
        Assert.NotNull(vm.SelectedItem);
        Assert.Contains("新增", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(vm.LastErrorMessage));

        var payload = GetPersistedTaskListPayload(fixture.Config);
        Assert.NotNull(payload);
        var node = JsonNode.Parse(payload!);
        var array = Assert.IsType<JsonArray>(node);
        Assert.Single(array);
    }

    [Fact]
    public async Task NavNameOverrideFlag_ShouldPersistAndReload_WpfParity()
    {
        // WPF dev-v2 c2021e694a：条目新增 IsNavNameOverride，重启后仍需保留覆盖标记
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        var item = Assert.Single(vm.Items);
        item.Name = "1-7-override";
        item.IsNavNameOverride = true;
        await vm.SetListItemCheckedAsync(item, false);

        var persistedArray = Assert.IsType<JsonArray>(JsonNode.Parse(GetPersistedTaskListPayload(fixture.Config)!));
        Assert.Equal("1-7-override", persistedArray[0]?["Name"]?.GetValue<string>());
        Assert.True(persistedArray[0]?["IsNavNameOverride"]?.GetValue<bool>());

        var reloaded = new CopilotPageViewModel(fixture.Runtime);
        var reloadedItem = Assert.Single(reloaded.Items);
        Assert.Equal("1-7-override", reloadedItem.Name);
        Assert.True(reloadedItem.IsNavNameOverride);
        Assert.False(reloadedItem.IsChecked);
    }

    [Fact]
    public async Task CopilotSettings_ShouldPersistPerProfile_AndReloadOnProfileSwitch()
    {
        // WPF dev-v2 382009b97b / 5b39509df6：Copilot 列表与设置随档案隔离；切换档案后重新加载
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        vm.LoopTimes = 9;
        vm.UseFormation = true;
        vm.FormationIndex = 3;

        var defaultProfile = fixture.Config.CurrentConfig.Profiles["Default"];
        Assert.Equal("9", defaultProfile.Values[LegacyConfigurationKeys.CopilotLoopTimes]?.GetValue<string>());
        Assert.False(fixture.Config.CurrentConfig.GlobalValues.ContainsKey(LegacyConfigurationKeys.CopilotLoopTimes));
        Assert.False(fixture.Config.CurrentConfig.GlobalValues.ContainsKey(LegacyConfigurationKeys.CopilotTaskList));

        fixture.Config.CurrentConfig.Profiles["Alt"] = new UnifiedProfile
        {
            Values =
            {
                [LegacyConfigurationKeys.CopilotTaskList] = JsonValue.Create(
                    """[{"Name":"Alt-1","Type":"主线/故事集/SideStory","SourcePath":"","InlinePayload":"{\"stage_name\":\"1-7\"}","CopilotId":0,"TabIndex":null,"IsRaid":false,"IsChecked":true}]"""),
                [LegacyConfigurationKeys.CopilotLoopTimes] = JsonValue.Create("4"),
                [LegacyConfigurationKeys.CopilotSelectFormation] = JsonValue.Create("2"),
                [CopilotSettingKeys.UseFormation] = JsonValue.Create("true"),
            },
        };
        fixture.Config.CurrentConfig.CurrentProfile = "Alt";

        vm.ReloadConfigurationContext();

        Assert.Equal("Alt-1", Assert.Single(vm.Items).Name);
        Assert.Equal(4, vm.LoopTimes);
        Assert.True(vm.UseFormation);
        Assert.Equal(2, vm.FormationIndex);
    }

    [Fact]
    public async Task RemoveSelectedAsync_WithoutSelection_ShouldSetFailureFeedbackAndLog()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.RemoveSelectedAsync();

        Assert.Contains("删除作业失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("请选择", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.True(await WaitForLogContainsAsync(fixture.Runtime.DiagnosticsService.ErrorLogPath, "[Copilot.Remove]"));
    }

    [Fact]
    public async Task MoveSelectedUpAndDown_ShouldReorderItemsAndPersist()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        await vm.AddEmptyTaskAsync();
        await vm.AddEmptyTaskAsync();
        vm.Items[0].Name = "First";
        vm.Items[1].Name = "Second";
        vm.Items[2].Name = "Third";

        vm.SelectedItem = vm.Items[1];
        await vm.MoveSelectedUpAsync();

        Assert.Equal(["Second", "First", "Third"], vm.Items.Select(i => i.Name).ToArray());
        Assert.Equal("Second", vm.SelectedItem?.Name);

        await vm.MoveSelectedDownAsync();

        Assert.Equal(["First", "Second", "Third"], vm.Items.Select(i => i.Name).ToArray());
        Assert.Equal("Second", vm.SelectedItem?.Name);

        var payload = GetPersistedTaskListPayload(fixture.Config);
        Assert.NotNull(payload);
        var persistedArray = Assert.IsType<JsonArray>(JsonNode.Parse(payload!));
        Assert.Equal("First", persistedArray[0]?["Name"]?.GetValue<string>());
        Assert.Equal("Second", persistedArray[1]?["Name"]?.GetValue<string>());
        Assert.Equal("Third", persistedArray[2]?["Name"]?.GetValue<string>());
    }

    [Fact]
    public async Task MoveListItemToAsync_ShouldMoveRequestedItem_KeepSelectionAndPersist()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        await vm.AddEmptyTaskAsync();
        await vm.AddEmptyTaskAsync();
        vm.Items[0].Name = "First";
        vm.Items[1].Name = "Second";
        vm.Items[2].Name = "Third";
        var movedItem = vm.Items[2];

        await vm.MoveListItemToAsync(movedItem, 0);

        Assert.Equal(["Third", "First", "Second"], vm.Items.Select(i => i.Name).ToArray());
        Assert.Same(movedItem, vm.SelectedItem);

        var persistedArray = Assert.IsType<JsonArray>(JsonNode.Parse(GetPersistedTaskListPayload(fixture.Config)!));
        Assert.Equal("Third", persistedArray[0]?["Name"]?.GetValue<string>());
        Assert.Equal("First", persistedArray[1]?["Name"]?.GetValue<string>());
        Assert.Equal("Second", persistedArray[2]?["Name"]?.GetValue<string>());
    }

    [Fact]
    public async Task MoveListItemToAsync_WhenPersistenceFails_ShouldRollbackOrder()
    {
        await using var fixture = await CopilotFixture.CreateAsync(failPersistence: true);
        var vm = fixture.ViewModel;

        vm.Items.Add(new CopilotItemViewModel("First", vm.Types[0]));
        vm.Items.Add(new CopilotItemViewModel("Second", vm.Types[0]));
        vm.Items.Add(new CopilotItemViewModel("Third", vm.Types[0]));
        var movedItem = vm.Items[2];

        await vm.MoveListItemToAsync(movedItem, 0);

        Assert.Equal(["First", "Second", "Third"], vm.Items.Select(i => i.Name).ToArray());
        Assert.Same(movedItem, vm.SelectedItem);
        Assert.Contains("失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("持久化", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetListItemCheckedAsync_ShouldUpdateItemAndPersist()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        var item = Assert.Single(vm.Items);

        await vm.SetListItemCheckedAsync(item, false);

        Assert.False(item.IsChecked);
        var persistedArray = Assert.IsType<JsonArray>(JsonNode.Parse(GetPersistedTaskListPayload(fixture.Config)!));
        Assert.False(persistedArray[0]?["IsChecked"]?.GetValue<bool>());
    }

    [Fact]
    public async Task ToggleListItemRaidAsync_ShouldToggleRaidFlagAndPersist()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        var item = Assert.Single(vm.Items);

        await vm.ToggleListItemRaidAsync(item);

        Assert.True(item.IsRaid);
        Assert.Contains(vm.RaidLabelText, item.DisplayName, StringComparison.Ordinal);
        var persistedArray = Assert.IsType<JsonArray>(JsonNode.Parse(GetPersistedTaskListPayload(fixture.Config)!));
        Assert.True(persistedArray[0]?["IsRaid"]?.GetValue<bool>());
    }

    [Fact]
    public async Task ConfirmAndClearAllAsync_WhenCancelled_ShouldKeepItemsAndSkipPersistence()
    {
        var dialog = new ScriptedDialogService(DialogReturnSemantic.Cancel);
        await using var fixture = await CopilotFixture.CreateAsync(dialogService: dialog);
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        await vm.AddEmptyTaskAsync();
        var persistedBefore = GetPersistedTaskListPayload(fixture.Config);

        await vm.ConfirmAndClearAllAsync();

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(1, dialog.WarningConfirmCallCount);
        Assert.Equal(persistedBefore, GetPersistedTaskListPayload(fixture.Config));
    }

    [Fact]
    public async Task ConfirmAndClearAllAsync_WhenConfirmed_ShouldClearItemsAndPersist()
    {
        var dialog = new ScriptedDialogService(DialogReturnSemantic.Confirm);
        await using var fixture = await CopilotFixture.CreateAsync(dialogService: dialog);
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        await vm.AddEmptyTaskAsync();

        await vm.ConfirmAndClearAllAsync();

        Assert.Empty(vm.Items);
        Assert.Null(vm.SelectedItem);
        Assert.Equal(1, dialog.WarningConfirmCallCount);
        Assert.Equal(vm.ClearAllConfirmTitleText, dialog.WarningConfirmRequests[0].Title);
        Assert.Equal(vm.ClearAllConfirmMessageText, dialog.WarningConfirmRequests[0].Message);
        Assert.Equal(vm.ClearAllConfirmButtonText, dialog.WarningConfirmRequests[0].ConfirmText);
        var persistedArray = Assert.IsType<JsonArray>(JsonNode.Parse(GetPersistedTaskListPayload(fixture.Config)!));
        Assert.Empty(persistedArray);
    }

    [Fact]
    public async Task CopilotTaskName_ShouldRenameActiveListItemAndPersistWithoutSchemaExpansion()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        var item = Assert.Single(vm.Items);
        item.SourcePath = "resource/copilot/old-name.json";
        item.InlinePayload = string.Empty;
        vm.SelectedItem = item;

        vm.CopilotTaskName = "JT8-3";

        Assert.Equal("JT8-3", item.Name);
        Assert.True(await WaitForPersistedItemNameAsync(fixture.Config, "JT8-3"));
        var persistedArray = Assert.IsType<JsonArray>(JsonNode.Parse(GetPersistedTaskListPayload(fixture.Config)!));
        var persistedItem = Assert.IsType<JsonObject>(persistedArray[0]);
        Assert.Equal("JT8-3", persistedItem["Name"]?.GetValue<string>());
        Assert.DoesNotContain("stage_name", persistedItem.Select(property => property.Key), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("StageName", persistedItem.Select(property => property.Key), StringComparer.Ordinal);
    }

    [Fact]
    public async Task CopilotTabIndex_ShouldUpdateActiveListItemTypeAndPersist()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        var item = Assert.Single(vm.Items);
        vm.SelectedItem = item;

        vm.CopilotTabIndex = 3;

        Assert.Equal(3, item.TabIndex);
        Assert.Equal(vm.Types[3], item.Type);
        Assert.True(await WaitForPersistedItemTabAsync(fixture.Config, item.Name, 3, vm.Types[3]));
    }

    [Fact]
    public async Task SendLikeAsync_WithoutSelection_ShouldSetFailureFeedbackAndLog()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.SendLikeAsync(true);

        Assert.Contains("反馈失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("请选择", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.True(await WaitForLogContainsAsync(fixture.Runtime.DiagnosticsService.ErrorLogPath, "[Copilot.Feedback]"));
    }

    [Fact]
    public async Task AddEmptyTaskAsync_WhenPersistenceFails_ShouldRollbackListState()
    {
        await using var fixture = await CopilotFixture.CreateAsync(failPersistence: true);
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();

        Assert.Empty(vm.Items);
        Assert.Contains("失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("持久化", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.True(await WaitForLogContainsAsync(fixture.Runtime.DiagnosticsService.ErrorLogPath, "[Copilot.Add]"));
    }

    [Fact]
    public async Task Constructor_ShouldLoadPersistedTaskList_AndMapLegacyTabIndex()
    {
        var payload = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "Legacy-SSS",
                ["tab_index"] = 1,
            },
            new JsonObject
            {
                ["name"] = "Typed-Main",
                ["type"] = "主线",
            },
        }.ToJsonString();

        await using var fixture = await CopilotFixture.CreateAsync(persistedPayload: payload);
        var vm = fixture.ViewModel;

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("Legacy-SSS", vm.Items[0].Name);
        Assert.Equal("保全派驻", vm.Items[0].Type);
        Assert.Equal("Typed-Main", vm.Items[1].Name);
        Assert.Equal("主线/故事集/SideStory", vm.Items[1].Type);
        Assert.Equal("Legacy-SSS", vm.SelectedItem?.Name);
    }

    [Fact]
    public async Task LoadSssCopilot_WithSelectedListItem_ShouldNotRewriteSelectedItemType()
    {
        // WPF ParseSSSCopilot 对齐：加载 SSS 作业程序化切页签，不触发「选中条目类型编辑」手势（O1 手动切换仍同步）
        var payload = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "1-7",
                ["type"] = "主线/故事集/SideStory",
                ["tab_index"] = 0,
                ["is_checked"] = true,
            },
        }.ToJsonString();

        await using var fixture = await CopilotFixture.CreateAsync(persistedPayload: payload);
        var vm = fixture.ViewModel;
        Assert.Equal("1-7", vm.SelectedItem?.Name);
        Assert.Equal(0, vm.CopilotTabIndex);

        await vm.LoadCurrentFromClipboardAsync("""{"type":"SSS","stage_name":"sss-test","copilot_id":66}""");

        Assert.Equal(1, vm.CopilotTabIndex);
        Assert.Equal("主线/故事集/SideStory", vm.Items[0].Type);
        Assert.Equal(0, vm.Items[0].TabIndex);

        var persisted = JsonNode.Parse(GetPersistedTaskListPayload(fixture.Config)!)!.AsArray();
        // 加载 SSS 不触发重新持久化，配置中仍为初始载荷
        Assert.Equal("主线/故事集/SideStory", persisted[0]?["type"]?.GetValue<string>());
        Assert.Equal(0, persisted[0]?["tab_index"]?.GetValue<int>());

        // O1 既有偏差保留：用户手动切换页签仍作为编辑手势改写选中条目
        vm.SelectedTypeIndex = 2;
        Assert.Equal(vm.Types[2], vm.Items[0].Type);
        Assert.Equal(2, vm.Items[0].TabIndex);
    }

    [Fact]
    public async Task SetLanguage_ShouldUpdateRootLocalizationLanguage()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        vm.SetLanguage("en-us");

        Assert.Equal("en-us", vm.RootTexts.Language);
        Assert.Equal("en-us", vm.Texts.Language);
        Assert.Equal("Fill gap", vm.SupportUnitUsageOptions[0].DisplayName);
        Assert.Contains("Tips:", vm.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetLanguage_ShouldNotifyLocalizedTextMapsForViewBindings()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SetLanguage("en-us");

        Assert.Contains(nameof(CopilotPageViewModel.Texts), changed);
        Assert.Contains(nameof(CopilotPageViewModel.RootTexts), changed);
        Assert.Contains(nameof(CopilotPageViewModel.MainTabTitle), changed);
        Assert.Contains(nameof(CopilotPageViewModel.StartButtonText), changed);
        Assert.Contains(string.Empty, changed);
    }

    [Fact]
    public async Task SetLanguage_ShouldRelocalizeTrackedCopilotItemsAcrossRepeatedSwitches()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        var item = new CopilotItemViewModel("Task-A", "主线/故事集/SideStory", inlinePayload: "{}")
        {
            IsRaid = true,
        };

        vm.Items.Add(item);

        foreach (var language in new[] { "en-us", "ja-jp", "ko-kr", "zh-tw" })
        {
            vm.SetLanguage(language);

            Assert.Equal(language, vm.Texts.Language);
            Assert.Contains(vm.RaidLabelText, item.DisplayName, StringComparison.Ordinal);
            Assert.Equal(vm.InlineJsonHintText, item.ExecutionPathHint);
        }
    }

    [Fact]
    public async Task SetLanguage_ShouldKeepLocalizedBindingsFreshAcrossRepeatedSwitches()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        var texts = vm.Texts;
        var rootTexts = vm.RootTexts;

        foreach (var language in new[] { "en-us", "ja-jp", "ko-kr", "zh-tw" })
        {
            vm.SetLanguage(language);

            Assert.Same(texts, vm.Texts);
            Assert.Same(rootTexts, vm.RootTexts);
            Assert.Equal(language, vm.Texts.Language);
            Assert.Equal(language, vm.RootTexts.Language);
            Assert.Equal(vm.Texts["Copilot.Tab.Main"], vm.MainTabTitle);
            Assert.Equal(vm.Texts["Copilot.Button.File"], vm.FileButtonText);
            Assert.Equal(vm.Texts["Copilot.Option.UseSupportUnit"], vm.UseSupportUnitText);
            Assert.Equal(vm.Texts["Copilot.Rating.Prompt"], vm.RatingPromptText);
            Assert.Equal(vm.Texts["Copilot.HelpText"], vm.HelpText);
            Assert.Equal(vm.Texts["Copilot.Option.SupportUnitUsage.FillGap"], vm.SupportUnitUsageOptions[0].DisplayName);
        }
    }

    [Fact]
    public async Task SetLanguage_ShouldRefreshHelpLogAcrossRepeatedSwitches()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        Assert.Single(vm.Logs);

        foreach (var language in new[] { "en-us", "ja-jp", "ko-kr", "zh-tw" })
        {
            vm.SetLanguage(language);

            var helpLog = Assert.Single(vm.Logs);
            Assert.False(helpLog.HasTime);
            Assert.Equal(vm.HelpText.TrimEnd(), helpLog.Content);
        }
    }

    [Fact]
    public async Task ImportFromClipboardAsync_DifficultyNormalAndRaid_ShouldAddBothVariants()
    {
        // WPF 对齐：difficulty 位掩码同时包含普通(1)与突袭(2)时添加两条列表项
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.ImportFromClipboardAsync("{\"stage_name\":\"1-7\",\"difficulty\":3}");

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(1, vm.Items.Count(item => item.IsRaid));
        Assert.Equal(1, vm.Items.Count(item => !item.IsRaid));
        Assert.All(vm.Items, item => Assert.True(item.IsChecked));
        Assert.All(vm.Items, item => Assert.Equal("1-7", item.Name));
    }

    [Fact]
    public async Task ImportFromClipboardAsync_DifficultyRaidOnly_ShouldAddSingleRaidItem()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.ImportFromClipboardAsync("{\"stage_name\":\"1-7\",\"difficulty\":2}");

        var item = Assert.Single(vm.Items);
        Assert.True(item.IsRaid);
    }

    [Fact]
    public async Task ImportFromClipboardAsync_WithoutDifficulty_ShouldAddSingleNormalItem()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.ImportFromClipboardAsync("{\"stage_name\":\"1-7\"}");

        var item = Assert.Single(vm.Items);
        Assert.False(item.IsRaid);
    }

    [Fact]
    public async Task ImportFromClipboardAsync_UnknownDifficultyBits_ShouldAddNoItem_WpfParity()
    {
        // WPF AddCopilotTaskToList 对齐（冻结 :1676-1688）：difficulty 仅含未知位（如 4）时
        // HasFlag(Normal)/HasFlag(Raid) 均不命中，不添加任何条目
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.ImportFromClipboardAsync("{\"stage_name\":\"1-7\",\"difficulty\":4}");

        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task ImportFromClipboardAsync_DifficultyNone_ShouldAddSingleNormalItem_WpfParity()
    {
        // WPF ParseCopilotAsync 对齐（冻结 :1214-1216）：DifficultyFlags.None(0) 按普通难度入列
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.ImportFromClipboardAsync("{\"stage_name\":\"1-7\",\"difficulty\":0}");

        var item = Assert.Single(vm.Items);
        Assert.False(item.IsRaid);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_NonIntegerDifficulty_ShouldRejectLoad_WpfParity()
    {
        // WPF 对齐（冻结 CopilotModel.cs:47-48）：Newtonsoft 反序列化 DifficultyFlags 遇非整数 difficulty
        // （如 "abc"）抛异常 → CopilotFileReadError，整个作业拒绝加载
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.LoadCurrentFromClipboardAsync(
            """{"stage_name":"1-7","difficulty":"abc","opers":[{"name":"阿米娅","skill":2}]}""");

        Assert.False(vm.HasLoadedCopilot);
        Assert.Contains("读取剪贴板作业失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("difficulty", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_NullDifficulty_ShouldRejectLoad_WpfParity()
    {
        // WPF 对齐（冻结 CopilotModel.cs:47-48）：Difficulty 为非空值类型枚举，Newtonsoft 遇 JSON null
        // 抛 JsonSerializationException → CopilotFileReadError，整个作业拒绝加载
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.LoadCurrentFromClipboardAsync(
            """{"stage_name":"1-7","difficulty":null,"opers":[{"name":"阿米娅","skill":2}]}""");

        Assert.False(vm.HasLoadedCopilot);
        Assert.Contains("读取剪贴板作业失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("difficulty", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_EnumNameDifficulty_ShouldLoad_WpfParity()
    {
        // WPF 对齐：Newtonsoft 枚举反序列化接受枚举名（大小写不敏感，支持 "Normal, Raid" flags 组合）
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.LoadCurrentFromClipboardAsync(
            """{"stage_name":"1-7","difficulty":"Normal, Raid","opers":[{"name":"阿米娅","skill":2}]}""");

        Assert.True(vm.HasLoadedCopilot);
        Assert.Equal(string.Empty, vm.LastErrorMessage);
    }

    [Fact]
    public async Task ImportFromClipboardAsync_EnumNameDifficulty_ShouldSplitVariants_WpfParity()
    {
        // WPF 对齐：枚举名 difficulty 按位拆分入列（"normal, raid" → 普通 + 突袭各一条）
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.ImportFromClipboardAsync("""{"stage_name":"1-7","difficulty":"normal, raid"}""");

        Assert.Equal(2, vm.Items.Count);
        Assert.False(vm.Items[0].IsRaid);
        Assert.True(vm.Items[1].IsRaid);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardSetAsync_EnumNameDifficulty_ShouldAddEntry_WpfParity()
    {
        // WPF 对齐：作业集条目的枚举名 difficulty 正常解析入列
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;

        await vm.LoadCurrentFromClipboardSetAsync(
            "[{\"stage_name\":\"1-7\",\"difficulty\":\"Raid\"}]");

        var item = Assert.Single(vm.Items);
        Assert.Equal("1-7", item.Name);
        Assert.True(item.IsRaid);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardSetAsync_NonIntegerDifficulty_ShouldSkipEntry_WpfParity()
    {
        // WPF 对齐：作业集中 difficulty 非整数的条目反序列化失败被跳过，合法条目正常入列
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;

        await vm.LoadCurrentFromClipboardSetAsync(
            "[{\"stage_name\":\"1-7\"},{\"stage_name\":\"1-8\",\"difficulty\":\"abc\"}]");

        var item = Assert.Single(vm.Items);
        Assert.Equal("1-7", item.Name);
        Assert.Contains("已添加 1 个作业", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoopTimes_ShouldAllowZeroAndClampNegative_WpfParity()
    {
        // WPF 对齐：LoopTimes 的 NumericUpDown Minimum=0，允许 0（不循环）
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        vm.LoopTimes = 0;

        Assert.Equal(0, vm.LoopTimes);

        vm.LoopTimes = -5;

        Assert.Equal(0, vm.LoopTimes);
    }

    [Fact]
    public async Task ClipboardSetButtonAndListPanel_ShouldBeHiddenOnUnsupportedTabs()
    {
        // WPF 对齐：作业集按钮与战斗列表面板仅在主线(0)/悖论(2)页签可用
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        vm.CopilotTabIndex = 0;
        vm.UseCopilotList = true;
        Assert.True(vm.ShowClipboardSetButton);
        Assert.True(vm.ShowCopilotListPanel);

        vm.CopilotTabIndex = 1;
        Assert.False(vm.ShowClipboardSetButton);
        Assert.False(vm.UseCopilotList);
        Assert.False(vm.ShowCopilotListPanel);

        vm.CopilotTabIndex = 3;
        Assert.False(vm.ShowClipboardSetButton);
        Assert.False(vm.ShowCopilotListPanel);

        vm.CopilotTabIndex = 2;
        Assert.True(vm.ShowClipboardSetButton);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardSetAsync_OnUnsupportedTab_ShouldFailWithActionableError()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 1;

        await vm.LoadCurrentFromClipboardSetAsync("[{\"stage_name\":\"1-7\"}]");

        Assert.Empty(vm.Items);
        Assert.Contains("不支持作业集", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportFilesToListAsync_OnUnsupportedTab_ShouldFailWithActionableError()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 3;

        await vm.ImportFilesToListAsync(["/nonexistent/copilot.json"]);

        Assert.Empty(vm.Items);
        Assert.Contains("不支持作业集", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanInactiveListItemsAsync_ShouldRemoveOnlyUncheckedItems()
    {
        // WPF 对齐：清空按钮右键 = CleanUnableCopilotTask，仅移除未勾选项
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        await vm.AddEmptyTaskAsync();
        await vm.SetListItemCheckedAsync(vm.Items[0], false);

        await vm.CleanInactiveListItemsAsync();

        var remaining = Assert.Single(vm.Items);
        Assert.True(remaining.IsChecked);
        var persistedArray = Assert.IsType<JsonArray>(JsonNode.Parse(GetPersistedTaskListPayload(fixture.Config)!));
        Assert.Single(persistedArray);
    }

    [Fact]
    public async Task UserAdditionalPopup_SkillShouldClampToWpfZeroToThree()
    {
        // WPF dev-v2 b2d8591974 对齐：追加干员技能夹取到 [0,3]，默认 0（不指定技能）
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.UserAdditional = "[{\"name\":\"阿米娅\",\"skill\":0},{\"name\":\"能天使\",\"skill\":9}]";

        vm.OpenUserAdditionalPopup();

        Assert.Equal(2, vm.UserAdditionalItems.Count);
        Assert.Equal(0, vm.UserAdditionalItems[0].Skill);
        Assert.Equal(3, vm.UserAdditionalItems[1].Skill);
    }

    [Fact]
    public async Task UserAdditionalPopup_LegacyFormat_ShouldDefaultSkillToZero()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.UserAdditional = "阿米娅;能天使,0;";

        vm.OpenUserAdditionalPopup();

        Assert.Equal(2, vm.UserAdditionalItems.Count);
        Assert.Equal(0, vm.UserAdditionalItems[0].Skill);
        Assert.Equal(0, vm.UserAdditionalItems[1].Skill);
    }

    [Fact]
    public async Task UserAdditionalPopup_LegacyMultiDigitOrNonNumericSkill_ShouldDropEntry_WpfParity()
    {
        // WPF ParseUserAdditionals 旧格式正则对齐（冻结 CopilotViewModel.cs:1941）：
        // (?<=;)(?<name>[^,;]+)(?:, *(?<skill>\d))?(?=;) 要求技能为单个数字且紧跟分号边界，
        // "name,10"（多数字）与 "name,x"（非数字）整条丢弃，不得夹取保留
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.UserAdditional = "能天使,10;阿米娅,2;史尔特尔,x";

        vm.OpenUserAdditionalPopup();

        var item = Assert.Single(vm.UserAdditionalItems);
        Assert.Equal("阿米娅", item.Name);
        Assert.Equal(2, item.Skill);
    }

    [Fact]
    public void IsParadoxStageFromOverviewJson_ShouldDetectMemStages_WpfParity()
    {
        // WPF 对齐：DataHelper.FindMap（overview.json）命中且 stageId 以 mem_ 开头视为悖论模拟；
        // 生产资源 stages.json 不含 mem_ 关卡（overview.json 才有），不能以其判定
        var overviewJson = """
            {
              "mem_amiya_1-obt/memory/level_memory_amiya_1": {
                "code": "mem_amiya_1",
                "levelId": "obt/memory/level_memory_amiya_1",
                "name": "阿米娅的悖论模拟",
                "stageId": "mem_amiya_1"
              },
              "main_01-07-obt/main/level_main_01-07": {
                "code": "1-7",
                "levelId": "obt/main/level_main_01-07",
                "name": "暴君",
                "stageId": "main_01-07"
              }
            }
            """;

        Assert.True(CopilotPageViewModel.IsParadoxStageFromOverviewJson("mem_amiya_1", overviewJson));
        Assert.True(CopilotPageViewModel.IsParadoxStageFromOverviewJson("阿米娅的悖论模拟", overviewJson));
        Assert.False(CopilotPageViewModel.IsParadoxStageFromOverviewJson("1-7", overviewJson));
        Assert.False(CopilotPageViewModel.IsParadoxStageFromOverviewJson("不存在", overviewJson));
        Assert.False(CopilotPageViewModel.IsParadoxStageFromOverviewJson(string.Empty, overviewJson));
    }

    [Fact]
    public async Task LoadParadoxCopilot_WithoutTypeField_ShouldDetectParadoxFromOverviewJson_WpfParity()
    {
        // WPF ParseCopilotAsync 对齐：无 type 字段的悖论作业经 overview.json stageId（mem_ 前缀）识别为悖论模拟，
        // 入列恒单条目（isRaid=false）且条目名取干员名
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;
        var resourceDir = Path.Combine(fixture.Root, "resource");
        Directory.CreateDirectory(Path.Combine(resourceDir, "Arknights-Tile-Pos"));
        await File.WriteAllTextAsync(
            Path.Combine(resourceDir, "Arknights-Tile-Pos", "overview.json"),
            """
            {
              "mem_12fce_1-obt/memory/level_memory_12fce_1": {
                "code": "mem_12fce_1",
                "levelId": "obt/memory/level_memory_12fce_1",
                "name": "12F的悖论模拟",
                "stageId": "mem_12fce_1"
              }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(resourceDir, "battle_data.json"),
            """{ "chars": { "char_009_12fce": { "name": "12F", "profession": "CASTER" } } }""");
        vm.ResourceDirectoryOverride = resourceDir;

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"mem_12fce_1"}""");
        await vm.AddCurrentToListAsync(isRaid: true);

        var item = Assert.Single(vm.Items);
        Assert.False(item.IsRaid);
        Assert.Equal(2, item.TabIndex);
        Assert.Equal("12F", item.Name);
    }

    [Fact]
    public async Task AddCurrentToListAsync_WhenStageNameIsAlias_ShouldKeepNormalizedCodeName_WpfParity()
    {
        // WPF AddCopilotTaskToList 对齐：条目名取 navigateName（ParseCopilotAsync 已规范化为 mapInfo.Code），
        // 不得覆盖回原始 stage_name，否则运行期导航失败
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;
        var resourceDir = Path.Combine(fixture.Root, "resource");
        Directory.CreateDirectory(Path.Combine(resourceDir, "Arknights-Tile-Pos"));
        await File.WriteAllTextAsync(
            Path.Combine(resourceDir, "Arknights-Tile-Pos", "overview.json"),
            """
            {
              "a001_01-activities/a001/level_a001_01": {
                "code": "GT-1",
                "levelId": "activities/a001/level_a001_01",
                "name": "日正当中",
                "stageId": "a001_01"
              }
            }
            """);
        vm.ResourceDirectoryOverride = resourceDir;

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"日正当中"}""");
        Assert.Equal("GT-1", vm.CopilotTaskName);
        Assert.DoesNotContain(vm.Logs, log => log.Content.Contains("不支持的关卡", StringComparison.Ordinal));

        await vm.AddCurrentToListAsync(isRaid: false);

        var item = Assert.Single(vm.Items);
        Assert.Equal("GT-1", item.Name);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_WhenStageUnknown_ShouldLogUnsupportedStagesAndUnlockMapOutdated_WpfParity()
    {
        // WPF ParseCopilotAsync 对齐：地图数据存在但 FindMap 失败时打 UnsupportedStages 错误日志并解锁 MapOutdated
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;
        var resourceDir = Path.Combine(fixture.Root, "resource");
        Directory.CreateDirectory(Path.Combine(resourceDir, "Arknights-Tile-Pos"));
        await File.WriteAllTextAsync(
            Path.Combine(resourceDir, "Arknights-Tile-Pos", "overview.json"),
            """
            {
              "a001_01-activities/a001/level_a001_01": {
                "code": "GT-1",
                "levelId": "activities/a001/level_a001_01",
                "name": "日正当中",
                "stageId": "a001_01"
              }
            }
            """);
        vm.ResourceDirectoryOverride = resourceDir;

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"9-99"}""");

        Assert.Contains(vm.Logs, log => log.Content.Contains("不支持的关卡", StringComparison.Ordinal));
        var snapshot = await fixture.Runtime.AchievementTrackerService.GetSnapshotAsync();
        Assert.Contains(snapshot.Value!.Items, item => item.Id == "MapOutdated" && item.IsUnlocked);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_WhitespaceStageName_ShouldLogUnsupportedStagesAndUnlockMapOutdated_WpfParity()
    {
        // WPF ParseCopilotAsync 对齐（冻结 CopilotViewModel.cs:1139-1194）：stage_name 必填校验为
        // IsNullOrEmpty，纯空白关卡名放行后由 FindMap 判定 null → UnsupportedStages 错误日志 +
        // MapOutdated 解锁；剪贴板/maa://来源不得因描述符层回退把 StageName 吞成空串而静默成功
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;
        var resourceDir = Path.Combine(fixture.Root, "resource");
        Directory.CreateDirectory(Path.Combine(resourceDir, "Arknights-Tile-Pos"));
        await File.WriteAllTextAsync(
            Path.Combine(resourceDir, "Arknights-Tile-Pos", "overview.json"),
            """
            {
              "main_01-07-obt/main/level_main_01-07": {
                "code": "1-7",
                "levelId": "obt/main/level_main_01-07",
                "name": "暴君",
                "stageId": "main_01-07"
              }
            }
            """);
        vm.ResourceDirectoryOverride = resourceDir;

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"   "}""");

        Assert.True(vm.HasLoadedCopilot);
        Assert.Contains(vm.Logs, log => log.Content.Contains("不支持的关卡", StringComparison.Ordinal) && log.IsError);
        var snapshot = await fixture.Runtime.AchievementTrackerService.GetSnapshotAsync();
        Assert.Contains(snapshot.Value!.Items, item => item.Id == "MapOutdated" && item.IsUnlocked);
    }

    [Fact]
    public async Task ImportFilesToListAsync_WithSssFile_ShouldLogSssNotSupported_WpfParity()
    {
        // WPF ImportFiles 对齐：批量导入遇到 SSS 作业逐文件打 CopilotSSSNotSupport 错误日志
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;
        var normalPath = Path.Combine(fixture.Root, "normal.json");
        var sssPath = Path.Combine(fixture.Root, "sss.json");
        await File.WriteAllTextAsync(normalPath, """{"stage_name":"1-7"}""");
        await File.WriteAllTextAsync(sssPath, """{"type":"SSS","stage_name":"sss-test"}""");

        await vm.ImportFilesToListAsync([normalPath, sssPath]);

        var item = Assert.Single(vm.Items);
        Assert.Equal("1-7", item.Name);
        Assert.Contains(vm.Logs, log => log.Content.Contains("不支持的类型：SSS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportFilesToListAsync_InvalidMiddleFile_ShouldLogReadErrorAndAbortBatch_WpfParity()
    {
        // WPF ImportFiles 对齐（冻结 CopilotViewModel.cs:878-903）：第二个文件 difficulty 非法
        // （反序列化抛 JsonSerializationException）→ CopilotFileReadError 错误日志并中止整批，
        // 第三个文件不再处理
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;
        var valid1Path = Path.Combine(fixture.Root, "valid1.json");
        var invalidPath = Path.Combine(fixture.Root, "invalid.json");
        var valid2Path = Path.Combine(fixture.Root, "valid2.json");
        await File.WriteAllTextAsync(valid1Path, """{"stage_name":"1-7"}""");
        await File.WriteAllTextAsync(invalidPath, """{"stage_name":"1-7","difficulty":null}""");
        await File.WriteAllTextAsync(valid2Path, """{"stage_name":"1-7"}""");

        await vm.ImportFilesToListAsync([valid1Path, invalidPath, valid2Path]);

        var item = Assert.Single(vm.Items);
        Assert.Equal("1-7", item.Name);
        Assert.Contains("批量导入失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("difficulty", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.Contains(vm.Logs, log => log.Content.Contains("读取文件失败", StringComparison.Ordinal) && log.IsError);
    }

    [Fact]
    public async Task ImportFilesToListAsync_InvalidMiddleFile_ShouldPersistPartialBatchBeforeAbort_WpfParity()
    {
        // WPF 对齐（冻结 CopilotViewModel.cs:1690-1691）：AddCopilotTaskToList 逐文件末尾
        // SaveCopilotTask，中止整批时前半批已入列条目已持久化，重启后不丢失
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;
        var valid1Path = Path.Combine(fixture.Root, "valid1.json");
        var invalidPath = Path.Combine(fixture.Root, "invalid.json");
        var valid2Path = Path.Combine(fixture.Root, "valid2.json");
        await File.WriteAllTextAsync(valid1Path, """{"stage_name":"1-7"}""");
        await File.WriteAllTextAsync(invalidPath, """{"stage_name":"1-7","difficulty":null}""");
        await File.WriteAllTextAsync(valid2Path, """{"stage_name":"1-7"}""");

        await vm.ImportFilesToListAsync([valid1Path, invalidPath, valid2Path]);

        Assert.Single(vm.Items);
        Assert.Contains("批量导入失败", vm.StatusMessage, StringComparison.Ordinal);
        var persistedArray = Assert.IsType<JsonArray>(JsonNode.Parse(GetPersistedTaskListPayload(fixture.Config)!));
        var persistedItem = Assert.Single(persistedArray);
        Assert.Equal("1-7", persistedItem?["Name"]?.GetValue<string>());
    }

    [Fact]
    public async Task ImportFilesToListAsync_AfterSingleJobLoaded_ShouldInvalidateLoadedCopilotAndRejectStart_WpfParity()
    {
        // WPF ImportFiles 入口对齐（冻结 CopilotViewModel.cs:867-868）：确认批量导入后无条件
        // CopilotId = 0; _copilotCache = null；首文件非法中止后旧单作业不得残留供 Start 执行
        await using var fixture = await CopilotFixture.CreateAsync(
            ensureConnectedAsync: _ => Task.FromResult(UiOperationResult.Ok("ok")));
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;
        var loadedPath = Path.Combine(fixture.Root, "loaded.json");
        var invalidPath = Path.Combine(fixture.Root, "invalid.json");
        await File.WriteAllTextAsync(loadedPath, """{"stage_name":"1-7"}""");
        await File.WriteAllTextAsync(invalidPath, """{"stage_name":"1-7","difficulty":null}""");
        await vm.LoadCurrentFromFileAsync(loadedPath);
        Assert.True(vm.HasLoadedCopilot);
        Assert.False(vm.UseCopilotList);

        await vm.ImportFilesToListAsync([invalidPath]);

        Assert.False(vm.HasLoadedCopilot);
        Assert.Contains("批量导入失败", vm.StatusMessage, StringComparison.Ordinal);

        await vm.StartAsync();

        Assert.Contains("启动失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("请选择要执行的作业", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportFilesToListAsync_WhitespaceStageName_ShouldLogStageNotFoundAndContinue_WpfParity()
    {
        // WPF 对齐（冻结 CopilotViewModel.cs:1595-1620）：stage_name 必填校验为 string.IsNullOrEmpty，
        // 纯空白关卡名通过语义校验后由 FindMap 判定为无法识别 → 逐文件错误日志并继续批量导入，
        // 不得整批中止
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;
        var resourceDir = Path.Combine(fixture.Root, "resource");
        Directory.CreateDirectory(Path.Combine(resourceDir, "Arknights-Tile-Pos"));
        await File.WriteAllTextAsync(
            Path.Combine(resourceDir, "Arknights-Tile-Pos", "overview.json"),
            """
            {
              "main_01-07-obt/main/level_main_01-07": {
                "code": "1-7",
                "levelId": "obt/main/level_main_01-07",
                "name": "暴君",
                "stageId": "main_01-07"
              }
            }
            """);
        vm.ResourceDirectoryOverride = resourceDir;
        var whitespacePath = Path.Combine(fixture.Root, "whitespace.json");
        var validPath = Path.Combine(fixture.Root, "valid.json");
        await File.WriteAllTextAsync(whitespacePath, """{"stage_name":"   "}""");
        await File.WriteAllTextAsync(validPath, """{"stage_name":"1-7"}""");

        await vm.ImportFilesToListAsync([whitespacePath, validPath]);

        var item = Assert.Single(vm.Items);
        Assert.Equal("1-7", item.Name);
        Assert.Contains(vm.Logs, log => log.Content.Contains("无法识别作业关卡", StringComparison.Ordinal) && log.IsError);
        Assert.Contains("已批量导入 1 个作业", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadCurrentFromDisplayInputAsync_BareFileName_ShouldResolveNestedCopilotDirectory_WpfParity()
    {
        // WPF DisplayFilename setter 对齐：_copilotJsonPathMap 覆盖 resource/copilot 嵌套子目录，裸文件名可解析
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        var resourceDir = Path.Combine(fixture.Root, "resource");
        var nestedDir = Path.Combine(resourceDir, "copilot", "活动");
        Directory.CreateDirectory(nestedDir);
        var nestedFile = Path.Combine(nestedDir, "nested-job.json");
        await File.WriteAllTextAsync(nestedFile, """{"stage_name":"1-7","copilot_id":42}""");
        vm.ResourceDirectoryOverride = resourceDir;

        vm.DisplayFilename = "nested-job.json";
        await vm.LoadCurrentFromDisplayInputAsync();

        Assert.True(vm.HasLoadedCopilot);
        Assert.Equal(nestedFile, vm.FilePath);
        Assert.Equal("1-7", vm.CopilotTaskName);
    }

    [Fact]
    public async Task LoadCurrentFromDisplayInputAsync_NonexistentAbsolutePath_ShouldFallbackToFileNameMapping_WpfParity()
    {
        // WPF DisplayFilename setter 对齐（冻结 :271-274）：不存在的绝对路径同样按文件名查
        // _copilotJsonPathMap（resource/copilot 嵌套目录）回退解析
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        var resourceDir = Path.Combine(fixture.Root, "resource");
        var nestedDir = Path.Combine(resourceDir, "copilot", "活动");
        Directory.CreateDirectory(nestedDir);
        var nestedFile = Path.Combine(nestedDir, "abs-job.json");
        await File.WriteAllTextAsync(nestedFile, """{"stage_name":"1-7","copilot_id":43}""");
        vm.ResourceDirectoryOverride = resourceDir;

        vm.DisplayFilename = Path.Combine(fixture.Root, "nonexistent-dir", "abs-job.json");
        await vm.LoadCurrentFromDisplayInputAsync();

        Assert.True(vm.HasLoadedCopilot);
        Assert.Equal(nestedFile, vm.FilePath);
        Assert.Equal("1-7", vm.CopilotTaskName);
    }

    [Fact]
    public async Task LoadCurrentFromDisplayInputAsync_GarbageInput_ShouldInvalidateLoadedCopilotAndRejectStart_WpfParity()
    {
        // WPF DisplayFilename setter 对齐（冻结 :1002-1009、:1056-1062、:1813-1820）：
        // 有效作业加载后提交无法解析的输入，已加载作业立即作废并打 CopilotJsonError 错误日志，
        // Start 不得再静默执行旧作业
        await using var fixture = await CopilotFixture.CreateAsync(
            ensureConnectedAsync: _ => Task.FromResult(UiOperationResult.Ok("ok")));
        var vm = fixture.ViewModel;
        var filePath = Path.Combine(fixture.Root, "job.json");
        await File.WriteAllTextAsync(filePath, """{"stage_name":"1-7"}""");
        await vm.LoadCurrentFromFileAsync(filePath);
        Assert.True(vm.HasLoadedCopilot);

        vm.DisplayFilename = "asdf";
        await vm.LoadCurrentFromDisplayInputAsync();

        Assert.False(vm.HasLoadedCopilot);
        Assert.Contains("读取作业失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("解析作业文件错误", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.Contains(vm.Logs, log => log.Content.Contains("解析作业文件错误", StringComparison.Ordinal));

        await vm.StartAsync();

        Assert.Contains("启动失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("请选择要执行的作业", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadCurrentFromDisplayInputAsync_MultiSlashCopilotCode_ShouldReject_WpfParity()
    {
        // WPF GetCopilotAsync 对齐（冻结 CopilotViewModel.cs:1097-1103）：剥离 maa:// 前缀后
        // int.TryParse 不容忍多余斜杠，maa:///123 解析失败（CopilotNoFound），不得当作有效作业码
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        vm.DisplayFilename = "maa:///123";
        await vm.LoadCurrentFromDisplayInputAsync();

        Assert.False(vm.HasLoadedCopilot);
        Assert.Contains("读取作业失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("解析作业文件错误", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadCurrentFromFileAsync_ParseFailure_ShouldInvalidateLoadedCopilotAndRejectStart_WpfParity()
    {
        // WPF UpdateFileDoc 对齐（冻结 :1007-1009、:1031-1035）：选择解析失败的新文件后
        // 已加载作业立即作废，Start 拒绝执行旧作业
        await using var fixture = await CopilotFixture.CreateAsync(
            ensureConnectedAsync: _ => Task.FromResult(UiOperationResult.Ok("ok")));
        var vm = fixture.ViewModel;
        var validPath = Path.Combine(fixture.Root, "valid.json");
        var brokenPath = Path.Combine(fixture.Root, "broken.json");
        await File.WriteAllTextAsync(validPath, """{"stage_name":"1-7"}""");
        await File.WriteAllTextAsync(brokenPath, "not-a-json");
        await vm.LoadCurrentFromFileAsync(validPath);
        Assert.True(vm.HasLoadedCopilot);

        await vm.LoadCurrentFromFileAsync(brokenPath);

        Assert.False(vm.HasLoadedCopilot);
        Assert.Contains("读取作业失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(vm.LastErrorMessage));

        await vm.StartAsync();

        Assert.Contains("启动失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("请选择要执行的作业", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverviewJsonCache_ShouldInvalidate_WhenResourceFileChanges()
    {
        // O7 资源文本缓存：按（路径 + 写入时间 + 长度）缓存；资源文件改写后必须重新读取，不得返回陈旧内容
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;
        var resourceDir = Path.Combine(fixture.Root, "resource");
        var overviewDir = Path.Combine(resourceDir, "Arknights-Tile-Pos");
        Directory.CreateDirectory(overviewDir);
        var overviewPath = Path.Combine(overviewDir, "overview.json");
        await File.WriteAllTextAsync(
            overviewPath,
            """
            {
              "a001_01-activities/a001/level_a001_01": {
                "code": "GT-1",
                "levelId": "activities/a001/level_a001_01",
                "name": "日正当中",
                "stageId": "a001_01"
              }
            }
            """);
        vm.ResourceDirectoryOverride = resourceDir;

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"日正当中"}""");
        await vm.AddCurrentToListAsync(isRaid: false);
        Assert.Equal("GT-1", Assert.Single(vm.Items).Name);
        // 入列后 UseCopilotList 被置 true，后续加载会自动入列；此处关闭以隔离手动添加路径
        vm.UseCopilotList = false;

        // 改写 overview.json（内容长度不同），新关卡应能命中、旧关卡应不再命中
        await File.WriteAllTextAsync(
            overviewPath,
            """
            {
              "a001_02-activities/a001/level_a001_02": {
                "code": "GT-2",
                "levelId": "activities/a001/level_a001_02",
                "name": "夕照",
                "stageId": "a001_02",
                "extra": "padding-to-change-length"
              }
            }
            """);

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"夕照"}""");
        await vm.AddCurrentToListAsync(isRaid: false);
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("GT-2", vm.Items[1].Name);

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"日正当中"}""");
        var countBefore = vm.Items.Count;
        await vm.AddCurrentToListAsync(isRaid: false);
        Assert.Equal(countBefore, vm.Items.Count);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardSetAsync_MixedWithSssEntry_ShouldSkipSssEntries_WpfParity()
    {
        // WPF 对齐：ParseCopilotSetAsync 仅处理 CopilotModel，type=SSS 的保全派驻作业不进入战斗列表
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;

        await vm.LoadCurrentFromClipboardSetAsync(
            "[{\"stage_name\":\"1-7\"},{\"type\":\"SSS\",\"stage_name\":\"sss-test\",\"copilot_id\":77}]");

        var item = Assert.Single(vm.Items);
        Assert.Equal("1-7", item.Name);
        Assert.Equal(0, item.TabIndex);
        Assert.Contains("已添加 1 个作业", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardSetAsync_OnlySssEntries_ShouldFailWithoutAddingItems()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;

        await vm.LoadCurrentFromClipboardSetAsync("[{\"type\":\"SSS\",\"stage_name\":\"sss-test\"}]");

        Assert.Empty(vm.Items);
        Assert.Contains("读取作业集失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("没有可识别的作业条目", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_SssPayloadOnMainTab_ShouldNotAutoAddToList_WpfParity()
    {
        // WPF 对齐：SSSCopilotModel 走 ParseSSSCopilot 只写缓存，从不加入战斗列表
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;
        vm.UseCopilotList = true;

        await vm.LoadCurrentFromClipboardAsync("{\"type\":\"SSS\",\"stage_name\":\"sss-test\",\"copilot_id\":66}");

        Assert.Empty(vm.Items);
        Assert.True(vm.HasLoadedCopilot);
    }

    [Fact]
    public async Task AddCurrentToListAsync_SssLoaded_ShouldRejectWithWpfParityError()
    {
        // WPF 对齐：ParseSSSCopilot 置 CopilotTabIndex=1，页签 1 不支持战斗列表（UseCopilotList 强制 false），
        // AddCopilotTaskToList 的 SSS 拒绝分支在页签 1 下不可达，由页签守卫拦截且不添加
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;
        vm.UseCopilotList = true;

        await vm.LoadCurrentFromClipboardAsync("{\"type\":\"SSS\",\"stage_name\":\"sss-test\",\"copilot_id\":66}");
        await vm.AddCurrentToListAsync(isRaid: false);

        Assert.Empty(vm.Items);
        Assert.Equal(1, vm.CopilotTabIndex);
        Assert.Contains("当前页签不支持作业集", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddCurrentToListAsync_ParadoxRaidRequest_ShouldForceNonRaidItem_WpfParity()
    {
        // WPF 对齐：悖论分支恒 isRaid=false，右键「添加突袭」不透传难度
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 2;

        await vm.LoadCurrentFromClipboardAsync("{\"stage_name\":\"mem_12fce_1\",\"type\":\"ParadoxCopilot\"}");
        await vm.AddCurrentToListAsync(isRaid: true);

        var item = Assert.Single(vm.Items);
        Assert.False(item.IsRaid);
        Assert.Equal(2, item.TabIndex);
        Assert.Contains("已添加作业到战斗列表", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_WhenListValidationFails_ShouldNotAttemptConnection_WpfParity()
    {
        // WPF 对齐：Start() 先 ValidateStartAsync，校验失败直接返回，不发起连接
        await using var fixture = await CopilotFixture.CreateAsync();
        var ensureCalls = 0;
        var vm = new CopilotPageViewModel(
            fixture.Runtime,
            ensureConnectedAsync: _ =>
            {
                ensureCalls++;
                return Task.FromResult(UiOperationResult.Ok("connected"));
            });
        vm.UseCopilotList = true;

        await vm.StartAsync();

        Assert.Equal(0, ensureCalls);
        Assert.Contains("未勾选任何作业", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportFromClipboardAsync_ParadoxWithDifficulty_ShouldAddSingleNonRaidItem_WpfParity()
    {
        // WPF 对齐：悖论页签恒添加单个条目，不按 difficulty 位掩码拆分普通/突袭
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 2;

        await vm.ImportFromClipboardAsync("{\"stage_name\":\"mem_12fce_1\",\"type\":\"ParadoxCopilot\",\"difficulty\":3}");

        var item = Assert.Single(vm.Items);
        Assert.False(item.IsRaid);
        Assert.Equal(2, item.TabIndex);
        // 测试环境无地图/干员资源，条目名回退为关卡名
        Assert.Equal("mem_12fce_1", item.Name);
    }

    [Fact]
    public void ResolveParadoxOperatorName_ShouldMapStageIdToOperatorName_WpfParity()
    {
        // WPF 对齐：FindMap(stage_name).StageId[4..^2] → GetCharacterByCodeName → 干员名
        var overview = """
            {
              "mem_12fce_1-obt/memory/level_memory_12fce_1": {
                "code": "mem_12fce_1",
                "filename": "mem_12fce_1-obt-memory-level_memory_12fce_1.json",
                "levelId": "obt/memory/level_memory_12fce_1",
                "name": "12F的悖论模拟",
                "stageId": "mem_12fce_1"
              }
            }
            """;
        var battleData = """
            {
              "chars": {
                "char_009_12fce": { "name": "12F", "profession": "CASTER" },
                "trap_001_dummy": { "name": "假人" }
              }
            }
            """;

        Assert.Equal("12F", CopilotPageViewModel.ResolveParadoxOperatorName("mem_12fce_1", overview, battleData));
        // 按中文名/levelId 也能命中同一关卡（WPF FindMap 多键匹配）
        Assert.Equal("12F", CopilotPageViewModel.ResolveParadoxOperatorName("12F的悖论模拟", overview, battleData));
        Assert.Equal("12F", CopilotPageViewModel.ResolveParadoxOperatorName("obt/memory/level_memory_12fce_1", overview, battleData));
        Assert.Null(CopilotPageViewModel.ResolveParadoxOperatorName("unknown-stage", overview, battleData));
        Assert.Null(CopilotPageViewModel.ResolveParadoxOperatorName("mem_12fce_1", null, battleData));
        Assert.Null(CopilotPageViewModel.ResolveParadoxOperatorName("mem_12fce_1", overview, null));
    }

    [Fact]
    public void IsKnownStageFromOverviewJson_ShouldMatchCodeNameStageIdAndLevelId_WpfParity()
    {
        var overview = """
            {
              "a001_01-activities/a001/level_a001_01": {
                "code": "GT-1",
                "filename": "a001_01-activities-a001-level_a001_01.json",
                "levelId": "activities/a001/level_a001_01",
                "name": "日正当中",
                "stageId": "a001_01"
              }
            }
            """;

        Assert.True(CopilotPageViewModel.IsKnownStageFromOverviewJson("GT-1", overview));
        Assert.True(CopilotPageViewModel.IsKnownStageFromOverviewJson("日正当中", overview));
        Assert.True(CopilotPageViewModel.IsKnownStageFromOverviewJson("a001_01", overview));
        Assert.True(CopilotPageViewModel.IsKnownStageFromOverviewJson("activities/a001/level_a001_01", overview));
        Assert.False(CopilotPageViewModel.IsKnownStageFromOverviewJson("GT-2", overview));
        Assert.False(CopilotPageViewModel.IsKnownStageFromOverviewJson(string.Empty, overview));
        Assert.False(CopilotPageViewModel.IsKnownStageFromOverviewJson("GT-1", "not-json"));
    }

    [Fact]
    public void IsKnownOperatorNameFromBattleDataJson_ShouldMatchOperatorNamesAcrossLanguages_WpfParity()
    {
        var battleData = """
            {
              "chars": {
                "char_002_amiya": { "name": "阿米娅", "name_en": "Amiya", "name_jp": "アーミヤ", "profession": "CASTER" },
                "token_10027_vigil_summon": { "name": "召唤物", "profession": "TOKEN" },
                "char_999_device": { "name": "干扰装置", "profession": "TRAP" }
              }
            }
            """;

        Assert.True(CopilotPageViewModel.IsKnownOperatorNameFromBattleDataJson("阿米娅", battleData));
        Assert.True(CopilotPageViewModel.IsKnownOperatorNameFromBattleDataJson("Amiya", battleData));
        Assert.True(CopilotPageViewModel.IsKnownOperatorNameFromBattleDataJson("アーミヤ", battleData));
        // WPF GetCharacterByNameOrAlias 对齐：名称索引 OrdinalIgnoreCase，大小写不敏感
        Assert.True(CopilotPageViewModel.IsKnownOperatorNameFromBattleDataJson("amiya", battleData));
        Assert.True(CopilotPageViewModel.IsKnownOperatorNameFromBattleDataJson("AMIYA", battleData));
        // 非 char_ 前缀与非干员职业不算合法干员名（WPF DataHelper.Operators 过滤）
        Assert.False(CopilotPageViewModel.IsKnownOperatorNameFromBattleDataJson("召唤物", battleData));
        Assert.False(CopilotPageViewModel.IsKnownOperatorNameFromBattleDataJson("干扰装置", battleData));
        Assert.False(CopilotPageViewModel.IsKnownOperatorNameFromBattleDataJson("不存在", battleData));
        Assert.False(CopilotPageViewModel.IsKnownOperatorNameFromBattleDataJson(string.Empty, battleData));
    }

    private static string? GetPersistedTaskListPayload(UnifiedConfigurationService config)
    {
        if (!config.TryGetCurrentProfile(out var profile))
        {
            return null;
        }

        if (!profile.Values.TryGetValue(LegacyConfigurationKeys.CopilotTaskList, out var node)
            && !config.CurrentConfig.GlobalValues.TryGetValue(LegacyConfigurationKeys.CopilotTaskList, out node))
        {
            return null;
        }

        return node switch
        {
            JsonArray array => array.ToJsonString(),
            JsonValue value when value.TryGetValue(out string? persisted) => persisted,
            _ => null,
        };
    }

    private static async Task<bool> WaitForLogContainsAsync(string path, string expected, int retry = 30, int delayMs = 25)
    {
        for (var i = 0; i < retry; i++)
        {
            if (File.Exists(path))
            {
                var content = await File.ReadAllTextAsync(path);
                if (content.Contains(expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    private static async Task<bool> WaitForPersistedItemNameAsync(
        UnifiedConfigurationService config,
        string expectedName,
        int retry = 30,
        int delayMs = 25)
    {
        for (var i = 0; i < retry; i++)
        {
            var payload = GetPersistedTaskListPayload(config);
            if (!string.IsNullOrWhiteSpace(payload)
                && JsonNode.Parse(payload) is JsonArray array
                && array.OfType<JsonObject>().Any(item =>
                    string.Equals(item["Name"]?.GetValue<string>(), expectedName, StringComparison.Ordinal)))
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    private static async Task<bool> WaitForPersistedItemTabAsync(
        UnifiedConfigurationService config,
        string expectedName,
        int expectedTabIndex,
        string expectedType,
        int retry = 30,
        int delayMs = 25)
    {
        for (var i = 0; i < retry; i++)
        {
            var payload = GetPersistedTaskListPayload(config);
            if (!string.IsNullOrWhiteSpace(payload)
                && JsonNode.Parse(payload) is JsonArray array
                && array.OfType<JsonObject>().Any(item =>
                    string.Equals(item["Name"]?.GetValue<string>(), expectedName, StringComparison.Ordinal)
                    && item["TabIndex"]?.GetValue<int>() == expectedTabIndex
                    && string.Equals(item["Type"]?.GetValue<string>(), expectedType, StringComparison.Ordinal)))
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_MinimumRequiredAboveCoreVersion_ShouldRejectLoad_WpfParity()
    {
        // WPF CopilotContentConverter 对齐（冻结 CopilotHelper.cs:228-272）：require > 当前 core → 拒绝加载
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CoreVersionOverride = "v5.0.0";
        vm.UiVersionOverride = "v1.0.0";

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"1-7","minimum_required":"v5.1.0"}""");

        Assert.False(vm.HasLoadedCopilot);
        Assert.Contains("最低 core 版本", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_MinimumRequiredMissing_ShouldRejectLoad_WpfParity()
    {
        // WPF 对齐：CopilotBase.MinimumRequired 默认 string.Empty，SemVersion.TryParse 失败即抛
        // （发布版本下 minimum_required 事实上必填）
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CoreVersionOverride = "v5.0.0";
        vm.UiVersionOverride = "v1.0.0";

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"1-7"}""");

        Assert.False(vm.HasLoadedCopilot);
        Assert.Contains("minimum_required", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_MinimumRequiredWithinCoreVersion_ShouldLoad_WpfParity()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CoreVersionOverride = "v5.0.0";
        vm.UiVersionOverride = "v1.0.0";

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"1-7","minimum_required":"v4.24.0"}""");

        Assert.True(vm.HasLoadedCopilot);
        Assert.Equal(string.Empty, vm.LastErrorMessage);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_DebugUiVersion_ShouldSkipMinimumRequiredGate_WpfParity()
    {
        // WPF 对齐（冻结 CopilotHelper.cs:230/:253 + VersionUpdateDialogViewModel.cs:1221-1230）：
        // UI 版本命中 IsDebugVersion 正则时整体跳过 minimum_required 门控
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CoreVersionOverride = "v5.0.0";
        vm.UiVersionOverride = "DEBUG_VERSION";

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"1-7","minimum_required":"v99.0.0"}""");

        Assert.True(vm.HasLoadedCopilot);
        Assert.Equal(string.Empty, vm.LastErrorMessage);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_CoreVersionUnavailable_ShouldSkipGate()
    {
        // 版本来源不可用（未安装 core 的开发/测试环境）跳过门控；WPF 发布包必含 core，无此状态
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"1-7","minimum_required":"v99.0.0"}""");

        Assert.True(vm.HasLoadedCopilot);
    }

    [Fact]
    public async Task CopilotFeatureService_MinimumRequiredGate_ShouldCoverServiceValidatedChannels_WpfParity()
    {
        // 服务层门控（远程/文件/剪贴板校验共用 TryValidateCopilotPayload）：require > core、
        // 缺失 minimum_required、SSS 作业均拒绝；满足要求则放行
        var service = new CopilotFeatureService(
            coreVersionResolver: () => "v5.0.0",
            uiVersionResolver: () => "v1.0.0");

        var tooNew = await service.ImportFromClipboardAsync("""{"stage_name":"1-7","minimum_required":"v5.1.0"}""");
        Assert.False(tooNew.Success);
        Assert.Equal(UiErrorCode.CopilotPayloadVersionNotMet, tooNew.Error?.Code);

        var missing = await service.ImportFromClipboardAsync("""{"stage_name":"1-7"}""");
        Assert.False(missing.Success);
        Assert.Equal(UiErrorCode.CopilotPayloadVersionNotMet, missing.Error?.Code);

        var sssTooNew = await service.ImportFromClipboardAsync(
            """{"type":"SSS","stage_name":"GT-1","minimum_required":"v5.1.0","stages":[{"stage_name":"GT-1"}]}""");
        Assert.False(sssTooNew.Success);
        Assert.Equal(UiErrorCode.CopilotPayloadVersionNotMet, sssTooNew.Error?.Code);

        var satisfied = await service.ImportFromClipboardAsync("""{"stage_name":"1-7","minimum_required":"v4.24.0"}""");
        Assert.True(satisfied.Success);
    }

    [Fact]
    public async Task CopilotFeatureService_MinimumRequiredSemVerParse_ShouldRejectWpfRejectedForms_WpfParity()
    {
        // WPF Semver 3.0.0 锚定解析对齐：首尾空白、主版本号前导零、数字 prerelease 前导零均拒绝
        var service = new CopilotFeatureService(
            coreVersionResolver: () => "v5.0.0",
            uiVersionResolver: () => "v1.0.0");

        var padded = await service.ImportFromClipboardAsync("""{"stage_name":"1-7","minimum_required":" v4.24.0"}""");
        Assert.False(padded.Success);
        Assert.Equal(UiErrorCode.CopilotPayloadVersionNotMet, padded.Error?.Code);

        var leadingZeroCore = await service.ImportFromClipboardAsync("""{"stage_name":"1-7","minimum_required":"05.0.0"}""");
        Assert.False(leadingZeroCore.Success);
        Assert.Equal(UiErrorCode.CopilotPayloadVersionNotMet, leadingZeroCore.Error?.Code);

        var leadingZeroPrerelease = await service.ImportFromClipboardAsync("""{"stage_name":"1-7","minimum_required":"1.0.0-01"}""");
        Assert.False(leadingZeroPrerelease.Success);
        Assert.Equal(UiErrorCode.CopilotPayloadVersionNotMet, leadingZeroPrerelease.Error?.Code);

        // 空 prerelease 形式（WPF Semver 3.0 锚定解析要求 "-" 后至少一个标识符）：解析失败 → 整份拒绝
        var emptyPrerelease = await service.ImportFromClipboardAsync("""{"stage_name":"1-7","minimum_required":"1.2.3-"}""");
        Assert.False(emptyPrerelease.Success);
        Assert.Equal(UiErrorCode.CopilotPayloadVersionNotMet, emptyPrerelease.Error?.Code);

        var emptyPrereleaseWithV = await service.ImportFromClipboardAsync("""{"stage_name":"1-7","minimum_required":"v1.2.3-"}""");
        Assert.False(emptyPrereleaseWithV.Success);
        Assert.Equal(UiErrorCode.CopilotPayloadVersionNotMet, emptyPrereleaseWithV.Error?.Code);

        var emptyPrereleaseWithBuild = await service.ImportFromClipboardAsync("""{"stage_name":"1-7","minimum_required":"1.2.3-+build"}""");
        Assert.False(emptyPrereleaseWithBuild.Success);
        Assert.Equal(UiErrorCode.CopilotPayloadVersionNotMet, emptyPrereleaseWithBuild.Error?.Code);

        // core 版本含首尾空白：WPF SemVersion.TryParse 原样解析失败 → "Unable to parse Maa version" 拒绝
        var whitespaceCoreService = new CopilotFeatureService(
            coreVersionResolver: () => " v5.0.0 ",
            uiVersionResolver: () => "v1.0.0");
        var whitespaceCore = await whitespaceCoreService.ImportFromClipboardAsync("""{"stage_name":"1-7","minimum_required":"v4.24.0"}""");
        Assert.False(whitespaceCore.Success);
        Assert.Equal(UiErrorCode.CopilotPayloadVersionNotMet, whitespaceCore.Error?.Code);
    }

    [Fact]
    public async Task CopilotFeatureService_HugeNumericPrerelease_ShouldCompareNumerically_WpfParity()
    {
        // 超过 int.MaxValue 的数字 prerelease 标识符按数值比较（Semver 语义）：
        // 5.0.0-2 < 5.0.0-1000000000000000000000 → 版本不足拒绝
        var service = new CopilotFeatureService(
            coreVersionResolver: () => "5.0.0-2",
            uiVersionResolver: () => "v1.0.0");

        var result = await service.ImportFromClipboardAsync("""{"stage_name":"1-7","minimum_required":"5.0.0-1000000000000000000000"}""");

        Assert.False(result.Success);
        Assert.Equal(UiErrorCode.CopilotPayloadVersionNotMet, result.Error?.Code);

        // 常规长度数字 prerelease 同样按数值比较：5.0.0-1 < 5.0.0-2 → 版本不足拒绝
        // （覆盖 prerelease 标识符逐项比较路径）
        var smallService = new CopilotFeatureService(
            coreVersionResolver: () => "5.0.0-1",
            uiVersionResolver: () => "v1.0.0");
        var smallResult = await smallService.ImportFromClipboardAsync("""{"stage_name":"1-7","minimum_required":"5.0.0-2"}""");
        Assert.False(smallResult.Success);
        Assert.Equal(UiErrorCode.CopilotPayloadVersionNotMet, smallResult.Error?.Code);

        // 反向：core 5.0.0-2 >= require 5.0.0-1 → 放行
        var passService = new CopilotFeatureService(
            coreVersionResolver: () => "5.0.0-2",
            uiVersionResolver: () => "v1.0.0");
        var passResult = await passService.ImportFromClipboardAsync("""{"stage_name":"1-7","minimum_required":"5.0.0-1"}""");
        Assert.True(passResult.Success);
    }

    [Theory]
    [InlineData("maa://123", CopilotCodeSourceKind.Copilot, 123)]
    [InlineData("MAA://123", CopilotCodeSourceKind.Copilot, 123)]
    [InlineData("prts://123", CopilotCodeSourceKind.Copilot, 123)]
    [InlineData("prts://s123", CopilotCodeSourceKind.CopilotSet, 123)]
    [InlineData("PRTS://S123", CopilotCodeSourceKind.CopilotSet, 123)]
    [InlineData("s123", CopilotCodeSourceKind.CopilotSet, 123)]
    [InlineData("S123", CopilotCodeSourceKind.CopilotSet, 123)]
    [InlineData("123", CopilotCodeSourceKind.Copilot, 123)]
    public void CopilotCodeSourceParser_ShouldRecognizeAllWpfCodeFormats(
        string source,
        CopilotCodeSourceKind expectedKind,
        int expectedId)
    {
        // WPF dev-v2 72213def33：maa://、prts://、prts://s、s<id>、纯数字；前缀从长到短匹配
        Assert.True(CopilotCodeSourceParser.TryParse(source, out var kind, out var id));
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedId, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("maa:///123")]
    [InlineData("prts://s")]
    [InlineData("s")]
    [InlineData("s123x")]
    [InlineData("prts://abc")]
    [InlineData("not-a-code")]
    public void CopilotCodeSourceParser_ShouldRejectInvalidWpfCodeFormats(string source)
    {
        Assert.False(CopilotCodeSourceParser.TryParse(source, out var kind, out _));
        Assert.Equal(CopilotCodeSourceKind.None, kind);
    }

    [Theory]
    [InlineData("6.3")]
    [InlineData("v6.3")]
    [InlineData("6.3.0")]
    [InlineData("v6.3.7")]
    public void CopilotVersionGate_MinimumRequiredTwoSegmentVersion_ShouldPass_WpfParity(string minimumRequired)
    {
        // WPF dev-v2 25481d16ca：minimum_required 使用 SemVersionStyles.OptionalPatch，
        // ｢6.3｣ 视为 6.3.0；core 版本仍要求三段
        var payload = new JsonObject
        {
            ["stage_name"] = "1-7",
            ["minimum_required"] = minimumRequired,
        };

        Assert.Null(CopilotVersionGate.Evaluate(payload, "v6.3.7", "v1.0.0"));
    }

    [Theory]
    [InlineData("6")]
    [InlineData(" 6.3")]
    [InlineData("6.3 ")]
    [InlineData("v6")]
    public void CopilotVersionGate_InvalidMinimumRequiredVersion_ShouldReject(string minimumRequired)
    {
        var payload = new JsonObject
        {
            ["stage_name"] = "1-7",
            ["minimum_required"] = minimumRequired,
        };

        Assert.NotNull(CopilotVersionGate.Evaluate(payload, "v6.3.7", "v1.0.0"));
    }

#if DEBUG
    [Fact]
    public void CopilotVersionGate_ResolveUiVersion_LocalDebugBuild_ShouldMatchWpfDebugVersion()
    {
        // WPF 对齐：本地 Debug 配置构建的 UI 版本恒为 DEBUG_VERSION，命中 IsDebugVersion
        // 豁免正则（Release/CI 构建维持按版本字符串正则判定）
        Assert.Equal("DEBUG_VERSION", CopilotVersionGate.ResolveUiVersion());
        Assert.True(CopilotVersionGate.IsDebugVersion(CopilotVersionGate.ResolveUiVersion()));
    }
#endif

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_TypeKeyMixedCaseSss_ShouldClassifyAsSss_WpfParity()
    {
        // WPF 对齐（冻结 CopilotHelper.cs:227）："type" 键大小写不敏感 → {"Type":"SSS"} 识别为 SSS
        // （SSS 豁免 difficulty 校验并自动切到保全派驻页签）
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;

        await vm.LoadCurrentFromClipboardAsync(
            """{"Type":"SSS","stage_name":"GT-1","difficulty":null,"stages":[{"stage_name":"GT-1"}]}""");

        Assert.True(vm.HasLoadedCopilot);
        Assert.Equal(1, vm.CopilotTabIndex);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_DuplicateCaseVariantStageNameKey_ShouldUseLastValue_WpfParity()
    {
        // Newtonsoft POCO 绑定对齐：大小写变体重复键按文档序 last-wins
        // {"stage_name":"ZZ-9","STAGE_NAME":"1-7"} → StageName = "1-7"
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"ZZ-9","STAGE_NAME":"1-7"}""");

        Assert.True(vm.HasLoadedCopilot);
        Assert.Equal("1-7", vm.CopilotTaskName);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_DuplicateCaseVariantTypeKey_ShouldUseFirstMatchForSssDetection_WpfParity()
    {
        // WPF CopilotContentConverter 对齐（冻结 CopilotHelper.cs:227）：SSS 判定走
        // JObject.TryGetValue("type", OrdinalIgnoreCase)，返回插入序首个匹配（非 last-wins）
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;

        // {"Type":"SSS","type":"X"} → 首个匹配 "SSS" → SSS（豁免 difficulty 校验、自动切页签 1）
        await vm.LoadCurrentFromClipboardAsync(
            """{"Type":"SSS","type":"X","stage_name":"GT-1","difficulty":null,"stages":[{"stage_name":"GT-1"}]}""");

        Assert.True(vm.HasLoadedCopilot);
        Assert.Equal(1, vm.CopilotTabIndex);

        await using var fixture2 = await CopilotFixture.CreateAsync();
        var vm2 = fixture2.ViewModel;
        vm2.CopilotTabIndex = 0;

        // {"type":"X","Type":"SSS"} → 首个匹配 "X" → 普通作业，difficulty null 拒绝加载
        await vm2.LoadCurrentFromClipboardAsync("""{"type":"X","Type":"SSS","stage_name":"1-7","difficulty":null}""");

        Assert.False(vm2.HasLoadedCopilot);
        Assert.Contains("difficulty", vm2.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_LowercaseSssTypeValue_ShouldTreatAsNormalJob_WpfParity()
    {
        // WPF 对齐（冻结 CopilotHelper.cs:227）：值 "SSS" 大小写敏感 → "sss" 按普通作业解析，
        // difficulty null 拒绝；缺省 difficulty 则按普通作业加载且不切页签
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;

        await vm.LoadCurrentFromClipboardAsync("""{"type":"sss","stage_name":"1-7","difficulty":null}""");

        Assert.False(vm.HasLoadedCopilot);
        Assert.Contains("difficulty", vm.LastErrorMessage, StringComparison.Ordinal);

        await vm.LoadCurrentFromClipboardAsync("""{"type":"sss","stage_name":"1-7"}""");

        Assert.True(vm.HasLoadedCopilot);
        Assert.Equal(0, vm.CopilotTabIndex);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_StageNameKeyMixedCase_ShouldResolveStage_WpfParity()
    {
        // Newtonsoft 属性名大小写不敏感对齐：{"Stage_Name": ...} 正常识别关卡并解析导航名
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;
        var resourceDir = Path.Combine(fixture.Root, "resource");
        Directory.CreateDirectory(Path.Combine(resourceDir, "Arknights-Tile-Pos"));
        await File.WriteAllTextAsync(
            Path.Combine(resourceDir, "Arknights-Tile-Pos", "overview.json"),
            """
            {
              "a001_01-activities/a001/level_a001_01": {
                "code": "GT-1",
                "levelId": "activities/a001/level_a001_01",
                "name": "日正当中",
                "stageId": "a001_01"
              }
            }
            """);
        vm.ResourceDirectoryOverride = resourceDir;

        await vm.LoadCurrentFromClipboardAsync("""{"Stage_Name":"GT-1"}""");

        Assert.True(vm.HasLoadedCopilot);
        Assert.Equal("GT-1", vm.CopilotTaskName);
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_DifficultyKeyMixedCaseNull_ShouldRejectLoad_WpfParity()
    {
        // Newtonsoft 属性名大小写不敏感对齐：{"Difficulty":null} 同样绑定 Difficulty 并因 null 拒绝
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"1-7","Difficulty":null}""");

        Assert.False(vm.HasLoadedCopilot);
        Assert.Contains("difficulty", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void IsKnownOperatorNameFromBattleDataJson_ShouldExcludeVirtualOperators_WpfParity()
    {
        // WPF 对齐（冻结 DataHelper.cs:54、:441-476）：VerifyParadoxTasks 使用 DataHelper.Operators，
        // 排除 _virtualOperators（预备干员/集成战略专属等未实装干员）
        var battleData = """
            {
              "chars": {
                "char_504_rguard": { "name": "预备干员-近战", "profession": "WARRIOR" },
                "char_513_apionr": { "name": "郁金香", "profession": "PIONEER" },
                "char_617_sharp2": { "name": "领主·Sharp", "profession": "WARRIOR" },
                "char_002_amiya": { "name": "阿米娅", "profession": "CASTER" }
              }
            }
            """;

        Assert.False(CopilotPageViewModel.IsKnownOperatorNameFromBattleDataJson("预备干员-近战", battleData));
        Assert.False(CopilotPageViewModel.IsKnownOperatorNameFromBattleDataJson("郁金香", battleData));
        Assert.False(CopilotPageViewModel.IsKnownOperatorNameFromBattleDataJson("领主·Sharp", battleData));
        Assert.True(CopilotPageViewModel.IsKnownOperatorNameFromBattleDataJson("阿米娅", battleData));
    }

    [Fact]
    public async Task LoadCurrentFromClipboardAsync_StageNameTrailingSpace_ShouldReportUnsupportedStage_WpfParity()
    {
        // WPF 对齐（冻结 DataHelper.cs:187-188）：FindMap 精确比较、不裁剪 → "GT-1 " 按未知关卡处理
        // （仍加载但打 UnsupportedStages 错误日志并解锁 MapOutdated，导航名保留原值）
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.CopilotTabIndex = 0;
        var resourceDir = Path.Combine(fixture.Root, "resource");
        Directory.CreateDirectory(Path.Combine(resourceDir, "Arknights-Tile-Pos"));
        await File.WriteAllTextAsync(
            Path.Combine(resourceDir, "Arknights-Tile-Pos", "overview.json"),
            """
            {
              "a001_01-activities/a001/level_a001_01": {
                "code": "GT-1",
                "levelId": "activities/a001/level_a001_01",
                "name": "日正当中",
                "stageId": "a001_01"
              }
            }
            """);
        vm.ResourceDirectoryOverride = resourceDir;

        await vm.LoadCurrentFromClipboardAsync("""{"stage_name":"GT-1 "}""");

        // 未识别信号以 UnsupportedStages 日志 + MapOutdated 解锁为准
        // （CopilotTaskName 属性 setter 的显示层裁剪属既有行为，不作为识别信号）
        Assert.True(vm.HasLoadedCopilot);
        Assert.Contains(vm.Logs, log => log.Content.Contains("不支持的关卡", StringComparison.Ordinal));
        var snapshot = await fixture.Runtime.AchievementTrackerService.GetSnapshotAsync();
        Assert.Contains(snapshot.Value!.Items, item => item.Id == "MapOutdated" && item.IsUnlocked);
    }

    [Fact]
    public async Task ImportFromClipboardAsync_StageNameTrailingSpace_ShouldRejectEntry_WpfParity()
    {
        // WPF AddCopilotTaskToList 对齐：mapInfo is null（"GT-1 " 精确比较未命中）拒绝入列
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        var resourceDir = Path.Combine(fixture.Root, "resource");
        Directory.CreateDirectory(Path.Combine(resourceDir, "Arknights-Tile-Pos"));
        await File.WriteAllTextAsync(
            Path.Combine(resourceDir, "Arknights-Tile-Pos", "overview.json"),
            """
            {
              "a001_01-activities/a001/level_a001_01": {
                "code": "GT-1",
                "levelId": "activities/a001/level_a001_01",
                "name": "日正当中",
                "stageId": "a001_01"
              }
            }
            """);
        vm.ResourceDirectoryOverride = resourceDir;

        await vm.ImportFromClipboardAsync("""{"stage_name":"GT-1 "}""");

        Assert.Empty(vm.Items);
        Assert.Contains(vm.Logs, log => log.Content.Contains("无法识别作业关卡", StringComparison.Ordinal));
    }

    private sealed class CopilotFixture : IAsyncDisposable
    {
        private CopilotFixture(
            string root,
            UnifiedConfigurationService config,
            MAAUnifiedRuntime runtime,
            CopilotPageViewModel viewModel)
        {
            Root = root;
            Config = config;
            Runtime = runtime;
            ViewModel = viewModel;
        }

        public string Root { get; }

        public UnifiedConfigurationService Config { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public CopilotPageViewModel ViewModel { get; }

        public static async Task<CopilotFixture> CreateAsync(
            bool failPersistence = false,
            string? persistedPayload = null,
            IAppDialogService? dialogService = null,
            Func<CancellationToken, Task<UiOperationResult>>? ensureConnectedAsync = null)
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

            if (!string.IsNullOrWhiteSpace(persistedPayload))
            {
                config.CurrentConfig.GlobalValues[LegacyConfigurationKeys.CopilotTaskList] = JsonValue.Create(persistedPayload);
            }

            var bridge = new FakeBridge();
            var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
            var platform = new PlatformServiceBundle
            {
                TrayService = new NoOpTrayService(),
                NotificationService = new NoOpNotificationService(),
                HotkeyService = new NoOpGlobalHotkeyService(),
                AutostartService = new NoOpAutostartService(),
                FileDialogService = new NoOpFileDialogService(),
                OverlayService = new NoOpOverlayCapabilityService(),
                PostActionExecutorService = new NoOpPostActionExecutorService(),
            };

            var capability = new PlatformCapabilityFeatureService(platform, diagnostics);
            var connect = new ConnectFeatureService(session, config);
            var settings = new SettingsFeatureService(config, capability, diagnostics);
            ISettingsFeatureService settingsFeature = failPersistence
                ? new FailingSettingsFeatureService(settings)
                : settings;

            var runtime = new MAAUnifiedRuntime
            {
                CoreBridge = bridge,
                ConfigurationService = config,
                ResourceWorkflowService = new ResourceWorkflowService(root, bridge, log),
                SessionService = session,
                Platform = platform,
                LogService = log,
                DiagnosticsService = diagnostics,
                ConnectFeatureService = connect,
                ShellFeatureService = new ShellFeatureService(connect),
                TaskQueueFeatureService = new TaskQueueFeatureService(session, config),
                CopilotFeatureService = new CopilotFeatureService(),
                ToolboxFeatureService = new ToolboxFeatureService(),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = capability,
                OverlayFeatureService = new OverlayFeatureService(capability),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = settingsFeature,
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(
                    config,
                    diagnostics,
                    platform.PostActionExecutorService),
            };

            var vm = new CopilotPageViewModel(runtime, dialogService, ensureConnectedAsync: ensureConnectedAsync);
            return new CopilotFixture(root, config, runtime, vm);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temporary test directories
            }
        }
    }

    private sealed class ScriptedDialogService : IAppDialogService
    {
        private readonly DialogReturnSemantic _warningConfirmReturn;

        public ScriptedDialogService(DialogReturnSemantic warningConfirmReturn)
        {
            _warningConfirmReturn = warningConfirmReturn;
        }

        public int WarningConfirmCallCount { get; private set; }

        public List<WarningConfirmDialogRequest> WarningConfirmRequests { get; } = [];

        public Task<DialogCompletion<AnnouncementDialogPayload>> ShowAnnouncementAsync(
            AnnouncementDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<AnnouncementDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<VersionUpdateDialogPayload>> ShowVersionUpdateAsync(
            VersionUpdateDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<VersionUpdateDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<ProcessPickerDialogPayload>> ShowProcessPickerAsync(
            ProcessPickerDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<ProcessPickerDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<EmulatorPathDialogPayload>> ShowEmulatorPathAsync(
            EmulatorPathDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<EmulatorPathDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<ErrorDialogPayload>> ShowErrorAsync(
            ErrorDialogRequest request,
            string sourceScope,
            Func<CancellationToken, Task<UiOperationResult>>? openIssueReportAsync = null,
            Func<CancellationToken, Task<UiOperationResult>>? openSettingsAsync = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<ErrorDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<AchievementListDialogPayload>> ShowAchievementListAsync(
            AchievementListDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<AchievementListDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<TextDialogPayload>> ShowTextAsync(
            TextDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<TextDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<WarningConfirmDialogPayload>> ShowWarningConfirmAsync(
            WarningConfirmDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
        {
            WarningConfirmCallCount++;
            WarningConfirmRequests.Add(request);
            return Task.FromResult(new DialogCompletion<WarningConfirmDialogPayload>(
                _warningConfirmReturn,
                _warningConfirmReturn == DialogReturnSemantic.Confirm ? new WarningConfirmDialogPayload(true) : null,
                "scripted"));
        }
    }

    private sealed class FailingSettingsFeatureService : ISettingsFeatureService
    {
        private readonly ISettingsFeatureService _inner;

        public FailingSettingsFeatureService(ISettingsFeatureService inner)
        {
            _inner = inner;
        }

        public Task<UiOperationResult> SaveGlobalSettingAsync(string key, string value, CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Fail(
                UiErrorCode.CopilotListPersistenceFailed,
                "Copilot 列表持久化失败（模拟）。"));

        public Task<UiOperationResult> SaveGlobalSettingsAsync(
            IReadOnlyDictionary<string, string> updates,
            CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Fail(
                UiErrorCode.CopilotListPersistenceFailed,
                "Copilot 列表持久化失败（模拟）。"));

        public Task<UiOperationResult> SaveProfileSettingsAsync(
            IReadOnlyDictionary<string, string> updates,
            CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Fail(
                UiErrorCode.CopilotListPersistenceFailed,
                "Copilot 列表持久化失败（模拟）。"));

        public Task<UiOperationResult> TestNotificationAsync(string title, string message, CancellationToken cancellationToken = default)
            => _inner.TestNotificationAsync(title, message, cancellationToken);

        public Task<UiOperationResult> RegisterHotkeyAsync(string name, string gesture, CancellationToken cancellationToken = default)
            => _inner.RegisterHotkeyAsync(name, gesture, cancellationToken);

        public Task<UiOperationResult<bool>> GetAutostartStatusAsync(CancellationToken cancellationToken = default)
            => _inner.GetAutostartStatusAsync(cancellationToken);

        public Task<UiOperationResult> SetAutostartAsync(bool enabled, CancellationToken cancellationToken = default)
            => _inner.SetAutostartAsync(enabled, cancellationToken);

        public Task<UiOperationResult<string>> BuildIssueReportAsync(CancellationToken cancellationToken = default)
            => _inner.BuildIssueReportAsync(cancellationToken);
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _channel = Channel.CreateUnbounded<CoreCallbackEvent>();

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<int>.Ok(1));

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
