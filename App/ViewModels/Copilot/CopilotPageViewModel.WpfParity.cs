using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using MAAUnified.Application.Models;
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
    private static readonly Regex InvalidNavigationStageNameRegex = new(
        "[:',\\.\\(\\)\\|\\[\\]\\?，。【】｛｝；：]",
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

    public bool CanEdit => !IsRunning;

    public bool ShowClipboardSetButton => true;

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
                PersistGlobalSetting(LegacyConfigurationKeys.CopilotSelectFormation, FormationIndex.ToString());
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
                PersistGlobalSetting(LegacyConfigurationKeys.CopilotSelectFormation, normalized.ToString());
                OnPropertyChanged(nameof(SelectedFormationOption));
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
                PersistGlobalSetting(LegacyConfigurationKeys.CopilotSupportUnitUsage, normalized.ToString());
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
                PersistGlobalSetting(LegacyConfigurationKeys.CopilotAddUserAdditional, value.ToString());
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
                PersistGlobalSetting(LegacyConfigurationKeys.CopilotUserAdditional, normalized);
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

    public bool ShowCopilotListPanel
        => (UseCopilotList && CopilotTabIndex is 0 or 2) || CopilotTabIndex is 1 or 3;

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
            var normalized = Math.Max(1, value);
            if (SetProperty(ref _loopTimes, normalized))
            {
                PersistGlobalSetting(LegacyConfigurationKeys.CopilotLoopTimes, normalized.ToString());
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
        _formationIndex = ReadGlobalInt(LegacyConfigurationKeys.CopilotSelectFormation, 1, 1, 4);
        _supportUnitUsage = ReadGlobalInt(LegacyConfigurationKeys.CopilotSupportUnitUsage, 1, 1, 3) == 3 ? 3 : 1;
        _loopTimes = ReadGlobalInt(LegacyConfigurationKeys.CopilotLoopTimes, 1, 1, 9999);
        _addUserAdditional = ReadGlobalBool(LegacyConfigurationKeys.CopilotAddUserAdditional, false);
        _userAdditional = ReadGlobalString(LegacyConfigurationKeys.CopilotUserAdditional);
    }

    private int ReadGlobalInt(string key, int fallback, int min, int max)
    {
        if (!Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(key, out var node)
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

    private bool ReadGlobalBool(string key, bool fallback)
    {
        if (!Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(key, out var node)
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

    private string ReadGlobalString(string key)
    {
        if (!Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(key, out var node)
            || node is not JsonValue jsonValue
            || !jsonValue.TryGetValue(out string? raw))
        {
            return string.Empty;
        }

        return raw?.Trim() ?? string.Empty;
    }

    private void PersistGlobalSetting(string key, string value)
    {
        Runtime.ConfigurationService.CurrentConfig.GlobalValues[key] = JsonValue.Create(value);
        _ = PersistGlobalSettingCoreAsync(key, value);
    }

    private async Task PersistGlobalSettingCoreAsync(string key, string value)
    {
        _ = await RunTrackedConfigurationSaveAsync(
            $"Copilot.Config.{key}",
            Texts.GetOrDefault("Copilot.Title", "抄作业"),
            $"Config.{key}.Save",
            ct => Runtime.SettingsFeatureService.SaveGlobalSettingAsync(key, value, ct));
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

        if (LooksLikeCopilotCodeSource(raw))
        {
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

        FilePath = expanded;
        StatusMessage = T("Copilot.Status.InputUpdated", "已更新作业输入。");
        LastErrorMessage = string.Empty;
    }

    public async Task LoadCurrentFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
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
        var normalized = (payload ?? string.Empty).Trim();
        if (LooksLikeCopilotCodeSource(normalized))
        {
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
        if (LooksLikeCopilotCodeSource(normalizedPayload))
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

                if (descriptor.CopilotId <= 0)
                {
                    descriptor = descriptor with { CopilotId = remote.CopilotId };
                }

                Items.Add(CreateListItemFromDescriptor(descriptor, string.Empty, remote.PayloadJson, isRaid: false));
                remoteAdded++;
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

            var item = CreateListItemFromDescriptor(descriptor, string.Empty, json, isRaid: false);
            Items.Add(item);
            added++;
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

    public async Task ImportFilesToListAsync(IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

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
                return;
            }

            var payload = await File.ReadAllTextAsync(normalizedPath, cancellationToken);
            if (!TryReadLoadedCopilotDescriptor(payload, normalizedPath, out var descriptor, out _))
            {
                continue;
            }

            Items.Add(CreateListItemFromDescriptor(descriptor, normalizedPath, string.Empty, isRaid: false));
            added++;
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
        var item = CreateListItemFromDescriptor(descriptor, _loadedSourcePath, _loadedInlinePayload, isRaid);
        item.Name = ResolveListItemName(CopilotTaskName, descriptor.StageName);
        Items.Add(item);
        SetSelectedItemSilently(item);
        UseCopilotList = CopilotTabIndex is 0 or 2;
        await PersistItemsAsync(cancellationToken);
        StatusMessage = isRaid
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
                ["module"] = Math.Clamp(item.Module, 0, 4),
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

        var result = await Runtime.CopilotFeatureService.SubmitFeedbackAsync(copilotId, like, cancellationToken);
        if (!result.Success)
        {
            StatusMessage = T("Copilot.Status.FeedbackFailed", "反馈失败。");
            LastErrorMessage = result.Message;
            await RecordFailedResultAsync("Copilot.Feedback.Loaded", result, cancellationToken);
            return;
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
        if (!ShowCopilotListPanel || !HasLoadedCopilot || _suppressAutoAddLoadedCopilot)
        {
            return;
        }

        // Match WPF behavior: battle-list auto append only applies to clipboard/web payloads,
        // while local files stay as the currently loaded copilot.
        if (string.IsNullOrWhiteSpace(_loadedInlinePayload))
        {
            return;
        }

        await AddCurrentToListAsync(isRaid: false, cancellationToken);
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
        SetCopilotTaskNameFromLoadedCopilot(descriptor.StageName);
        CopilotUrl = PrtsPlusUrl;
        MapUrl = MapPrtsUrl;
        CouldLikeWebJson = descriptor.CopilotId > 0;
        OnPropertyChanged(nameof(HasLoadedCopilot));
        OnPropertyChanged(nameof(LoadedCopilotInputHint));
    }

    private void ClearLoadedCopilot()
    {
        _loadedSourcePath = string.Empty;
        _loadedInlinePayload = string.Empty;
        _loadedDisplayName = string.Empty;
        _loadedStageName = string.Empty;
        _loadedType = ResolveTypeDisplayNameForTab(CopilotTabIndex);
        _loadedCopilotId = 0;
        FilePath = string.Empty;
        DisplayFilename = string.Empty;
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

    private static string ResolveCopilotResourceRoot()
    {
        return Path.Combine(RuntimeLayout.ResolveRuntimeBaseDirectory(), "resource", "copilot");
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

    private static bool LooksLikeCopilotCodeSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var normalized = source.Trim();
        if (int.TryParse(normalized, out _))
        {
            return true;
        }

        if (!normalized.StartsWith(CopilotIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = normalized[CopilotIdPrefix.Length..].TrimStart('/');
        return int.TryParse(remainder, out _);
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
        var rawType = ReadJsonString(obj, "type");
        var type = string.IsNullOrWhiteSpace(rawType)
            ? ResolveTypeDisplayNameForTab(CopilotTabIndex)
            : NormalizeTypeDisplayName(rawType);

        descriptor = new LoadedCopilotDescriptor(
            string.IsNullOrWhiteSpace(displayName) ? $"Imported-{DateTime.Now:HHmmss}" : displayName.Trim(),
            type,
            string.IsNullOrWhiteSpace(stageName) ? displayName.Trim() : stageName.Trim(),
            ReadJsonInt(obj, "copilot_id") ?? ReadJsonInt(obj, "id") ?? 0);
        return true;
    }

    private static string ReadJsonString(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node)
            || node is not JsonValue jsonValue
            || !jsonValue.TryGetValue(out string? raw))
        {
            return string.Empty;
        }

        return raw?.Trim() ?? string.Empty;
    }

    private static int? ReadJsonInt(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node)
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

    private CopilotItemViewModel CreateListItemFromDescriptor(
        LoadedCopilotDescriptor descriptor,
        string sourcePath,
        string inlinePayload,
        bool isRaid)
    {
        return new CopilotItemViewModel(
            ResolveListItemName(descriptor.DisplayName, descriptor.StageName),
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
            var fallbackItems = new List<CopilotUserAdditionalItemViewModel>();
            foreach (var segment in UserAdditional.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = segment.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
                {
                    continue;
                }

                fallbackItems.Add(new CopilotUserAdditionalItemViewModel
                {
                    Name = parts[0],
                    Skill = parts.Length > 1 && int.TryParse(parts[1], out var skill) ? Math.Clamp(skill, 0, 3) : 0,
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
                Module = Math.Clamp(ReadJsonInt(item, "module") ?? 0, 0, 4),
            });
        }

        return parsedItems;
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
                ["module"] = Math.Clamp(item.Module, 0, 4),
            });
        }

        return result;
    }

    private async Task<AppendPlan?> AppendConfiguredCopilotAsync(CancellationToken cancellationToken)
    {
        if (ShowCopilotListPanel)
        {
            var checkedItems = await ValidateCopilotListSelectionAsync(cancellationToken);
            if (checkedItems is null)
            {
                return null;
            }

            return await AppendCopilotListAsync(checkedItems, cancellationToken);
        }

        if (!await ValidateSingleSelectionAsync(cancellationToken))
        {
            return null;
        }

        if (HasLoadedCopilot)
        {
            return await AppendLoadedCopilotAsync(cancellationToken);
        }

        if (SelectedItem is not null)
        {
            return await AppendSingleItemAsync(SelectedItem, cancellationToken);
        }

        StatusMessage = T("Copilot.Status.StartFailed", "启动失败。");
        LastErrorMessage = T("Copilot.Error.StartSelectTask", "请选择要执行的作业。");
        await RecordFailedResultAsync(
            "Copilot.Start",
            UiOperationResult.Fail(UiErrorCode.CopilotSelectionMissing, LastErrorMessage),
            cancellationToken);
        return null;
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

        if (checkedItems.Any(item => item.TabIndex is null))
        {
            await FailStartValidationAsync(
                "Copilot.Start.List",
                T("Copilot.Error.ListLegacyMissingTab", "正在使用「战斗列表」，但列表包含旧版本条目（缺少页签信息），请在正确的页签重新添加这些作业后再启动"),
                UiErrorCode.TaskValidationFailed,
                cancellationToken);
            return null;
        }

        var tabs = checkedItems
            .Select(item => item.TabIndex!.Value)
            .Distinct()
            .ToArray();
        if (tabs.Length > 1)
        {
            await FailStartValidationAsync(
                "Copilot.Start.List",
                T("Copilot.Error.ListMixedTabs", "正在使用「战斗列表」，但不允许混用「主线/故事集/SideStory」与「悖论模拟」，请分别在对应页签建立列表后再启动"),
                UiErrorCode.TaskValidationFailed,
                cancellationToken);
            return null;
        }

        var listTab = tabs[0];
        if (listTab != CopilotTabIndex)
        {
            await FailStartValidationAsync(
                "Copilot.Start.List",
                string.Format(
                    T("Copilot.Error.ListTabMismatch", "正在使用「战斗列表」，当前页签为「{0}」，但列表来自「{1}」，请切换到对应页签后再启动"),
                    GetCopilotTabDisplayName(CopilotTabIndex),
                    GetCopilotTabDisplayName(listTab)),
                UiErrorCode.TaskValidationFailed,
                cancellationToken);
            return null;
        }

        if (CopilotTabIndex != 2)
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
        }

        return checkedItems;
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
        var appendResult = await Runtime.CoreBridge.AppendTaskAsync(request, cancellationToken);
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
        return new AppendPlan(appendResult.Value, displayName, ResolveCopilotTaskChain(taskType));
    }

    private async Task<AppendPlan?> AppendSingleItemAsync(CopilotItemViewModel item, CancellationToken cancellationToken)
    {
        var filePath = await ResolveExecutionFilePathAsync(item, cancellationToken);
        if (filePath is null)
        {
            return null;
        }

        var request = BuildSingleTaskRequest(item.Type, item.Name, filePath);
        var appendResult = await Runtime.CoreBridge.AppendTaskAsync(request, cancellationToken);
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
        return new AppendPlan(appendResult.Value, item.Name, ResolveCopilotTaskChain(item.Type));
    }

    private async Task<AppendPlan?> AppendCopilotListAsync(
        IReadOnlyList<CopilotItemViewModel> checkedItems,
        CancellationToken cancellationToken)
    {
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

                list.Add(filePath);
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

            tasks.Add(new JsonObject
            {
                ["filename"] = filePath,
                ["stage_name"] = item.Name,
                ["is_raid"] = item.IsRaid,
            });
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
        var appendResult = await Runtime.CoreBridge.AppendTaskAsync(request, cancellationToken);
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
            ["loop_times"] = ShowLoopSetting && Loop ? LoopTimes : 1,
            ["use_sanity_potion"] = false,
        };
        if (UseFormation)
        {
            payload["formation_index"] = FormationIndex;
        }

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

            return resolvedSourcePath;
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

        var debugDirectory = Path.GetDirectoryName(Runtime.DiagnosticsService.EventLogPath)
            ?? Path.Combine(RuntimeLayout.ResolveRuntimeBaseDirectory(), "debug");
        var directory = Path.Combine(debugDirectory, "copilot-cache");
        Directory.CreateDirectory(directory);
        var baseName = string.IsNullOrWhiteSpace(displayName) ? "copilot-inline" : displayName;
        var fileName = string.Concat(baseName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var filePath = Path.Combine(directory, $"{fileName}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.json");
        await File.WriteAllTextAsync(filePath, inlinePayload, cancellationToken);
        return filePath;
    }

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
            set => SetProperty(ref _skill, Math.Clamp(value, 0, 3));
        }

        public int Module
        {
            get => _module;
            set => SetProperty(ref _module, Math.Clamp(value, 0, 4));
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
