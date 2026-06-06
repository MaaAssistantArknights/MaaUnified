using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.CoreBridge;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.App.ViewModels.Copilot;

internal readonly record struct CopilotCallbackPayload(
    string? TaskChain,
    string? SubTask,
    int? TaskId,
    string? What = null,
    string? Why = null,
    JsonObject? Details = null,
    JsonObject? Root = null,
    string? ParseError = null)
{
    public static CopilotCallbackPayload Empty { get; } = new(null, null, null, null, null, null, null, null);

    public bool HasParseError => !string.IsNullOrWhiteSpace(ParseError);
}

public sealed partial class CopilotPageViewModel : PageViewModelBase
{
    private const string CopilotTaskListConfigScope = "Config.Copilot.CopilotTaskList";
    private const string CopilotRunOwner = "Copilot";
    private const string MainStageStoryCollectionSideStoryType = "主线/故事集/SideStory";
    private const string SecurityServiceStationType = "保全派驻";
    private const string ParadoxSimulationType = "悖论模拟";
    private const string OtherActivityType = "其他活动";

    private string _filePath = string.Empty;
    private int _selectedTypeIndex;
    private bool _autoSquad = true;
    private bool _useSupportUnit;
    private bool _addTrust;
    private bool _overlayEnabled;
    private SessionState _currentSessionState;
    private string? _activeItemName;
    private int? _activeItemCoreTaskId;
    private string? _activeTaskChain;
    private bool _hasActiveRun;
    private bool _isStartRequestActive;
    private CopilotItemViewModel? _selectedItem;
    private bool _suppressSelectionFeedback;
    private bool _suppressSelectedTypeListItemSync;
    private int _selectedTypePersistVersion;
    private RootLocalizationTextMap _rootTexts;
    private CopilotLocalizationTextMap _texts;
    private string _currentLanguage = UiLanguageCatalog.DefaultLanguage;
    private readonly IUiLanguageCoordinator _uiLanguageCoordinator;
    private readonly IAppDialogService _dialogService;
    private readonly Func<string, CancellationToken, Task>? _stopRunOwnerAsync;
    private readonly Func<CancellationToken, Task<UiOperationResult>>? _ensureConnectedAsync;
    private readonly ConcurrentQueue<SessionCallbackEnvelope> _pendingSessionCallbacks = new();
    private int _callbackDrainScheduled;

    public CopilotPageViewModel(
        MAAUnifiedRuntime runtime,
        IAppDialogService? dialogService = null,
        Func<string, CancellationToken, Task>? stopRunOwnerAsync = null,
        Func<CancellationToken, Task<UiOperationResult>>? ensureConnectedAsync = null)
        : base(runtime)
    {
        _dialogService = dialogService ?? NoOpAppDialogService.Instance;
        _stopRunOwnerAsync = stopRunOwnerAsync;
        _ensureConnectedAsync = ensureConnectedAsync;
        Types =
        [
            MainStageStoryCollectionSideStoryType,
            SecurityServiceStationType,
            ParadoxSimulationType,
            OtherActivityType,
        ];
        Items = new ObservableCollection<CopilotItemViewModel>();
        Items.CollectionChanged += (_, _) => NotifySelectionDerivedPropertiesChanged();
        Logs = new ObservableCollection<TaskQueueLogEntryViewModel>();
        _uiLanguageCoordinator = runtime.UiLanguageCoordinator;
        _uiLanguageCoordinator.LanguageChanged += OnUnifiedLanguageChanged;
        _currentLanguage = ResolveLanguage();
        _rootTexts = CreateRootTexts(_currentLanguage);
        _texts = CreateTexts(_currentLanguage);
        runtime.SessionService.CallbackProjected += OnSessionCallbackProjected;
        runtime.SessionService.SessionStateChanged += OnSessionStateChanged;
        _currentSessionState = runtime.SessionService.CurrentState;
        LoadPersistedItems();
        InitializeWpfParityState();
    }

    public IReadOnlyList<string> Types { get; }

    public ObservableCollection<CopilotItemViewModel> Items { get; }

    public ObservableCollection<TaskQueueLogEntryViewModel> Logs { get; }

    public RootLocalizationTextMap RootTexts => _rootTexts;

    public CopilotLocalizationTextMap Texts => _texts;

    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    public int SelectedTypeIndex
    {
        get => _selectedTypeIndex;
        set
        {
            if (SetProperty(ref _selectedTypeIndex, Math.Clamp(value, 0, Types.Count - 1)))
            {
                OnSelectedTypeIndexChanged();
            }
        }
    }

    public bool AutoSquad
    {
        get => _autoSquad;
        set
        {
            if (SetProperty(ref _autoSquad, value))
            {
                OnPropertyChanged(nameof(Form));
                RefreshVisibilityState();
            }
        }
    }

    public bool UseSupportUnit
    {
        get => _useSupportUnit;
        set
        {
            if (SetProperty(ref _useSupportUnit, value))
            {
                OnPropertyChanged(nameof(UseSupportUnitUsage));
            }
        }
    }

    public bool AddTrust
    {
        get => _addTrust;
        set => SetProperty(ref _addTrust, value);
    }

    public bool OverlayEnabled
    {
        get => _overlayEnabled;
        set => SetProperty(ref _overlayEnabled, value);
    }

    public CopilotItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
            {
                return;
            }

