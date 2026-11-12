using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.ThirdParty;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.CoreBridge;
using MAAUnified.Compat.Runtime;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.App.ViewModels.Copilot;

public sealed partial class CopilotPageViewModel
{
    private const string CopilotIdPrefix = "maa://";
    private const string PrtsPlusUrl = "https://prts.plus";
    private const string MapPrtsUrl = "https://map.ark-nights.com/areas?coord_override=maa";
    private const string BilibiliVideoUrl = "https://www.bilibili.com/video/";
    private static readonly Regex InvalidNavigationStageNameRegex = new(
        "[:',\\.\\(\\)\\|\\[\\]\\?，。【】｛｝；：]",
        RegexOptions.Compiled);
    private static readonly Regex LegacyUserAdditionalRegex = new(
        @"(?<=;)(?<name>[^,;]+)(?:, *(?<skill>\d))?(?=;)",
        RegexOptions.Compiled);
    private static readonly Regex BilibiliLinkRegex = new(
        @"(?:av\d+|bv[a-z0-9]{10})(?:\/\?p=\d+)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // WPF CopilotViewModel dev-v2 GetCopilotType 对齐：按地图 stageId 前缀判定作业类型
    private static readonly Regex MainStageIdRegex = new(
        @"^(main|sub|tough|hard)_",
        RegexOptions.Compiled);
    private static readonly Regex SideStoryStageIdRegex = new(
        @"^(act\d+(side|mini|d\d+)|a00\d+)_",
        RegexOptions.Compiled);
    private static readonly string[] LocalizedBindingPropertyNames =
    [
        nameof(Texts),
        nameof(RootTexts),
        nameof(MainTabTitle),
        nameof(SecurityTabTitle),
        nameof(ParadoxTabTitle),
        nameof(OtherTabTitle),
        nameof(PathOrCodeWatermark),
        nameof(FileButtonText),
        nameof(FileButtonTip),
        nameof(PasteButtonText),
        nameof(PasteButtonTip),
        nameof(PasteSetButtonText),
        nameof(PasteSetButtonTip),
        nameof(StartButtonText),
        nameof(StopButtonText),
        nameof(AutoSquadText),
        nameof(AutoSquadTip),
        nameof(UseFormationText),
        nameof(EnableOperBoxAssistText),
        nameof(EnableOperBoxAssistTip),
        nameof(IgnoreRequirementsText),
        nameof(IgnoreRequirementsTip),
        nameof(UseSupportUnitText),
        nameof(UseSupportUnitTip),
        nameof(AddTrustText),
        nameof(AddUserAdditionalText),
        nameof(EditButtonText),
        nameof(UserAdditionalFormatTip),
        nameof(UserAdditionalPopupTitle),
        nameof(OperatorNameWatermark),
        nameof(DeleteButtonText),
        nameof(AddButtonText),
        nameof(ConfirmButtonText),
        nameof(CancelButtonText),
        nameof(BattleListText),
        nameof(BattleListTip),
        nameof(UseSanityPotionText),
        nameof(LoopTimesText),
        nameof(LoadButtonText),
        nameof(ImportBatchButtonText),
        nameof(ImportBatchButtonTip),
        nameof(StageNameWatermark),
        nameof(StageNameTip),
        nameof(AddListButtonText),
        nameof(AddListButtonTip),
        nameof(ClearButtonText),
        nameof(ClearButtonTip),
        nameof(CopilotListLoadSingleButtonText),
        nameof(CopilotListToggleRaidButtonText),
        nameof(CopilotListEnableButtonText),
        nameof(CopilotListDisableButtonText),
        nameof(ClearAllButtonText),
        nameof(ClearAllConfirmTitleText),
        nameof(ClearAllConfirmMessageText),
        nameof(ClearAllConfirmButtonText),
        nameof(RatingPromptText),
        nameof(LikeButtonText),
        nameof(DislikeButtonText),
        nameof(SelectTaskFilePickerTitle),
        nameof(ImportBatchFilePickerTitle),
        nameof(HelpText),
        nameof(ListSelectionHint),
        nameof(LoadedCopilotInputHint),
        nameof(RaidLabelText),
        nameof(InlineJsonHintText),
    ];

    private readonly HashSet<CopilotItemViewModel> _trackedCopilotItems = new();
    private readonly ObservableCollection<CopilotFileItemViewModel> _fileItems = [];
    private readonly ObservableCollection<CopilotUserAdditionalItemViewModel> _userAdditionalItems = [];
    private bool _isFilePopupOpen;
    private string _displayFilename = string.Empty;
    private bool _useFormation;
    private int _formationIndex = 1;
    private string _operBoxDataPath = string.Empty;
    private bool _enableOperBoxAssist;
    private int _supportUnitUsage = 1;
    private bool _ignoreRequirements;
    private bool _addUserAdditional;
    private string _userAdditional = string.Empty;
    private bool _isUserAdditionalPopupOpen;
    private bool _useCopilotList;
    private bool _useSanityPotion;
    private string _copilotTaskName = string.Empty;
    private bool _loop;
    private int _loopTimes = 1;
    private string _loadedSourcePath = string.Empty;
    private string _loadedInlinePayload = string.Empty;
    private string _loadedDisplayName = string.Empty;
    private string _loadedStageName = string.Empty;
    private string _loadedType = MainStageStoryCollectionSideStoryType;
    private int _loadedCopilotId;
    private bool _couldLikeWebJson;
    private string _copilotUrl = PrtsPlusUrl;
    private string _mapUrl = MapPrtsUrl;
    private bool _suppressAutoAddLoadedCopilot;
    private bool _suppressCopilotTaskNameListItemSync;
    private int _copilotTaskNamePersistVersion;
    private readonly List<int> _copilotIdList = []; // WPF 对齐：同一作业 id 的全部列表条目都成功后才自动点赞
    private readonly HashSet<int> _recentlyRatedCopilotIds = []; // WPF 对齐：_recentlyRatedCopilotId，同一会话同一作业 id 至多成功评分一次
    private readonly object _recentlyRatedCopilotIdsGate = new();
    private bool _hasRequirementIgnored;
    private int _currentCopilotId = -1; // WPF 对齐（4843310bec）：CopilotListLoadTaskFileSuccess 回报的当前列表项 id
    private string _lastRenderedHelpText = string.Empty;
    private IReadOnlyList<IntOption> _supportUnitUsageOptions = [];
    private IReadOnlyList<IntOption> _moduleOptions = [];

    public ObservableCollection<CopilotFileItemViewModel> FileItems => _fileItems;

    public ObservableCollection<CopilotUserAdditionalItemViewModel> UserAdditionalItems => _userAdditionalItems;

    public IReadOnlyList<IntOption> FormationOptions { get; } =
    [
        new(1, "1"),
        new(2, "2"),
        new(3, "3"),
        new(4, "4"),
    ];

    public IReadOnlyList<IntOption> SupportUnitUsageOptions
    {
        get => _supportUnitUsageOptions;
        private set => SetProperty(ref _supportUnitUsageOptions, value);
    }

    public IReadOnlyList<IntOption> ModuleOptions
    {
        get => _moduleOptions;
        private set => SetProperty(ref _moduleOptions, value);
    }

    public string HelpText => T("Copilot.HelpText", string.Empty);

    public string MainTabTitle => T("Copilot.Tab.Main", "主线/故事集/SideStory");

    public string SecurityTabTitle => T("Copilot.Tab.Security", "保全派驻");

    public string ParadoxTabTitle => T("Copilot.Tab.Paradox", "悖论模拟");

    public string OtherTabTitle => T("Copilot.Tab.Other", "其他活动");

    public string PathOrCodeWatermark => T("Copilot.Input.PathOrCodeWatermark", "作业路径/神秘代码");

    public string FileButtonText => T("Copilot.Button.File", "文件");

    public string FileButtonTip => T("Copilot.Tip.File", "可以直接拖拽作业文件到这里。");

    public string PasteButtonText => T("Copilot.Button.Paste", "粘贴");

    public string PasteButtonTip => T("Copilot.Tip.Paste", "读取剪贴板并添加为作业");

    public string PasteSetButtonText => T("Copilot.Button.PasteSet", "作业集");

    public string PasteSetButtonTip => T("Copilot.Tip.PasteSet", "读取剪贴板并添加为作业集");

    public string StartButtonText => IsStartRequestActive
        ? T("Copilot.Status.Starting", "启动中...")
        : T("Copilot.Action.Start", "开始");

    public string StopButtonText => T("Copilot.Action.Stop", "停止");

    public string AutoSquadText => T("Copilot.Option.AutoSquad", "自动编队");

    public string AutoSquadTip => T("Copilot.Tip.AutoSquad", "自动编队可能无法识别带有「特别关注」标记的干员");

    public string UseFormationText => T("Copilot.Option.UseFormation", "使用编队");

    public string EnableOperBoxAssistText => T("Copilot.Option.EnableOperBoxAssist", "使用一图流识别数据辅助编队");

    public string EnableOperBoxAssistTip => T(
        "Copilot.Tip.EnableOperBoxAssist",
        "自动编队前按干员识别数据（含技能与模组）预匹配；需先在 ｢设置 - 三方服务｣ 启用一图流数据源并完成识别。");

    public string IgnoreRequirementsText => T("Copilot.Option.IgnoreRequirements", "忽略干员属性要求");

    public string IgnoreRequirementsTip => T("Copilot.Tip.IgnoreRequirements", "部分作业对模组等属性有要求，启用后可能跳过校验并导致失败。");

    public string UseSupportUnitText => T("Copilot.Option.UseSupportUnit", "借助战");

    public string UseSupportUnitTip => T("Copilot.Tip.UseSupportUnit", "少一名干员可能还能运行，缺失更多时通常会失败。");

    public string AddTrustText => T("Copilot.Option.AddTrust", "补充低信赖干员");

    public string AddUserAdditionalText => T("Copilot.Option.AddUserAdditional", "追加自定干员");

    public string EditButtonText => T("Copilot.Button.Edit", "编辑");

    public string UserAdditionalFormatTip => T("Copilot.Tip.UserAdditionalFormat", "使用 ';' 分隔条目，使用 ',' 分隔干员名和技能，例如 Exusiai,3;Eyjafjalla,1");

    public string UserAdditionalPopupTitle => T("Copilot.Popup.UserAdditionalTitle", "追加自定干员");

    public string OperatorNameWatermark => T("Copilot.Input.OperatorNameWatermark", "干员名");

    public string DeleteButtonText => T("Copilot.Button.Delete", "删除");

    public string AddButtonText => T("Copilot.Button.Add", "添加");

    public string ConfirmButtonText => T("Copilot.Button.Confirm", "确定");

    public string CancelButtonText => T("Copilot.Button.Cancel", "取消");

    public string BattleListText => T("Copilot.Option.BattleList", "战斗列表");

    public string BattleListTip => T("Copilot.Tip.BattleList", "启用后，选中作业会自动加入战斗列表。");

    public string UseSanityPotionText => T("Copilot.Option.UseSanityPotion", "使用理智药");

    public string LoopTimesText => T("Copilot.Option.LoopTimes", "循环次数");

    public string LoadButtonText => T("Copilot.Button.Load", "载入");

    public string ImportBatchButtonText => T("Copilot.Button.ImportBatch", "批量导入");

    public string ImportBatchButtonTip => T("Copilot.Tip.ImportBatch", "批量导入");

    public string StageNameWatermark => T("Copilot.Input.StageNameWatermark", "关卡名");

    public string StageNameTip => T("Copilot.Tip.StageName", "关卡名，例如 1-7");

    public string AddListButtonText => T("Copilot.Button.Add", "添加");

    public string AddListButtonTip => T("Copilot.Tip.AddList", "左键添加普通难度\n右键添加突袭难度");

    public string ClearButtonText => T("Copilot.Button.Clear", "清除");

    public string ClearButtonTip => T("Copilot.Tip.ClearList", "删除战斗列表中的全部作业。");

    public string CopilotListLoadSingleButtonText => T("Copilot.List.Button.LoadSingle", "载入单个");

    public string CopilotListToggleRaidButtonText => T("Copilot.List.Button.ToggleRaid", "切换突袭");

    public string CopilotListEnableButtonText => T("Copilot.List.Button.Enable", "启用");

    public string CopilotListDisableButtonText => T("Copilot.List.Button.Disable", "禁用");

    public string ClearAllButtonText => T("Copilot.List.Button.ClearAll", "删除全部");

    public string ClearAllConfirmTitleText => T("Copilot.List.ClearAllConfirm.Title", "删除战斗列表");

    public string ClearAllConfirmMessageText => T("Copilot.List.ClearAllConfirm.Message", "将删除战斗列表中的全部作业，此操作不可撤销。");

    public string ClearAllConfirmButtonText => T("Copilot.List.ClearAllConfirm.Confirm", "删除全部");

    public string RatingPromptText => T("Copilot.Rating.Prompt", "作业怎么样？评价下吧！");

    public string LikeButtonText => T("Copilot.Button.Like", "点赞");

    public string DislikeButtonText => T("Copilot.Button.Dislike", "点踩");

    public string SelectTaskFilePickerTitle => T("Copilot.FilePicker.SelectTask.Title", "选择作业");

    public string ImportBatchFilePickerTitle => T("Copilot.FilePicker.ImportBatch.Title", "批量导入作业");

    public string RaidLabelText => T("Copilot.List.RaidLabel", "突袭");

    public string InlineJsonHintText => T("Copilot.List.InlineJsonHint", "inline-json");

    public int CopilotTabIndex
    {
        get => SelectedTypeIndex;
        set => SelectedTypeIndex = value;
    }

    public bool CanEdit =>
        !_isStartRequestActive
        && !IsRunning
        && CurrentSessionState != SessionState.Connecting
        && _connectionState?.IsSessionOperationActive != true;

    public bool ShowClipboardSetButton => CopilotTabIndex is 0 or 2;

    public bool Form
    {
        get => CopilotTabIndex is 1 or 2 ? false : AutoSquad;
        set
        {
            if (SetProperty(ref _autoSquad, value, nameof(AutoSquad)))
            {
                OnPropertyChanged(nameof(Form));
                RefreshVisibilityState();
            }
        }
    }

    public bool ShowFormationGroup => CopilotTabIndex is 0 or 3;

    public bool ShowUseFormation => ShowFormationGroup && Form;

    public bool UseFormation
    {
        get => _useFormation;
        set
        {
            if (SetProperty(ref _useFormation, value))
            {
                PersistCopilotSetting(CopilotSettingKeys.UseFormation, value.ToString());
                OnPropertyChanged(nameof(SelectedFormationOption));
            }
        }
    }

    public IntOption? SelectedFormationOption
    {
        get => FormationOptions.FirstOrDefault(option => option.Value == FormationIndex);
        set => FormationIndex = value?.Value ?? 1;
    }

    public int FormationIndex
    {
        get => _formationIndex;
        set
        {
            var normalized = Math.Clamp(value, 1, 4);
            if (SetProperty(ref _formationIndex, normalized))
            {
                PersistCopilotSetting(LegacyConfigurationKeys.CopilotSelectFormation, normalized.ToString());
                OnPropertyChanged(nameof(SelectedFormationOption));
            }
        }
    }

    /// <summary>
    /// Gets or sets an explicit operator-box data file handed to Core as <c>operbox_data_path</c>
    /// (WPF 86e58bda95, AsstCopilotTask.OperBoxDataPath). When empty,
    /// <see cref="EnableOperBoxAssist"/> selects <c>DataDir/OperBoxData.json</c> if it exists.
    /// </summary>
    public string OperBoxDataPath
    {
        get => _operBoxDataPath;
        set => SetProperty(ref _operBoxDataPath, value?.Trim() ?? string.Empty);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the operator-box data file is handed to Core as
    /// the automatic <c>operbox_data_path</c> source (WPF dev-v2 86e58bda95, EnableOperBoxAssist).
    /// The switch is only a gate: a missing file is treated as "no data" and the parameter stays
    /// absent, so Core keeps its local formation behavior.
    /// </summary>
    public bool EnableOperBoxAssist
    {
        get => _enableOperBoxAssist;
        set
        {
            if (SetProperty(ref _enableOperBoxAssist, value))
            {
                PersistCopilotSetting(ThirdPartySettingKeys.EnableOperBoxAssist, value.ToString());
            }
        }
    }

    public bool UseSupportUnitUsage
    {
        get => UseSupportUnit;
        set
        {
            if (SetProperty(ref _useSupportUnit, value, nameof(UseSupportUnit)))
            {
                OnPropertyChanged(nameof(UseSupportUnitUsage));
                RefreshVisibilityState();
            }
        }
    }

    public bool ShowSupportUnitUsage => ShowFormationGroup && Form;

    public IntOption? SelectedSupportUnitUsageOption
    {
        get => SupportUnitUsageOptions.FirstOrDefault(option => option.Value == SupportUnitUsage);
        set => SupportUnitUsage = value?.Value ?? 1;
    }

    public int SupportUnitUsage
    {
        get => _supportUnitUsage;
        set
        {
            var normalized = value == 3 ? 3 : 1;
            if (SetProperty(ref _supportUnitUsage, normalized))
            {
                PersistCopilotSetting(LegacyConfigurationKeys.CopilotSupportUnitUsage, normalized.ToString());
                OnPropertyChanged(nameof(SelectedSupportUnitUsageOption));
            }
        }
    }

    public bool ShowIgnoreRequirements => ShowFormationGroup && Form;

    public bool IgnoreRequirements
    {
        get => _ignoreRequirements;
        set => SetProperty(ref _ignoreRequirements, value);
    }

    public bool ShowAddTrust => ShowFormationGroup && Form;

    public bool ShowAddUserAdditional => ShowFormationGroup && Form;

    public bool AddUserAdditional
    {
        get => _addUserAdditional;
        set
        {
            if (SetProperty(ref _addUserAdditional, value))
            {
                PersistCopilotSetting(LegacyConfigurationKeys.CopilotAddUserAdditional, value.ToString());
            }
        }
    }

    public string UserAdditional
    {
        get => _userAdditional;
        set
        {
            var normalized = (value ?? string.Empty).Trim();
            if (SetProperty(ref _userAdditional, normalized))
            {
                PersistCopilotSetting(LegacyConfigurationKeys.CopilotUserAdditional, normalized);
            }
        }
    }

    public bool IsUserAdditionalPopupOpen
    {
        get => _isUserAdditionalPopupOpen;
        set => SetProperty(ref _isUserAdditionalPopupOpen, value);
    }

    public bool UseCopilotList
    {
        get => CopilotTabIndex is 1 or 3 ? false : _useCopilotList;
        set
        {
            if (value)
            {
                Form = true;
            }

            if (SetProperty(ref _useCopilotList, value))
            {
                RefreshVisibilityState();
            }
        }
    }

    public bool ShowUseCopilotList => CopilotTabIndex is 0 or 2;

    public bool ShowCopilotListPanel => UseCopilotList && CopilotTabIndex is 0 or 2;

    private bool SupportsCopilotList => CopilotTabIndex is 0 or 2;

    public bool ShowUseSanityPotion => UseCopilotList && CopilotTabIndex == 0;

    public bool UseSanityPotion
    {
        get => _useSanityPotion;
        set => SetProperty(ref _useSanityPotion, value);
    }

    public bool ShowLoopSetting => !UseCopilotList && CopilotTabIndex is 1 or 3;

    public bool Loop
    {
        get => _loop;
        set => SetProperty(ref _loop, value);
    }

    public int LoopTimes
    {
        get => _loopTimes;
        set
        {
            var normalized = Math.Max(0, value);
            if (SetProperty(ref _loopTimes, normalized))
            {
                PersistCopilotSetting(LegacyConfigurationKeys.CopilotLoopTimes, normalized.ToString());
            }
        }
    }

    public string CopilotTaskName
    {
        get => _copilotTaskName;
        set
        {
            var sanitized = InvalidNavigationStageNameRegex.Replace(value ?? string.Empty, string.Empty).Trim();
            if (SetProperty(ref _copilotTaskName, sanitized)
                && !_suppressCopilotTaskNameListItemSync)
            {
                SyncCopilotTaskNameToSelectedItem(sanitized);
            }
        }
    }

    private void SyncCopilotTaskNameToSelectedItem(string name)
    {
        var item = SelectedItem;
        if (item is null
            || string.Equals(item.Name, name, StringComparison.Ordinal))
        {
            return;
        }

        var version = ++_copilotTaskNamePersistVersion;
        item.Name = name;
        // 用户手动改名视为覆盖导航关卡名：dev-v2 c2021e694a 仅在覆盖时下发 nav_name_override
        item.IsNavNameOverride = true;
        _ = PersistCopilotTaskNameChangeAsync(item, name, version, CancellationToken.None);
    }

    private async Task PersistCopilotTaskNameChangeAsync(
        CopilotItemViewModel item,
        string name,
        int version,
        CancellationToken cancellationToken)
    {
        UiOperationResult persistResult;
        try
        {
            persistResult = await PersistItemsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            if (version != _copilotTaskNamePersistVersion || !ReferenceEquals(SelectedItem, item))
            {
                return;
            }

            StatusMessage = T("Copilot.Status.UpdateListItemNameFailed", "更新作业名称失败。");
            await RecordUnhandledExceptionAsync(
                "Copilot.List.Rename",
                ex,
                UiErrorCode.CopilotListPersistenceFailed,
                T("Copilot.Error.UpdateListItemPersistFail", "更新作业列表失败：列表保存失败。"),
                cancellationToken);
            return;
        }

        if (version != _copilotTaskNamePersistVersion || !ReferenceEquals(SelectedItem, item))
        {
            return;
        }

        if (!persistResult.Success)
        {
            StatusMessage = T("Copilot.Status.UpdateListItemNameFailed", "更新作业名称失败。");
            LastErrorMessage = persistResult.Message;
            await RecordFailedResultAsync(
                "Copilot.List.Rename",
                BuildPersistFailedResult(
                    T("Copilot.Error.UpdateListItemPersistFail", "更新作业列表失败：列表保存失败。"),
                    persistResult),
                cancellationToken);
            return;
        }

        StatusMessage = string.Format(
            T("Copilot.Status.UpdateListItemNameSuccess", "已更新作业名称：{0}"),
            name);
        LastErrorMessage = string.Empty;
        await RecordEventAsync("Copilot.List.Rename", StatusMessage, cancellationToken);
    }

    public bool IsFilePopupOpen
    {
        get => _isFilePopupOpen;
        set
        {
            if (SetProperty(ref _isFilePopupOpen, value) && value)
            {
                LoadFileItems();
            }
        }
    }

    public string DisplayFilename
    {
        get => _displayFilename;
        set
        {
            if (SetProperty(ref _displayFilename, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(LoadedCopilotInputHint));
            }
        }
    }

    public bool HasLoadedCopilot
        => !string.IsNullOrWhiteSpace(_loadedSourcePath) || !string.IsNullOrWhiteSpace(_loadedInlinePayload);

    public string LoadedCopilotInputHint
    {
        get
        {
            if (!HasLoadedCopilot)
            {
                return string.Empty;
            }

            var display = !string.IsNullOrWhiteSpace(_loadedSourcePath)
                ? BuildDisplayFilename(_loadedSourcePath, string.Empty)
                : !string.IsNullOrWhiteSpace(_loadedDisplayName)
                    ? _loadedDisplayName
                    : T("Copilot.List.InlineJsonHint", "inline-json");
            return string.Format(
                CultureInfo.InvariantCulture,
                T("Copilot.Hint.LoadedFile", "已导入作业文件：{0}"),
                display);
        }
    }

    public bool CouldLikeWebJson
    {
        get => _couldLikeWebJson;
        private set => SetProperty(ref _couldLikeWebJson, value);
    }

    public string CopilotUrl
    {
        get => _copilotUrl;
        private set => SetProperty(ref _copilotUrl, value);
    }

    public string MapUrl
    {
        get => _mapUrl;
        private set => SetProperty(ref _mapUrl, value);
    }

    public string ListSelectionHint
        => ShowCopilotListPanel
            ? T("Copilot.Hint.ListPanelEnabled", "战斗列表中的勾选项会参与启动。")
            : T("Copilot.Hint.ListPanelDisabled", "当前将直接启动输入框中的单个作业。");

    // 档案切换/配置导入后重载当前档案的 Copilot 列表与设置（WPF dev-v2 382009b97b 的档案隔离）
    public void ReloadConfigurationContext()
    {
        if (_hasActiveRun)
        {
            return;
        }

        LoadPersistedItems();
        LoadPersistedWpfParitySettings();
        OnPropertyChanged(nameof(UseFormation));
        OnPropertyChanged(nameof(EnableOperBoxAssist));
        OnPropertyChanged(nameof(FormationIndex));
        OnPropertyChanged(nameof(SelectedFormationOption));
        OnPropertyChanged(nameof(SupportUnitUsage));
        OnPropertyChanged(nameof(SelectedSupportUnitUsageOption));
        OnPropertyChanged(nameof(LoopTimes));
        OnPropertyChanged(nameof(AddUserAdditional));
        OnPropertyChanged(nameof(UserAdditional));
        OnPropertyChanged(nameof(UseSupportUnitUsage));
        RefreshVisibilityState();
    }

    private void InitializeWpfParityState()
    {
        foreach (var item in Items)
        {
            TrackCopilotItem(item);
        }

        Items.CollectionChanged += OnItemsCollectionChangedForWpfParity;
        LoadPersistedWpfParitySettings();
        RebuildLocalizedOptionLists();
        EnsureHelpLogPresent();
        RefreshVisibilityState();
    }

    private void EnsureHelpLogPresent()
    {
        if (Logs.Count > 0)
        {
            return;
        }

        AddLog(HelpText, showTime: false);
        _lastRenderedHelpText = HelpText;
    }

    private void RebuildLocalizedOptionLists()
    {
        SupportUnitUsageOptions =
        [
            new IntOption(1, T("Copilot.Option.SupportUnitUsage.FillGap", "补漏")),
            new IntOption(3, T("Copilot.Option.SupportUnitUsage.Random", "随机")),
        ];

        ModuleOptions =
        [
            new IntOption(0, T("Copilot.Option.Module.None", "不使用模组")),
            new IntOption(1, T("Copilot.Option.Module.Chi", "χ")),
            new IntOption(2, T("Copilot.Option.Module.Gamma", "γ")),
            new IntOption(3, T("Copilot.Option.Module.Alpha", "α")),
            new IntOption(4, T("Copilot.Option.Module.Delta", "Δ")),
            new IntOption(5, T("Copilot.Option.Module.Beta", "β")),
        ];

        OnPropertyChanged(nameof(SelectedFormationOption));
        OnPropertyChanged(nameof(SelectedSupportUnitUsageOption));
    }

    private void RefreshLocalizedUiState()
    {
        RefreshLocalizedBindingProperties();
        RefreshLocalizedStatusTexts();
        RefreshLocalizedCollections();
        OnPropertyChanged(string.Empty);
    }

    private void RefreshLocalizedBindingProperties()
    {
        foreach (var propertyName in LocalizedBindingPropertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void RefreshLocalizedStatusTexts()
    {
        if (Logs.Count == 1 && string.Equals(Logs[0].Content, _lastRenderedHelpText, StringComparison.Ordinal))
        {
            Logs.Clear();
            AddLog(HelpText, showTime: false);
        }

        _lastRenderedHelpText = HelpText;
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(LastErrorMessage));
    }

    private void RefreshLocalizedCollections()
    {
        RebuildLocalizedOptionLists();
        RelocalizeTrackedCopilotItems();
        RefreshVisibilityState();
    }

    private void RelocalizeTrackedCopilotItems()
    {
        foreach (var item in _trackedCopilotItems)
        {
            item.ApplyLocalization(RaidLabelText, InlineJsonHintText);
        }
    }

    private void OnSelectedTypeIndexChanged()
    {
        if (!_suppressSelectedTypeListItemSync)
        {
            SyncSelectedTypeIndexToSelectedItem();
        }

        OnPropertyChanged(nameof(CopilotTabIndex));
        OnPropertyChanged(nameof(Form));
        RefreshVisibilityState();
    }

    private void SyncSelectedTypeIndexToSelectedItem()
    {
        var item = SelectedItem;
        if (item is null)
        {
            return;
        }

        var tabIndex = Math.Clamp(SelectedTypeIndex, 0, Types.Count - 1);
        var type = ResolveTypeDisplayNameForTab(tabIndex);
        if (item.TabIndex == tabIndex
            && string.Equals(item.Type, type, StringComparison.Ordinal))
        {
            return;
        }

        var previousTabIndex = item.TabIndex;
        var previousType = item.Type;
        var version = ++_selectedTypePersistVersion;
        item.TabIndex = tabIndex;
        item.Type = type;
        _ = PersistSelectedTypeChangeAsync(item, previousType, previousTabIndex, version, CancellationToken.None);
    }

    private async Task PersistSelectedTypeChangeAsync(
        CopilotItemViewModel item,
        string previousType,
        int? previousTabIndex,
        int version,
        CancellationToken cancellationToken)
    {
        UiOperationResult persistResult;
        try
        {
            persistResult = await PersistItemsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            if (version != _selectedTypePersistVersion || !ReferenceEquals(SelectedItem, item))
            {
                return;
            }

            item.Type = previousType;
            item.TabIndex = previousTabIndex;
            StatusMessage = T("Copilot.Status.UpdateListItemFailed", "更新作业失败。");
            await RecordUnhandledExceptionAsync(
                "Copilot.List.UpdateType",
                ex,
                UiErrorCode.CopilotListPersistenceFailed,
                T("Copilot.Error.UpdateListItemPersistFail", "更新作业列表失败：列表保存失败。"),
                cancellationToken);
            return;
        }

        if (version != _selectedTypePersistVersion || !ReferenceEquals(SelectedItem, item))
        {
            return;
        }

        if (!persistResult.Success)
        {
            item.Type = previousType;
            item.TabIndex = previousTabIndex;
            StatusMessage = T("Copilot.Status.UpdateListItemFailed", "更新作业失败。");
            LastErrorMessage = persistResult.Message;
            await RecordFailedResultAsync(
                "Copilot.List.UpdateType",
                BuildPersistFailedResult(
                    T("Copilot.Error.UpdateListItemPersistFail", "更新作业列表失败：列表保存失败。"),
                    persistResult),
                cancellationToken);
            return;
        }

        StatusMessage = T("Copilot.Status.UpdateListItemSuccess", "已更新作业。");
        LastErrorMessage = string.Empty;
        await RecordEventAsync("Copilot.List.UpdateType", StatusMessage, cancellationToken);
    }

    private void RefreshVisibilityState()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(ShowClipboardSetButton));
        OnPropertyChanged(nameof(ShowFormationGroup));
        OnPropertyChanged(nameof(ShowUseFormation));
        OnPropertyChanged(nameof(ShowSupportUnitUsage));
        OnPropertyChanged(nameof(ShowIgnoreRequirements));
        OnPropertyChanged(nameof(ShowAddTrust));
        OnPropertyChanged(nameof(ShowAddUserAdditional));
        OnPropertyChanged(nameof(ShowUseCopilotList));
        OnPropertyChanged(nameof(ShowCopilotListPanel));
        OnPropertyChanged(nameof(ShowUseSanityPotion));
        OnPropertyChanged(nameof(ShowLoopSetting));
        OnPropertyChanged(nameof(ListSelectionHint));
        OnPropertyChanged(nameof(UseCopilotList));
    }

    private void LoadPersistedWpfParitySettings()
    {
        // WPF dev-v2 382009b97b / 5b39509df6：Copilot 设置随档案保存；旧全局键由
        // UnifiedConfigurationService 在加载时迁移，仍保留全局读取兜底以兼容未迁移的旧档。
        _formationIndex = ReadCopilotInt(LegacyConfigurationKeys.CopilotSelectFormation, 1, 1, 4);
        _supportUnitUsage = ReadCopilotInt(LegacyConfigurationKeys.CopilotSupportUnitUsage, 1, 1, 3) == 3 ? 3 : 1;
        _loopTimes = ReadCopilotInt(LegacyConfigurationKeys.CopilotLoopTimes, 1, 0, 9999);
        _addUserAdditional = ReadCopilotBool(LegacyConfigurationKeys.CopilotAddUserAdditional, false);
        _userAdditional = ReadCopilotString(LegacyConfigurationKeys.CopilotUserAdditional);
        _useFormation = ReadCopilotBool(CopilotSettingKeys.UseFormation, false);
        _enableOperBoxAssist = ReadCopilotBool(ThirdPartySettingKeys.EnableOperBoxAssist, false);
    }

    private bool TryReadCopilotSetting(string key, out JsonNode? node)
    {
        if (Runtime.ConfigurationService.TryGetCurrentProfile(out var profile)
            && profile.Values.TryGetValue(key, out node))
        {
            return true;
        }

        return Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(key, out node);
    }

    private int ReadCopilotInt(string key, int fallback, int min, int max)
    {
        if (!TryReadCopilotSetting(key, out var node)
            || node is not JsonValue jsonValue)
        {
            return fallback;
        }

        if (jsonValue.TryGetValue(out int parsedInt))
        {
            return Math.Clamp(parsedInt, min, max);
        }

        if (jsonValue.TryGetValue(out string? raw) && int.TryParse(raw, out parsedInt))
        {
            return Math.Clamp(parsedInt, min, max);
        }

        return fallback;
    }

    private bool ReadCopilotBool(string key, bool fallback)
    {
        if (!TryReadCopilotSetting(key, out var node)
            || node is not JsonValue jsonValue)
        {
            return fallback;
        }

        if (jsonValue.TryGetValue(out bool parsedBool))
        {
            return parsedBool;
        }

        if (jsonValue.TryGetValue(out int parsedInt))
        {
            return parsedInt != 0;
        }

        if (jsonValue.TryGetValue(out string? raw))
        {
            if (bool.TryParse(raw, out parsedBool))
            {
                return parsedBool;
            }

            if (int.TryParse(raw, out parsedInt))
            {
                return parsedInt != 0;
            }
        }

        return fallback;
    }

    private string ReadCopilotString(string key)
    {
        if (!TryReadCopilotSetting(key, out var node) || node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue(out string? raw))
        {
            return raw?.Trim() ?? string.Empty;
        }

        // dev-v2 的 Copilot.UserAdditional 是对象数组；保序转成字符串交给统一解析
        return node is JsonArray or JsonObject ? node.ToJsonString() : string.Empty;
    }

    private void PersistCopilotSetting(string key, string value)
    {
        if (Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
        {
            profile.Values[key] = JsonValue.Create(value);
            // 旧全局值已迁移到档案；删除避免它在档案值缺失时再次遮蔽
            Runtime.ConfigurationService.CurrentConfig.GlobalValues.Remove(key);
        }
        else
        {
            // 档案缺失的异常配置：退回旧全局存储，避免设置静默丢失
            Runtime.ConfigurationService.CurrentConfig.GlobalValues[key] = JsonValue.Create(value);
        }

        _ = PersistCopilotSettingCoreAsync(key, value);
    }

    private async Task PersistCopilotSettingCoreAsync(string key, string value)
    {
        _ = await RunTrackedConfigurationSaveAsync(
            $"Copilot.Config.{key}",
            Texts.GetOrDefault("Copilot.Title", "抄作业"),
            $"Config.{key}.Save",
            ct => Runtime.SettingsFeatureService.SaveProfileSettingsAsync(
                new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value },
                ct));
    }

    private void OnItemsCollectionChangedForWpfParity(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<CopilotItemViewModel>())
            {
                UntrackCopilotItem(item);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<CopilotItemViewModel>())
            {
                TrackCopilotItem(item);
            }
        }
    }

    private void TrackCopilotItem(CopilotItemViewModel item)
    {
        if (!_trackedCopilotItems.Add(item))
        {
            return;
        }

        item.ApplyLocalization(RaidLabelText, InlineJsonHintText);
        item.PropertyChanged += OnTrackedCopilotItemPropertyChanged;
    }

    private void UntrackCopilotItem(CopilotItemViewModel item)
    {
        if (!_trackedCopilotItems.Remove(item))
        {
            return;
        }

        item.PropertyChanged -= OnTrackedCopilotItemPropertyChanged;
    }

    private void OnTrackedCopilotItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Item setters only notify UI. List persistence is triggered by page-level operations.
    }

    public void ToggleFilePopup()
    {
        if (!IsFilePopupOpen)
        {
            LoadFileItems();
        }

        IsFilePopupOpen = !IsFilePopupOpen;
    }

    public void LoadFileItems()
    {
        _fileItems.Clear();
        var root = ResolveCopilotResourceRoot();
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(root, "*.json"))
        {
            _fileItems.Add(CreateFileNode(file, root));
        }

        CopilotFileItemViewModel? oldFolder = null;
        foreach (var directory in Directory.GetDirectories(root))
        {
            var node = CreateFolderNode(directory, root);
            if (node is null)
            {
                continue;
            }

            if (string.Equals(node.Name, "old", StringComparison.OrdinalIgnoreCase))
            {
                oldFolder = node;
            }
            else
            {
                _fileItems.Add(node);
            }
        }

        if (oldFolder is not null)
        {
            _fileItems.Add(oldFolder);
        }
    }

    public async Task OnFileSelectedAsync(CopilotFileItemViewModel? fileItem, CancellationToken cancellationToken = default)
    {
        if (fileItem is null || fileItem.IsFolder || string.IsNullOrWhiteSpace(fileItem.FullPath))
        {
            return;
        }

        IsFilePopupOpen = false;
        await LoadCurrentFromFileAsync(fileItem.FullPath, cancellationToken);
    }

    public async Task LoadCurrentFromDisplayInputAsync(CancellationToken cancellationToken = default)
    {
        var raw = (DisplayFilename ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            ClearLoadedCopilot();
            return;
        }

        // WPF UpdateFileDoc 入口对齐（冻结 :1007-1009）：任何输入提交都先作废已加载作业
        // （_copilotCache = null; CopilotId = 0），成功路径再由 ApplyLoadedCopilot 重新填充，
        // 防止无效输入或加载失败后 Start 静默执行旧作业
        InvalidateLoadedCopilot();

        var expanded = Environment.ExpandEnvironmentVariables(raw);
        var copilotRoot = ResolveCopilotResourceRoot();
        var absolute = Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(copilotRoot, expanded);

        if (File.Exists(absolute))
        {
            await LoadCurrentFromFileAsync(absolute, cancellationToken);
            return;
        }

        if (File.Exists(expanded))
        {
            await LoadCurrentFromFileAsync(expanded, cancellationToken);
            return;
        }

        // WPF DisplayFilename setter 对齐（冻结 :271-274）：文件名映射回退不限于相对路径，
        // 不存在的绝对路径同样按文件名查 resource/copilot 映射
        if (FindCopilotJsonByFileName(copilotRoot, Path.GetFileName(expanded)) is { } nestedMatch)
        {
            await LoadCurrentFromFileAsync(nestedMatch, cancellationToken);
            return;
        }

        if (TryParseCopilotCodeSource(raw, out var codeKind, out _))
        {
            // WPF UpdateFileDoc 对齐（dev-v2 72213def33）：作业集代码自动进入作业集加载
            if (codeKind == CopilotCodeSourceKind.CopilotSet)
            {
                await LoadCurrentFromClipboardSetAsync(raw, cancellationToken);
                return;
            }

            var remote = await Runtime.CopilotFeatureService.LoadFromCodeAsync(raw, cancellationToken);
            if (!remote.Success || remote.Value is null)
            {
                StatusMessage = T("Copilot.Status.LoadCurrentFailed", "读取作业失败。");
                LastErrorMessage = remote.Message;
                await RecordFailedResultAsync(
                    "Copilot.LoadCurrent.Code",
                    UiOperationResult.Fail(remote.Error?.Code ?? UiErrorCode.CopilotPayloadInvalidType, remote.Message, remote.Error?.Details),
                    cancellationToken);
                return;
            }

            if (!TryReadLoadedCopilotDescriptor(remote.Value.PayloadJson, sourcePath: null, out var descriptor, out var warning))
            {
                StatusMessage = T("Copilot.Status.LoadCurrentFailed", "读取作业失败。");
                LastErrorMessage = warning;
                await RecordFailedResultAsync(
                    "Copilot.LoadCurrent.Code",
                    UiOperationResult.Fail(UiErrorCode.CopilotPayloadInvalidType, warning),
                    cancellationToken);
                return;
            }

            if (descriptor.CopilotId <= 0)
            {
                descriptor = descriptor with { CopilotId = remote.Value.CopilotId };
            }

            ApplyLoadedCopilot(string.Empty, remote.Value.PayloadJson, descriptor);
            var displayCode = BuildCopilotCodeDisplay(remote.Value.CopilotId);
            DisplayFilename = displayCode;
            FilePath = displayCode;
            StatusMessage = remote.Message;
            LastErrorMessage = string.Empty;
            await RecordEventAsync("Copilot.LoadCurrent.Code", StatusMessage, cancellationToken);
            await TryAutoAddLoadedCopilotAsync(cancellationToken);
            return;
        }

        if (raw.StartsWith('{') || raw.StartsWith('['))
        {
            await LoadCurrentFromClipboardAsync(raw, cancellationToken);
            return;
        }

        // WPF UpdateFileDoc 对齐（冻结 :1056-1062）：输入既不是可用文件、作业码也不是 JSON 时
        // 打 CopilotJsonError 错误日志；已加载状态已在入口作废，Start 将因无作业被拒绝
        // （对齐 WPF CopilotEmptyError 语义），不再显示成功态提示或静默保留旧作业
        var jsonError = GetRootText("CopilotJsonError", "解析作业文件错误！");
        StatusMessage = T("Copilot.Status.LoadCurrentFailed", "读取作业失败。");
        LastErrorMessage = jsonError;
        AddLog(jsonError, "ERROR", showTime: false);
        await RecordFailedResultAsync(
            "Copilot.LoadCurrent.Input",
            UiOperationResult.Fail(UiErrorCode.CopilotPayloadInvalidType, jsonError),
            cancellationToken);
    }

    public async Task LoadCurrentFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // WPF UpdateFileDoc 入口对齐（冻结 :1007-1009）：先作废已加载作业，读取/解析失败出口不得残留旧作业
        InvalidateLoadedCopilot();

        var normalizedPath = ResolveStoredSourcePath(filePath);
        var result = await Runtime.CopilotFeatureService.ImportFromFileAsync(normalizedPath, cancellationToken);
        if (!result.Success)
        {
            StatusMessage = T("Copilot.Status.LoadCurrentFailed", "读取作业失败。");
            LastErrorMessage = result.Message;
            await RecordFailedResultAsync("Copilot.LoadCurrent.File", result, cancellationToken);
            return;
        }

        string payload;
        try
        {
            payload = await File.ReadAllTextAsync(normalizedPath, cancellationToken);
        }
        catch (Exception ex)
        {
            StatusMessage = T("Copilot.Status.LoadCurrentFailed", "读取作业失败。");
            LastErrorMessage = ex.Message;
            await RecordUnhandledExceptionAsync(
                "Copilot.LoadCurrent.File",
                ex,
                UiErrorCode.CopilotFileReadFailed,
                T("Copilot.Error.ReadFileFailed", "读取作业文件失败。"),
                cancellationToken);
            return;
        }

        if (!TryReadLoadedCopilotDescriptor(payload, normalizedPath, out var descriptor, out var warning))
        {
            StatusMessage = T("Copilot.Status.LoadCurrentFailed", "读取作业失败。");
            LastErrorMessage = warning;
            await RecordFailedResultAsync(
                "Copilot.LoadCurrent.File",
                UiOperationResult.Fail(UiErrorCode.CopilotPayloadInvalidType, warning),
                cancellationToken);
            return;
        }

        ApplyLoadedCopilot(normalizedPath, string.Empty, descriptor);
        StatusMessage = string.Empty;
        LastErrorMessage = string.Empty;
        await RecordEventAsync("Copilot.LoadCurrent.File", result.Message, cancellationToken);
        await TryAutoAddLoadedCopilotAsync(cancellationToken);
    }

    public async Task LoadCurrentFromClipboardAsync(string payload, CancellationToken cancellationToken = default)
    {
        // WPF PasteClipboard -> Filename setter -> UpdateFileDoc 对齐（冻结 :812-820、:1007-1009）：
        // 剪贴板加载先作废已加载作业，失败出口不得残留旧作业
        InvalidateLoadedCopilot();

        var normalized = (payload ?? string.Empty).Trim();
        if (TryParseCopilotCodeSource(normalized, out var codeKind, out _))
        {
            if (codeKind == CopilotCodeSourceKind.CopilotSet)
            {
                await LoadCurrentFromClipboardSetAsync(normalized, cancellationToken);
                return;
            }

            var remote = await Runtime.CopilotFeatureService.LoadFromCodeAsync(normalized, cancellationToken);
            if (!remote.Success || remote.Value is null)
            {
                StatusMessage = T("Copilot.Status.LoadClipboardFailed", "读取剪贴板作业失败。");
                LastErrorMessage = remote.Message;
                await RecordFailedResultAsync(
                    "Copilot.LoadCurrent.Clipboard",
                    UiOperationResult.Fail(remote.Error?.Code ?? UiErrorCode.CopilotPayloadInvalidType, remote.Message, remote.Error?.Details),
                    cancellationToken);
                return;
            }

            if (!TryReadLoadedCopilotDescriptor(remote.Value.PayloadJson, sourcePath: null, out var remoteDescriptor, out var remoteWarning))
            {
                StatusMessage = T("Copilot.Status.LoadClipboardFailed", "读取剪贴板作业失败。");
                LastErrorMessage = remoteWarning;
                await RecordFailedResultAsync(
                    "Copilot.LoadCurrent.Clipboard",
                    UiOperationResult.Fail(UiErrorCode.CopilotPayloadInvalidType, remoteWarning),
                    cancellationToken);
                return;
            }

            if (remoteDescriptor.CopilotId <= 0)
            {
                remoteDescriptor = remoteDescriptor with { CopilotId = remote.Value.CopilotId };
            }

            ApplyLoadedCopilot(string.Empty, remote.Value.PayloadJson, remoteDescriptor);
            var displayCode = BuildCopilotCodeDisplay(remote.Value.CopilotId);
            DisplayFilename = displayCode;
            FilePath = displayCode;
            StatusMessage = remote.Message;
            LastErrorMessage = string.Empty;
            await RecordEventAsync("Copilot.LoadCurrent.Clipboard", StatusMessage, cancellationToken);
            await TryAutoAddLoadedCopilotAsync(cancellationToken);
            return;
        }

        var result = await Runtime.CopilotFeatureService.ImportFromClipboardAsync(normalized, cancellationToken);
        if (!result.Success)
        {
            StatusMessage = T("Copilot.Status.LoadClipboardFailed", "读取剪贴板作业失败。");
            LastErrorMessage = result.Message;
            await RecordFailedResultAsync("Copilot.LoadCurrent.Clipboard", result, cancellationToken);
            return;
        }

        var pathCandidate = ResolveClipboardPathCandidate(normalized);
        if (!string.IsNullOrWhiteSpace(pathCandidate))
        {
            await LoadCurrentFromFileAsync(pathCandidate, cancellationToken);
            return;
        }

        if (!TryReadLoadedCopilotDescriptor(normalized, sourcePath: null, out var descriptor, out var warning))
        {
            StatusMessage = T("Copilot.Status.LoadClipboardFailed", "读取剪贴板作业失败。");
            LastErrorMessage = warning;
            await RecordFailedResultAsync(
                "Copilot.LoadCurrent.Clipboard",
                UiOperationResult.Fail(UiErrorCode.CopilotPayloadInvalidType, warning),
                cancellationToken);
            return;
        }

        ApplyLoadedCopilot(string.Empty, normalized, descriptor);
        StatusMessage = result.Message;
        LastErrorMessage = string.Empty;
        await RecordEventAsync("Copilot.LoadCurrent.Clipboard", StatusMessage, cancellationToken);
        await TryAutoAddLoadedCopilotAsync(cancellationToken);
    }

    public async Task LoadCurrentFromClipboardSetAsync(string payload, CancellationToken cancellationToken = default)
    {
        var normalizedPayload = (payload ?? string.Empty).Trim();
        var hasCode = TryParseCopilotCodeSource(normalizedPayload, out var codeKind, out _);
        if (hasCode && codeKind == CopilotCodeSourceKind.Copilot)
        {
            // WPF UpdateFileDoc 对齐：作业集入口收到单作业代码时按单作业加载，不因页签能力拦截
            await LoadCurrentFromClipboardAsync(normalizedPayload, cancellationToken);
            return;
        }

        if (!SupportsCopilotList)
        {
            StatusMessage = T("Copilot.Status.LoadSetFailed", "读取作业集失败。");
            LastErrorMessage = T("Copilot.Error.LoadSetUnsupportedTab", "当前页签不支持作业集。请在主线/故事集/SideStory或悖论模拟页签导入。");
            await RecordFailedResultAsync(
                "Copilot.LoadSet.Clipboard",
                UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, LastErrorMessage),
                cancellationToken);
            return;
        }

        // WPF ParseCopilotSetAsync 入口对齐（冻结 :1330-1331）：作业集加载同样作废已加载单作业
        InvalidateLoadedCopilot();

        if (hasCode)
        {
            var setResult = await Runtime.CopilotFeatureService.LoadSetFromCodeAsync(normalizedPayload, cancellationToken);
            if (!setResult.Success || setResult.Value is null)
            {
                StatusMessage = T("Copilot.Status.LoadSetFailed", "读取作业集失败。");
                LastErrorMessage = setResult.Message;
                await RecordFailedResultAsync(
                    "Copilot.LoadSet.Clipboard",
                    UiOperationResult.Fail(setResult.Error?.Code ?? UiErrorCode.CopilotPayloadInvalidType, setResult.Message, setResult.Error?.Details),
                    cancellationToken);
                return;
            }

            var remoteSet = setResult.Value;
            UseCopilotList = true;
            var remoteAdded = 0;
            foreach (var remote in remoteSet.Items)
            {
                if (!TryReadLoadedCopilotDescriptor(remote.PayloadJson, sourcePath: null, out var descriptor, out _))
                {
                    continue;
                }

                // WPF 对齐：ParseCopilotSetAsync 仅处理 CopilotModel，type=SSS 的保全派驻作业不进入战斗列表
                if (IsSssDescriptorType(descriptor.Type))
                {
                    continue;
                }

                if (descriptor.CopilotId <= 0)
                {
                    descriptor = descriptor with { CopilotId = remote.CopilotId };
                }

                remoteAdded += AddListItemsFromDescriptor(descriptor, string.Empty, remote.PayloadJson);
            }

            if (remoteAdded == 0)
            {
                StatusMessage = T("Copilot.Status.LoadSetFailed", "读取作业集失败。");
                LastErrorMessage = T("Copilot.Error.LoadSetNoRecognized", "作业集内没有可识别的作业条目。");
                return;
            }

            SetSelectedItemSilently(Items.LastOrDefault());
            await PersistItemsAsync(cancellationToken);
            StatusMessage = setResult.Message;
            LastErrorMessage = remoteSet.FailedCopilotIds.Count > 0
                ? string.Format(
                    T("Copilot.Status.LoadSetPartialFailed", "以下作业加载失败：{0}"),
                    string.Join(", ", remoteSet.FailedCopilotIds))
                : string.Empty;
            await RecordEventAsync("Copilot.LoadSet.Clipboard", StatusMessage, cancellationToken);
            return;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(normalizedPayload);
        }
        catch (Exception ex)
        {
            StatusMessage = T("Copilot.Status.LoadSetFailed", "读取作业集失败。");
            LastErrorMessage = ex.Message;
            await RecordUnhandledExceptionAsync(
                "Copilot.LoadSet.Clipboard",
                ex,
                UiErrorCode.CopilotPayloadInvalidJson,
                T("Copilot.Status.LoadSetFailed", "读取作业集失败。"),
                cancellationToken);
            return;
        }

        if (root is not JsonArray array)
        {
            StatusMessage = T("Copilot.Status.LoadSetFailed", "读取作业集失败。");
            LastErrorMessage = T("Copilot.Error.LoadSetNotArray", "作业集必须是 JSON 数组。");
            await RecordFailedResultAsync(
                "Copilot.LoadSet.Clipboard",
                UiOperationResult.Fail(UiErrorCode.CopilotPayloadInvalidType, LastErrorMessage),
                cancellationToken);
            return;
        }

        UseCopilotList = true;
        var added = 0;
        foreach (var node in array.OfType<JsonObject>())
        {
            var json = node.ToJsonString();
            if (!TryReadLoadedCopilotDescriptor(json, sourcePath: null, out var descriptor, out _))
            {
                continue;
            }

            // WPF 对齐：作业集中的保全派驻（SSS）作业不进入战斗列表
            if (IsSssDescriptorType(descriptor.Type))
            {
                continue;
            }

            added += AddListItemsFromDescriptor(descriptor, string.Empty, json);
        }

        if (added == 0)
        {
            StatusMessage = T("Copilot.Status.LoadSetFailed", "读取作业集失败。");
            LastErrorMessage = T("Copilot.Error.LoadSetNoRecognized", "作业集内没有可识别的作业条目。");
            return;
        }

        await PersistItemsAsync(cancellationToken);
        StatusMessage = string.Format(
            T("Copilot.Status.LoadSetAddedCount", "已添加 {0} 个作业到战斗列表。"),
            added);
        LastErrorMessage = string.Empty;
        await RecordEventAsync("Copilot.LoadSet.Clipboard", StatusMessage, cancellationToken);
    }

    // WPF ImportFiles 对齐（冻结 CopilotViewModel.cs AddCopilotTaskToList 末尾逐文件
    // SaveCopilotTask）：批量导入中止整批时，前半批已入列条目必须已持久化（重启后不丢失）。
    // 中止路径尽力持久化：失败不回滚已入列条目（WPF SaveCopilotTask 无失败回滚语义），
    // 批量失败本身已通过 StatusMessage/日志反馈。
    private async Task PersistAddedItemsBeforeBatchAbortAsync(int added, CancellationToken cancellationToken)
    {
        if (added <= 0)
        {
            return;
        }

        // RunTrackedConfigurationSaveAsync 成功路径会清空 LastErrorMessage，
        // 快照并恢复中止错误反馈，避免覆盖批量失败提示
        var statusMessage = StatusMessage;
        var lastErrorMessage = LastErrorMessage;
        _ = await PersistItemsAsync(cancellationToken);
        StatusMessage = statusMessage;
        LastErrorMessage = lastErrorMessage;
    }

    public async Task ImportFilesToListAsync(IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        if (!SupportsCopilotList)
        {
            StatusMessage = T("Copilot.Status.BatchImportFailed", "批量导入失败。");
            LastErrorMessage = T("Copilot.Error.LoadSetUnsupportedTab", "当前页签不支持作业集。请在主线/故事集/SideStory或悖论模拟页签导入。");
            await RecordFailedResultAsync(
                "Copilot.ImportFiles",
                UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, LastErrorMessage),
                cancellationToken);
            return;
        }

        // WPF ImportFiles 入口对齐（冻结 CopilotViewModel.cs:867-868）：确认文件后无条件作废
        // 已加载单作业（CopilotId = 0; _copilotCache = null），首文件非法/中途失败中止后
        // Start 不得再静默执行旧作业（走单作业校验被 CopilotEmptyError 拒绝），
        // CouldLikeWebJson 随 _loadedCopilotId 复位；成功路径由 ApplyLoadedCopilot 重新填充
        InvalidateLoadedCopilot();

        var added = 0;
        foreach (var filePath in filePaths)
        {
            var normalizedPath = ResolveStoredSourcePath(filePath);
            var result = await Runtime.CopilotFeatureService.ImportFromFileAsync(normalizedPath, cancellationToken);
            if (!result.Success)
            {
                StatusMessage = T("Copilot.Status.BatchImportFailed", "批量导入失败。");
                LastErrorMessage = result.Message;
                await RecordFailedResultAsync("Copilot.ImportFiles", result, cancellationToken);
                await PersistAddedItemsBeforeBatchAbortAsync(added, cancellationToken);
                return;
            }

            var payload = await File.ReadAllTextAsync(normalizedPath, cancellationToken);
            if (!TryReadLoadedCopilotDescriptor(payload, normalizedPath, out var descriptor, out var fileWarning))
            {
                // WPF ImportFiles 对齐（冻结 CopilotViewModel.cs:894-899）：单文件反序列化失败
                // （非法 JSON / 非法 difficulty / minimum_required 版本门控拒绝）→ CopilotFileReadError
                // 错误日志并中止整批，后续文件不再处理
                StatusMessage = T("Copilot.Status.BatchImportFailed", "批量导入失败。");
                LastErrorMessage = string.IsNullOrWhiteSpace(fileWarning)
                    ? T("Copilot.Error.FileReadFailed", "读取文件失败！")
                    : fileWarning;
                AddLog(T("Copilot.Error.FileReadFailed", "读取文件失败！"), "ERROR", showTime: false);
                await RecordFailedResultAsync(
                    "Copilot.ImportFiles",
                    UiOperationResult.Fail(UiErrorCode.CopilotFileReadFailed, LastErrorMessage),
                    cancellationToken);
                await PersistAddedItemsBeforeBatchAbortAsync(added, cancellationToken);
                return;
            }

            if (IsSssDescriptorType(descriptor.Type))
            {
                // WPF ImportFiles 对齐：批量导入遇到 SSS 作业逐文件打 CopilotSSSNotSupport 错误日志
                AddLog(T("Copilot.Error.SssNotSupported", "不支持的类型：SSS"), "ERROR", showTime: false);
                continue;
            }

            added += AddListItemsFromDescriptor(descriptor, normalizedPath, payload);
        }

        if (added == 0)
        {
            StatusMessage = T("Copilot.Status.BatchImportFailed", "批量导入失败。");
            LastErrorMessage = T("Copilot.Error.BatchImportNone", "未找到可导入的作业文件。");
            return;
        }

        UseCopilotList = CopilotTabIndex is 0 or 2;
        await PersistItemsAsync(cancellationToken);
        StatusMessage = string.Format(
            T("Copilot.Status.BatchImportedCount", "已批量导入 {0} 个作业。"),
            added);
        LastErrorMessage = string.Empty;
        await RecordEventAsync("Copilot.ImportFiles", StatusMessage, cancellationToken);
    }

    public async Task AddCurrentToListAsync(bool isRaid, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SupportsCopilotList)
        {
            StatusMessage = T("Copilot.Status.AddCurrentFailed", "添加作业失败。");
            LastErrorMessage = T("Copilot.Error.LoadSetUnsupportedTab", "当前页签不支持作业集。请在主线/故事集/SideStory或悖论模拟页签导入。");
            await RecordFailedResultAsync(
                "Copilot.List.AddCurrent",
                UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, LastErrorMessage),
                cancellationToken);
            return;
        }

        if (!HasLoadedCopilot)
        {
            StatusMessage = T("Copilot.Status.AddCurrentFailed", "添加作业失败。");
            LastErrorMessage = T("Copilot.Error.SelectCopilotBeforeAdd", "请先选择一个作业。");
            await RecordFailedResultAsync(
                "Copilot.List.AddCurrent",
                UiOperationResult.Fail(UiErrorCode.CopilotFileMissing, LastErrorMessage),
                cancellationToken);
            return;
        }

        var descriptor = new LoadedCopilotDescriptor(
            ResolveListItemName(_loadedDisplayName, _loadedStageName),
            string.IsNullOrWhiteSpace(_loadedType) ? ResolveTypeDisplayNameForTab(CopilotTabIndex) : _loadedType,
            string.IsNullOrWhiteSpace(_loadedStageName) ? _loadedDisplayName : _loadedStageName,
            _loadedCopilotId);
        // WPF 对齐：AddCopilotTaskToList 对 SSSCopilotModel 打 CopilotSSSNotSupport 错误且不添加
        if (IsSssDescriptorType(descriptor.Type))
        {
            StatusMessage = T("Copilot.Status.AddCurrentFailed", "添加作业失败。");
            LastErrorMessage = T("Copilot.Error.SssNotSupported", "不支持的类型：SSS");
            await RecordFailedResultAsync(
                "Copilot.List.AddCurrent",
                UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, LastErrorMessage),
                cancellationToken);
            return;
        }

        // WPF 对齐：悖论分支恒 isRaid=false，右键「添加突袭」不透传难度
        var isParadox = IsParadoxDescriptorType(descriptor.Type);
        var effectiveIsRaid = !isParadox && isRaid;
        var item = CreateListItemFromDescriptor(descriptor, _loadedSourcePath, _loadedInlinePayload, effectiveIsRaid);
        if (item is null)
        {
            // WPF AddCopilotTaskToList 对齐：mapInfo is null 时拒绝入列
            StatusMessage = T("Copilot.Status.AddCurrentFailed", "添加作业失败。");
            LastErrorMessage = string.Format(T("Copilot.Error.StageNotFound", "无法识别作业关卡：{0}"), descriptor.StageName);
            await RecordFailedResultAsync(
                "Copilot.List.AddCurrent",
                UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, LastErrorMessage),
                cancellationToken);
            return;
        }

        // WPF AddCopilotTaskToList 对齐：条目名取导航名输入框值（ParseCopilotAsync 已规范化为 mapInfo.Code，
        // 用户亦可手动编辑），不得覆盖回原始 stage_name；悖论页签条目名取干员名
        item.Name = isParadox
            ? ResolveParadoxItemName(descriptor)
            : !string.IsNullOrWhiteSpace(CopilotTaskName)
                ? CopilotTaskName.Trim()
                : ResolveListItemName(descriptor.DisplayName, descriptor.StageName);
        // WPF AddCopilotTaskToList 对齐：手动添加时导航名来自用户输入，标记为覆盖
        item.IsNavNameOverride = !isParadox && !string.IsNullOrWhiteSpace(CopilotTaskName);
        Items.Add(item);
        SetSelectedItemSilently(item);
        UseCopilotList = CopilotTabIndex is 0 or 2;
        await PersistItemsAsync(cancellationToken);
        StatusMessage = effectiveIsRaid
            ? T("Copilot.Status.AddCurrentRaidSuccess", "已添加突袭作业到战斗列表。")
            : T("Copilot.Status.AddCurrentSuccess", "已添加作业到战斗列表。");
        LastErrorMessage = string.Empty;
        await RecordEventAsync("Copilot.List.AddCurrent", StatusMessage, cancellationToken);
    }

    public async Task DeleteListItemAsync(CopilotItemViewModel item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Items.Contains(item))
        {
            return;
        }

        Items.Remove(item);
        if (ReferenceEquals(SelectedItem, item))
        {
            SetSelectedItemSilently(Items.LastOrDefault());
        }

        await PersistItemsAsync(cancellationToken);
        StatusMessage = T("Copilot.Status.DeleteItemSuccess", "已删除作业。");
        LastErrorMessage = string.Empty;
        await RecordEventAsync("Copilot.List.Delete", StatusMessage, cancellationToken);
    }

    public async Task CleanInactiveListItemsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var removed = Items.RemoveAll(item => !item.IsChecked);
        await PersistItemsAsync(cancellationToken);
        StatusMessage = removed == 0
            ? T("Copilot.Status.CleanInactiveNone", "没有未激活作业需要清理。")
            : string.Format(T("Copilot.Status.CleanInactiveCount", "已清理 {0} 个未激活作业。"), removed);
        LastErrorMessage = string.Empty;
        await RecordEventAsync("Copilot.List.CleanInactive", StatusMessage, cancellationToken);
    }

    public async Task LoadListItemAsync(CopilotItemViewModel item, bool disableListMode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetSelectedItemSilently(item);
        if (disableListMode)
        {
            UseCopilotList = false;
        }

        _suppressAutoAddLoadedCopilot = true;
        try
        {
            var resolvedSourcePath = ResolveStoredSourcePath(item.SourcePath);
            if (!string.IsNullOrWhiteSpace(resolvedSourcePath) && File.Exists(resolvedSourcePath))
            {
                await LoadCurrentFromFileAsync(resolvedSourcePath, cancellationToken);
                return;
            }

            if (!string.IsNullOrWhiteSpace(item.InlinePayload))
            {
                await LoadCurrentFromClipboardAsync(item.InlinePayload, cancellationToken);
                return;
            }
        }
        finally
        {
            _suppressAutoAddLoadedCopilot = false;
        }

        StatusMessage = T("Copilot.Status.LoadCurrentFailed", "读取作业失败。");
        LastErrorMessage = T("Copilot.Error.ListItemMissingSource", "所选列表项缺少可用的作业来源。");
    }

    public void OpenUserAdditionalPopup()
    {
        _userAdditionalItems.Clear();
        foreach (var item in ParseUserAdditionalItems())
        {
            _userAdditionalItems.Add(item);
        }

        if (_userAdditionalItems.Count == 0)
        {
            _userAdditionalItems.Add(new CopilotUserAdditionalItemViewModel());
        }

        IsUserAdditionalPopupOpen = true;
    }

    public void AddUserAdditionalItem()
    {
        if (_userAdditionalItems.Any(item => string.IsNullOrWhiteSpace(item.Name)))
        {
            return;
        }

        _userAdditionalItems.Add(new CopilotUserAdditionalItemViewModel());
    }

    public void RemoveUserAdditionalItem(CopilotUserAdditionalItemViewModel item)
    {
        _userAdditionalItems.Remove(item);
        if (_userAdditionalItems.Count == 0)
        {
            _userAdditionalItems.Add(new CopilotUserAdditionalItemViewModel());
        }
    }

    public void SaveUserAdditional()
    {
        var payload = new JsonArray();
        foreach (var item in _userAdditionalItems.Where(item => !string.IsNullOrWhiteSpace(item.Name)))
        {
            payload.Add(new JsonObject
            {
                ["name"] = item.Name.Trim(),
                ["skill"] = Math.Clamp(item.Skill, 0, 3),
                ["module"] = Math.Clamp(item.Module, 0, 5),
            });
        }

        UserAdditional = payload.Count == 0 ? string.Empty : payload.ToJsonString();
        IsUserAdditionalPopupOpen = false;
    }

    public void CancelUserAdditionalEdit()
    {
        IsUserAdditionalPopupOpen = false;
    }

    public async Task SubmitLoadedFeedbackAsync(bool like, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var copilotId = _loadedCopilotId > 0
            ? _loadedCopilotId.ToString()
            : SelectedItem?.CopilotId > 0
                ? SelectedItem.CopilotId.ToString()
                : string.Empty;
        if (string.IsNullOrWhiteSpace(copilotId))
        {
            StatusMessage = T("Copilot.Status.FeedbackFailed", "反馈失败。");
            LastErrorMessage = T("Copilot.Error.LoadedFeedbackMissingId", "当前作业没有可反馈的作业站 ID。");
            return;
        }

        // WPF 对齐：RateCopilot 开头 _recentlyRatedCopilotId 拦截，同一会话同一 id 至多评分一次（点踩同样占用）
        var ratedId = int.TryParse(copilotId, out var parsedRatedId) ? parsedRatedId : 0;
        if (ratedId > 0 && IsCopilotRecentlyRated(ratedId))
        {
            StatusMessage = T("Copilot.Status.FeedbackAlreadyRated", "本会话已对该作业提交过反馈。");
            LastErrorMessage = string.Empty;
            CouldLikeWebJson = false;
            await RecordEventAsync("Copilot.Feedback.Loaded", StatusMessage, cancellationToken);
            return;
        }

        var result = await Runtime.CopilotFeatureService.SubmitFeedbackAsync(copilotId, like, cancellationToken);
        if (!result.Success)
        {
            StatusMessage = T("Copilot.Status.FeedbackFailed", "反馈失败。");
            LastErrorMessage = result.Message;
            await RecordFailedResultAsync("Copilot.Feedback.Loaded", result, cancellationToken);
            return;
        }

        if (ratedId > 0)
        {
            MarkCopilotRecentlyRated(ratedId);
        }

        // WPF LikeWebJson 对齐：点赞成功累计 CopilotLikeGiven 成就（DislikeWebJson 不累计）
        if (like)
        {
            _ = Runtime.AchievementTrackerService.AddProgressToGroup("CopilotLikeGiven");
        }

        StatusMessage = like
            ? T("Copilot.Status.LoadedFeedbackLikeSuccess", "已提交点赞。")
            : T("Copilot.Status.LoadedFeedbackDislikeSuccess", "已提交点踩。");
        LastErrorMessage = string.Empty;
        CouldLikeWebJson = false;
        await RecordEventAsync("Copilot.Feedback.Loaded", StatusMessage, cancellationToken);
    }

    private async Task TryAutoAddLoadedCopilotAsync(CancellationToken cancellationToken)
    {
        if (!UseCopilotList || !HasLoadedCopilot || _suppressAutoAddLoadedCopilot)
        {
            return;
        }

        // Match WPF behavior: battle-list auto append only applies to clipboard/web payloads,
        // while local files stay as the currently loaded copilot.
        if (string.IsNullOrWhiteSpace(_loadedInlinePayload))
        {
            return;
        }

        var descriptor = new LoadedCopilotDescriptor(
            ResolveListItemName(_loadedDisplayName, _loadedStageName),
            string.IsNullOrWhiteSpace(_loadedType) ? ResolveTypeDisplayNameForTab(CopilotTabIndex) : _loadedType,
            string.IsNullOrWhiteSpace(_loadedStageName) ? _loadedDisplayName : _loadedStageName,
            _loadedCopilotId);

        // WPF 对齐：SSSCopilotModel 走 ParseSSSCopilot 只写缓存，从不加入战斗列表
        if (IsSssDescriptorType(descriptor.Type))
        {
            return;
        }

        var added = AddListItemsFromDescriptor(descriptor, _loadedSourcePath, _loadedInlinePayload);
        if (added > 0)
        {
            SetSelectedItemSilently(Items.LastOrDefault());
            await PersistItemsAsync(cancellationToken);
        }
    }

    private void ApplyLoadedCopilot(string sourcePath, string inlinePayload, LoadedCopilotDescriptor descriptor)
    {
        _loadedSourcePath = sourcePath;
        _loadedInlinePayload = inlinePayload;
        _loadedDisplayName = descriptor.DisplayName;
        _loadedStageName = descriptor.StageName;
        _loadedType = descriptor.Type;
        _loadedCopilotId = descriptor.CopilotId;
        FilePath = !string.IsNullOrWhiteSpace(sourcePath) ? sourcePath : inlinePayload;
        DisplayFilename = BuildDisplayFilename(sourcePath, inlinePayload);
        // WPF ParseSSSCopilot 对齐：加载 SSS 作业自动切到「保全派驻」页签
        if (IsSssDescriptorType(descriptor.Type) && CopilotTabIndex != 1)
        {
            // 程序化切页签不触发「选中条目类型编辑」手势（O1 手动切换 SelectedTypeIndex 的同步语义保留），
            // 避免静默改写并持久化当前选中的战斗列表条目
            var previousSuppress = _suppressSelectedTypeListItemSync;
            _suppressSelectedTypeListItemSync = true;
            try
            {
                CopilotTabIndex = 1;
            }
            finally
            {
                _suppressSelectedTypeListItemSync = previousSuppress;
            }
        }

        var loadedPayload = !string.IsNullOrWhiteSpace(inlinePayload) ? inlinePayload : SafeReadAllText(sourcePath);
        // WPF ParseCopilotAsync 对齐：导航名取 DataHelper.FindMap(stage_name).Code，未知关卡保留原名
        var overviewJson = ReadOverviewJsonCached();
        var navigationName = string.IsNullOrWhiteSpace(overviewJson)
            ? string.Empty
            : FindOverviewStage(descriptor.StageName, overviewJson).Code ?? string.Empty;
        SetCopilotTaskNameFromLoadedCopilot(
            !string.IsNullOrEmpty(navigationName) ? navigationName : descriptor.StageName);
        // WPF ParseCopilotAsync/ParseSSSCopilot 对齐：地图数据存在但关卡无法识别时打 UnsupportedStages 错误日志
        // 并解锁 MapOutdated 成就；overview.json 缺失沿用放行偏差（不触发 WPF 的资源更新，属既有 non-goal）
        if (!string.IsNullOrWhiteSpace(overviewJson) && HasUnsupportedLoadedStage(descriptor, loadedPayload, overviewJson))
        {
            AddLog(
                T("Copilot.Error.UnsupportedStages", "不支持的关卡") + $"  {descriptor.StageName}",
                "ERROR",
                showTime: false);
            _ = Runtime.AchievementTrackerService.Unlock("MapOutdated");
        }

        // WPF 对齐：解析 documentation.details 视频链接、地图链接并输出作业信息日志
        ApplyLoadedCopilotMetadata(loadedPayload, descriptor);
        CouldLikeWebJson = descriptor.CopilotId > 0;
        OnPropertyChanged(nameof(HasLoadedCopilot));
        OnPropertyChanged(nameof(LoadedCopilotInputHint));
    }

    // WPF ParseCopilotAsync/ParseSSSCopilot 对齐：判定已加载作业关卡是否在地图数据中无法识别
    private static bool HasUnsupportedLoadedStage(LoadedCopilotDescriptor descriptor, string payload, string overviewJson)
    {
        if (!IsSssDescriptorType(descriptor.Type))
        {
            // WPF 对齐（冻结 CopilotViewModel.cs:1139-1194）：守卫与 ParseCopilotAsync 的 IsNullOrEmpty
            // 拒绝域一致——纯空白 StageName 同样送 FindMap 判定并触发 UnsupportedStages + MapOutdated
            return !string.IsNullOrEmpty(descriptor.StageName)
                && FindOverviewStage(descriptor.StageName, overviewJson) is { StageId: null, Code: null };
        }

        // WPF ParseSSSCopilot 对齐：SSS 校验 stages 数组内每个 stage_name，数组缺失或任一未命中即视为不支持
        JsonArray? stages;
        try
        {
            stages = JsonNode.Parse(payload) is JsonObject root
                && TryGetJsonProperty(root, "stages", out var stagesNode)
                    ? stagesNode as JsonArray
                    : null;
        }
        catch
        {
            return true;
        }

        if (stages is null)
        {
            return true;
        }

        foreach (var stageNode in stages)
        {
            var stageName = stageNode is JsonObject stageObj ? ReadJsonString(stageObj, "stage_name") : string.Empty;
            if (FindOverviewStage(stageName, overviewJson) is { StageId: null, Code: null })
            {
                return true;
            }
        }

        return false;
    }

    // WPF ParseCopilotAsync/ParseSSSCopilot 对齐：视频链接解析、地图链接与作业信息日志
    private void ApplyLoadedCopilotMetadata(string payload, LoadedCopilotDescriptor descriptor)
    {
        CopilotUrl = PrtsPlusUrl;
        MapUrl = string.IsNullOrWhiteSpace(descriptor.StageName)
            ? MapPrtsUrl
            : MapPrtsUrl.Replace("areas", "map/" + descriptor.StageName, StringComparison.Ordinal);

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(payload) as JsonObject;
        }
        catch
        {
            return;
        }

        if (root is null)
        {
            return;
        }

        var title = string.Empty;
        var titleColor = string.Empty;
        var details = string.Empty;
        var detailsColor = string.Empty;
        if (TryGetJsonProperty(root, "documentation", out var docNode) && docNode is JsonObject documentation)
        {
            title = ReadJsonString(documentation, "title");
            titleColor = ReadJsonString(documentation, "title_color");
            details = ReadJsonString(documentation, "details");
            detailsColor = ReadJsonString(documentation, "details_color");
        }

        if (!string.IsNullOrEmpty(details))
        {
            var match = BilibiliLinkRegex.Match(details);
            if (match.Success)
            {
                CopilotUrl = BilibiliVideoUrl + match.Value;
            }
        }

        AppendLoadedCopilotOutputLogs(descriptor, title, titleColor, details, detailsColor, root);
    }

    // WPF CopilotModel.Output/SSSCopilotModel.Output 对齐：作业信息写日志面板
    private void AppendLoadedCopilotOutputLogs(
        LoadedCopilotDescriptor descriptor,
        string title,
        string titleColor,
        string details,
        string detailsColor,
        JsonObject root)
    {
        if (!string.IsNullOrEmpty(title))
        {
            AddLog(title, MapDocColorToLevel(titleColor), showTime: false);
        }

        if (!string.IsNullOrEmpty(details))
        {
            AddLog(details, MapDocColorToLevel(detailsColor), showTime: false);
        }

        var isSss = IsSssDescriptorType(descriptor.Type);
        var skillText = T("Copilot.CopilotSkill", "技能");
        var count = 0;
        if (TryGetJsonProperty(root, "opers", out var opersNode) && opersNode is JsonArray opers)
        {
            foreach (var oper in opers.OfType<JsonObject>())
            {
                count++;
                var name = ReadJsonString(oper, "name");
                var skill = ReadJsonInt(oper, "skill") ?? 0; // WPF dev-v2 Oper.Skill 默认 0（不指定技能）
                AddLog(
                    isSss
                        ? string.Format(CultureInfo.InvariantCulture, "{0}, {1} {2}", name, skillText, skill)
                        : string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} {1} {2}{3}",
                            name,
                            skillText,
                            skill,
                            FormatSkillLevelInfo(oper)).Trim(),
                    "INFO",
                    showTime: false);
            }
        }

        if (!isSss && TryGetJsonProperty(root, "groups", out var groupsNode) && groupsNode is JsonArray groups)
        {
            foreach (var group in groups.OfType<JsonObject>())
            {
                count++;
                var groupName = ReadJsonString(group, "name");
                var infos = TryGetJsonProperty(group, "opers", out var groupOpersNode) && groupOpersNode is JsonArray groupOpers
                    ? groupOpers.OfType<JsonObject>()
                        .Select(oper => string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} {1}{2}",
                            ReadJsonString(oper, "name"),
                            ReadJsonInt(oper, "skill") ?? 0, // WPF dev-v2 Oper.Skill 默认 0
                            FormatSkillLevelInfo(oper)).Trim())
                        .ToList()
                    : new List<string>();
                AddLog($"{groupName}: {string.Join(" / ", infos)}", "INFO", showTime: false);
            }
        }

        if (count > 0)
        {
            AddLog(
                string.Format(CultureInfo.InvariantCulture, T("Copilot.TotalOperatorsCount", "共 {0} 名干员"), count),
                "INFO",
                showTime: false);
        }
    }

    private static string FormatSkillLevelInfo(JsonObject oper)
    {
        if (TryGetJsonProperty(oper, "requirements", out var reqNode) && reqNode is JsonObject requirements
            && ReadJsonInt(requirements, "skill_level") is { } skillLevel
            && skillLevel > 0 && skillLevel <= 10)
        {
            return string.Format(CultureInfo.InvariantCulture, " [Lv.{0}]", skillLevel);
        }

        return string.Empty;
    }

    private void ClearLoadedCopilot()
    {
        InvalidateLoadedCopilot();
        FilePath = string.Empty;
        DisplayFilename = string.Empty;
    }

    // WPF UpdateFileDoc/ParseCopilotSetAsync 入口对齐（冻结 :1007-1009、:1330-1331）：
    // 作废当前已加载作业（_copilotCache = null; CopilotId = 0），但保留输入框文本
    // （DisplayFilename/FilePath）；各加载入口在尝试前调用，成功路径由 ApplyLoadedCopilot 重新填充
    private void InvalidateLoadedCopilot()
    {
        _loadedSourcePath = string.Empty;
        _loadedInlinePayload = string.Empty;
        _loadedDisplayName = string.Empty;
        _loadedStageName = string.Empty;
        _loadedType = ResolveTypeDisplayNameForTab(CopilotTabIndex);
        _loadedCopilotId = 0;
        SetCopilotTaskNameFromLoadedCopilot(string.Empty);
        CouldLikeWebJson = false;
        OnPropertyChanged(nameof(HasLoadedCopilot));
        OnPropertyChanged(nameof(LoadedCopilotInputHint));
    }

    private void SetCopilotTaskNameFromLoadedCopilot(string name)
    {
        var previousSuppress = _suppressCopilotTaskNameListItemSync;
        _suppressCopilotTaskNameListItemSync = true;
        try
        {
            CopilotTaskName = name;
        }
        finally
        {
            _suppressCopilotTaskNameListItemSync = previousSuppress;
        }
    }

    private string BuildDisplayFilename(string sourcePath, string inlinePayload)
    {
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var root = ResolveCopilotResourceRoot();
            if (!string.IsNullOrWhiteSpace(root)
                && sourcePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetRelativePath(root, sourcePath);
            }

            return sourcePath;
        }

        return inlinePayload;
    }

    private string ResolveCopilotResourceRoot()
    {
        return Path.Combine(ResolveCopilotResourceDirectory(), "copilot");
    }

    // WPF DisplayFilename setter 对齐：_copilotJsonPathMap 覆盖 resource/copilot 全部嵌套子目录，
    // 输入裸文件名时可解析到子目录中的作业文件
    private static string? FindCopilotJsonByFileName(string copilotRoot, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(copilotRoot))
        {
            return null;
        }

        try
        {
            return Directory
                .EnumerateFiles(copilotRoot, "*.json", SearchOption.AllDirectories)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.Ordinal));
        }
        catch
        {
            return null;
        }
    }

    private CopilotFileItemViewModel CreateFileNode(string filePath, string root)
    {
        return new CopilotFileItemViewModel(
            Path.GetFileName(filePath),
            filePath,
            Path.GetRelativePath(root, filePath),
            isFolder: false);
    }

    private CopilotFileItemViewModel? CreateFolderNode(string directoryPath, string root)
    {
        var item = new CopilotFileItemViewModel(
            Path.GetFileName(directoryPath),
            directoryPath,
            Path.GetRelativePath(root, directoryPath),
            isFolder: true);

        foreach (var file in Directory.GetFiles(directoryPath, "*.json"))
        {
            item.Children.Add(CreateFileNode(file, root));
        }

        foreach (var childDirectory in Directory.GetDirectories(directoryPath))
        {
            var child = CreateFolderNode(childDirectory, root);
            if (child is not null)
            {
                item.Children.Add(child);
            }
        }

        return item.Children.Count == 0 ? null : item;
    }

    private static string ResolveStoredSourcePath(string sourcePath)
    {
        var raw = (sourcePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(raw))
        {
            return raw;
        }

        return Path.Combine(RuntimeLayout.ResolveRuntimeBaseDirectory(), raw);
    }

    private static bool TryParseCopilotCodeSource(
        string source,
        out CopilotCodeSourceKind kind,
        out int copilotId)
    {
        // WPF IsCopilotCode/TryParseCopilotCode 对齐（dev-v2 72213def33）：支持 maa://、prts://、
        // prts://s、s<id> 与纯数字；前缀剥离后直接 int.TryParse，不额外容忍多余斜杠
        // （maa:///123 解析失败，按非代码输入处理）。
        return CopilotCodeSourceParser.TryParse(source, out kind, out copilotId);
    }

    private static string BuildCopilotCodeDisplay(int copilotId)
    {
        return $"{CopilotIdPrefix}{copilotId}";
    }

    private static string ResolveTypeDisplayNameForTab(int tabIndex)
    {
        return tabIndex switch
        {
            1 => SecurityServiceStationType,
            2 => ParadoxSimulationType,
            3 => OtherActivityType,
            _ => MainStageStoryCollectionSideStoryType,
        };
    }

    private static string ResolveListItemName(string displayName, string stageName)
    {
        if (!string.IsNullOrWhiteSpace(stageName))
        {
            return stageName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName.Trim();
        }

        return $"Task-{DateTime.Now:HHmmss}";
    }

    private bool TryReadLoadedCopilotDescriptor(
        string payload,
        string? sourcePath,
        out LoadedCopilotDescriptor descriptor,
        out string warning)
    {
        descriptor = new LoadedCopilotDescriptor(
            Path.GetFileNameWithoutExtension(sourcePath ?? string.Empty),
            ResolveTypeDisplayNameForTab(CopilotTabIndex),
            string.Empty,
            0);
        warning = string.Empty;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payload);
        }
        catch (Exception ex)
        {
            warning = ex.Message;
            return false;
        }

        if (root is not JsonObject obj)
        {
            warning = T("Copilot.Error.CurrentPayloadNotObject", "当前作业必须是单个 JSON 对象。");
            return false;
        }

        var stageName = ReadJsonString(obj, "stage_name");
        var displayName = !string.IsNullOrWhiteSpace(stageName)
            ? stageName
            : Path.GetFileNameWithoutExtension(sourcePath ?? string.Empty);
        // WPF 对齐（冻结 CopilotHelper.cs:227）：SSS 判定走 JObject.TryGetValue("type", OrdinalIgnoreCase)，
        // 大小写变体重复键取插入序首个匹配（区别于 POCO 绑定键的 last-wins）
        var rawType = ReadJsonStringFirstMatch(obj, "type");
        var type = string.IsNullOrWhiteSpace(rawType)
            ? ResolveTypeDisplayNameForStage(stageName)
            : NormalizeTypeDisplayName(rawType);

        // WPF 对齐（冻结 CopilotModel.cs:47-48，Newtonsoft DifficultyFlags 枚举反序列化）：
        // SSS 作业（SSSCopilotModel 无 Difficulty 属性）的 difficulty 字段被 Newtonsoft 忽略，不参与校验；
        // 其余作业的合法域为字段缺省、整数（含未知位）、数字字符串与枚举名（大小写不敏感，支持
        // "Normal, Raid" 逗号组合）；JSON null 或其他无法解析的值 → JsonSerializationException
        // → CopilotFileReadError，整个作业拒绝加载。
        // SSS 判定对齐冻结 CopilotHelper.cs:227：键大小写不敏感、值大小写敏感且不裁剪
        // （"sss"/"SSS " 均按普通作业解析并参与 difficulty 校验）
        if (!string.Equals(rawType, "SSS", StringComparison.Ordinal)
            && !TryReadWpfDifficulty(obj, out _))
        {
            warning = T("Copilot.Error.CurrentPayloadInvalidDifficulty", "作业 difficulty 字段必须是整数或难度枚举名。");
            return false;
        }

        // WPF CopilotContentConverter 对齐（冻结 CopilotHelper.cs:228-272）：非 Debug 版本强制
        // minimum_required SemVersion 门控（缺失/不可解析/高于当前 core 版本均拒绝），
        // 覆盖本地文件/剪贴板/maa://远程/合集全部导入通道；门控在 difficulty 校验之后，
        // 与 WPF 先 ToObject（字段绑定失败先抛）后比较版本的错误优先级一致
        var versionRejection = CopilotVersionGate.Evaluate(obj, ResolveCoreVersionForGate(), ResolveUiVersionForGate());
        if (versionRejection is not null)
        {
            warning = versionRejection;
            return false;
        }

        descriptor = new LoadedCopilotDescriptor(
            string.IsNullOrWhiteSpace(displayName) ? $"Imported-{DateTime.Now:HHmmss}" : displayName.Trim(),
            type,
            // WPF 对齐：stage_name 原样保留不裁剪（DataHelper.FindMap 精确比较）；仅 null/空串
            // （ParseCopilotAsync 的 IsNullOrEmpty 拒绝域）回退文件名。纯空白名必须保留——WPF 放行后由
            // FindMap 判定 null → UnsupportedStages 错误日志 + MapOutdated 解锁；若回退为文件名/空串，
            // 剪贴板/maa://来源会把 StageName 吞成 ""，下方校验被 !IsNullOrWhiteSpace 守卫跳过而静默放行
            string.IsNullOrEmpty(stageName) ? displayName.Trim() : stageName,
            ReadJsonInt(obj, "copilot_id") ?? ReadJsonInt(obj, "id") ?? 0);
        return true;
    }

    // Newtonsoft POCO 绑定对齐：键大小写不敏感；大小写变体重复键按文档序后出现的覆盖先出现的
    // （last-wins，JsonSerializer 顺序读取逐属性赋值；同键重复两侧解析器均为 last-wins）
    private static bool TryGetJsonProperty(JsonObject obj, string key, out JsonNode? value)
    {
        value = null;
        var found = false;
        foreach (var property in obj)
        {
            if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }

        return found;
    }

    // Newtonsoft JObject.TryGetValue(name, OrdinalIgnoreCase) 对齐：忽略大小写、返回插入序首个匹配
    // （WPF CopilotContentConverter 的 "type" SSS 判定路径专用）
    private static bool TryGetJsonPropertyFirstMatch(JsonObject obj, string key, out JsonNode? value)
    {
        foreach (var property in obj)
        {
            if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string ReadJsonString(JsonObject obj, string key)
    {
        // WPF/Newtonsoft 对齐：键大小写不敏感；值原样返回不裁剪（FindMap/类型判定均为精确比较）
        if (!TryGetJsonProperty(obj, key, out var node)
            || node is not JsonValue jsonValue
            || !jsonValue.TryGetValue(out string? raw))
        {
            return string.Empty;
        }

        return raw ?? string.Empty;
    }

    private static string ReadJsonStringFirstMatch(JsonObject obj, string key)
    {
        if (!TryGetJsonPropertyFirstMatch(obj, key, out var node)
            || node is not JsonValue jsonValue
            || !jsonValue.TryGetValue(out string? raw))
        {
            return string.Empty;
        }

        return raw ?? string.Empty;
    }

    private static int? ReadJsonInt(JsonObject obj, string key)
    {
        if (!TryGetJsonProperty(obj, key, out var node)
            || node is not JsonValue jsonValue)
        {
            return null;
        }

        if (jsonValue.TryGetValue(out int parsedInt))
        {
            return parsedInt;
        }

        if (jsonValue.TryGetValue(out string? raw) && int.TryParse(raw, out parsedInt))
        {
            return parsedInt;
        }

        return null;
    }

    // WPF CopilotModel.DifficultyFlags（冻结 CopilotModel.cs:336-352）
    [Flags]
    private enum WpfDifficultyFlags
    {
        None = 0,
        Normal = 1,
        Raid = 2,
    }

    // Newtonsoft DifficultyFlags 反序列化接受域：字段缺省（视为 None）、整数（含未知位）、数字字符串、
    // 枚举名（大小写不敏感，支持 "Normal, Raid" 逗号组合）；JSON null 与其余值均无效
    // （WPF 抛 JsonSerializationException → CopilotFileReadError 整份拒绝）
    private static bool TryReadWpfDifficulty(JsonObject obj, out int difficulty)
    {
        difficulty = 0;
        if (!TryGetJsonProperty(obj, "difficulty", out var node))
        {
            return true;
        }

        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out int parsed))
        {
            difficulty = parsed;
            return true;
        }

        if (jsonValue.TryGetValue(out string? raw)
            && Enum.TryParse<WpfDifficultyFlags>(raw, ignoreCase: true, out var flags))
        {
            difficulty = (int)flags;
            return true;
        }

        return false;
    }

    private CopilotItemViewModel? CreateListItemFromDescriptor(
        LoadedCopilotDescriptor descriptor,
        string sourcePath,
        string inlinePayload,
        bool isRaid)
    {
        // WPF AddCopilotTaskToList 对齐：条目名取关卡 code（core 以 nav_name 拼 tasks.json 导航键）；
        // 地图数据存在但关卡无法识别（WPF mapInfo is null）时拒绝入列，返回 null；
        // overview.json 缺失沿用放行偏差（回退原名，启动校验仍会拦截）
        var overviewJson = ReadOverviewJsonCached();
        var navigationName = string.Empty;
        if (!string.IsNullOrWhiteSpace(overviewJson))
        {
            navigationName = FindOverviewStage(descriptor.StageName, overviewJson).Code ?? string.Empty;
            if (string.IsNullOrEmpty(navigationName))
            {
                return null;
            }
        }

        return new CopilotItemViewModel(
            !string.IsNullOrEmpty(navigationName)
                ? navigationName
                : ResolveListItemName(descriptor.DisplayName, descriptor.StageName),
            descriptor.Type,
            sourcePath,
            inlinePayload)
        {
            CopilotId = Math.Max(0, descriptor.CopilotId),
            IsChecked = true,
            IsRaid = isRaid,
            TabIndex = ResolveTabIndexFromType(descriptor.Type),
        };
    }

    private int AddListItemsFromDescriptor(
        LoadedCopilotDescriptor descriptor,
        string sourcePath,
        string inlinePayload)
    {
        var payload = !string.IsNullOrWhiteSpace(inlinePayload)
            ? inlinePayload
            : string.IsNullOrWhiteSpace(sourcePath) ? string.Empty : SafeReadAllText(sourcePath);
        // WPF 对齐：悖论页签（AddCopilotTaskToList 页签 2 分支）恒添加单个条目，忽略难度位掩码，条目名取干员名
        var isParadox = IsParadoxDescriptorType(descriptor.Type);
        var added = 0;
        foreach (var isRaid in isParadox ? new List<bool> { false } : ResolveDifficultyVariants(payload))
        {
            var item = CreateListItemFromDescriptor(descriptor, sourcePath, inlinePayload, isRaid);
            if (item is null)
            {
                // WPF AddCopilotTaskToList 对齐：mapInfo is null 时打错误日志并拒绝入列
                AddLog(
                    string.Format(T("Copilot.Error.StageNotFound", "无法识别作业关卡：{0}"), descriptor.StageName),
                    "ERROR",
                    showTime: false);
                continue;
            }

            if (isParadox)
            {
                item.Name = ResolveParadoxItemName(descriptor);
            }

            Items.Add(item);
            added++;
        }

        return added;
    }

    private string ResolveTypeDisplayNameForStage(string stageName)
    {
        return IsParadoxStage(stageName)
            ? ParadoxSimulationType
            : ResolveTypeDisplayNameForTab(CopilotTabIndex);
    }

    private static IEnumerable<bool> ResolveDifficultyVariants(string payload)
    {
        try
        {
            if (JsonNode.Parse(payload) is not JsonObject obj
                || !TryReadWpfDifficulty(obj, out var difficulty))
            {
                return [false];
            }

            var variants = new List<bool>();
            if ((difficulty & 1) != 0)
            {
                variants.Add(false);
            }

            if ((difficulty & 2) != 0)
            {
                variants.Add(true);
            }

            // WPF 对齐：DifficultyFlags.None(0) 视为普通（冻结 :1214-1216）；
            // 难度仅含未知位（如 4）时 HasFlag(Normal/Raid) 均不命中，不添加任何条目（冻结 :1676-1688）
            if (variants.Count > 0 || difficulty != 0)
            {
                return variants;
            }

            return [false];
        }
        catch
        {
            return [false];
        }
    }

    private static string SafeReadAllText(string sourcePath)
    {
        try
        {
            return File.ReadAllText(sourcePath);
        }
        catch
        {
            return string.Empty;
        }
    }

    // 资源文本缓存：合集/批量导入逐条目读取并解析 overview.json / battle_data.json（数千条目）开销大，
    // 按（路径 + 最后写入时间 + 长度）缓存文件内容，功能行为不变（WPF DataHelper 同为进程内缓存）
    private readonly object _resourceTextCacheLock = new();
    private readonly Dictionary<string, (DateTime TimestampUtc, long Length, string Content)> _resourceTextCache = new(StringComparer.Ordinal);

    private string ReadResourceTextCached(string path)
    {
        DateTime timestampUtc;
        long length;
        try
        {
            if (!File.Exists(path))
            {
                lock (_resourceTextCacheLock)
                {
                    _resourceTextCache.Remove(path);
                }

                return string.Empty;
            }

            var info = new FileInfo(path);
            timestampUtc = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch
        {
            return string.Empty;
        }

        lock (_resourceTextCacheLock)
        {
            if (_resourceTextCache.TryGetValue(path, out var entry)
                && entry.TimestampUtc == timestampUtc
                && entry.Length == length)
            {
                return entry.Content;
            }
        }

        var content = SafeReadAllText(path);
        lock (_resourceTextCacheLock)
        {
            _resourceTextCache[path] = (timestampUtc, length, content);
        }

        return content;
    }

    private string ReadOverviewJsonCached()
        => ReadResourceTextCached(Path.Combine(ResolveCopilotResourceDirectory(), "Arknights-Tile-Pos", "overview.json"));

    private string ReadBattleDataJsonCached()
        => ReadResourceTextCached(Path.Combine(ResolveCopilotResourceDirectory(), "battle_data.json"));

    // WPF ParseCopilotAsync 对齐：DataHelper.FindMap（overview.json）命中且 stageId 以 mem_ 开头判定为悖论模拟；
    // 生产资源 stages.json 不含 mem_ 关卡（overview.json 才有），不能以其判定
    private bool IsParadoxStage(string stageName)
    {
        if (string.IsNullOrWhiteSpace(stageName))
        {
            return false;
        }

        var overviewJson = ReadOverviewJsonCached();
        return !string.IsNullOrWhiteSpace(overviewJson) && IsParadoxStageFromOverviewJson(stageName, overviewJson);
    }

    internal static bool IsParadoxStageFromOverviewJson(string stageName, string overviewJson)
    {
        if (string.IsNullOrWhiteSpace(stageName) || string.IsNullOrWhiteSpace(overviewJson))
        {
            return false;
        }

        var (stageId, _) = FindOverviewStage(stageName, overviewJson);
        return stageId?.StartsWith("mem_", StringComparison.Ordinal) == true;
    }

    // 测试钩子：覆盖资源目录（overview.json / battle_data.json 所在目录）
    internal string? ResourceDirectoryOverride { get; set; }

    // 测试钩子：覆盖 minimum_required 门控的 core/UI 版本来源
    internal string? CoreVersionOverride { get; set; }

    internal string? UiVersionOverride { get; set; }

    private bool _coreVersionForGateResolved;
    private string? _coreVersionForGate;

    private string? ResolveCoreVersionForGate()
    {
        if (CoreVersionOverride is not null)
        {
            return CoreVersionOverride;
        }

        if (!_coreVersionForGateResolved)
        {
            _coreVersionForGate = CopilotVersionGate.ResolveInstalledCoreVersion();
            _coreVersionForGateResolved = true;
        }

        return _coreVersionForGate;
    }

    private string? ResolveUiVersionForGate()
        => UiVersionOverride ?? CopilotVersionGate.ResolveUiVersion();

    private string ResolveCopilotResourceDirectory()
        => ResourceDirectoryOverride
            ?? Path.Combine(RuntimeLayout.ResolveRuntimeBaseDirectory(), "resource");

    private static bool IsSssDescriptorType(string type)
        => string.Equals(ResolveCopilotTaskType(type), "SSSCopilot", StringComparison.Ordinal);

    private static bool IsParadoxDescriptorType(string type)
        => string.Equals(NormalizeTypeDisplayName(type), ParadoxSimulationType, StringComparison.Ordinal);

    // WPF AddCopilotTaskToList 悖论分支对齐：条目名取干员名，找不到时回退关卡 code / 原名
    private string ResolveParadoxItemName(LoadedCopilotDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(descriptor.StageName))
        {
            var overviewJson = ReadOverviewJsonCached();
            if (!string.IsNullOrWhiteSpace(overviewJson))
            {
                var (stageId, code) = FindOverviewStage(descriptor.StageName, overviewJson);
                if (!string.IsNullOrEmpty(stageId) && stageId.Length > 6)
                {
                    // WPF：stageId[4..^2] 提取干员代号，例如 mem_12fce_1 → 12fce
                    var battleDataJson = ReadBattleDataJsonCached();
                    var operatorName = FindOperatorNameByCodeName(stageId[4..^2], battleDataJson);
                    if (!string.IsNullOrWhiteSpace(operatorName))
                    {
                        return operatorName;
                    }
                }

                if (!string.IsNullOrWhiteSpace(code))
                {
                    return code;
                }
            }
        }

        return ResolveListItemName(descriptor.DisplayName, descriptor.StageName);
    }

    // WPF AddCopilotTaskToList 悖论分支对齐：stage_name → overview.json 查 stageId → stageId[4..^2] → battle_data.json 查干员名
    internal static string? ResolveParadoxOperatorName(string stageName, string? overviewJson, string? battleDataJson)
    {
        if (string.IsNullOrWhiteSpace(stageName) || string.IsNullOrWhiteSpace(overviewJson))
        {
            return null;
        }

        var (stageId, _) = FindOverviewStage(stageName, overviewJson);
        if (string.IsNullOrEmpty(stageId) || stageId.Length <= 6)
        {
            return null;
        }

        return FindOperatorNameByCodeName(stageId[4..^2], battleDataJson);
    }

    internal static bool IsKnownStageFromOverviewJson(string stageName, string overviewJson)
    {
        if (string.IsNullOrWhiteSpace(stageName) || string.IsNullOrWhiteSpace(overviewJson))
        {
            return false;
        }

        var (stageId, code) = FindOverviewStage(stageName, overviewJson);
        return stageId is not null || code is not null;
    }

    // WPF DataHelper.FindMap 对齐：Code/Name/StageId/LevelId 任一精确匹配
    private static (string? StageId, string? Code) FindOverviewStage(string stageName, string overviewJson)
    {
        if (string.IsNullOrEmpty(stageName))
        {
            return (null, null);
        }

        try
        {
            if (JsonNode.Parse(overviewJson) is not JsonObject root)
            {
                return (null, null);
            }

            foreach (var pair in root)
            {
                if (pair.Value is not JsonObject entry)
                {
                    continue;
                }

                if (string.Equals(ReadJsonString(entry, "code"), stageName, StringComparison.Ordinal)
                    || string.Equals(ReadJsonString(entry, "name"), stageName, StringComparison.Ordinal)
                    || string.Equals(ReadJsonString(entry, "stageId"), stageName, StringComparison.Ordinal)
                    || string.Equals(ReadJsonString(entry, "levelId"), stageName, StringComparison.Ordinal))
                {
                    var stageId = ReadJsonString(entry, "stageId");
                    var code = ReadJsonString(entry, "code");
                    return (string.IsNullOrEmpty(stageId) ? null : stageId, string.IsNullOrEmpty(code) ? null : code);
                }
            }
        }
        catch
        {
            // 地图数据损坏时按未命中处理
        }

        return (null, null);
    }

    internal static bool IsKnownOperatorNameFromBattleDataJson(string operName, string battleDataJson)
    {
        if (string.IsNullOrWhiteSpace(operName) || string.IsNullOrWhiteSpace(battleDataJson))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(battleDataJson) is not JsonObject root
                || !root.TryGetPropertyValue("chars", out var charsNode)
                || charsNode is not JsonObject chars)
            {
                return false;
            }

            foreach (var pair in chars)
            {
                // WPF DataHelper.Operators：仅 char_ 前缀且职业为干员职业，且排除 _virtualOperators
                if (!pair.Key.StartsWith("char_", StringComparison.Ordinal) || pair.Value is not JsonObject charObj)
                {
                    continue;
                }

                // WPF DataHelper._virtualOperators（冻结 DataHelper.cs:441-476）对齐：
                // 预备干员/集成战略专属等未实装虚拟干员不算合法悖论干员
                if (VirtualOperatorIds.Contains(pair.Key))
                {
                    continue;
                }

                var profession = ReadJsonString(charObj, "profession");
                if (!string.IsNullOrEmpty(profession) && !OperatorProfessions.Contains(profession))
                {
                    continue;
                }

                // WPF VerifyParadoxTasks 对齐：GetCharacterByNameOrAlias 的名称索引为 OrdinalIgnoreCase，
                // 外层 == 比较的是同一干员的 zh-cn 名，整体等效大小写不敏感匹配
                if (string.Equals(ReadJsonString(charObj, "name"), operName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ReadJsonString(charObj, "name_tw"), operName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ReadJsonString(charObj, "name_en"), operName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ReadJsonString(charObj, "name_jp"), operName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ReadJsonString(charObj, "name_kr"), operName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 干员数据损坏时按未命中处理
        }

        return false;
    }

    // WPF DataHelper.GetCharacterByCodeName 对齐：从 "char_002_amiya" 提取 "amiya" 匹配代号
    private static string? FindOperatorNameByCodeName(string codeName, string? battleDataJson)
    {
        if (string.IsNullOrWhiteSpace(codeName) || string.IsNullOrWhiteSpace(battleDataJson))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(battleDataJson) is not JsonObject root
                || !root.TryGetPropertyValue("chars", out var charsNode)
                || charsNode is not JsonObject chars)
            {
                return null;
            }

            foreach (var pair in chars)
            {
                if (!pair.Key.StartsWith("char_", StringComparison.Ordinal) || pair.Value is not JsonObject charObj)
                {
                    continue;
                }

                var parts = pair.Key.Split('_');
                var charCodeName = parts.Length >= 3 ? parts[2] : pair.Key;
                if (!string.Equals(charCodeName, codeName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = ReadJsonString(charObj, "name");
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }
        }
        catch
        {
            // 干员数据损坏时按未命中处理
        }

        return null;
    }

    private static readonly HashSet<string> OperatorProfessions = new(StringComparer.OrdinalIgnoreCase)
    {
        "CASTER",
        "MEDIC",
        "PIONEER",
        "SNIPER",
        "SPECIAL",
        "SUPPORT",
        "TANK",
        "WARRIOR",
    };

    // WPF DataHelper._virtualOperators（冻结 DataHelper.cs:441-476）：未实装但在 battle_data 中的虚拟干员，
    // battle_data.json 无标记字段，WPF 侧同为硬编码列表
    private static readonly HashSet<string> VirtualOperatorIds = new(StringComparer.Ordinal)
    {
        "char_504_rguard", // 预备干员-近战
        "char_505_rcast",  // 预备干员-术师
        "char_506_rmedic", // 预备干员-后勤
        "char_507_rsnipe", // 预备干员-狙击
        "char_508_aguard", // Sharp
        "char_509_acast",  // Pith
        "char_510_amedic", // Touch
        "char_511_asnipe", // Stormeye
        "char_512_aprot",  // 暮落
        "char_513_apionr", // 郁金香
        "char_514_rdfend", // 预备干员-重装
        "char_600_cpione", // 预备干员-先锋 4★
        "char_601_cguard", // 预备干员-近卫 4★
        "char_602_cdfend", // 预备干员-重装 4★
        "char_603_csnipe", // 预备干员-狙击 4★
        "char_604_ccast",  // 预备干员-术师 4★
        "char_605_cmedic", // 预备干员-医疗 4★
        "char_606_csuppo", // 预备干员-辅助 4★
        "char_607_cspec",  // 预备干员-特种 4★
        "char_608_acpion", // 郁金香 6★
        "char_609_acguad", // Sharp 6★
        "char_610_acfend", // Mechanist 6★
        "char_611_acnipe", // Stormeye 6★
        "char_612_accast", // Pith 6★
        "char_613_acmedc", // Touch 6★
        "char_614_acsupo", // Raidian 6★
        "char_615_acspec", // Misery 6★
        "char_616_pithst", // 盟约·辅助干员
        "char_617_sharp2", // 领主·Sharp
        "char_1001_amiya2", // 阿米娅-WARRIOR
        "char_1037_amiya3", // 阿米娅-MEDIC
    };

    private bool IsCopilotRecentlyRated(int copilotId)
    {
        lock (_recentlyRatedCopilotIdsGate)
        {
            return _recentlyRatedCopilotIds.Contains(copilotId);
        }
    }

    private void MarkCopilotRecentlyRated(int copilotId)
    {
        lock (_recentlyRatedCopilotIdsGate)
        {
            _recentlyRatedCopilotIds.Add(copilotId);
        }
    }

    // 成功占用返回 true（失败时需 Release 以便重试）；本会话已评分过返回 false
    private bool TryReserveCopilotRating(int copilotId)
    {
        lock (_recentlyRatedCopilotIdsGate)
        {
            return _recentlyRatedCopilotIds.Add(copilotId);
        }
    }

    private void ReleaseCopilotRatingReservation(int copilotId)
    {
        lock (_recentlyRatedCopilotIdsGate)
        {
            _recentlyRatedCopilotIds.Remove(copilotId);
        }
    }

    private IEnumerable<CopilotUserAdditionalItemViewModel> ParseUserAdditionalItems()
    {
        if (string.IsNullOrWhiteSpace(UserAdditional))
        {
            return [];
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(UserAdditional);
        }
        catch
        {
            // WPF ParseUserAdditionals 旧格式回退对齐（冻结 CopilotViewModel.cs:1938-1966）：
            // 正则 (?<=;)(?<name>[^,;]+)(?:, *(?<skill>\d))?(?=;) 要求技能为单个数字且紧跟分号边界；
            // "name,10"（多数字）、"name,abc"（非数字）等不匹配先行断言的条目整条丢弃，
            // 不得按夹取技能值保留
            var fallbackItems = new List<CopilotUserAdditionalItemViewModel>();
            foreach (Match match in LegacyUserAdditionalRegex.Matches(";" + UserAdditional + ";"))
            {
                var name = match.Groups["name"].Value.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var skillText = match.Groups["skill"].Value;
                // WPF dev-v2 b2d8591974：技能默认值改为 0（不指定技能）
                var skill = string.IsNullOrEmpty(skillText) ? 0 : int.Parse(skillText, CultureInfo.InvariantCulture);
                fallbackItems.Add(new CopilotUserAdditionalItemViewModel
                {
                    Name = name,
                    Skill = Math.Clamp(skill, 0, 3),
                    Module = 0,
                });
            }

            return fallbackItems;
        }

        if (root is not JsonArray array)
        {
            return [];
        }

        var parsedItems = new List<CopilotUserAdditionalItemViewModel>();
        foreach (var item in array.OfType<JsonObject>())
        {
            var name = ReadJsonString(item, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            parsedItems.Add(new CopilotUserAdditionalItemViewModel
            {
                Name = name,
                Skill = Math.Clamp(ReadJsonInt(item, "skill") ?? 0, 0, 3),
                Module = Math.Clamp(ReadJsonInt(item, "module") ?? 0, 0, 5),
            });
        }

        return parsedItems;
    }

    // WPF parity (86e58bda95, AsstCopilotTask.Serialize): the operator-box data file is only
    // forwarded when a path is configured; Core treats an empty path as "disabled".
    private void AppendOperBoxDataPath(JsonObject payload)
    {
        var path = ResolveOperBoxDataPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            payload["operbox_data_path"] = path;
        }
    }

    /// <summary>
    /// Resolves the file handed to Core. An explicitly configured path wins for backwards
    /// compatibility; otherwise the assist switch enables <c>DataDir/OperBoxData.json</c> only
    /// while that file exists (WPF dev-v2 86e58bda95 / 131f2d248d).
    /// </summary>
    /// <returns>Path to send, or an empty string to omit the parameter.</returns>
    private string ResolveOperBoxDataPath()
    {
        if (!string.IsNullOrWhiteSpace(_operBoxDataPath))
        {
            return _operBoxDataPath;
        }

        if (!_enableOperBoxAssist)
        {
            return string.Empty;
        }

        var baseDirectory = Runtime.ConfigurationService.BaseDirectory;
        if (OperBoxDataFile.TryResolveExistingPath(baseDirectory, out var path))
        {
            return path;
        }

        Runtime.LogService.Warn(
            $"OperBox assist is enabled but the operator-box data file is missing: {OperBoxDataFile.ResolvePath(baseDirectory)}");
        return string.Empty;
    }

    private JsonArray BuildUserAdditionalPayload()
    {
        var result = new JsonArray();
        if (!AddUserAdditional)
        {
            return result;
        }

        foreach (var item in ParseUserAdditionalItems())
        {
            result.Add(new JsonObject
            {
                ["name"] = item.Name.Trim(),
                ["skill"] = Math.Clamp(item.Skill, 0, 3),
                ["module"] = Math.Clamp(item.Module, 0, 5),
            });
        }

        return result;
    }

    // WPF 对齐：Start() 的 ValidateStartAsync 前置校验，在连接模拟器之前执行
    private async Task<StartSelection?> ValidateStartSelectionAsync(CancellationToken cancellationToken)
    {
        if (UseCopilotList)
        {
            var checkedItems = await ValidateCopilotListSelectionAsync(cancellationToken);
            if (checkedItems is null)
            {
                return null;
            }

            return new StartSelection(checkedItems);
        }

        if (!await ValidateSingleSelectionAsync(cancellationToken))
        {
            return null;
        }

        if (!HasLoadedCopilot && SelectedItem is null)
        {
            StatusMessage = T("Copilot.Status.StartFailed", "启动失败。");
            LastErrorMessage = T("Copilot.Error.StartSelectTask", "请选择要执行的作业。");
            await RecordFailedResultAsync(
                "Copilot.Start",
                UiOperationResult.Fail(UiErrorCode.CopilotSelectionMissing, LastErrorMessage),
                cancellationToken);
            return null;
        }

        return new StartSelection(null);
    }

    private async Task<AppendPlan?> AppendConfiguredCopilotAsync(StartSelection selection, CancellationToken cancellationToken)
    {
        if (selection.CheckedItems is { } checkedItems)
        {
            return await AppendCopilotListAsync(checkedItems, cancellationToken);
        }

        if (HasLoadedCopilot)
        {
            return await AppendLoadedCopilotAsync(cancellationToken);
        }

        return await AppendSingleItemAsync(SelectedItem!, cancellationToken);
    }

    private async Task<bool> ValidateSingleSelectionAsync(CancellationToken cancellationToken)
    {
        var selectedType = HasLoadedCopilot
            ? _loadedType
            : SelectedItem?.Type;
        if (string.IsNullOrWhiteSpace(selectedType))
        {
            return true;
        }

        var isSss = string.Equals(ResolveCopilotTaskType(selectedType), "SSSCopilot", StringComparison.Ordinal);
        if ((isSss && CopilotTabIndex != 1) || (!isSss && CopilotTabIndex == 1))
        {
            return await FailStartValidationAsync(
                "Copilot.Start.Validate",
                T("Copilot.Error.ValidateTabMismatch", "当前选择的作业与页签不匹配"),
                UiErrorCode.TaskValidationFailed,
                cancellationToken);
        }

        return true;
    }

    private async Task<IReadOnlyList<CopilotItemViewModel>?> ValidateCopilotListSelectionAsync(CancellationToken cancellationToken)
    {
        var checkedItems = Items
            .Where(item => item.IsChecked)
            .ToList();
        if (checkedItems.Count == 0)
        {
            await FailStartValidationAsync(
                "Copilot.Start.List",
                T("Copilot.Error.ListNoChecked", "正在使用「战斗列表」，但未勾选任何作业。"),
                UiErrorCode.CopilotSelectionMissing,
                cancellationToken);
            return null;
        }

        // WPF dev-v2 d6601334f2 对齐：不再按列表项的 tab_index 判定，而是读取作业内容判定类型；
        // 未知类型只记错误日志不阻断，混用两种具体类型才拒绝（其他活动页签跳过类型检查）。
        if (CopilotTabIndex != 3)
        {
            var types = await Task.WhenAll(checkedItems.Select(item => GetCopilotContentTypeAsync(item, cancellationToken)));
            var concreteTypes = types
                .Where(type => type != CopilotListContentType.Unknown)
                .Distinct()
                .ToArray();
            if (types.Any(type => type == CopilotListContentType.Unknown))
            {
                AddLog(
                    T("Copilot.TaskTypeParseFailedSkipCheck", "部分作业类型解析失败，跳过类型检查"),
                    "ERROR",
                    showTime: false);
            }

            if (concreteTypes.Length > 1)
            {
                await FailStartValidationAsync(
                    "Copilot.Start.List",
                    T("Copilot.TaskListMixedModeNotAllowed", "正在使用「战斗列表」，不支持混用不同类型作业")
                        + "\n"
                        + string.Join(", ", concreteTypes.Select(type => GetCopilotTabDisplayName((int)type))),
                    UiErrorCode.TaskValidationFailed,
                    cancellationToken);
                return null;
            }
        }

        // WPF 对齐：页签 2 走 VerifyParadoxTasks（干员名合法性校验），其他页签走 VerifyCopilotListTask（读作业内容 + 地图数据校验关卡），失败拒绝启动
        if (CopilotTabIndex == 2)
        {
            if (!await VerifyParadoxListItemsAsync(checkedItems, cancellationToken))
            {
                return null;
            }
        }
        else
        {
            if (checkedItems.Count == 1)
            {
                AddLog(T("Copilot.Warn.ListSingleItem", "正在使用「战斗列表」执行单个作业, 不推荐此行为。 单个作业请直接运行"), "WARN", showTime: false);
            }

            if (checkedItems.Any(item => string.IsNullOrWhiteSpace(item.Name)))
            {
                await FailStartValidationAsync(
                    "Copilot.Start.List",
                    T("Copilot.Error.ListEmptyStageName", "存在关卡名为空的作业"),
                    UiErrorCode.TaskValidationFailed,
                    cancellationToken);
                return null;
            }

            if (!await VerifyCopilotListStageContentAsync(checkedItems, cancellationToken))
            {
                return null;
            }
        }

        return checkedItems;
    }

    // WPF VerifyCopilotListTask 对齐：逐个读取勾选项的作业内容，stage_name 无法解析或不在地图数据中时拒绝启动
    private async Task<bool> VerifyCopilotListStageContentAsync(
        IReadOnlyList<CopilotItemViewModel> checkedItems,
        CancellationToken cancellationToken)
    {
        var overviewPath = Path.Combine(ResolveCopilotResourceDirectory(), "Arknights-Tile-Pos", "overview.json");
        if (!File.Exists(overviewPath))
        {
            // 有意偏差：地图数据缺失时无法判定关卡合法性，跳过校验（WPF 运行环境资源随包发布，缺失时全部拒绝启动）
            AddLog(
                T("Copilot.Warn.ListStageVerifyNoMapData", "缺少地图数据（resource/Arknights-Tile-Pos/overview.json），跳过战斗列表关卡校验。"),
                "WARN",
                showTime: false);
            return true;
        }

        string overviewJson;
        try
        {
            overviewJson = await File.ReadAllTextAsync(overviewPath, cancellationToken);
        }
        catch (Exception ex)
        {
            AddLog(
                string.Format(T("Copilot.Warn.ListStageVerifyReadFailed", "读取地图数据失败（{0}），跳过战斗列表关卡校验。"), ex.Message),
                "WARN",
                showTime: false);
            return true;
        }

        foreach (var item in checkedItems)
        {
            var stageName = await TryReadListItemStageNameAsync(item, cancellationToken);
            if (!string.IsNullOrEmpty(stageName) && IsKnownStageFromOverviewJson(stageName, overviewJson))
            {
                continue;
            }

            var message = string.Format(
                T("Copilot.Error.ListStageUnsupported", "正在使用「战斗列表」，但作业「{0}」的关卡（{1}）暂不支持，请更新资源后重试"),
                item.Name,
                string.IsNullOrEmpty(stageName) ? item.Name : stageName);
            // WPF VerifyCopilotListTask 对齐：关卡校验失败解锁 MapOutdated 成就
            _ = Runtime.AchievementTrackerService.Unlock("MapOutdated");
            await FailStartValidationAsync(
                "Copilot.Start.List",
                message,
                UiErrorCode.TaskValidationFailed,
                cancellationToken);
            return false;
        }

        return true;
    }

    // WPF VerifyParadoxTasks 对齐：悖论列表逐条校验干员名合法性，非法干员名拒绝启动
    private async Task<bool> VerifyParadoxListItemsAsync(
        IReadOnlyList<CopilotItemViewModel> checkedItems,
        CancellationToken cancellationToken)
    {
        var battleDataPath = Path.Combine(ResolveCopilotResourceDirectory(), "battle_data.json");
        if (!File.Exists(battleDataPath))
        {
            // 有意偏差：干员数据缺失时无法校验，跳过（WPF 运行环境资源随包发布）
            AddLog(
                T("Copilot.Warn.ParadoxVerifyNoBattleData", "缺少干员数据（resource/battle_data.json），跳过悖论作业干员名校验。"),
                "WARN",
                showTime: false);
            return true;
        }

        string battleDataJson;
        try
        {
            battleDataJson = await File.ReadAllTextAsync(battleDataPath, cancellationToken);
        }
        catch (Exception ex)
        {
            AddLog(
                string.Format(T("Copilot.Warn.ParadoxVerifyReadFailed", "读取干员数据失败（{0}），跳过悖论作业干员名校验。"), ex.Message),
                "WARN",
                showTime: false);
            return true;
        }

        foreach (var item in checkedItems)
        {
            if (IsKnownOperatorNameFromBattleDataJson(item.Name, battleDataJson))
            {
                continue;
            }

            var message = string.Format(
                T("Copilot.Error.ParadoxIllegalOperName", "悖论作业「{0}」的干员名不合法，请更新资源后重试"),
                item.Name);
            // WPF VerifyParadoxTasks 对齐：干员名不合法解锁 MapOutdated 成就
            _ = Runtime.AchievementTrackerService.Unlock("MapOutdated");
            await FailStartValidationAsync(
                "Copilot.Start.List",
                message,
                UiErrorCode.TaskValidationFailed,
                cancellationToken);
            return false;
        }

        return true;
    }

    private static async Task<string?> TryReadListItemPayloadAsync(CopilotItemViewModel item, CancellationToken cancellationToken)
    {
        var payload = item.InlinePayload;
        if (string.IsNullOrWhiteSpace(payload))
        {
            var sourcePath = ResolveStoredSourcePath(item.SourcePath);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                // WPF 对齐：作业文件缺失视为校验失败
                return null;
            }

            try
            {
                payload = await File.ReadAllTextAsync(sourcePath, cancellationToken);
            }
            catch
            {
                return null;
            }
        }

        return payload;
    }

    private static async Task<string?> TryReadListItemStageNameAsync(CopilotItemViewModel item, CancellationToken cancellationToken)
    {
        var payload = await TryReadListItemPayloadAsync(item, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(payload) is not JsonObject obj)
            {
                return null;
            }

            var stageName = ReadJsonString(obj, "stage_name");
            return string.IsNullOrEmpty(stageName) ? null : stageName;
        }
        catch
        {
            return null;
        }
    }

    // WPF GetCopilotTypeAsync 对齐（dev-v2 d6601334f2）：列表校验只认作业内容类型，不认 tab_index。
    private async Task<CopilotListContentType> GetCopilotContentTypeAsync(
        CopilotItemViewModel item,
        CancellationToken cancellationToken)
    {
        var payload = await TryReadListItemPayloadAsync(item, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return CopilotListContentType.Unknown;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(payload) as JsonObject;
        }
        catch
        {
            return CopilotListContentType.Unknown;
        }

        if (root is null)
        {
            return CopilotListContentType.Unknown;
        }

        // WPF CopilotContentConverter：type 字段大小写不敏感匹配属性名，值 "SSS" 大小写敏感
        if (string.Equals(ReadJsonStringFirstMatch(root, "type"), "SSS", StringComparison.Ordinal))
        {
            return CopilotListContentType.SecurityServiceStation;
        }

        var stageName = ReadJsonString(root, "stage_name");
        if (string.IsNullOrEmpty(stageName))
        {
            return CopilotListContentType.Unknown;
        }

        var overviewJson = ReadOverviewJsonCached();
        if (string.IsNullOrWhiteSpace(overviewJson))
        {
            return CopilotListContentType.Unknown;
        }

        var (stageId, _) = FindOverviewStage(stageName, overviewJson);
        if (string.IsNullOrEmpty(stageId))
        {
            return CopilotListContentType.Unknown;
        }

        if (stageId.StartsWith("mem_", StringComparison.Ordinal))
        {
            return CopilotListContentType.ParadoxSimulation;
        }

        if (stageId.StartsWith("lt_", StringComparison.Ordinal))
        {
            return CopilotListContentType.SecurityServiceStation;
        }

        if (MainStageIdRegex.IsMatch(stageId) || SideStoryStageIdRegex.IsMatch(stageId))
        {
            return CopilotListContentType.MainStageAndSideStory;
        }

        return CopilotListContentType.Unknown;
    }

    // WPF CopilotType：Unknown = -1，其余取值与页签下标一致（0 主线/故事集、1 保全、2 悖论、3 其他）
    private enum CopilotListContentType
    {
        Unknown = -1,
        MainStageAndSideStory = 0,
        SecurityServiceStation = 1,
        ParadoxSimulation = 2,
    }

    private async Task<bool> FailStartValidationAsync(
        string scope,
        string message,
        string errorCode,
        CancellationToken cancellationToken)
    {
        StatusMessage = T("Copilot.Status.StartFailed", "启动失败。");
        LastErrorMessage = message;
        AddLog(message, "ERROR", showTime: false);
        await RecordFailedResultAsync(
            scope,
            UiOperationResult.Fail(errorCode, message),
            cancellationToken);
        return false;
    }

    private async Task<AppendPlan?> AppendLoadedCopilotAsync(CancellationToken cancellationToken)
    {
        var filePath = await ResolveExecutionFilePathAsync(
            _loadedSourcePath,
            _loadedInlinePayload,
            _loadedDisplayName,
            cancellationToken);
        if (filePath is null)
        {
            return null;
        }

        var displayName = string.IsNullOrWhiteSpace(_loadedDisplayName)
            ? Path.GetFileNameWithoutExtension(filePath)
            : _loadedDisplayName;
        var taskType = string.IsNullOrWhiteSpace(_loadedType)
            ? ResolveTypeDisplayNameForTab(CopilotTabIndex)
            : _loadedType;
        var request = BuildSingleTaskRequest(taskType, displayName, filePath);
        var taskChain = ResolveCopilotTaskChain(taskType);
        var appendResult = await Runtime.ConnectFeatureService.AppendAndStartCoreTaskAsync(
            request,
            cancellationToken,
            taskId => PrepareCopilotStart(taskId, displayName, taskChain));
        if (!appendResult.Success)
        {
            StatusMessage = T("Copilot.Status.StartFailed", "启动失败。");
            LastErrorMessage = string.Format(
                T("Copilot.Error.AppendTaskFailed", "追加 Copilot 任务失败：{0} {1}"),
                appendResult.Error?.Code,
                appendResult.Error?.Message);
            await RecordFailedResultAsync(
                "Copilot.Append.Loaded",
                UiOperationResult.Fail(UiErrorCode.CopilotFileReadFailed, LastErrorMessage),
                cancellationToken);
            return null;
        }

        await RecordEventAsync(
            "Copilot.Append.Loaded",
            $"Appended current copilot task #{appendResult.Value}: {displayName}",
            cancellationToken);
        return new AppendPlan(appendResult.Value, displayName, taskChain);
    }

    private async Task<AppendPlan?> AppendSingleItemAsync(CopilotItemViewModel item, CancellationToken cancellationToken)
    {
        var filePath = await ResolveExecutionFilePathAsync(item, cancellationToken);
        if (filePath is null)
        {
            return null;
        }

        var request = BuildSingleTaskRequest(item.Type, item.Name, filePath);
        var taskChain = ResolveCopilotTaskChain(item.Type);
        var appendResult = await Runtime.ConnectFeatureService.AppendAndStartCoreTaskAsync(
            request,
            cancellationToken,
            taskId => PrepareCopilotStart(taskId, item.Name, taskChain));
        if (!appendResult.Success)
        {
            StatusMessage = T("Copilot.Status.StartFailed", "启动失败。");
            LastErrorMessage = string.Format(
                T("Copilot.Error.AppendTaskFailed", "追加 Copilot 任务失败：{0} {1}"),
                appendResult.Error?.Code,
                appendResult.Error?.Message);
            await RecordFailedResultAsync(
                "Copilot.Append",
                UiOperationResult.Fail(UiErrorCode.CopilotFileReadFailed, LastErrorMessage),
                cancellationToken);
            return null;
        }

        await RecordEventAsync(
            "Copilot.Append",
            $"Appended copilot task #{appendResult.Value}: {item.Name}",
            cancellationToken);
        return new AppendPlan(appendResult.Value, item.Name, taskChain);
    }

    private async Task<AppendPlan?> AppendCopilotListAsync(
        IReadOnlyList<CopilotItemViewModel> checkedItems,
        CancellationToken cancellationToken)
    {
        // WPF 对齐：记录本次列表运行勾选作业的 copilotId，用于战斗成功后的自动点赞去重
        _copilotIdList.Clear();
        foreach (var item in checkedItems)
        {
            // WPF 对齐（4843310bec AsstCopilotTask.MultiTask.Index）：id 取列表全局序号，
            // 而不是本次勾选项内的序号；Core 会原样回报该 id。
            item.Index = Items.IndexOf(item);
            _copilotIdList.Add(item.CopilotId);
        }

        _hasRequirementIgnored = false;

        if (CopilotTabIndex == 2)
        {
            var list = new JsonArray();
            foreach (var item in checkedItems)
            {
                var filePath = await ResolveExecutionFilePathAsync(item, cancellationToken);
                if (filePath is null)
                {
                    return null;
                }

                // WPF 对齐（4843310bec AsstParadoxCopilotTask.MultiTask）：悖论列表同样下发 id
                list.Add(new JsonObject
                {
                    ["id"] = item.Index,
                    ["filename"] = filePath,
                });
            }

            var paradoxRequest = new CoreTaskRequest(
                "ParadoxCopilot",
                checkedItems[0].Name,
                true,
                new JsonObject
                {
                    ["list"] = list,
                }.ToJsonString());
            return await AppendListRequestAsync(paradoxRequest, checkedItems[0].Name, "ParadoxCopilot", cancellationToken);
        }

        var tasks = new JsonArray();
        foreach (var item in checkedItems)
        {
            var filePath = await ResolveExecutionFilePathAsync(item, cancellationToken);
            if (filePath is null)
            {
                return null;
            }

            var listItem = new JsonObject
            {
                ["id"] = item.Index,
                ["filename"] = filePath,
                ["is_raid"] = item.IsRaid,
            };
            // WPF dev-v2 c2021e694a：stage_name 更名为 nav_name_override，且仅在用户覆盖关卡名时下发；
            // Core 只读 nav_name_override，未覆盖时由作业内容自带 stage_name 推导导航关卡。
            if (item.IsNavNameOverride)
            {
                listItem["nav_name_override"] = item.Name;
            }

            tasks.Add(listItem);
        }

        var payload = new JsonObject
        {
            ["copilot_list"] = tasks,
            ["formation"] = Form,
            ["support_unit_usage"] = UseSupportUnitUsage ? SupportUnitUsage : 0,
            ["add_trust"] = AddTrust,
            ["ignore_requirements"] = IgnoreRequirements,
            ["use_sanity_potion"] = ShowUseSanityPotion && UseSanityPotion,
        };
        if (UseFormation)
        {
            payload["formation_index"] = FormationIndex;
        }

        AppendOperBoxDataPath(payload);

        var userAdditional = BuildUserAdditionalPayload();
        if (userAdditional.Count > 0)
        {
            payload["user_additional"] = userAdditional;
        }

        var request = new CoreTaskRequest("Copilot", checkedItems[0].Name, true, payload.ToJsonString());
        return await AppendListRequestAsync(request, checkedItems[0].Name, "Copilot", cancellationToken);
    }

    private async Task<AppendPlan?> AppendListRequestAsync(
        CoreTaskRequest request,
        string activeItemName,
        string taskChain,
        CancellationToken cancellationToken)
    {
        var appendResult = await Runtime.ConnectFeatureService.AppendAndStartCoreTaskAsync(
            request,
            cancellationToken,
            taskId => PrepareCopilotStart(taskId, activeItemName, taskChain));
        if (!appendResult.Success)
        {
            StatusMessage = T("Copilot.Status.StartFailed", "启动失败。");
            LastErrorMessage = string.Format(
                T("Copilot.Error.AppendTaskFailed", "追加 Copilot 任务失败：{0} {1}"),
                appendResult.Error?.Code,
                appendResult.Error?.Message);
            await RecordFailedResultAsync(
                "Copilot.Append.List",
                UiOperationResult.Fail(UiErrorCode.CopilotFileReadFailed, LastErrorMessage),
                cancellationToken);
            return null;
        }

        await RecordEventAsync(
            "Copilot.Append.List",
            $"Appended copilot list task #{appendResult.Value}: {activeItemName}",
            cancellationToken);
        return new AppendPlan(appendResult.Value, activeItemName, taskChain);
    }

    private CoreTaskRequest BuildSingleTaskRequest(string type, string displayName, string filePath)
    {
        var taskType = ResolveCopilotTaskType(type);
        if (string.Equals(taskType, "ParadoxCopilot", StringComparison.Ordinal))
        {
            return new CoreTaskRequest(
                taskType,
                displayName,
                true,
                new JsonObject
                {
                    ["filename"] = filePath,
                }.ToJsonString());
        }

        var payload = new JsonObject
        {
            ["filename"] = filePath,
            ["formation"] = Form,
            ["support_unit_usage"] = UseSupportUnitUsage ? SupportUnitUsage : 0,
            ["add_trust"] = AddTrust,
            ["ignore_requirements"] = IgnoreRequirements,
            // WPF 对齐（冻结 CopilotViewModel.cs:2048 LoopTimes = Loop ? LoopTimes : 1）：不按页签门控；
            // 循环控件虽仅在保全(1)/其他活动(3)页签可见，但 Loop 值跨页签保留，切回主线页签启动单作业
            // 时 WPF 仍发送隐藏的 Loop 状态
            ["loop_times"] = Loop ? LoopTimes : 1,
            ["use_sanity_potion"] = false,
        };
        if (UseFormation)
        {
            payload["formation_index"] = FormationIndex;
        }

        AppendOperBoxDataPath(payload);

        var userAdditional = BuildUserAdditionalPayload();
        if (userAdditional.Count > 0)
        {
            payload["user_additional"] = userAdditional;
        }

        return new CoreTaskRequest(taskType, displayName, true, payload.ToJsonString());
    }

    private async Task<string?> ResolveExecutionFilePathAsync(
        string sourcePath,
        string inlinePayload,
        string displayName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedSourcePath = ResolveStoredSourcePath(sourcePath);
        if (!string.IsNullOrWhiteSpace(resolvedSourcePath))
        {
            if (!File.Exists(resolvedSourcePath))
            {
                // 来源文件缺失但列表项仍持有入列时的 InlinePayload 快照（本地文件批量导入同持两者）时
                // 回退快照执行，避免一次性缓存路径/已移动文件遮蔽完好的内联内容；
                // 无快照可回退才按 WPF 语义报「作业文件不存在」
                if (!string.IsNullOrWhiteSpace(inlinePayload))
                {
                    var correctedSnapshot = CanonicalizeCopilotPayload(inlinePayload, LoadBattleDataJson(), out var snapshotWarnings);
                    AppendCopilotCanonicalizationWarnings(snapshotWarnings);
                    return await WriteExecutionCacheFileAsync(correctedSnapshot, displayName, cancellationToken);
                }

                StatusMessage = T("Copilot.Status.StartFailed", "启动失败。");
                LastErrorMessage = string.Format(
                    T("Copilot.Error.StartInputFileNotFound", "作业文件不存在：{0}"),
                    resolvedSourcePath);
                await RecordFailedResultAsync(
                    "Copilot.Start.Input",
                    UiOperationResult.Fail(UiErrorCode.CopilotFileNotFound, LastErrorMessage),
                    cancellationToken);
                return null;
            }

            // WPF ParseCopilotAsync 对齐：执行前按稀有度校验并修正技能，修正后的作业写缓存文件执行
            var sourcePayload = SafeReadAllText(resolvedSourcePath);
            if (string.IsNullOrWhiteSpace(sourcePayload))
            {
                return resolvedSourcePath;
            }

            var correctedSource = CanonicalizeCopilotPayload(sourcePayload, LoadBattleDataJson(), out var sourceWarnings);
            AppendCopilotCanonicalizationWarnings(sourceWarnings);
            if (string.Equals(correctedSource, sourcePayload, StringComparison.Ordinal))
            {
                return resolvedSourcePath;
            }

            return await WriteExecutionCacheFileAsync(correctedSource, displayName, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(inlinePayload))
        {
            StatusMessage = T("Copilot.Status.StartFailed", "启动失败。");
            LastErrorMessage = T("Copilot.Error.StartInputMissingSource", "当前作业缺少可执行来源（文件路径或 JSON 内容）。");
            await RecordFailedResultAsync(
                "Copilot.Start.Input",
                UiOperationResult.Fail(UiErrorCode.CopilotFileMissing, LastErrorMessage),
                cancellationToken);
            return null;
        }

        var correctedInline = CanonicalizeCopilotPayload(inlinePayload, LoadBattleDataJson(), out var inlineWarnings);
        AppendCopilotCanonicalizationWarnings(inlineWarnings);
        return await WriteExecutionCacheFileAsync(correctedInline, displayName, cancellationToken);
    }

    private async Task<string> WriteExecutionCacheFileAsync(
        string payload,
        string displayName,
        CancellationToken cancellationToken)
    {
        var debugDirectory = Path.GetDirectoryName(Runtime.DiagnosticsService.EventLogPath)
            ?? Path.Combine(RuntimeLayout.ResolveRuntimeBaseDirectory(), "debug");
        var directory = Path.Combine(debugDirectory, "copilot-cache");
        Directory.CreateDirectory(directory);
        var baseName = string.IsNullOrWhiteSpace(displayName) ? "copilot-inline" : displayName;
        var fileName = string.Concat(baseName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var filePath = Path.Combine(directory, $"{fileName}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.json");
        await File.WriteAllTextAsync(filePath, payload, cancellationToken);
        return filePath;
    }

    private string? LoadBattleDataJson()
    {
        var battleDataJson = ReadBattleDataJsonCached();
        return string.IsNullOrWhiteSpace(battleDataJson) ? null : battleDataJson;
    }

    private void AppendCopilotCanonicalizationWarnings(IReadOnlyList<CopilotCanonicalizationWarning> warnings)
    {
        foreach (var warning in warnings)
        {
            var (message, level) = warning.Kind switch
            {
                CopilotCanonicalizationWarningKind.UnsupportedSkill => (
                    string.Format(
                        CultureInfo.InvariantCulture,
                        T("Copilot.UnsupportedSkill", "{0} 不支持技能 {1}, 不再选择技能"),
                        warning.Name,
                        warning.Skill),
                    "WARN"),
                CopilotCanonicalizationWarningKind.EliteEmpty => (
                    string.Format(
                        CultureInfo.InvariantCulture,
                        T("Copilot.EliteEmpty", "{0} 未设置精英等级要求, 但其他属性要求 精{1}, 已自动补全"),
                        warning.Name,
                        warning.RequiredElite),
                    "INFO"),
                CopilotCanonicalizationWarningKind.EliteAdjust => (
                    string.Format(
                        CultureInfo.InvariantCulture,
                        T("Copilot.EliteAdjust", "{0} 设置为要求 精{1}, 但是其他属性要求 精{2}, 已自动调整"),
                        warning.Name,
                        warning.PreviousElite,
                        warning.RequiredElite),
                    "WARN"),
                CopilotCanonicalizationWarningKind.ActionWithBothLocationAndOperator => (
                    string.Format(
                        CultureInfo.InvariantCulture,
                        T("Copilot.ActionWithBothLocAndOper", "{0} 同时设置了位置和干员, 将按位置执行"),
                        warning.ActionDetail),
                    "WARN"),
                CopilotCanonicalizationWarningKind.ActionWithBothRectAndLocation => (
                    string.Format(
                        CultureInfo.InvariantCulture,
                        T("Copilot.ActionWithBothRectAndLoc", "{0} 同时设置了像素区域和位置, 将按像素区域执行"),
                        warning.ActionDetail),
                    "WARN"),
                _ => (string.Empty, "WARN"),
            };
            if (!string.IsNullOrEmpty(message))
            {
                AddLog(message, level, showTime: false);
            }
        }
    }

    // WPF ParseCopilotAsync（dev-v2）对齐：执行前对 payload 做一次集中归一化——
    // 技能默认值 0、按稀有度清零不支持技能（阿米娅技能 3 例外）、按技能/等级/模组推导精英化要求、
    // 动作同填干员与坐标时保留坐标（清 name/role）、点击动作同填 rect 与 location 时保留 rect。
    // 只改写命中的字段，未知字段原样透传；SSS 作业 WPF 不做这些校验，整份跳过。
    internal static string CanonicalizeCopilotPayload(
        string payload,
        string? battleDataJson,
        out IReadOnlyList<CopilotCanonicalizationWarning> warnings)
    {
        warnings = [];
        if (string.IsNullOrWhiteSpace(payload))
        {
            return payload;
        }

        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(payload);
        }
        catch
        {
            return payload;
        }

        // SSS 判定对齐冻结 CopilotHelper.cs:227：值大小写敏感（"sss" 按普通作业处理并校验技能）；
        // 大小写变体重复键取插入序首个匹配（JObject.TryGetValue OrdinalIgnoreCase 语义）
        if (rootNode is not JsonObject root
            || string.Equals(ReadJsonStringFirstMatch(root, "type"), "SSS", StringComparison.Ordinal))
        {
            return payload;
        }

        var collected = new List<CopilotCanonicalizationWarning>();
        var changed = false;
        var hasBattleData = !string.IsNullOrWhiteSpace(battleDataJson);
        foreach (var oper in EnumerateCopilotOpers(root))
        {
            var name = ReadJsonString(oper, "name");
            var skill = ReadJsonInt(oper, "skill") ?? 0;
            if (hasBattleData)
            {
                var operatorInfo = FindOperatorInfoByName(name, battleDataJson);
                var rarity = operatorInfo?.Rarity ?? -1;
                var operatorId = operatorInfo?.Id ?? string.Empty;
                // skill3 对 6★ 与阿米娅（char_002_amiya）放行；稀有度未知按 -1 处理（WPF `?? -1`）
                if ((skill == 3 && rarity < 6 && !string.Equals(operatorId, "char_002_amiya", StringComparison.Ordinal))
                    || (skill == 2 && rarity < 4)
                    || (skill == 1 && rarity < 3))
                {
                    collected.Add(new CopilotCanonicalizationWarning(
                        CopilotCanonicalizationWarningKind.UnsupportedSkill,
                        name,
                        Skill: skill));
                    oper["skill"] = 0;
                    skill = 0;
                    changed = true;
                }
            }

            if (!TryGetJsonProperty(oper, "requirements", out var requirementsNode))
            {
                var requirements = new JsonObject();
                oper["requirements"] = requirements;
                if (TryDeriveEliteRequirement(skill, requirements, out var requiredElite))
                {
                    requirements["elite"] = requiredElite;
                    collected.Add(new CopilotCanonicalizationWarning(
                        CopilotCanonicalizationWarningKind.EliteEmpty,
                        name,
                        RequiredElite: requiredElite));
                    changed = true;
                }
                else
                {
                    oper.Remove("requirements");
                }

                continue;
            }

            if (requirementsNode is not JsonObject requirementsObject)
            {
                // requirements 非对象时保持原样（未知结构透传，不猜测语义）
                continue;
            }

            if (TryDeriveEliteRequirement(skill, requirementsObject, out var eliteRequirement))
            {
                var currentElite = ReadJsonInt(requirementsObject, "elite") ?? 0;
                if (currentElite < eliteRequirement)
                {
                    collected.Add(new CopilotCanonicalizationWarning(
                        CopilotCanonicalizationWarningKind.EliteAdjust,
                        name,
                        PreviousElite: currentElite,
                        RequiredElite: eliteRequirement));
                    requirementsObject["elite"] = eliteRequirement;
                    changed = true;
                }
            }
        }

        changed |= CanonicalizeCopilotActions(root, collected);

        if (!changed)
        {
            return payload;
        }

        warnings = collected;
        return root.ToJsonString();
    }

    // WPF ParseCopilotAsync：skillElite = skill - 1；skill_level <=4/<=7/<=10 → 0/1/2；
    // module > 0 → 2；取三者最大值。0 表示无需推导。
    private static bool TryDeriveEliteRequirement(int skill, JsonObject requirements, out int elite)
    {
        var skillElite = skill - 1;
        var skillLevelElite = (ReadJsonInt(requirements, "skill_level") ?? 0) switch
        {
            <= 4 => 0,
            <= 7 => 1,
            <= 10 => 2,
            _ => 0,
        };
        var moduleElite = (ReadJsonInt(requirements, "module") ?? -1) > 0 ? 2 : 0;
        elite = Math.Max(skillElite, Math.Max(skillLevelElite, moduleElite));
        return elite > 0;
    }

    private static bool CanonicalizeCopilotActions(
        JsonObject root,
        List<CopilotCanonicalizationWarning> warnings)
    {
        if (!TryGetJsonProperty(root, "actions", out var actionsNode) || actionsNode is not JsonArray actions)
        {
            return false;
        }

        var changed = false;
        foreach (var action in actions.OfType<JsonObject>())
        {
            var type = ReadJsonString(action, "type");
            if (type is "Skill" or "Retreat" or "BulletTime" or "SkillUsage")
            {
                // Core 对同填 name 与 location 的动作按位置执行；role 仅在配合 name 时有意义，一并移除。
                var hasLocation = TryGetJsonProperty(action, "location", out var locationNode) && locationNode is not null;
                var hasName = TryGetJsonProperty(action, "name", out var nameNode) && nameNode is not null;
                if (hasLocation && hasName)
                {
                    warnings.Add(new CopilotCanonicalizationWarning(
                        CopilotCanonicalizationWarningKind.ActionWithBothLocationAndOperator,
                        string.Empty,
                        ActionDetail: $"{type}[{FormatActionCoordinates(locationNode)}]"));
                    action.Remove("role");
                    action.Remove("name");
                    changed = true;
                }
            }
            else if (type == "Click")
            {
                // Core 对同填 rect 与 location 的点击动作按 rect 执行，移除 location 以与 Core 语义一致。
                var hasRect = TryGetJsonProperty(action, "rect", out var rectNode) && rectNode is not null;
                var hasLocation = TryGetJsonProperty(action, "location", out var locationNode) && locationNode is not null;
                if (hasRect && hasLocation)
                {
                    warnings.Add(new CopilotCanonicalizationWarning(
                        CopilotCanonicalizationWarningKind.ActionWithBothRectAndLocation,
                        string.Empty,
                        ActionDetail: $"{type}[{FormatActionCoordinates(rectNode)}]"));
                    action.Remove("location");
                    changed = true;
                }
            }
        }

        return changed;
    }

    private static string FormatActionCoordinates(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return node?.ToJsonString() ?? string.Empty;
        }

        return string.Join(
            ",",
            array.Select(item => item is JsonValue value && value.TryGetValue(out int coordinate)
                ? coordinate.ToString(CultureInfo.InvariantCulture)
                : item?.ToJsonString() ?? string.Empty));
    }

    internal enum CopilotCanonicalizationWarningKind
    {
        UnsupportedSkill,
        EliteEmpty,
        EliteAdjust,
        ActionWithBothLocationAndOperator,
        ActionWithBothRectAndLocation,
    }

    internal sealed record CopilotCanonicalizationWarning(
        CopilotCanonicalizationWarningKind Kind,
        string Name,
        int Skill = 0,
        int PreviousElite = 0,
        int RequiredElite = 0,
        string ActionDetail = "");

    private static IEnumerable<JsonObject> EnumerateCopilotOpers(JsonObject root)
    {
        if (TryGetJsonProperty(root, "opers", out var opersNode) && opersNode is JsonArray opers)
        {
            foreach (var oper in opers.OfType<JsonObject>())
            {
                yield return oper;
            }
        }

        if (TryGetJsonProperty(root, "groups", out var groupsNode) && groupsNode is JsonArray groups)
        {
            foreach (var group in groups.OfType<JsonObject>())
            {
                if (TryGetJsonProperty(group, "opers", out var groupOpersNode) && groupOpersNode is JsonArray groupOpers)
                {
                    foreach (var oper in groupOpers.OfType<JsonObject>())
                    {
                        yield return oper;
                    }
                }
            }
        }
    }

    // WPF DataHelper.GetCharacterByNameOrAlias 对齐：按任意服务器干员名查稀有度与干员 id
    internal static CopilotOperatorInfo? FindOperatorInfoByName(string operName, string? battleDataJson)
    {
        if (string.IsNullOrWhiteSpace(operName) || string.IsNullOrWhiteSpace(battleDataJson))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(battleDataJson) is not JsonObject root
                || !root.TryGetPropertyValue("chars", out var charsNode)
                || charsNode is not JsonObject chars)
            {
                return null;
            }

            foreach (var pair in chars)
            {
                if (!pair.Key.StartsWith("char_", StringComparison.Ordinal) || pair.Value is not JsonObject charObj)
                {
                    continue;
                }

                var profession = ReadJsonString(charObj, "profession");
                if (!string.IsNullOrEmpty(profession) && !OperatorProfessions.Contains(profession))
                {
                    continue;
                }

                // WPF DataHelper 干员名索引为 OrdinalIgnoreCase（BuildNameIndex）
                if (string.Equals(ReadJsonString(charObj, "name"), operName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ReadJsonString(charObj, "name_tw"), operName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ReadJsonString(charObj, "name_en"), operName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ReadJsonString(charObj, "name_jp"), operName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ReadJsonString(charObj, "name_kr"), operName, StringComparison.OrdinalIgnoreCase))
                {
                    return new CopilotOperatorInfo(ReadJsonInt(charObj, "rarity"), pair.Key);
                }
            }
        }
        catch
        {
            // 干员数据损坏时按未命中处理
        }

        return null;
    }

    internal sealed record CopilotOperatorInfo(int? Rarity, string Id);

    // 兼容既有测试/调用：仅取稀有度
    internal static int? FindOperatorRarityByName(string operName, string? battleDataJson)
        => FindOperatorInfoByName(operName, battleDataJson)?.Rarity;

    private string GetCopilotTabDisplayName(int tabIndex)
    {
        return tabIndex switch
        {
            1 => T("Copilot.Tab.Display.Security", "保全派驻"),
            2 => T("Copilot.Tab.Display.Paradox", "悖论模拟"),
            3 => T("Copilot.Tab.Display.Other", "其他活动"),
            _ => T("Copilot.Tab.Display.Main", "主线/故事集/SideStory"),
        };
    }

    private int ResolveTabIndexFromType(string type)
    {
        var normalized = NormalizeTypeDisplayName(type);
        for (var i = 0; i < Types.Count; i++)
        {
            if (string.Equals(Types[i], normalized, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }

    private sealed record LoadedCopilotDescriptor(string DisplayName, string Type, string StageName, int CopilotId);

    private sealed record StartSelection(IReadOnlyList<CopilotItemViewModel>? CheckedItems);

    private sealed record AppendPlan(int TaskId, string ActiveItemName, string TaskChain);

    public sealed class CopilotFileItemViewModel : ObservableObject
    {
        private bool _isExpanded;

        public CopilotFileItemViewModel(string name, string fullPath, string relativePath, bool isFolder)
        {
            Name = name;
            FullPath = fullPath;
            RelativePath = relativePath;
            IsFolder = isFolder;
        }

        public string Name { get; }

        public string FullPath { get; }

        public string RelativePath { get; }

        public bool IsFolder { get; }

        public bool CanSelect => !IsFolder;

        public bool HasChildren => Children.Count > 0;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value))
                {
                    OnPropertyChanged(nameof(ExpandGlyph));
                }
            }
        }

        public string ExpandGlyph => IsExpanded ? "▼" : "▶";

        public ObservableCollection<CopilotFileItemViewModel> Children { get; } = [];

        public override string ToString() => Name;
    }

    public sealed class CopilotUserAdditionalItemViewModel : ObservableObject
    {
        private string _name = string.Empty;
        private int _skill;
        private int _module;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value ?? string.Empty);
        }

        public int Skill
        {
            get => _skill;
            // WPF dev-v2 b2d8591974：0 = 不指定技能
            set => SetProperty(ref _skill, Math.Clamp(value, 0, 3));
        }

        public int Module
        {
            get => _module;
            // WPF dev-v2 6341749473：模组编号允许 5（β）
            set => SetProperty(ref _module, Math.Clamp(value, 0, 5));
        }
    }

    public sealed record IntOption(int Value, string DisplayName);
}

file static class CopilotCollectionExtensions
{
    public static int RemoveAll<T>(this ObservableCollection<T> collection, Func<T, bool> predicate)
    {
        var removed = 0;
        for (var i = collection.Count - 1; i >= 0; i--)
        {
            if (!predicate(collection[i]))
            {
                continue;
            }

            collection.RemoveAt(i);
            removed++;
        }

        return removed;
    }
}
