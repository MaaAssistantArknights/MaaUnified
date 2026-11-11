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
using MAAUnified.Platform;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

using MAAUnified.App.ViewModels.Settings;

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
    private bool _autoSquad;
    private bool _useSupportUnit;
    private bool _addTrust;
    private bool _overlayEnabled;
    private SessionState _currentSessionState;
    private string? _activeItemName;
    private int? _activeItemCoreTaskId;
    private string? _activeTaskChain;
    private bool _hasActiveRun;
    private bool _isStartRequestActive;
    private bool _isStopRequestActive;
    private readonly object _startRequestGate = new();
    private CancellationTokenSource? _startRequestCts;
    private TaskCompletionSource? _startRequestCompletion;
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
    private readonly Func<CancellationToken, Task>? _stopActiveSessionOperationAsync;
    private readonly Func<CancellationToken, Task<UiOperationResult>>? _ensureConnectedAsync;
    private readonly ConcurrentQueue<SessionCallbackEnvelope> _pendingSessionCallbacks = new();
    private int _callbackDrainScheduled;

    private readonly ConnectionGameSharedStateViewModel? _connectionState;

    public CopilotPageViewModel(
        MAAUnifiedRuntime runtime,
        IAppDialogService? dialogService = null,
        Func<string, CancellationToken, Task>? stopRunOwnerAsync = null,
        Func<CancellationToken, Task<UiOperationResult>>? ensureConnectedAsync = null,
        ConnectionGameSharedStateViewModel? connectionState = null,
        Func<CancellationToken, Task>? stopActiveSessionOperationAsync = null)
        : base(runtime)
    {
        _connectionState = connectionState;
        _dialogService = dialogService ?? NoOpAppDialogService.Instance;
        _stopRunOwnerAsync = stopRunOwnerAsync;
        _stopActiveSessionOperationAsync = stopActiveSessionOperationAsync;
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
            if (_isStartRequestActive)
            {
                OnPropertyChanged(nameof(SelectedItem));
                return;
            }

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
                OnPropertyChanged(nameof(ShowStopButton));
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

    public bool IsExternalSessionActivityActive
    {
        get
        {
            if (_isStartRequestActive)
            {
                return false;
            }

            if (CurrentSessionState == SessionState.Connecting || IsRunOwnedByAnotherFeature)
            {
                return true;
            }

            return CurrentSessionState is not (SessionState.Running or SessionState.Stopping)
                && _connectionState?.IsSessionOperationActive == true;
        }
    }

    private bool IsRunOwnedByAnotherFeature
    {
        get
        {
            if (CurrentSessionState is not (SessionState.Connecting or SessionState.Running or SessionState.Stopping))
            {
                return false;
            }

            var currentOwner = Runtime.SessionService.CurrentRunOwner;
            return !string.IsNullOrWhiteSpace(currentOwner)
                && !Runtime.SessionService.IsRunOwner(CopilotRunOwner);
        }
    }

    public bool ShowStartButton => !ShowStopButton;

    public bool ShowStopButton =>
        _isStartRequestActive
        || CurrentSessionState is SessionState.Connecting or SessionState.Running or SessionState.Stopping
        || IsExternalSessionActivityActive;

    public bool CanStart =>
        !_isStartRequestActive
        && _connectionState?.IsSessionOperationActive != true
        && !IsExternalSessionActivityActive
        && (CurrentSessionState == SessionState.Connected
            || (CurrentSessionState == SessionState.Idle && _ensureConnectedAsync is not null));

    public bool CanStop => ShowStopButton && !_isStopRequestActive;

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
                AddListItemsFromDescriptor(descriptor, normalizedPath, string.Empty);
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
                    AddListItemsFromDescriptor(descriptor, sourcePath, string.Empty);
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
                AddListItemsFromDescriptor(descriptor, string.Empty, inlinePayload);
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
            var sessionActivityBlocked = _connectionState?.IsSessionOperationActive == true
                || IsExternalSessionActivityActive;
            LastErrorMessage = sessionActivityBlocked
                ? BuildSessionActivityBlockedMessage()
                : BuildSessionStateNotAllowedMessage(
                    CurrentSessionState,
                    T("Copilot.Action.Start", "启动"),
                    "start");
            await RecordFailedResultAsync(
                "Copilot.Start",
                UiOperationResult.Fail(
                    sessionActivityBlocked
                        ? UiErrorCode.OperationAlreadyRunning
                        : UiErrorCode.TaskQueueEditBlocked,
                    LastErrorMessage),
                cancellationToken);
            return;
        }

        using var copilotActivity = _connectionState?.TryBeginSessionOperation(
            "Copilot.Start",
            requireIdle: true);
        if (_connectionState is not null && copilotActivity is null)
        {
            LastErrorMessage = BuildSessionActivityBlockedMessage();
            await RecordFailedResultAsync(
                "Copilot.Start.SessionActivity",
                UiOperationResult.Fail(UiErrorCode.OperationAlreadyRunning, LastErrorMessage),
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
        CancellationTokenSource startRequestCts;
        TaskCompletionSource startRequestCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_startRequestGate)
        {
            startRequestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _startRequestCts = startRequestCts;
            _startRequestCompletion = startRequestCompletion;
        }

        var startToken = startRequestCts.Token;
        try
        {
            // WPF 对齐：Start() 先 ValidateStartAsync，校验失败直接返回，不发起连接
            var selection = await ValidateStartSelectionAsync(startToken);
            if (selection is null)
            {
                return;
            }

            var connectResult = await EnsureConnectedForStartAsync(startToken);
            if (!connectResult.Success)
            {
                StatusMessage = T("Copilot.Status.StartFailed", "启动失败。");
                LastErrorMessage = connectResult.Message;
                await RecordFailedResultAsync("Copilot.Start.Connect", connectResult, cancellationToken);
                return;
            }

            CurrentSessionState = Runtime.SessionService.CurrentState;
            AddLog(GetRootText("ConnectingToEmulator", "Connecting to emulator……"));
            var appendPlan = await AppendConfiguredCopilotAsync(selection, startToken);
            if (appendPlan is null)
            {
                return;
            }

            AddLog(GetRootText("Running", "Running……"));
            CurrentSessionState = Runtime.SessionService.CurrentState;
            SetStartRequestActive(false);
            keepRunOwner = true;
        }
        catch (OperationCanceledException) when (startRequestCts.IsCancellationRequested || !cancellationToken.IsCancellationRequested)
        {
            if (_hasActiveRun)
            {
                CompleteActiveRun();
            }

            StatusMessage = string.Empty;
            LastErrorMessage = string.Empty;
        }
        finally
        {
            lock (_startRequestGate)
            {
                _startRequestCts = null;
                _startRequestCompletion = null;
            }

            startRequestCts.Dispose();
            startRequestCompletion.TrySetResult();
            if (!keepRunOwner)
            {
                ResetFailedStartState();
                Runtime.SessionService.EndRun(CopilotRunOwner);
                SetStartRequestActive(false);
            }
        }
    }

    private void PrepareCopilotStart(int taskId, string activeItemName, string taskChain)
    {
        _activeItemName = activeItemName;
        _activeItemCoreTaskId = taskId;
        _activeTaskChain = taskChain;
        _hasActiveRun = true;
        foreach (var item in Items.Where(item => item.IsChecked || string.Equals(item.Name, activeItemName, StringComparison.Ordinal)))
        {
            item.Status = "Queued";
        }
    }

    private void ResetFailedStartState()
    {
        _hasActiveRun = false;
        _activeItemName = null;
        _activeItemCoreTaskId = null;
        _activeTaskChain = null;
        foreach (var item in Items.Where(item => item.Status == "Queued"))
        {
            item.Status = "Ready";
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
        OnPropertyChanged(nameof(ShowStopButton));
        OnPropertyChanged(nameof(StartButtonText));
    }

    private async Task<UiOperationResult> EnsureConnectedForStartAsync(CancellationToken cancellationToken)
    {
        CurrentSessionState = Runtime.SessionService.CurrentState;
        if (CurrentSessionState == SessionState.Connected)
        {
            if (_ensureConnectedAsync is not null)
            {
                var ensureResult = await _ensureConnectedAsync(cancellationToken);
                CurrentSessionState = Runtime.SessionService.CurrentState;
                if (ensureResult.Success && CurrentSessionState == SessionState.Connected)
                {
                    return ensureResult;
                }

                return UiOperationResult.Fail(
                    ensureResult.Error?.Code ?? UiErrorCode.SessionStateNotAllowed,
                    ensureResult.Message,
                    ensureResult.Error?.Details);
            }

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
        if (_isStopRequestActive)
        {
            return;
        }

        CancellationTokenSource? startRequestCts;
        Task? startRequestCompletionTask;
        lock (_startRequestGate)
        {
            startRequestCts = _startRequestCts;
            startRequestCompletionTask = _startRequestCompletion?.Task;
        }

        if (startRequestCts is null && !CanStop)
        {
            if (CurrentSessionState == SessionState.Stopping)
            {
                return;
            }

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

        SetStopRequestActive(true);
        try
        {
            startRequestCts?.Cancel();
            if (startRequestCompletionTask is not null)
            {
                await startRequestCompletionTask.WaitAsync(cancellationToken);
            }

            CurrentSessionState = Runtime.SessionService.CurrentState;
            if (IsExternalSessionActivityActive)
            {
                await StopExternalSessionActivityAsync(cancellationToken);
                return;
            }

            if (CurrentSessionState is SessionState.Running or SessionState.Stopping)
            {
                var owner = Runtime.SessionService.CurrentRunOwner;
                // Only a *different* feature's ownership blocks this Stop.  With no
                // owner at all there is nothing to protect, and refusing here used
                // to leave the enabled Stop inert while reporting a bogus
                // `Unknown` owner; TaskQueue already stops an ownerless run.
                if (!string.IsNullOrWhiteSpace(owner) && !Runtime.SessionService.IsRunOwner(CopilotRunOwner))
                {
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

                if (!await ApplyResultAsync(
                        await Runtime.ConnectFeatureService.StopAsync(cancellationToken),
                        "Copilot.Stop",
                        cancellationToken))
                {
                    CurrentSessionState = Runtime.SessionService.CurrentState;
                    SyncStoppedUiStateIfSessionNotActive();
                    return;
                }
            }
            else if (Runtime.SessionService.IsRunOwner(CopilotRunOwner))
            {
                Runtime.SessionService.EndRun(CopilotRunOwner);
            }

            CurrentSessionState = Runtime.SessionService.CurrentState;
            SyncStoppedUiStateIfSessionNotActive();
        }
        finally
        {
            SetStopRequestActive(false);
        }
    }

    private async Task StopExternalSessionActivityAsync(CancellationToken cancellationToken)
    {
        try
        {
            var owner = Runtime.SessionService.CurrentRunOwner;
            if (CurrentSessionState is SessionState.Running or SessionState.Stopping
                && !string.IsNullOrWhiteSpace(owner)
                && !Runtime.SessionService.IsRunOwner(CopilotRunOwner)
                && _stopRunOwnerAsync is not null)
            {
                // Route the external owner's own page first; the shared activity stop would
                // call back into the shell, which then tries to stop the same owner again.
                await _stopRunOwnerAsync(owner, cancellationToken);
            }
            else if (_stopActiveSessionOperationAsync is not null)
            {
                await _stopActiveSessionOperationAsync(cancellationToken);
            }
            else
            {
                if (!await ApplyResultAsync(
                             await Runtime.ConnectFeatureService.StopAsync(cancellationToken),
                             "Copilot.Stop",
                             cancellationToken))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RecordUnhandledExceptionAsync(
                "Copilot.Stop.SessionActivity",
                ex,
                UiErrorCode.UiOperationFailed,
                "Stopping the active session operation failed.",
                cancellationToken);
        }
        finally
        {
            CurrentSessionState = Runtime.SessionService.CurrentState;
            SyncStoppedUiStateIfSessionNotActive();
        }
    }

    private void SetStopRequestActive(bool value)
    {
        if (_isStopRequestActive == value)
        {
            return;
        }

        _isStopRequestActive = value;
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(ShowStartButton));
        OnPropertyChanged(nameof(ShowStopButton));
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(StopButtonText));
    }

    private async Task<string?> ResolveExecutionFilePathAsync(CopilotItemViewModel item, CancellationToken cancellationToken)
    {
        // WPF 对齐：列表项恒指向入列快照来源，执行产物（技能修正缓存文件）仅以局部返回值传给 core，
        // 不得写回 item.SourcePath——否则一次性缓存路径会随持久化扩散，缓存清理后遮蔽完好的
        // InlinePayload/原始文件（上游基线 b51fbd9 的写回无其他消费方依赖，ExecutionPathHint 展示
        // 与 LoadListItemAsync 重载均因写回指向缓存文件而偏离原始来源）
        return await ResolveExecutionFilePathAsync(
            item.SourcePath,
            item.InlinePayload,
            item.Name,
            cancellationToken);
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


    private string BuildSessionActivityBlockedMessage()
    {
        return _connectionState?.IsTimerSessionOperationActive == true
            ? T(
                "Copilot.Error.SessionActivity.TimerActive",
                "A timed task is active. Wait for it to finish before starting Copilot.")
            : T(
                "Copilot.Error.SessionActivity.OtherActive",
                "Another session operation is active. Stop or wait for it before starting Copilot.");
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
                    (DialogTextCatalog.ChromeKeys.HelpButton, DialogTextCatalog.ErrorDialogHelpButton(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.QqGroupButton, DialogTextCatalog.ErrorDialogQqGroupButton(nextLanguage)),
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
        var selectedCopilotId = SelectedItem.CopilotId;
        var feedbackTarget = selectedCopilotId > 0 ? selectedCopilotId.ToString() : itemName;
        // WPF 对齐：_recentlyRatedCopilotId 会话级去重同样作用于列表项反馈
        if (selectedCopilotId > 0 && IsCopilotRecentlyRated(selectedCopilotId))
        {
            StatusMessage = T("Copilot.Status.FeedbackAlreadyRated", "本会话已对该作业提交过反馈。");
            LastErrorMessage = string.Empty;
            await RecordEventAsync("Copilot.Feedback", StatusMessage, cancellationToken);
            return;
        }

        var result = await Runtime.CopilotFeatureService.SubmitFeedbackAsync(feedbackTarget, like, cancellationToken);
        if (!result.Success)
        {
            StatusMessage = T("Copilot.Status.FeedbackFailed", "反馈失败。");
            LastErrorMessage = result.Message;
            await RecordFailedResultAsync("Copilot.Feedback", result, cancellationToken);
            return;
        }

        if (selectedCopilotId > 0)
        {
            MarkCopilotRecentlyRated(selectedCopilotId);
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

        if (!IsCopilotCallbackForActiveRun(metadata))
        {
            return;
        }

        AppendWpfCallbackLog(callback, metadata);

        switch (callback.MsgName)
        {
            case "TaskChainStart":
                if (active is not null)
                {
                    active.Status = "Running";
                }
                break;
            case "TaskChainCompleted":
                if (active is not null)
                {
                    active.Status = "Success";
                }

                // WPF AsstProxy 对齐：isCopilotTaskChain 仅含 Copilot/SSSCopilot，ParadoxCopilot 运行不累计 UseCopilot
                var completedChain = metadata.TaskChain ?? _activeTaskChain;
                if (string.Equals(completedChain, "Copilot", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(completedChain, "SSSCopilot", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Runtime.AchievementTrackerService.AddProgressToGroup("UseCopilot");
                }
                break;
            case "AllTasksCompleted":
                if (active is not null)
                {
                    active.Status = "Success";
                }
                // WPF AsstProxy.AllTasksCompleted 对齐（冻结 :1186、:1279-1282）：主任务队列完成弹 AllTasksComplete
                // 通知（主任务队列属 TaskQueue 模块职责），else-if isCopilotTaskChain（仅 Copilot/SSSCopilot，:1019）
                // 弹「完成任务 + 任务链名」Toast；ParadoxCopilot 不弹。
                // UseCopilot 成就进度仅 TaskChainCompleted 累计，AllTasksCompleted 不重复计数（避免一次运行 +2）
                var finishedChain = metadata.TaskChain ?? _activeTaskChain;
                if (string.Equals(finishedChain, "Copilot", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(finishedChain, "SSSCopilot", StringComparison.OrdinalIgnoreCase))
                {
                    QueueWpfSystemNotification(
                        GetRootText("CompleteTask", "Complete task: ")
                        + GetRootText(finishedChain, finishedChain),
                        "copilot completion");
                }
                CompleteActiveRun();
                break;
            case "TaskChainStopped":
                if (active is not null)
                {
                    active.Status = "Stopped";
                }
                CompleteActiveRun();
                break;
            case "TaskChainError":
                if (active is not null)
                {
                    active.Status = "Error";
                }
                // WPF AsstProxy.TaskChainError 对齐：Copilot 任务链错误解锁 CopilotError 成就
                _ = Runtime.AchievementTrackerService.Unlock("CopilotError");
                LastErrorMessage = $"{callback.MsgName}: {callback.PayloadJson}";
                QueueWpfSystemNotification(
                    GetRootText("TaskError", "Task error: ")
                    + GetRootText(metadata.TaskChain ?? _activeTaskChain ?? "Copilot", metadata.TaskChain ?? _activeTaskChain ?? "Copilot"),
                    "copilot task failure");
                QueueWpfExternalErrorNotification(
                    GetRootText("TaskError", "Task error: ")
                    + GetRootText(metadata.TaskChain ?? _activeTaskChain ?? "Copilot", metadata.TaskChain ?? _activeTaskChain ?? "Copilot"));
                CompleteActiveRun();
                break;
            case "SubTaskError":
                LastErrorMessage = $"{callback.MsgName}: {callback.PayloadJson}";
                break;
        }
    }

    private void QueueWpfSystemNotification(string title, string reason)
    {
        var useSystemNotification = ReadUseSystemNotificationSetting();
        _ = Task.Run(async () =>
        {
            try
            {
                await Runtime.PlatformCapabilityService.SendSystemNotificationAsync(
                    new SystemNotificationRequest(title, string.Empty, [], UseSystemNotification: useSystemNotification));
            }
            catch (Exception ex)
            {
                Runtime.LogService.Error($"Failed to dispatch {reason} notification: {ex.Message}");
            }
        });
    }

    private bool ReadUseSystemNotificationSetting()
    {
        var values = Runtime.ConfigurationService.CurrentConfig.GlobalValues;
        if (!values.TryGetValue(LegacyConfigurationKeys.UseNotify, out var node) || node is not JsonValue value)
        {
            return true;
        }

        if (value.TryGetValue(out bool boolean))
        {
            return boolean;
        }

        return !value.TryGetValue(out string? text) || !bool.TryParse(text, out boolean) || boolean;
    }

    private void QueueWpfExternalErrorNotification(string message)
    {
        var requests = TaskQueuePageViewModel.BuildAutomaticExternalNotificationRequests(
            "TaskChainError",
            new TaskQueuePageViewModel.TaskQueueSystemNotification(
                message,
                message,
                "Copilot.Notification.Error",
                "copilot task failure"),
            Runtime.ConfigurationService.CurrentConfig);
        if (requests.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var request in requests)
            {
                try
                {
                    var result = await Runtime.NotificationProviderFeatureService.SendTestAsync(request);
                    if (!result.Success)
                    {
                        Runtime.LogService.Warn($"Failed to dispatch Copilot external notification via {request.Provider}: {result.Message}");
                        // WPF parity (frozen 23ddd271 ExternalNotificationService.SendAsync):
                        // failed automatic sends surface a toast; mirror the main queue's
                        // error-dialog feedback instead of leaving only a log line.
                        await Runtime.DialogFeatureService.ReportErrorAsync(
                            "Copilot.Notification.Error",
                            UiOperationResult.Fail(
                                result.Error?.Code ?? UiErrorCode.NotificationProviderFailed,
                                $"External notification via {request.Provider} failed: {result.Message}",
                                result.Error?.Details));
                    }
                }
                catch (Exception ex)
                {
                    Runtime.LogService.Warn($"Failed to dispatch Copilot external notification via {request.Provider}: {ex.Message}");
                    await Runtime.DialogFeatureService.ReportErrorAsync(
                        "Copilot.Notification.Error",
                        UiOperationResult.Fail(
                            UiErrorCode.NotificationProviderFailed,
                            $"External notification via {request.Provider} failed."));
                }
            }
        });
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

            // WPF 对齐（冻结 AsstProxy.cs:1476-1480）：缺失干员分组 >= 2 时解锁 Irreplaceable 成就
            if (groups is not null && groups.Count >= 2)
            {
                _ = Runtime.AchievementTrackerService.Unlock("Irreplaceable");
            }

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
                _ = CompleteFirstCheckedCopilotListItemAsync();
                break;
        }
    }

    private async Task CompleteFirstCheckedCopilotListItemAsync()
    {
        if (!UseCopilotList)
        {
            return;
        }

        var completed = Items.FirstOrDefault(item => item.IsChecked);
        if (completed is null)
        {
            return;
        }

        completed.IsChecked = false;
        var persistResult = await PersistItemsAsync(CancellationToken.None);
        if (!persistResult.Success)
        {
            completed.IsChecked = true;
            LastErrorMessage = persistResult.Message;
            await RecordFailedResultAsync("Copilot.List.TaskSuccess", persistResult, CancellationToken.None);
            return;
        }

        await RecordEventAsync("Copilot.List.TaskSuccess", $"Completed copilot list item `{completed.Name}`.", CancellationToken.None);

        // WPF 对齐：同一作业 id 的全部列表条目都成功、且未忽略干员属性要求时才自动点赞
        if (completed.CopilotId > 0
            && _copilotIdList.Remove(completed.CopilotId)
            && !_copilotIdList.Contains(completed.CopilotId)
            && !_hasRequirementIgnored)
        {
            QueueAutomaticCopilotLike(completed);
        }
    }

    private void QueueAutomaticCopilotLike(CopilotItemViewModel completed)
    {
        if (completed.CopilotId <= 0)
        {
            return;
        }

        // WPF 对齐：_recentlyRatedCopilotId 会话级去重——同一 id 本会话已成功评分过则不再自动点赞；失败时释放占用以允许重试
        if (!TryReserveCopilotRating(completed.CopilotId))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await Runtime.CopilotFeatureService.SubmitFeedbackAsync(completed.CopilotId.ToString(), like: true);
                if (!result.Success)
                {
                    ReleaseCopilotRatingReservation(completed.CopilotId);
                    Runtime.LogService.Warn($"Failed to automatically like Copilot {completed.CopilotId}: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                ReleaseCopilotRatingReservation(completed.CopilotId);
                Runtime.LogService.Warn($"Failed to automatically like Copilot {completed.CopilotId}: {ex.Message}");
            }
        });
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
                _hasRequirementIgnored = true;
                AppendBattleFormationUnavailableLog(payload.Details, timestamp);
                break;
            case "CopilotAction":
                AppendCopilotActionLog(payload.Details, timestamp);
                break;
            case "CopilotListLoadTaskFileSuccess":
                _hasRequirementIgnored = false;
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
                // WPF AsstProxy.UnsupportedLevel 对齐：仅记录错误日志并触发资源更新（资源更新属既有 non-goal），
                // 不解锁 MapOutdated——WPF 的 MapOutdated 在加载/启动前校验（FindMap/Verify 失败）时解锁
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
        // WPF CopilotContentConverter 对齐（冻结 CopilotHelper.cs:227）：类型值不裁剪、大小写敏感
        // （"sss"/"SSS " 均不识别为 SSS，按普通作业处理）
        if (string.IsNullOrWhiteSpace(type))
        {
            return MainStageStoryCollectionSideStoryType;
        }

        return type switch
        {
            "主线" or MainStageStoryCollectionSideStoryType or "MainStageStoryCollectionSideStory" or "Copilot"
                => MainStageStoryCollectionSideStoryType,
            "SSS" or SecurityServiceStationType or "SSSCopilot"
                => SecurityServiceStationType,
            "悖论" or ParadoxSimulationType or "ParadoxCopilot"
                => ParadoxSimulationType,
            "活动" or OtherActivityType or "OtherActivityStage"
                => OtherActivityType,
            _ => type,
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