            OnSelectedItemChanged(value);
        }
    }

    public SessionState CurrentSessionState
    {
        get => _currentSessionState;
        private set
        {
            if (SetProperty(ref _currentSessionState, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(IsOwnRunActive));
                OnPropertyChanged(nameof(IsExternalRunActive));
                OnPropertyChanged(nameof(ShowStartButton));
                OnPropertyChanged(nameof(StartButtonText));
            }
        }
    }

    public bool IsRunning => CurrentSessionState is SessionState.Running or SessionState.Stopping;

    public bool IsOwnRunActive
    {
        get
        {
            if (!IsRunning)
            {
                return false;
            }

            var currentOwner = Runtime.SessionService.CurrentRunOwner;
            return string.IsNullOrWhiteSpace(currentOwner)
                || Runtime.SessionService.IsRunOwner(CopilotRunOwner);
        }
    }

    public bool IsExternalRunActive
    {
        get
        {
            if (!IsRunning)
            {
                return false;
            }

            var currentOwner = Runtime.SessionService.CurrentRunOwner;
            return !string.IsNullOrWhiteSpace(currentOwner)
                && !Runtime.SessionService.IsRunOwner(CopilotRunOwner);
        }
    }

    public bool ShowStartButton => true;

    public bool ShowStopButton => false;

    public bool CanStart =>
        !_isStartRequestActive
        && (CurrentSessionState == SessionState.Connected
            || (CurrentSessionState == SessionState.Idle && _ensureConnectedAsync is not null)
            || CurrentSessionState is SessionState.Running or SessionState.Stopping
            || IsExternalRunActive);

    public bool CanStop => CurrentSessionState == SessionState.Running && IsOwnRunActive;

    public bool IsStartRequestActive => _isStartRequestActive;

    public bool HasSelection => SelectedItem is not null;

    public bool CanMoveSelectedUp => SelectedItem is not null && Items.IndexOf(SelectedItem) > 0;

    public bool CanMoveSelectedDown
    {
        get
        {
            if (SelectedItem is null)
            {
                return false;
            }

            var index = Items.IndexOf(SelectedItem);
            return index >= 0 && index < Items.Count - 1;
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RecordEventAsync("Copilot", "Copilot page initialized.", cancellationToken);
    }

    public async Task ImportFromFileAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = CaptureListSnapshot();
        try
        {
            var importResult = await Runtime.CopilotFeatureService.ImportFromFileAsync(FilePath, cancellationToken);
            if (!importResult.Success)
            {
                RestoreListSnapshot(snapshot);
                StatusMessage = T("Copilot.Status.ImportFileFailed", "导入作业文件失败。");
                LastErrorMessage = importResult.Message;
                await RecordFailedResultAsync("Copilot.ImportFile", importResult, cancellationToken);
                return;
            }

            var normalizedPath = (FilePath ?? string.Empty).Trim();
            var payload = await File.ReadAllTextAsync(normalizedPath, cancellationToken);
            if (TryReadLoadedCopilotDescriptor(payload, normalizedPath, out var descriptor, out _))
            {
                Items.Add(CreateListItemFromDescriptor(descriptor, normalizedPath, string.Empty, isRaid: false));
            }
            else
            {
                var displayName = Path.GetFileName(normalizedPath);
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = $"Imported-{DateTime.Now:HHmmss}";
                }

                Items.Add(new CopilotItemViewModel(
                    displayName,
                    Types[SelectedTypeIndex],
                    sourcePath: normalizedPath));
            }
            SetSelectedItemSilently(Items.LastOrDefault());

            var persistResult = await PersistItemsAsync(cancellationToken);
            if (!persistResult.Success)
            {
                RestoreListSnapshot(snapshot);
                StatusMessage = T("Copilot.Status.ImportFilePersistRollback", "导入作业成功，但列表保存失败，已回滚。");
                LastErrorMessage = persistResult.Message;
                await RecordFailedResultAsync(
                    "Copilot.ImportFile",
                    BuildPersistFailedResult(StatusMessage, persistResult),
                    cancellationToken);
                return;
            }

            StatusMessage = importResult.Message;
            LastErrorMessage = string.Empty;
            await RecordEventAsync("Copilot.ImportFile", StatusMessage, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RestoreListSnapshot(snapshot);
            StatusMessage = T("Copilot.Status.ImportFileFailed", "导入作业文件失败。");
            LastErrorMessage = T("Copilot.Error.ImportFileRetry", "导入作业文件失败，请检查路径和 JSON 格式后重试。");
            await RecordUnhandledExceptionAsync(
                "Copilot.ImportFile",
                ex,
                UiErrorCode.CopilotFileReadFailed,
                LastErrorMessage,
                cancellationToken);
        }
    }

    public async Task ImportFromClipboardAsync(string payload, CancellationToken cancellationToken = default)
    {
        var snapshot = CaptureListSnapshot();
        try
        {
            var importResult = await Runtime.CopilotFeatureService.ImportFromClipboardAsync(payload, cancellationToken);
            if (!importResult.Success)
            {
                RestoreListSnapshot(snapshot);
                StatusMessage = T("Copilot.Status.ImportClipboardFailed", "导入剪贴板作业失败。");
                LastErrorMessage = importResult.Message;
                await RecordFailedResultAsync("Copilot.ImportClipboard", importResult, cancellationToken);
                return;
            }

            var normalizedPayload = (payload ?? string.Empty).Trim();
            var sourcePath = ResolveClipboardPathCandidate(normalizedPayload);
            var inlinePayload = sourcePath is null ? normalizedPayload : string.Empty;
            if (sourcePath is not null)
            {
                var filePayload = await File.ReadAllTextAsync(sourcePath, cancellationToken);
                if (TryReadLoadedCopilotDescriptor(filePayload, sourcePath, out var descriptor, out _))
                {
                    Items.Add(CreateListItemFromDescriptor(descriptor, sourcePath, string.Empty, isRaid: false));
                }
                else
                {
                    Items.Add(new CopilotItemViewModel(
                        $"Clipboard-{DateTime.Now:HHmmss}",
                        Types[SelectedTypeIndex],
                        sourcePath: sourcePath,
                        inlinePayload: string.Empty));
                }
            }
            else if (TryReadLoadedCopilotDescriptor(normalizedPayload, sourcePath: null, out var descriptor, out _))
            {
                Items.Add(CreateListItemFromDescriptor(descriptor, string.Empty, inlinePayload, isRaid: false));
            }
            else
            {
                Items.Add(new CopilotItemViewModel(
                    $"Clipboard-{DateTime.Now:HHmmss}",
                    Types[SelectedTypeIndex],
                    sourcePath: null,
                    inlinePayload: inlinePayload));
            }
            SetSelectedItemSilently(Items.LastOrDefault());

            var persistResult = await PersistItemsAsync(cancellationToken);
            if (!persistResult.Success)
            {
                RestoreListSnapshot(snapshot);
                StatusMessage = T("Copilot.Status.ImportClipboardPersistRollback", "导入剪贴板成功，但列表保存失败，已回滚。");
                LastErrorMessage = persistResult.Message;
                await RecordFailedResultAsync(
                    "Copilot.ImportClipboard",
                    BuildPersistFailedResult(StatusMessage, persistResult),
                    cancellationToken);
                return;
            }

            StatusMessage = importResult.Message;
            LastErrorMessage = string.Empty;
            await RecordEventAsync("Copilot.ImportClipboard", StatusMessage, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RestoreListSnapshot(snapshot);
            StatusMessage = T("Copilot.Status.ImportClipboardFailed", "导入剪贴板作业失败。");
            LastErrorMessage = T("Copilot.Error.ImportClipboardRetry", "导入剪贴板作业失败，请检查路径或 JSON 内容后重试。");
            await RecordUnhandledExceptionAsync(
                "Copilot.ImportClipboard",
                ex,
                UiErrorCode.CopilotPayloadInvalidJson,
                LastErrorMessage,
                cancellationToken);
        }
    }

    public async Task AddEmptyTaskAsync(CancellationToken cancellationToken = default)
    {
        await MutateListAndPersistAsync(
            () =>
            {
                var item = new CopilotItemViewModel($"Task-{Items.Count + 1}", Types[SelectedTypeIndex]);
                Items.Add(item);
                SetSelectedItemSilently(item);
            },
            "Copilot.Add",
            T("Copilot.Status.AddEmptyTaskSuccess", "已新增空白作业。"),
            T("Copilot.Error.AddEmptyTaskPersistFail", "新增作业失败：列表保存失败。"),
            cancellationToken);
    }

    public async Task RemoveSelectedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectedItem is null)
        {
            StatusMessage = T("Copilot.Status.RemoveFailed", "删除作业失败。");
            LastErrorMessage = T("Copilot.Error.SelectTaskToRemove", "请选择要删除的作业。");
            await RecordFailedResultAsync(
                "Copilot.Remove",
                UiOperationResult.Fail(UiErrorCode.CopilotSelectionMissing, LastErrorMessage),
                cancellationToken);
            return;
        }

        await MutateListAndPersistAsync(
            () =>
            {
                var current = SelectedItem;
                if (current is null)
                {
                    return;
                }

                var currentIndex = Items.IndexOf(current);
                Items.Remove(current);
                if (Items.Count == 0)
                {
                    SetSelectedItemSilently(null);
                    return;
                }

                var nextIndex = Math.Clamp(currentIndex, 0, Items.Count - 1);
                SetSelectedItemSilently(Items[nextIndex]);
            },
            "Copilot.Remove",
            T("Copilot.Status.RemoveSuccess", "已删除选中作业。"),
            T("Copilot.Error.RemovePersistFail", "删除作业失败：列表保存失败。"),
            cancellationToken);
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await MutateListAndPersistAsync(
            () =>
            {
                Items.Clear();
                SetSelectedItemSilently(null);
            },
            "Copilot.Clear",
            T("Copilot.Status.ClearSuccess", "已删除全部作业。"),
            T("Copilot.Error.ClearPersistFail", "删除全部作业失败：列表保存失败。"),
            cancellationToken);
    }

    public async Task MoveSelectedUpAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectedItem is null)
        {
            StatusMessage = T("Copilot.Status.SortFailed", "排序失败。");
            LastErrorMessage = T("Copilot.Error.SelectTaskToSort", "请选择要排序的作业。");
            await RecordFailedResultAsync(
                "Copilot.Sort",
                UiOperationResult.Fail(UiErrorCode.CopilotSelectionMissing, LastErrorMessage),
                cancellationToken);
            return;
        }

        var index = Items.IndexOf(SelectedItem);
        if (index <= 0)
        {
            StatusMessage = T("Copilot.Status.AlreadyTop", "当前作业已在顶部。");
            LastErrorMessage = string.Empty;
            await RecordEventAsync("Copilot.Sort", StatusMessage, cancellationToken);
            return;
        }

        await MutateListAndPersistAsync(
            () => Items.Move(index, index - 1),
            "Copilot.Sort",
            T("Copilot.Status.MoveUpSuccess", "已将选中作业上移。"),
            T("Copilot.Error.SortPersistFail", "排序失败：列表保存失败。"),
            cancellationToken);
    }

    public async Task MoveSelectedDownAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectedItem is null)
        {
            StatusMessage = T("Copilot.Status.SortFailed", "排序失败。");
            LastErrorMessage = T("Copilot.Error.SelectTaskToSort", "请选择要排序的作业。");
            await RecordFailedResultAsync(
                "Copilot.Sort",
                UiOperationResult.Fail(UiErrorCode.CopilotSelectionMissing, LastErrorMessage),
                cancellationToken);
            return;
        }

        var index = Items.IndexOf(SelectedItem);
        if (index < 0 || index >= Items.Count - 1)
        {
            StatusMessage = T("Copilot.Status.AlreadyBottom", "当前作业已在底部。");
            LastErrorMessage = string.Empty;
            await RecordEventAsync("Copilot.Sort", StatusMessage, cancellationToken);
            return;
        }

        await MutateListAndPersistAsync(
            () => Items.Move(index, index + 1),
            "Copilot.Sort",
            T("Copilot.Status.MoveDownSuccess", "已将选中作业下移。"),
            T("Copilot.Error.SortPersistFail", "排序失败：列表保存失败。"),
            cancellationToken);
    }

    public async Task PersistReorderedItemsAsync(
        CopilotItemViewModel item,
        int oldIndex,
        int newIndex,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var currentIndex = Items.IndexOf(item);
        if (currentIndex < 0 || currentIndex != newIndex)
        {
            StatusMessage = T("Copilot.Status.SortFailed", "排序失败。");
            LastErrorMessage = T("Copilot.Error.SortStateOutOfSync", "作业列表顺序已变化，请重试。");
            await RecordFailedResultAsync(
                "Copilot.Sort",
                UiOperationResult.Fail(UiErrorCode.CopilotListPersistenceFailed, LastErrorMessage),
                cancellationToken);
            return;
        }

        var persistResult = await PersistItemsAsync(cancellationToken);
        if (!persistResult.Success)
        {
            Items.Move(currentIndex, Math.Clamp(oldIndex, 0, Items.Count - 1));
            SetSelectedItemSilently(item);
            StatusMessage = T("Copilot.Status.SortFailed", "排序失败。");
            LastErrorMessage = persistResult.Message;
            await RecordFailedResultAsync(
                "Copilot.Sort",
                BuildPersistFailedResult(
                    T("Copilot.Error.SortPersistFail", "排序失败：列表保存失败。"),
                    persistResult),
                cancellationToken);
            return;
        }

        SetSelectedItemSilently(item);
        StatusMessage = T("Copilot.Status.ReorderSuccess", "已更新作业顺序。");
        LastErrorMessage = string.Empty;
        await RecordEventAsync("Copilot.Sort", StatusMessage, cancellationToken);
    }

    public async Task MoveListItemToAsync(
        CopilotItemViewModel item,
        int targetIndex,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var currentIndex = Items.IndexOf(item);
        if (currentIndex < 0)
        {
            StatusMessage = T("Copilot.Status.SortFailed", "排序失败。");
            LastErrorMessage = T("Copilot.Error.SelectTaskToSort", "请选择要排序的作业。");
            await RecordFailedResultAsync(
                "Copilot.List.Move",
                UiOperationResult.Fail(UiErrorCode.CopilotSelectionMissing, LastErrorMessage),
                cancellationToken);
            return;
        }

        var normalizedTargetIndex = Math.Clamp(targetIndex, 0, Items.Count - 1);
        if (currentIndex == normalizedTargetIndex)
        {
            SetSelectedItemSilently(item);
            StatusMessage = T("Copilot.Status.ReorderSuccess", "已更新作业顺序。");
            LastErrorMessage = string.Empty;
            await RecordEventAsync("Copilot.List.Move", StatusMessage, cancellationToken);
            return;
        }

        await MutateListAndPersistAsync(
            () =>
            {
                Items.Move(currentIndex, normalizedTargetIndex);
                SetSelectedItemSilently(item);
            },
            "Copilot.List.Move",
            T("Copilot.Status.ReorderSuccess", "已更新作业顺序。"),
            T("Copilot.Error.SortPersistFail", "排序失败：列表保存失败。"),
            cancellationToken);
    }

    public async Task SetListItemCheckedAsync(
        CopilotItemViewModel item,
        bool isChecked,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Items.Contains(item))
        {
            await ReportListItemUpdateMissingAsync("Copilot.List.Check", cancellationToken);
            return;
        }

        var previous = item.IsChecked;
        item.IsChecked = isChecked;
        var persistResult = await PersistItemsAsync(cancellationToken);
        if (!persistResult.Success)
        {
            item.IsChecked = previous;
            StatusMessage = T("Copilot.Status.UpdateListItemFailed", "更新作业失败。");
            LastErrorMessage = persistResult.Message;
            await RecordFailedResultAsync(
                "Copilot.List.Check",
                BuildPersistFailedResult(
                    T("Copilot.Error.UpdateListItemPersistFail", "更新作业列表失败：列表保存失败。"),
                    persistResult),
                cancellationToken);
            return;
        }

        StatusMessage = isChecked
            ? T("Copilot.Status.ListItemEnabled", "已启用作业。")
            : T("Copilot.Status.ListItemDisabled", "已禁用作业。");
        LastErrorMessage = string.Empty;
        await RecordEventAsync("Copilot.List.Check", StatusMessage, cancellationToken);
    }

    public async Task ToggleListItemRaidAsync(
        CopilotItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Items.Contains(item))
        {
            await ReportListItemUpdateMissingAsync("Copilot.List.ToggleRaid", cancellationToken);
            return;
        }

        var previous = item.IsRaid;
        item.IsRaid = !previous;
        var persistResult = await PersistItemsAsync(cancellationToken);
        if (!persistResult.Success)
        {
            item.IsRaid = previous;
            StatusMessage = T("Copilot.Status.UpdateListItemFailed", "更新作业失败。");
            LastErrorMessage = persistResult.Message;
            await RecordFailedResultAsync(
                "Copilot.List.ToggleRaid",
                BuildPersistFailedResult(
                    T("Copilot.Error.UpdateListItemPersistFail", "更新作业列表失败：列表保存失败。"),
                    persistResult),
                cancellationToken);
            return;
        }

        StatusMessage = item.IsRaid
            ? T("Copilot.Status.ListItemRaidEnabled", "已切换为突袭作业。")
            : T("Copilot.Status.ListItemRaidDisabled", "已切换为普通作业。");
        LastErrorMessage = string.Empty;
        await RecordEventAsync("Copilot.List.ToggleRaid", StatusMessage, cancellationToken);
    }

    public async Task ConfirmAndClearAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new WarningConfirmDialogRequest(
            Title: ClearAllConfirmTitleText,
            Message: ClearAllConfirmMessageText,
            ConfirmText: ClearAllConfirmButtonText,
            CancelText: CancelButtonText,
            Language: _currentLanguage);
        var completion = await _dialogService.ShowWarningConfirmAsync(
            request,
            "Copilot.List.ClearAll.Confirm",
            cancellationToken);

        if (completion.Return != DialogReturnSemantic.Confirm)
        {
            StatusMessage = T("Copilot.Status.ClearCancelled", "已取消删除全部作业。");
            LastErrorMessage = string.Empty;
            await RecordEventAsync("Copilot.List.ClearAll.Cancel", StatusMessage, cancellationToken);
            return;
        }

        await ClearAllAsync(cancellationToken);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!CanStart)
        {
            LastErrorMessage = BuildSessionStateNotAllowedMessage(
                CurrentSessionState,
                T("Copilot.Action.Start", "启动"),
                "start");
            await RecordFailedResultAsync(
                "Copilot.Start",
                UiOperationResult.Fail(UiErrorCode.TaskQueueEditBlocked, LastErrorMessage),
                cancellationToken);
            return;
        }

        if (CurrentSessionState is SessionState.Running or SessionState.Stopping)
        {
            var owner = Runtime.SessionService.CurrentRunOwner;
            if (string.IsNullOrWhiteSpace(owner))
            {
                owner = CopilotRunOwner;
            }

            var displayOwner = Runtime.SessionService.CurrentRunOwnerDisplayName;
            if (string.IsNullOrWhiteSpace(displayOwner))
            {
                displayOwner = owner;
            }

            StatusMessage = T("Copilot.Status.StartFailed", "启动失败。");
            LastErrorMessage = string.Format(
                T("Copilot.Error.StartRunOwnerBlocked", "Copilot 启动被拦截：当前运行所有者为 `{0}`。"),
                displayOwner);
            await RecordFailedResultAsync(
                "Copilot.Start.RunOwner",
                UiOperationResult.Fail(
                    UiErrorCode.OperationAlreadyRunning,
                    LastErrorMessage,
                    BuildRunOwnerBlockedDetails(owner, displayOwner)),
                cancellationToken);
            await ShowRunOwnerBlockedDialogAsync(owner, displayOwner, LastErrorMessage, cancellationToken);
            return;
        }

        if (!Runtime.SessionService.TryBeginRun(CopilotRunOwner, out var currentOwner))
        {
            var owner = string.IsNullOrWhiteSpace(currentOwner) ? "Unknown" : currentOwner;
            var displayOwner = Runtime.SessionService.CurrentRunOwnerDisplayName;
            if (string.IsNullOrWhiteSpace(displayOwner))
            {
                displayOwner = owner;
            }

            StatusMessage = T("Copilot.Status.StartFailed", "启动失败。");
            LastErrorMessage = string.Format(
                T("Copilot.Error.StartRunOwnerBlocked", "Copilot 启动被拦截：当前运行所有者为 `{0}`。"),
                displayOwner);
            await RecordFailedResultAsync(
                "Copilot.Start.RunOwner",
                UiOperationResult.Fail(
                    UiErrorCode.OperationAlreadyRunning,
                    LastErrorMessage,
                    BuildRunOwnerBlockedDetails(owner, displayOwner)),
                cancellationToken);
            await ShowRunOwnerBlockedDialogAsync(owner, displayOwner, LastErrorMessage, cancellationToken);
            return;
        }

        var keepRunOwner = false;
        SetStartRequestActive(true);
        try
        {
            if (CurrentSessionState != SessionState.Connected)
            {
                var connectResult = await EnsureConnectedForStartAsync(cancellationToken);
                if (!connectResult.Success)
                {
                    StatusMessage = T("Copilot.Status.StartFailed", "启动失败。");
                    LastErrorMessage = connectResult.Message;
                    await RecordFailedResultAsync("Copilot.Start.Connect", connectResult, cancellationToken);
                    return;
                }

                CurrentSessionState = Runtime.SessionService.CurrentState;
            }

            var appendPlan = await AppendConfiguredCopilotAsync(cancellationToken);
            if (appendPlan is null)
            {
                return;
            }

            _activeItemName = appendPlan.ActiveItemName;
            _activeItemCoreTaskId = appendPlan.TaskId;
            _activeTaskChain = appendPlan.TaskChain;
            _hasActiveRun = true;
            foreach (var item in Items.Where(item => item.IsChecked || string.Equals(item.Name, _activeItemName, StringComparison.Ordinal)))
            {
                item.Status = "Queued";
            }

            AddLog(GetRootText("ConnectingToEmulator", "Connecting to emulator……"));
            if (!await ApplyResultAsync(await Runtime.ConnectFeatureService.StartAsync(cancellationToken), "Copilot.Start", cancellationToken))
            {
                _hasActiveRun = false;
                _activeItemName = null;
                _activeItemCoreTaskId = null;
                _activeTaskChain = null;
                foreach (var item in Items.Where(item => item.Status == "Queued"))
                {
                    item.Status = "Ready";
                }
                return;
            }

            AddLog(GetRootText("Running", "Running……"));
            CurrentSessionState = Runtime.SessionService.CurrentState;
            SetStartRequestActive(false);
            keepRunOwner = true;
        }
        finally
        {
            if (!keepRunOwner)
            {
                Runtime.SessionService.EndRun(CopilotRunOwner);
                SetStartRequestActive(false);
            }
        }
    }

    private void SetStartRequestActive(bool value)
    {
        if (_isStartRequestActive == value)
        {
            return;
        }

        _isStartRequestActive = value;
        OnPropertyChanged(nameof(IsStartRequestActive));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ShowStartButton));
        OnPropertyChanged(nameof(StartButtonText));
    }

    private async Task<UiOperationResult> EnsureConnectedForStartAsync(CancellationToken cancellationToken)
    {
        CurrentSessionState = Runtime.SessionService.CurrentState;
        if (CurrentSessionState == SessionState.Connected)
        {
            return UiOperationResult.Ok("Session already connected.");
        }

        if (CurrentSessionState is SessionState.Running or SessionState.Stopping)
        {
            return UiOperationResult.Fail(
                UiErrorCode.SessionStateNotAllowed,
                BuildSessionStateNotAllowedMessage(
                    CurrentSessionState,
                    T("Copilot.Action.Start", "启动"),
                    "start"));
        }

        if (_ensureConnectedAsync is null)
        {
            return UiOperationResult.Fail(
                UiErrorCode.SessionStateNotAllowed,
                BuildSessionStateNotAllowedMessage(
                    CurrentSessionState,
                    T("Copilot.Action.Start", "启动"),
                    "start"));
        }

        var connectResult = await _ensureConnectedAsync(cancellationToken);
        CurrentSessionState = Runtime.SessionService.CurrentState;
        if (connectResult.Success && CurrentSessionState == SessionState.Connected)
        {
            return connectResult;
        }

        return UiOperationResult.Fail(
            connectResult.Error?.Code ?? UiErrorCode.SessionStateNotAllowed,
            connectResult.Message,
            connectResult.Error?.Details);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!CanStop)
        {
            LastErrorMessage = BuildSessionStateNotAllowedMessage(
                CurrentSessionState,
                T("Copilot.Action.Stop", "停止"),
                "stop");
            await RecordFailedResultAsync(
                "Copilot.Stop",
                UiOperationResult.Fail(UiErrorCode.TaskQueueEditBlocked, LastErrorMessage),
                cancellationToken);
            return;
        }

        if (!Runtime.SessionService.IsRunOwner(CopilotRunOwner))
        {
            var owner = Runtime.SessionService.CurrentRunOwner ?? "Unknown";
            StatusMessage = T("Copilot.Status.StopFailed", "停止失败。");
            LastErrorMessage = string.Format(
                T("Copilot.Error.StopRunOwnerBlocked", "Copilot 停止被拦截：当前运行所有者为 `{0}`。"),
                owner);
            await RecordFailedResultAsync(
                "Copilot.Stop.RunOwner",
                UiOperationResult.Fail(UiErrorCode.TaskQueueEditBlocked, LastErrorMessage),
                cancellationToken);
            return;
        }

        if (!await ApplyResultAsync(await Runtime.ConnectFeatureService.StopAsync(cancellationToken), "Copilot.Stop", cancellationToken))
        {
            CurrentSessionState = Runtime.SessionService.CurrentState;
            SyncStoppedUiStateIfSessionNotActive();
            return;
        }

        CurrentSessionState = Runtime.SessionService.CurrentState;
        SyncStoppedUiStateIfSessionNotActive();
    }

    private async Task<int?> AppendSelectedItemAsync(CopilotItemViewModel item, CancellationToken cancellationToken)
    {
        var filePath = await ResolveExecutionFilePathAsync(item, cancellationToken);
        if (filePath is null)
        {
            return null;
        }

        var request = BuildCopilotTaskRequest(item, filePath);
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
                UiOperationResult.Fail(
                    UiErrorCode.CopilotFileReadFailed,
                    LastErrorMessage),
                cancellationToken);
            return null;
        }

        await RecordEventAsync(
            "Copilot.Append",
            $"Appended copilot task #{appendResult.Value}: {item.Name}",
            cancellationToken);
        return appendResult.Value;
    }

    private async Task<string?> ResolveExecutionFilePathAsync(CopilotItemViewModel item, CancellationToken cancellationToken)
    {
        var resolvedPath = await ResolveExecutionFilePathAsync(
            item.SourcePath,
            item.InlinePayload,
            item.Name,
            cancellationToken);
        if (resolvedPath is not null)
        {
            item.SourcePath = resolvedPath;
        }

        return resolvedPath;
    }

    private CoreTaskRequest BuildCopilotTaskRequest(CopilotItemViewModel item, string filePath)
    {
        var taskType = ResolveCopilotTaskType(item.Type);
        JsonObject payload;
        if (string.Equals(taskType, "ParadoxCopilot", StringComparison.Ordinal))
        {
            payload = new JsonObject
            {
                ["filename"] = filePath,
            };
        }
        else
        {
            payload = new JsonObject
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
        }

        return new CoreTaskRequest(taskType, item.Name, true, payload.ToJsonString());
    }

    private static string ResolveCopilotTaskType(string type)
    {
        return NormalizeTypeDisplayName(type) switch
        {
            SecurityServiceStationType => "SSSCopilot",
            ParadoxSimulationType => "ParadoxCopilot",
            _ => "Copilot",
        };
    }

    private static string ResolveCopilotTaskChain(string type)
    {
        return ResolveCopilotTaskType(type);
    }

    private string BuildSessionStateNotAllowedMessage(
        SessionState state,
        string actionZh,
        string actionEn)
    {
        return string.Format(
            T(
                "Copilot.Error.SessionStateNotAllowed",
                DialogTextCatalog.Select(
                    _currentLanguage,
                    "会话状态 `{0}` 不允许{1}。",
                    "Session state `{0}` does not allow {2}.")),
            state,
            actionZh,
            actionEn);
    }

    private async Task ShowRunOwnerBlockedDialogAsync(
        string owner,
        string displayOwner,
        string message,
        CancellationToken cancellationToken)
    {
        var language = _currentLanguage;
        var errorResult = UiOperationResult.Fail(
            UiErrorCode.OperationAlreadyRunning,
            message,
            BuildRunOwnerBlockedDetails(owner, displayOwner));
        var chrome = CreateRunOwnerBlockedDialogChrome(language, displayOwner);
        var chromeSnapshot = chrome.GetSnapshot(language);
        var prompt = chromeSnapshot.GetNamedTextOrDefault(
            DialogTextCatalog.ChromeKeys.Prompt,
            message);
        var request = new WarningConfirmDialogRequest(
            Title: chromeSnapshot.Title,
            Message: prompt,
            ConfirmText: chromeSnapshot.ConfirmText ?? DialogTextCatalog.WarningDialogConfirmButton(language),
            CancelText: chromeSnapshot.CancelText ?? T("Copilot.RunOwnerBlockedDialog.StopButton", "停止任务"),
            Language: language,
            Chrome: chrome);

        var completion = await _dialogService.ShowWarningConfirmAsync(
            request,
            "Copilot.Start.RunOwner.Dialog",
            cancellationToken);
        if (completion.Return == DialogReturnSemantic.Cancel && _stopRunOwnerAsync is not null)
        {
            await _stopRunOwnerAsync(owner, cancellationToken);
            return;
        }

        if (completion.Return == DialogReturnSemantic.Details)
        {
            await ShowRunOwnerBlockedDetailsAsync(errorResult, cancellationToken);
        }
    }

    private async Task ShowRunOwnerBlockedDetailsAsync(
        UiOperationResult errorResult,
        CancellationToken cancellationToken)
    {
        var language = _currentLanguage;
        var localizedResult = DialogTextCatalog.LocalizeErrorResult(language, errorResult);
        var chrome = CreateErrorDetailsDialogChrome(language);
        var chromeSnapshot = chrome.GetSnapshot(language);
        var request = new ErrorDialogRequest(
            Title: chromeSnapshot.Title,
            Context: "Copilot.Start.RunOwner",
            Result: localizedResult,
            Suggestion: DialogTextCatalog.BuildErrorSuggestion(language, errorResult),
            ConfirmText: chromeSnapshot.ConfirmText ?? DialogTextCatalog.ErrorDialogCloseButton(language),
            CancelText: chromeSnapshot.CancelText ?? DialogTextCatalog.ErrorDialogIgnoreButton(language),
            Language: language,
            Chrome: chrome);
        await _dialogService.ShowErrorAsync(request, "Copilot.Start.RunOwner.ErrorDetails", cancellationToken: cancellationToken);
    }

    private static string BuildRunOwnerBlockedDetails(string owner, string displayOwner)
    {
        return JsonSerializer.Serialize(new
        {
            requestedOwner = CopilotRunOwner,
            currentOwner = owner,
            currentOwnerDisplayName = displayOwner,
            occurredAt = DateTimeOffset.Now,
        });
    }

    private static DialogChromeCatalog CreateRunOwnerBlockedDialogChrome(string language, string owner)
    {
        return DialogTextCatalog.CreateCatalog(
            language,
            nextLanguage =>
            {
                var texts = CreateTexts(nextLanguage);
                var title = texts.GetOrDefault("Copilot.RunOwnerBlockedDialog.Title", "Copilot start blocked");
                return new DialogChromeSnapshot(
                    title: title,
                    confirmText: texts.GetOrDefault("Copilot.RunOwnerBlockedDialog.ConfirmButton", "Confirm"),
                    cancelText: texts.GetOrDefault("Copilot.RunOwnerBlockedDialog.StopButton", "Stop task"),
                    namedTexts: DialogTextCatalog.CreateNamedTexts(
                        (DialogTextCatalog.ChromeKeys.SectionTitle, title),
                        (DialogTextCatalog.ChromeKeys.DetailsButton, texts.GetOrDefault(
                            "Copilot.RunOwnerBlockedDialog.DetailsButton",
                            DialogTextCatalog.WarningDialogDetailsButton(nextLanguage))),
                        (DialogTextCatalog.ChromeKeys.Prompt, string.Format(
                            CultureInfo.InvariantCulture,
                            texts.GetOrDefault(
                                "Copilot.RunOwnerBlockedDialog.Message",
                                "{0} is still running. Stop the current task before starting Copilot."),
                            owner))));
            });
    }

    private static DialogChromeCatalog CreateErrorDetailsDialogChrome(string language)
    {
        return DialogTextCatalog.CreateCatalog(
            language,
            nextLanguage => new DialogChromeSnapshot(
                title: DialogTextCatalog.ErrorDialogTitle(nextLanguage),
                confirmText: DialogTextCatalog.ErrorDialogCloseButton(nextLanguage),
                cancelText: DialogTextCatalog.ErrorDialogIgnoreButton(nextLanguage),
                namedTexts: DialogTextCatalog.CreateNamedTexts(
                    (DialogTextCatalog.ChromeKeys.SectionTitle, DialogTextCatalog.ErrorDialogSectionTitle(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.CopyButton, DialogTextCatalog.ErrorDialogCopyButton(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.IssueReportButton, DialogTextCatalog.ErrorDialogIssueReportButton(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.TimestampLabel, DialogTextCatalog.ErrorDialogTimestampLabel(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.ContextLabel, DialogTextCatalog.ErrorDialogContextLabel(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.CodeLabel, DialogTextCatalog.ErrorDialogCodeLabel(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.MessageLabel, DialogTextCatalog.ErrorDialogMessageLabel(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.DetailsLabel, DialogTextCatalog.ErrorDialogDetailsLabel(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.SuggestionLabel, DialogTextCatalog.ErrorDialogSuggestionLabel(nextLanguage)))));
    }

    public async Task SendLikeAsync(bool like, CancellationToken cancellationToken = default)
    {
        if (SelectedItem is null)
        {
            StatusMessage = T("Copilot.Status.FeedbackFailed", "反馈失败。");
            LastErrorMessage = T("Copilot.Error.SelectTaskForFeedback", "请选择要反馈的作业。");
            await RecordFailedResultAsync(
                "Copilot.Feedback",
                UiOperationResult.Fail(UiErrorCode.CopilotSelectionMissing, LastErrorMessage),
                cancellationToken);
            return;
        }

        var itemName = SelectedItem.Name;
        var feedbackTarget = SelectedItem.CopilotId > 0 ? SelectedItem.CopilotId.ToString() : itemName;
        var result = await Runtime.CopilotFeatureService.SubmitFeedbackAsync(feedbackTarget, like, cancellationToken);
        if (!result.Success)
        {
            StatusMessage = T("Copilot.Status.FeedbackFailed", "反馈失败。");
            LastErrorMessage = result.Message;
            await RecordFailedResultAsync("Copilot.Feedback", result, cancellationToken);
            return;
        }

        StatusMessage = string.Format(
            like
                ? T("Copilot.Status.FeedbackSubmittedLike", "已对作业 `{0}` 提交点赞。")
                : T("Copilot.Status.FeedbackSubmittedDislike", "已对作业 `{0}` 提交点踩。"),
            itemName);
        LastErrorMessage = string.Empty;
        if (like)
        {
            _ = Runtime.AchievementTrackerService.AddProgressToGroup("CopilotLikeGiven");
        }

        await RecordEventAsync("Copilot.Feedback", StatusMessage, cancellationToken);
    }

    private static string? ResolveClipboardPathCandidate(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        if (payload.StartsWith('{') || payload.StartsWith('['))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(payload);
        var normalized = Path.GetFullPath(expanded);
        return File.Exists(normalized) ? normalized : null;
    }

    private bool IsCopilotCallbackForActiveRun(CopilotCallbackPayload payload)
    {
        if (payload.TaskId.HasValue && _activeItemCoreTaskId.HasValue && payload.TaskId.Value != _activeItemCoreTaskId.Value)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload.TaskChain))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(_activeTaskChain))
        {
            return true;
        }

        if (string.Equals(payload.TaskChain, _activeTaskChain, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(payload.TaskChain, "Copilot", StringComparison.OrdinalIgnoreCase)
            && _activeTaskChain is "SSSCopilot" or "ParadoxCopilot";
    }

    private void CompleteActiveRun()
    {
        _hasActiveRun = false;
        _activeItemName = null;
        _activeItemCoreTaskId = null;
        _activeTaskChain = null;
        Runtime.SessionService.EndRun(CopilotRunOwner);
    }

    private static CopilotCallbackPayload BuildCopilotCallbackPayload(SessionCallbackEnvelope callback)
    {
        if (callback.Payload is null)
        {
            return new CopilotCallbackPayload(
                callback.TaskChain,
                callback.SubTask,
                callback.TaskId,
                callback.What,
                null,
                null,
                null,
                callback.ParseError);
        }

        var why = GetStringValue(callback.Payload, "why");
        var details = GetObjectValue(callback.Payload, "details");
        return new CopilotCallbackPayload(
            callback.TaskChain,
            callback.SubTask,
            callback.TaskId,
            callback.What,
            why,
            details,
            callback.Payload,
            callback.ParseError);
    }

    private Task HandleCallbackAsync(CoreCallbackEvent callback)
    {
        OnSessionCallbackProjected(SessionCallbackEnvelope.FromRaw(callback));
        return Task.CompletedTask;
    }

    private void OnSessionCallbackProjected(SessionCallbackEnvelope callback)
    {
        _pendingSessionCallbacks.Enqueue(callback);
        if (Interlocked.CompareExchange(ref _callbackDrainScheduled, 1, 0) != 0)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            DrainPendingSessionCallbacksOnUiThread();
            return;
        }

        Dispatcher.UIThread.Post(DrainPendingSessionCallbacksOnUiThread, DispatcherPriority.Background);
    }

    private void DrainPendingSessionCallbacksOnUiThread()
    {
        var shouldReschedule = false;
        try
        {
            while (_pendingSessionCallbacks.TryDequeue(out var callback))
            {
                ApplyRuntimeCallback(callback);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _callbackDrainScheduled, 0);
            shouldReschedule = !_pendingSessionCallbacks.IsEmpty
                && Interlocked.CompareExchange(ref _callbackDrainScheduled, 1, 0) == 0;
        }

        if (shouldReschedule)
        {
            Dispatcher.UIThread.Post(DrainPendingSessionCallbacksOnUiThread, DispatcherPriority.Background);
        }
    }

    internal void ApplyRuntimeCallback(CoreCallbackEvent callback)
    {
        ApplyRuntimeCallback(SessionCallbackEnvelope.FromRaw(callback));
    }

    internal void ApplyRuntimeCallback(SessionCallbackEnvelope callbackEnvelope)
    {
        var callback = callbackEnvelope.Callback;
        var metadata = BuildCopilotCallbackPayload(callbackEnvelope);
        if (metadata.HasParseError)
        {
            var warning = $"msgId={callback.MsgId}; msgName={callback.MsgName}; {metadata.ParseError}";
            Runtime.LogService.Warn($"Copilot callback payload parse failed: {warning}");
            _ = RecordEventAsync("Copilot.Callback.Parse", warning);
        }

        if (!_hasActiveRun || string.IsNullOrWhiteSpace(_activeItemName))
        {
            return;
        }

        var active = Items.FirstOrDefault(item => string.Equals(item.Name, _activeItemName, StringComparison.Ordinal));
        if (active is null)
        {
            return;
        }

        if (!IsCopilotCallbackForActiveRun(metadata))
        {
            return;
        }

        AppendWpfCallbackLog(callback, metadata);

        switch (callback.MsgName)
        {
            case "TaskChainStart":
                active.Status = "Running";
                break;
            case "TaskChainCompleted":
            case "AllTasksCompleted":
                active.Status = "Success";
                _ = Runtime.AchievementTrackerService.AddProgressToGroup("UseCopilot");
                CompleteActiveRun();
                break;
            case "TaskChainStopped":
                active.Status = "Stopped";
                CompleteActiveRun();
                break;
            case "TaskChainError":
            case "SubTaskError":
                active.Status = "Error";
                LastErrorMessage = $"{callback.MsgName}: {callback.PayloadJson}";
                CompleteActiveRun();
                break;
        }
    }

    private void AppendWpfCallbackLog(CoreCallbackEvent callback, CopilotCallbackPayload payload)
    {
        switch (callback.MsgName)
        {
            case "TaskChainError":
                AddLog(GetRootText("CombatError", "Combat error"), "ERROR", timestamp: callback.Timestamp);
                break;
            case "SubTaskError":
                AppendSubTaskErrorLog(payload, callback.Timestamp);
                break;
            case "SubTaskStart":
                AppendSubTaskStartLog(payload, callback.Timestamp);
                break;
            case "SubTaskExtraInfo":
                AppendSubTaskExtraInfoLog(payload, callback.Timestamp);
                break;
        }
    }

    private void AppendSubTaskErrorLog(CopilotCallbackPayload payload, DateTimeOffset timestamp)
    {
        if (string.Equals(payload.SubTask, "BattleFormationTask", StringComparison.OrdinalIgnoreCase)
            && string.Equals(payload.Why, "OperatorMissing", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new StringBuilder(GetRootText("MissingOperators", "Missing operators:"));
            var groups = GetObjectValue(payload.Details, "opers");
            if (groups is not null && groups.Count > 0)
            {
                foreach (var pair in groups)
                {
                    if (pair.Value is not JsonArray opers)
                    {
                        continue;
                    }

                    builder.AppendLine();
                    if (opers.Count <= 1)
                    {
                        builder.Append(pair.Key);
                        continue;
                    }

                    var names = opers
                        .Select(oper => oper is JsonObject obj ? GetStringValue(obj, "name") : oper?.ToString())
                        .Where(static name => !string.IsNullOrWhiteSpace(name))
                        .ToArray();
                    builder.Append(pair.Key);
                    builder.Append("=> ");
                    builder.Append(string.Join(" / ", names));
                }
            }

            AddLog(builder.ToString().TrimEnd(), "ERROR", timestamp: timestamp);
            return;
        }

        if (string.Equals(payload.SubTask, "CopilotTask", StringComparison.OrdinalIgnoreCase)
            && string.Equals(payload.What, "UserAdditionalOperInvalid", StringComparison.OrdinalIgnoreCase))
        {
            var operName = GetStringValue(payload.Details, "name") ?? string.Empty;
            AddLog(
                string.Format(
                    GetRootText("CopilotUserAdditionalNameInvalid", "Additional custom operator name invalid: {0}, please check spelling"),
                    operName),
                "ERROR",
                timestamp: timestamp);
        }
    }

    private void AppendSubTaskStartLog(CopilotCallbackPayload payload, DateTimeOffset timestamp)
    {
        if (string.Equals(payload.SubTask, "CombatRecordRecognitionTask", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(payload.What))
        {
            AddLog(payload.What, timestamp: timestamp);
            return;
        }

        if (!string.Equals(payload.SubTask, "ProcessTask", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var taskName = GetStringValue(payload.Details, "task");
        switch (taskName)
        {
            case "BattleStartAll":
                AddLog(GetRootText("MissionStart", "Mission started"), timestamp: timestamp);
                break;
            case "StageDrops-Stars-3":
            case "StageDrops-Stars-Adverse":
                AddLog(GetRootText("CompleteCombat", "Complete combat"), "SUCCESS", timestamp: timestamp);
                break;
        }
    }

    private void AppendSubTaskExtraInfoLog(CopilotCallbackPayload payload, DateTimeOffset timestamp)
    {
        switch (payload.What)
        {
            case "BattleFormation":
                AppendBattleFormationLog(payload.Details, timestamp);
                break;
            case "BattleFormationParseFailed":
                AddLog(GetRootText("BattleFormationParseFailed", "Formation parse failed"), timestamp: timestamp);
                break;
            case "BattleFormationSelected":
                AppendBattleFormationSelectedLog(payload.Details, timestamp);
                break;
            case "BattleFormationOperUnavailable":
                AppendBattleFormationUnavailableLog(payload.Details, timestamp);
                break;
            case "CopilotAction":
                AppendCopilotActionLog(payload.Details, timestamp);
                break;
            case "CopilotListLoadTaskFileSuccess":
                AddLog(
                    $"Parse {GetStringValue(payload.Details, "file_name")}[{GetStringValue(payload.Details, "stage_name")}] Success",
                    timestamp: timestamp);
                break;
            case "SSSStage":
                AddLog(
                    string.Format(
                        GetRootText("CurrentStage", "Current Stage: {0}"),
                        GetStringValue(payload.Details, "stage") ?? string.Empty),
                    timestamp: timestamp);
                break;
            case "SSSSettlement":
                if (!string.IsNullOrWhiteSpace(payload.Why))
                {
                    AddLog(payload.Why, timestamp: timestamp);
                }

                break;
            case "SSSGamePass":
                AddLog(GetRootText("SSSGamePass", "Game cleared! congratulations!"), timestamp: timestamp);
                break;
            case "UnsupportedLevel":
                _ = Runtime.AchievementTrackerService.Unlock("MapOutdated");
                AddLog(GetRootText("UnsupportedLevel", "Unsupported stage, please update resources and try again!"), "ERROR", timestamp: timestamp);
                break;
        }
    }

    private void AppendBattleFormationLog(JsonObject? details, DateTimeOffset timestamp)
    {
        var formation = GetArrayValue(details, "formation");
        var names = formation?
            .Select(oper => oper?.ToString())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToArray() ?? [];
        AddLog(
            $"{GetRootText("BattleFormation", "Start formation")}{Environment.NewLine}[{string.Join(", ", names)}]",
            timestamp: timestamp);
    }

    private void AppendBattleFormationSelectedLog(JsonObject? details, DateTimeOffset timestamp)
    {
        var selected = GetStringValue(details, "selected") ?? string.Empty;
        var groupName = GetStringValue(details, "group_name");
        if (!string.IsNullOrWhiteSpace(groupName) && !string.Equals(groupName, selected, StringComparison.Ordinal))
        {
            selected = $"{groupName} => {selected}";
        }

        AddLog(
            $"{GetRootText("BattleFormationSelected", "Selected: ")}{selected}",
            timestamp: timestamp);
    }

    private void AppendBattleFormationUnavailableLog(JsonObject? details, DateTimeOffset timestamp)
    {
        var operName = GetStringValue(details, "oper_name") ?? string.Empty;
        var type = GetStringValue(details, "requirement_type") ?? string.Empty;
        var level = !IgnoreRequirements;
        var reasonKey = type switch
        {
            "elite" => "BattleFormationOperUnavailable.Elite",
            "level" => "BattleFormationOperUnavailable.Level",
            "skill_level" => "BattleFormationOperUnavailable.SkillLevel",
            "module" => "BattleFormationOperUnavailable.Module",
            _ => string.Empty,
        };
        if (string.Equals(type, "elite", StringComparison.OrdinalIgnoreCase))
        {
            level = true;
        }

        var reason = string.IsNullOrWhiteSpace(reasonKey) ? type : GetRootText(reasonKey, type);
        AddLog(
            string.Format(
                GetRootText("BattleFormationOperUnavailable", "Operator unavailable: {0}, reason: {1}"),
                operName,
                reason),
            level ? "ERROR" : "WARN",
            timestamp: timestamp);
    }

    private void AppendCopilotActionLog(JsonObject? details, DateTimeOffset timestamp)
    {
        var doc = GetStringValue(details, "doc");
        if (!string.IsNullOrWhiteSpace(doc))
        {
            AddLog(doc, MapDocColorToLevel(GetStringValue(details, "doc_color")), timestamp: timestamp);
        }

        var action = GetStringValue(details, "action") ?? "UnknownAction";
        var target = GetStringValue(details, "target") ?? string.Empty;
        AddLog(
            string.Format(
                GetRootText("CurrentSteps", "Step: {0} {1}"),
                GetRootText(action, action),
                target),
            timestamp: timestamp);

        var elapsed = GetIntValue(details, "elapsed_time");
        if (elapsed.HasValue && elapsed.Value >= 0)
        {
            AddLog(
                string.Format(
                    GetRootText("ElapsedTime", "Elapsed time: {0}ms"),
                    elapsed.Value),
                timestamp: timestamp);
        }
    }

    private string GetRootText(string key, string fallback)
    {
        return _rootTexts.GetOrDefault(key, fallback);
    }

    private string T(string key, string fallback)
    {
        return _texts.GetOrDefault(key, fallback);
    }

    private void AddLog(string? content, string level = "INFO", bool showTime = true, DateTimeOffset? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var time = showTime ? (timestamp ?? DateTimeOffset.Now).ToLocalTime().ToString("HH:mm:ss") : string.Empty;
        Logs.Add(new TaskQueueLogEntryViewModel(time, content.TrimEnd(), level));
        const int maxLogs = 200;
        while (Logs.Count > maxLogs)
        {
            Logs.RemoveAt(0);
        }
    }

    private static string MapDocColorToLevel(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return "INFO";
        }

        if (color.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return "ERROR";
        }

        if (color.Contains("warn", StringComparison.OrdinalIgnoreCase))
        {
            return "WARN";
        }

        if (color.Contains("success", StringComparison.OrdinalIgnoreCase))
        {
            return "SUCCESS";
        }

        return "INFO";
    }

    private string ResolveLanguage()
    {
        if (Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(LegacyConfigurationKeys.Localization, out var value)
            && value is JsonValue jsonValue
            && jsonValue.TryGetValue(out string? language)
            && !string.IsNullOrWhiteSpace(language))
        {
            return UiLanguageCatalog.Normalize(language);
        }

        return UiLanguageCatalog.Normalize(_uiLanguageCoordinator.CurrentLanguage);
    }

    public void SetLanguage(string language)
    {
        var normalized = UiLanguageCatalog.Normalize(language);
        if (string.Equals(_currentLanguage, normalized, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_rootTexts.Language, normalized, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_texts.Language, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentLanguage = normalized;
        _rootTexts.Language = normalized;
        _texts.Language = normalized;
        RefreshLocalizedUiState();
    }

    private static CopilotLocalizationTextMap CreateTexts(string language)
    {
        return new CopilotLocalizationTextMap
        {
            Language = language,
        };
    }

    private static RootLocalizationTextMap CreateRootTexts(string language)
    {
        return new RootLocalizationTextMap("Root.Localization.Copilot")
        {
            Language = language,
        };
    }

    private void OnUnifiedLanguageChanged(object? sender, UiLanguageChangedEventArgs e)
    {
        if (Avalonia.Application.Current is null)
        {
            SetLanguage(e.CurrentLanguage);
            return;
        }

        Dispatcher.UIThread.Post(() => SetLanguage(e.CurrentLanguage), DispatcherPriority.Background);
    }

    private static string? GetStringValue(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            return text;
        }

        return node.ToString();
    }

    private static int? GetIntValue(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out int number))
            {
                return number;
            }

            if (value.TryGetValue(out string? text) && int.TryParse(text, out number))
            {
                return number;
            }
        }

        return null;
    }

    private static JsonObject? GetObjectValue(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node))
        {
            return null;
        }

        return node as JsonObject;
    }

    private static JsonArray? GetArrayValue(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node))
        {
            return null;
        }

        return node as JsonArray;
    }

    private void OnSessionStateChanged(SessionState state)
    {
        void Apply(SessionState changedState)
        {
            CurrentSessionState = changedState;
            SyncStoppedUiStateIfSessionNotActive();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply(state);
            return;
        }

        Dispatcher.UIThread.Post(() => Apply(state));
    }

    private void SyncStoppedUiStateIfSessionNotActive()
    {
        if (CurrentSessionState is SessionState.Running or SessionState.Stopping)
        {
            return;
        }

        foreach (var item in Items.Where(item => item.Status is "Queued" or "Running"))
        {
            item.Status = "Stopped";
        }

        if (_hasActiveRun)
        {
            CompleteActiveRun();
        }
    }

    private void OnSelectedItemChanged(CopilotItemViewModel? value)
    {
        NotifySelectionDerivedPropertiesChanged();
        SyncSelectedTypeIndexFromListItem(value);
        if (_suppressSelectionFeedback)
        {
            return;
        }

        if (value is null)
        {
            StatusMessage = T("Copilot.Status.NoSelection", "未选中作业。");
            LastErrorMessage = string.Empty;
            _ = RecordEventAsync("Copilot.Select", "Cleared selected copilot item.");
            return;
        }

        StatusMessage = string.Format(
            T("Copilot.Status.SelectedItem", "已选中作业：{0}"),
            value.Name);
        LastErrorMessage = string.Empty;
        _ = RecordEventAsync("Copilot.Select", $"Selected copilot item `{value.Name}`.");
    }

    private void SyncSelectedTypeIndexFromListItem(CopilotItemViewModel? item)
    {
        if (item?.TabIndex is not { } tabIndex)
        {
            return;
        }

        var normalizedTabIndex = Math.Clamp(tabIndex, 0, Types.Count - 1);
        if (SelectedTypeIndex == normalizedTabIndex)
        {
            return;
        }

        var previousSuppress = _suppressSelectedTypeListItemSync;
        _suppressSelectedTypeListItemSync = true;
        try
        {
            SelectedTypeIndex = normalizedTabIndex;
        }
        finally
        {
            _suppressSelectedTypeListItemSync = previousSuppress;
        }
    }

    private void NotifySelectionDerivedPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanMoveSelectedUp));
        OnPropertyChanged(nameof(CanMoveSelectedDown));
    }

    private async Task ReportListItemUpdateMissingAsync(string scope, CancellationToken cancellationToken)
    {
        StatusMessage = T("Copilot.Status.UpdateListItemFailed", "更新作业失败。");
        LastErrorMessage = T("Copilot.Error.SelectTaskToUpdate", "请选择要更新的作业。");
        await RecordFailedResultAsync(
            scope,
            UiOperationResult.Fail(UiErrorCode.CopilotSelectionMissing, LastErrorMessage),
            cancellationToken);
    }

    private async Task<bool> MutateListAndPersistAsync(
        Action mutation,
        string scope,
        string successMessage,
        string persistenceFailureMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = CaptureListSnapshot();

        try
        {
            mutation();
        }
        catch (Exception ex)
        {
            RestoreListSnapshot(snapshot);
            StatusMessage = persistenceFailureMessage;
            await RecordUnhandledExceptionAsync(
                scope,
                ex,
                UiErrorCode.CopilotListPersistenceFailed,
                persistenceFailureMessage,
                cancellationToken);
            return false;
        }

        var persistResult = await PersistItemsAsync(cancellationToken);
        if (!persistResult.Success)
        {
            RestoreListSnapshot(snapshot);
            StatusMessage = persistenceFailureMessage;
            LastErrorMessage = persistResult.Message;
            await RecordFailedResultAsync(
                scope,
                BuildPersistFailedResult(persistenceFailureMessage, persistResult),
                cancellationToken);
            return false;
        }

        StatusMessage = successMessage;
        LastErrorMessage = string.Empty;
        await RecordEventAsync(scope, successMessage, cancellationToken);
        return true;
    }

    private async Task<UiOperationResult> PersistItemsAsync(CancellationToken cancellationToken)
    {
        var payload = SerializeItemsPayload();
        UiOperationResult? saveResult = null;
        var succeeded = await RunTrackedConfigurationSaveAsync(
            CopilotTaskListConfigScope,
            Texts.GetOrDefault("Copilot.Title", "抄作业"),
            CopilotTaskListConfigScope,
            async ct =>
            {
                saveResult = await Runtime.SettingsFeatureService.SaveGlobalSettingAsync(
                    LegacyConfigurationKeys.CopilotTaskList,
                    payload,
                    ct);
                return saveResult;
            },
            cancellationToken);
        if (!succeeded)
        {
            return saveResult ?? UiOperationResult.Fail(
                UiErrorCode.CopilotListPersistenceFailed,
                T("Copilot.Error.TaskListPersistFail", "Copilot 列表持久化失败。"));
        }

        await RecordEventAsync(
            CopilotTaskListConfigScope,
            $"Saved {Items.Count} copilot item(s).",
            cancellationToken);
        return UiOperationResult.Ok("Copilot task list saved.");
    }

    private static UiOperationResult BuildPersistFailedResult(string actionMessage, UiOperationResult persistResult)
    {
        return UiOperationResult.Fail(
            persistResult.Error?.Code ?? UiErrorCode.CopilotListPersistenceFailed,
            $"{actionMessage} {persistResult.Message}",
            persistResult.Error?.Details);
    }

    private CopilotListSnapshot CaptureListSnapshot()
    {
        return new CopilotListSnapshot([.. Items], SelectedItem);
    }

    private void RestoreListSnapshot(CopilotListSnapshot snapshot)
    {
        var previousSuppress = _suppressSelectionFeedback;
        _suppressSelectionFeedback = true;
        try
        {
            Items.Clear();
            foreach (var item in snapshot.Items)
            {
                Items.Add(item);
            }

            if (snapshot.SelectedItem is not null && Items.Contains(snapshot.SelectedItem))
            {
                SelectedItem = snapshot.SelectedItem;
            }
            else
            {
                SelectedItem = Items.LastOrDefault();
            }
        }
        finally
        {
            _suppressSelectionFeedback = previousSuppress;
            NotifySelectionDerivedPropertiesChanged();
        }
    }

    private void SetSelectedItemSilently(CopilotItemViewModel? item)
    {
        var previousSuppress = _suppressSelectionFeedback;
        _suppressSelectionFeedback = true;
        try
        {
            SelectedItem = item;
        }
        finally
        {
            _suppressSelectionFeedback = previousSuppress;
            NotifySelectionDerivedPropertiesChanged();
        }
    }

    private void LoadPersistedItems()
    {
        if (!TryReadPersistedPayload(out var payload))
        {
            return;
        }

        if (!TryDeserializePersistedItems(payload, out var loadedItems, out var warning))
        {
            StatusMessage = T("Copilot.Status.CorruptedListIgnored", "已忽略损坏的 Copilot 列表配置。");
            LastErrorMessage = warning;
            _ = RecordFailedResultAsync(
                "Copilot.List.Load",
                UiOperationResult.Fail(UiErrorCode.CopilotPayloadInvalidJson, warning));
            return;
        }

        foreach (var item in loadedItems)
        {
            Items.Add(new CopilotItemViewModel(item.Name, item.Type, item.SourcePath, item.InlinePayload)
            {
                CopilotId = Math.Max(0, item.CopilotId),
                IsChecked = item.IsChecked,
                IsRaid = item.IsRaid,
                TabIndex = item.TabIndex,
            });
        }

        if (Items.Count > 0)
        {
            SetSelectedItemSilently(Items[0]);
        }

        _ = RecordEventAsync(
            "Copilot.List.Load",
            $"Loaded {Items.Count} copilot item(s) from persisted config.");
    }

    private bool TryReadPersistedPayload(out string payload)
    {
        payload = string.Empty;
        if (!Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(LegacyConfigurationKeys.CopilotTaskList, out var node)
            || node is null)
        {
            return false;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue(out string? raw))
        {
            payload = raw ?? string.Empty;
            return !string.IsNullOrWhiteSpace(payload);
        }

        if (node is JsonArray || node is JsonObject)
        {
            payload = node.ToJsonString();
            return !string.IsNullOrWhiteSpace(payload);
        }

        return false;
    }

    private bool TryDeserializePersistedItems(
        string payload,
        out List<PersistedCopilotItem> items,
        out string warning)
    {
        items = [];
        warning = string.Empty;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payload);
        }
        catch (Exception ex)
        {
            warning = string.Format(
                T("Copilot.Error.ReadListInvalidJson", "读取作业列表失败：配置不是合法 JSON。{0}"),
                ex.Message);
            return false;
        }

        if (root is not JsonArray array)
        {
            warning = T("Copilot.Error.ReadListNotArray", "读取作业列表失败：配置必须是 JSON 数组。");
            return false;
        }

        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject obj)
            {
                continue;
            }

            if (!TryGetRequiredStringProperty(obj, "name", out var name))
            {
                continue;
            }

            var type = ResolvePersistedType(obj);
            _ = TryGetOptionalStringProperty(obj, "source_path", out var sourcePath)
                || TryGetOptionalStringProperty(obj, "SourcePath", out sourcePath);
            _ = TryGetOptionalStringProperty(obj, "file_path", out var legacyFilePath)
                || TryGetOptionalStringProperty(obj, "FilePath", out legacyFilePath);
            _ = TryGetOptionalStringProperty(obj, "inline_payload", out var inlinePayload)
                || TryGetOptionalStringProperty(obj, "InlinePayload", out inlinePayload);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                sourcePath = legacyFilePath;
            }

            var copilotId = TryReadOptionalIntProperty(obj, "copilot_id", out var persistedCopilotId)
                ? persistedCopilotId ?? 0
                : TryReadOptionalIntProperty(obj, "CopilotId", out persistedCopilotId)
                    ? persistedCopilotId ?? 0
                    : 0;
            var tabIndex = TryReadOptionalIntProperty(obj, "tab_index", out var persistedTabIndex)
                ? persistedTabIndex
                : TryReadOptionalIntProperty(obj, "TabIndex", out persistedTabIndex)
                    ? persistedTabIndex
                    : (int?)null;
            var isRaid = TryReadOptionalBoolProperty(obj, "is_raid", out var persistedIsRaid)
                ? persistedIsRaid
                : TryReadOptionalBoolProperty(obj, "IsRaid", out persistedIsRaid) && persistedIsRaid;
            var isChecked = TryReadOptionalBoolProperty(obj, "is_checked", out var persistedIsChecked)
                ? persistedIsChecked
                : !TryReadOptionalBoolProperty(obj, "IsChecked", out persistedIsChecked) || persistedIsChecked;
            items.Add(new PersistedCopilotItem(name, type, sourcePath, inlinePayload, copilotId, tabIndex, isRaid, isChecked));
        }

        if (array.Count > 0 && items.Count == 0)
        {
            warning = T("Copilot.Error.ReadListMissingName", "读取作业列表失败：列表项缺少可识别字段（例如 name）。");
            return false;
        }

        return true;
    }

    private string ResolvePersistedType(JsonObject obj)
    {
        if (TryGetRequiredStringProperty(obj, "type", out var type))
        {
            return NormalizeTypeDisplayName(type);
        }

        if (TryGetPropertyCaseInsensitive(obj, "tab_index", out var tabNode)
            && tabNode is JsonValue tabValue
            && tabValue.TryGetValue(out int tabIndex)
            && tabIndex >= 0
            && tabIndex < Types.Count)
        {
            return Types[tabIndex];
        }

        return Types[0];
    }

    private static string NormalizeTypeDisplayName(string? type)
    {
        var normalized = type?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return MainStageStoryCollectionSideStoryType;
        }

        return normalized switch
        {
            "主线" or MainStageStoryCollectionSideStoryType or "MainStageStoryCollectionSideStory" or "Copilot"
                => MainStageStoryCollectionSideStoryType,
            "SSS" or SecurityServiceStationType or "SSSCopilot"
                => SecurityServiceStationType,
            "悖论" or ParadoxSimulationType or "ParadoxCopilot"
                => ParadoxSimulationType,
            "活动" or OtherActivityType or "OtherActivityStage"
                => OtherActivityType,
            _ => normalized,
        };
    }

    private static bool TryGetRequiredStringProperty(JsonObject obj, string key, out string value)
    {
        value = string.Empty;
        if (!TryGetPropertyCaseInsensitive(obj, key, out var node)
            || node is not JsonValue jsonValue
            || !jsonValue.TryGetValue(out string? raw))
        {
            return false;
        }

        value = raw?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryGetOptionalStringProperty(JsonObject obj, string key, out string value)
    {
        value = string.Empty;
        if (!TryGetPropertyCaseInsensitive(obj, key, out var node)
            || node is not JsonValue jsonValue
            || !jsonValue.TryGetValue(out string? raw))
        {
            return false;
        }

        value = raw?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryReadOptionalIntProperty(JsonObject obj, string key, out int? value)
    {
        value = null;
        if (!TryGetPropertyCaseInsensitive(obj, key, out var node)
            || node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out int parsedInt))
        {
            value = parsedInt;
            return true;
        }

        if (jsonValue.TryGetValue(out string? text) && int.TryParse(text, out parsedInt))
        {
            value = parsedInt;
            return true;
        }

        return false;
    }

    private static bool TryReadOptionalBoolProperty(JsonObject obj, string key, out bool value)
    {
        value = false;
        if (!TryGetPropertyCaseInsensitive(obj, key, out var node)
            || node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out bool parsedBool))
        {
            value = parsedBool;
            return true;
        }

        if (jsonValue.TryGetValue(out int parsedInt))
        {
            value = parsedInt != 0;
            return true;
        }

        if (jsonValue.TryGetValue(out string? text))
        {
            if (bool.TryParse(text, out parsedBool))
            {
                value = parsedBool;
                return true;
            }

            if (int.TryParse(text, out parsedInt))
            {
                value = parsedInt != 0;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonObject obj, string key, out JsonNode? value)
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

    private string SerializeItemsPayload()
    {
        var items = Items
            .Select(item => new PersistedCopilotItem(
                item.Name,
                item.Type,
                item.SourcePath,
                item.InlinePayload,
                item.CopilotId,
                item.TabIndex,
                item.IsRaid,
                item.IsChecked))
            .ToArray();
        return JsonSerializer.Serialize(items);
    }

    private sealed record CopilotListSnapshot(IReadOnlyList<CopilotItemViewModel> Items, CopilotItemViewModel? SelectedItem);

    private sealed record PersistedCopilotItem(
        string Name,
        string Type,
        string SourcePath,
        string InlinePayload,
        int CopilotId,
        int? TabIndex,
        bool IsRaid,
        bool IsChecked);
}
