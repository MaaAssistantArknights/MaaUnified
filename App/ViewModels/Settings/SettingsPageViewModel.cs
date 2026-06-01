using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.Services;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Constants;
using MAAUnified.Compat.Runtime;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.App.ViewModels.Settings;

public sealed partial class SettingsPageViewModel : PageViewModelBase
{
    private const string ThemeModeKey = "Theme.Mode";
    private const string DefaultTheme = "Light";
    private const string DefaultLanguage = UiLanguageCatalog.DefaultLanguage;
    private const string DefaultBackgroundStretchMode = "Fill";
    private const string DefaultLogItemDateFormat = "HH:mm:ss";
    private const string DefaultOperNameLanguage = "OperNameLanguageMAA";
    private const string DefaultInverseClearMode = "Clear";
    private const string DeveloperModeConfigKey = "GUI.DeveloperMode";
    private const string SoftwareRenderingConfigKey = ConfigurationKeys.IgnoreBadModulesAndUseSoftwareRendering;
    private const string ShowGuiHotkeyName = HotkeyConfigurationCodec.ShowGuiHotkeyName;
    private const string LinkStartHotkeyName = HotkeyConfigurationCodec.LinkStartHotkeyName;
    private static readonly string DefaultHotkeyShowGui = HotkeyConfigurationCodec.DefaultHotkeyShowGui;
    private static readonly string DefaultHotkeyLinkStart = HotkeyConfigurationCodec.DefaultHotkeyLinkStart;
    private const int EmulatorWaitSecondsMin = 0;
    private const int EmulatorWaitSecondsMax = 600;
    private const int DefaultEmulatorWaitSeconds = 60;
    private const int DefaultRemotePollIntervalMs = 1000;
    private const int DefaultTaskTimeoutMinutes = 60;
    private const int DefaultReminderIntervalMinutes = 30;
    private const int BackgroundOpacityMin = 0;
    private const int BackgroundOpacityMax = 100;
    private const int BackgroundBlurMin = 0;
    private const int BackgroundBlurMax = 80;
    private const int DefaultUiScalePercent = 100;
    private const int UiScalePercentMin = 70;
    private const int UiScalePercentMax = 140;
    private const int AutostartFeedbackDelayMs = 1000;
    private const int TimerSlotCount = 8;
    private const int TimerHourMin = 0;
    private const int TimerHourMax = 23;
    private const int TimerMinuteMin = 0;
    private const int TimerMinuteMax = 59;
    private const int DefaultTimerHour = 7;
    private const int DefaultTimerMinute = 0;
    private const string IssueReportHelpUrl = "https://maa.plus/docs/";
    private const string IssueReportIssueEntryUrl = "https://github.com/MaaAssistantArknights/MaaAssistantArknights/issues/new/choose";
    private const string AboutOfficialWebsiteUrl = "https://maa.plus/";
    private const string AboutCommunityUrl = "https://github.com/MaaAssistantArknights/MaaAssistantArknights/discussions";
    private const string AboutDownloadUrl = "https://github.com/MaaAssistantArknights/MaaAssistantArknights/releases";
    private const string AchievementGuideUrl = "https://maa.plus/docs/manual/introduction/";
    private const string VersionUpdateChangelogUrl = "https://github.com/MaaAssistantArknights/MaaAssistantArknights/releases";
    private const string VersionUpdateResourceRepositoryUrl = "https://github.com/MaaAssistantArknights/MaaResource";
    private const string VersionUpdateMirrorChyanUrl = "https://mirrorchyan.com/?source=maaunified-settings";
    private const string SettingsDataBucketGuiBackground = "GuiBackground";
    private const string SettingsDataBucketHotKey = "HotKey";
    private const string SettingsDataBucketTimer = "Timer";
    private const string SettingsDataBucketStartPerformance = "StartPerformance";
    private const string SettingsDataBucketAutostart = "Autostart";
    private const string SettingsDataBucketConnectionGame = "ConnectionGame";
    private const string SettingsDataBucketRemoteControl = "RemoteControl";
    private const string SettingsDataBucketExternalNotification = "ExternalNotification";
    private const string SettingsDataBucketVersionUpdate = "VersionUpdate";
    private const string SettingsDataBucketAchievement = "Achievement";
    private static readonly string[] AllSettingsDataBuckets =
    [
        SettingsDataBucketGuiBackground,
        SettingsDataBucketHotKey,
        SettingsDataBucketTimer,
        SettingsDataBucketStartPerformance,
        SettingsDataBucketAutostart,
        SettingsDataBucketConnectionGame,
        SettingsDataBucketRemoteControl,
        SettingsDataBucketExternalNotification,
        SettingsDataBucketVersionUpdate,
        SettingsDataBucketAchievement,
    ];
    private static readonly string[] SectionOrder =
    [
        "ConfigurationManager",
        "Timer",
        "Performance",
        "Game",
        "Connect",
        "Start",
        "RemoteControl",
        "GUI",
        "Background",
        "ExternalNotification",
        "HotKey",
        "Achievement",
        "VersionUpdate",
        "IssueReport",
        "About",
    ];
    private static readonly string[] DefaultNotificationProviders =
    [
        "Smtp",
        "ServerChan",
        "Bark",
        "Discord",
        "DingTalk",
        "Telegram",
        "Qmsg",
        "Gotify",
        "CustomWebhook",
    ];
    private static readonly IReadOnlyDictionary<string, string> EmptySettingUpdates =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ProviderConfigKeyMap =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Smtp"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["server"] = ConfigurationKeys.ExternalNotificationSmtpServer,
                ["port"] = ConfigurationKeys.ExternalNotificationSmtpPort,
                ["user"] = ConfigurationKeys.ExternalNotificationSmtpUser,
                ["password"] = ConfigurationKeys.ExternalNotificationSmtpPassword,
                ["useSsl"] = ConfigurationKeys.ExternalNotificationSmtpUseSsl,
                ["requiresAuthentication"] = ConfigurationKeys.ExternalNotificationSmtpRequiresAuthentication,
                ["from"] = ConfigurationKeys.ExternalNotificationSmtpFrom,
                ["to"] = ConfigurationKeys.ExternalNotificationSmtpTo,
            },
            ["ServerChan"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sendKey"] = ConfigurationKeys.ExternalNotificationServerChanSendKey,
            },
            ["Bark"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sendKey"] = ConfigurationKeys.ExternalNotificationBarkSendKey,
                ["server"] = ConfigurationKeys.ExternalNotificationBarkServer,
            },
            ["Discord"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["botToken"] = ConfigurationKeys.ExternalNotificationDiscordBotToken,
                ["userId"] = ConfigurationKeys.ExternalNotificationDiscordUserId,
                ["webhookUrl"] = ConfigurationKeys.ExternalNotificationDiscordWebhookUrl,
            },
            ["DingTalk"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["accessToken"] = ConfigurationKeys.ExternalNotificationDingTalkAccessToken,
                ["secret"] = ConfigurationKeys.ExternalNotificationDingTalkSecret,
            },
            ["Telegram"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["botToken"] = ConfigurationKeys.ExternalNotificationTelegramBotToken,
                ["chatId"] = ConfigurationKeys.ExternalNotificationTelegramChatId,
                ["topicId"] = ConfigurationKeys.ExternalNotificationTelegramTopicId,
            },
            ["Qmsg"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["server"] = ConfigurationKeys.ExternalNotificationQmsgServer,
                ["key"] = ConfigurationKeys.ExternalNotificationQmsgKey,
                ["user"] = ConfigurationKeys.ExternalNotificationQmsgUser,
                ["bot"] = ConfigurationKeys.ExternalNotificationQmsgBot,
            },
            ["Gotify"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["server"] = ConfigurationKeys.ExternalNotificationGotifyServer,
                ["token"] = ConfigurationKeys.ExternalNotificationGotifyToken,
            },
            ["CustomWebhook"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["url"] = ConfigurationKeys.ExternalNotificationCustomWebhookUrl,
                ["body"] = ConfigurationKeys.ExternalNotificationCustomWebhookBody,
            },
        };
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ProviderFieldPropertyMap =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ServerChan"] = [nameof(ServerChanSendKey)],
            ["Telegram"] = [nameof(TelegramBotToken), nameof(TelegramChatId), nameof(TelegramTopicId)],
            ["Discord"] = [nameof(DiscordBotToken), nameof(DiscordUserId), nameof(DiscordWebhookUrl)],
            ["DingTalk"] = [nameof(DingTalkAccessToken), nameof(DingTalkSecret)],
            ["Smtp"] = [
                nameof(SmtpServer),
                nameof(SmtpPort),
                nameof(SmtpUseSsl),
                nameof(SmtpRequireAuthentication),
                nameof(SmtpUser),
                nameof(SmtpPassword),
                nameof(SmtpFrom),
                nameof(SmtpTo),
            ],
            ["Bark"] = [nameof(BarkServer), nameof(BarkSendKey)],
            ["Qmsg"] = [nameof(QmsgServer), nameof(QmsgKey), nameof(QmsgUser), nameof(QmsgBot)],
            ["Gotify"] = [nameof(GotifyServer), nameof(GotifyToken)],
            ["CustomWebhook"] = [nameof(CustomWebhookUrl), nameof(CustomWebhookBody)],
        };
    private static readonly IReadOnlyList<string> ExternalNotificationSectionPropertyNames =
        [
            nameof(ServerChanSectionVisible),
            nameof(TelegramSectionVisible),
            nameof(DiscordSectionVisible),
            nameof(DiscordWebhookSectionVisible),
            nameof(DingTalkSectionVisible),
            nameof(SmtpSectionVisible),
            nameof(BarkSectionVisible),
            nameof(QmsgSectionVisible),
            nameof(GotifySectionVisible),
            nameof(CustomWebhookSectionVisible),
        ];
    private static readonly JsonSerializerOptions ConfigExportSerializerOptions = new()
    {
        WriteIndented = true,
    };

    private SettingsSectionViewModel? _selectedSection;
    private readonly SemaphoreSlim _guiSaveSemaphore = new(1, 1);
    private readonly SemaphoreSlim _configurationProfileSwitchSemaphore = new(1, 1);
    private readonly Action<LocalizationFallbackInfo>? _localizationFallbackReporter;
    private CancellationTokenSource? _guiAutoSaveCts;
    private CancellationTokenSource? _startPerformanceAutoSaveCts;
    private CancellationTokenSource? _timerAutoSaveCts;
    private CancellationTokenSource? _connectionGameAutoSaveCts;
    private CancellationTokenSource? _remoteControlAutoSaveCts;
    private CancellationTokenSource? _externalNotificationAutoSaveCts;
    private CancellationTokenSource? _versionUpdateAutoSaveCts;
    private CancellationTokenSource? _achievementAutoSaveCts;
    private CancellationTokenSource? _autostartAutoApplyCts;
    private CancellationTokenSource? _autostartFeedbackCts;
    private readonly SemaphoreSlim _settingsDataLoadSemaphore = new(1, 1);
    private readonly HashSet<string> _loadedSettingsDataBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _settingsSaveFailureGate = new();
    private readonly HashSet<string> _settingsSaveFailureScopes = new(StringComparer.Ordinal);
    private TaskCompletionSource<bool> _startupAnnouncementCompletionSource = CreateCompletedTaskCompletionSource();
    private bool _deferredSectionDataLoadEnabled;
    private bool _deferredSectionDataLoadRequested;
    private bool _suppressVersionUpdateResourceRefresh;
    private long _gpuRefreshSequence;
    private bool _autoSaveReady;
    private int _autoSaveSuspensionCount;
    private int _viewCompositionDepth;
    private bool _repairTimerProfilesWhenAutoSaveResumes;
    private bool _suppressPageAutoSave;
    private bool _suppressGuiAutoSave;
    private bool _suppressGuiPreview;
    private bool _suppressSelectedLanguageChangeRequest;
    private bool _suppressStartPerformanceDirtyTracking;
    private bool _suppressConfigurationProfileSelectionHandling;
    private string _theme = DefaultTheme;
    private string _language = DefaultLanguage;
    private string _selectedLanguageValue = DefaultLanguage;
    private string _logItemDateFormatString = DefaultLogItemDateFormat;
    private string _operNameLanguage = DefaultOperNameLanguage;
    private string _inverseClearMode = DefaultInverseClearMode;
    private bool _useTray = true;
    private bool _useNotify = true;
    private bool _minimizeToTray;
    private bool _windowTitleScrollable;
    private int _uiScalePercent = DefaultUiScalePercent;
    private bool _useSoftwareRendering;
    private bool _developerModeEnabled;
    private bool _startSelf;
    private string _autostartStatus = string.Empty;
    private DateTimeOffset? _lastAutostartToggleAt;
    private string _autostartWarningMessage = string.Empty;
    private string _autostartErrorMessage = string.Empty;
    private string _hotkeyShowGui = DefaultHotkeyShowGui;
    private string _hotkeyLinkStart = DefaultHotkeyLinkStart;
    private string _persistedHotkeyShowGui = DefaultHotkeyShowGui;
    private string _persistedHotkeyLinkStart = DefaultHotkeyLinkStart;
    private string _hotkeyStatusMessage = string.Empty;
    private string _hotkeyWarningMessage = string.Empty;
    private string _hotkeyErrorMessage = string.Empty;
    private readonly HotkeySettingItemViewModel _showGuiHotkeyState = new(ShowGuiHotkeyName);
    private readonly HotkeySettingItemViewModel _linkStartHotkeyState = new(LinkStartHotkeyName);
    private string _notificationTitle = string.Empty;
    private string _notificationMessage = string.Empty;
    private string _issueReportPath = string.Empty;
    private string _issueReportStatusMessage = string.Empty;
    private string _issueReportErrorMessage = string.Empty;
    private string _remoteGetTaskEndpoint = string.Empty;
    private string _remoteReportEndpoint = string.Empty;
    private string _remoteUserIdentity = string.Empty;
    private string _remoteDeviceIdentity = string.Empty;
    private int _remotePollInterval = DefaultRemotePollIntervalMs;
    private string _remoteControlStatusMessage = string.Empty;
    private string _remoteControlWarningMessage = string.Empty;
    private string _remoteControlErrorMessage = string.Empty;
    private string _backgroundImagePath = string.Empty;
    private int _backgroundOpacity = 45;
    private int _backgroundBlur = 12;
    private string _backgroundStretchMode = DefaultBackgroundStretchMode;
    private bool _hasPendingGuiChanges;
    private string _guiValidationMessage = string.Empty;
    private string _guiSectionValidationMessage = string.Empty;
    private string _backgroundValidationMessage = string.Empty;
    private DateTimeOffset? _lastSuccessfulGuiSaveAt;
    private bool _runDirectly;
    private bool _minimizeDirectly;
    private bool _openEmulatorAfterLaunch;
    private string _emulatorPath = string.Empty;
    private string _emulatorAddCommand = string.Empty;
    private int _emulatorWaitSeconds = DefaultEmulatorWaitSeconds;
    private bool _performanceUseGpu;
    private bool _performanceAllowDeprecatedGpu;
    private string _performancePreferredGpuDescription = string.Empty;
    private string _performancePreferredGpuInstancePath = string.Empty;
    private IReadOnlyList<GpuOptionDisplayItem> _availableGpuOptions = [];
    private GpuOptionDisplayItem? _selectedGpuOption;
    private string _gpuSupportMessage = string.Empty;
    private string _gpuWarningMessage = string.Empty;
    private string _gpuCustomDescription = string.Empty;
    private string _gpuCustomInstancePath = string.Empty;
    private bool _isGpuSelectionEnabled;
    private bool _isGpuDeprecatedToggleEnabled;
    private bool _isGpuCustomSelectionFieldsVisible;
    private bool _showGpuRestartRequiredHint;
    private bool _suppressGpuUiRefresh;
    private bool _suppressGpuSelectionChange;
    private bool _deploymentWithPause;
    private string _startsWithScript = string.Empty;
    private string _endsWithScript = string.Empty;
    private bool _copilotWithScript;
    private bool _manualStopWithScript;
    private bool _blockSleep;
    private bool _blockSleepWithScreenOn = true;
    private bool _enablePenguin = true;
    private bool _enableYituliu = true;
    private string _penguinId = string.Empty;
    private int _taskTimeoutMinutes = DefaultTaskTimeoutMinutes;
    private int _reminderIntervalMinutes = DefaultReminderIntervalMinutes;
    private bool _hasPendingStartPerformanceChanges;
    private string _startPerformanceValidationMessage = string.Empty;
    private DateTimeOffset? _lastSuccessfulStartPerformanceSaveAt;
    private bool _forceScheduledStart;
    private bool _showWindowBeforeForceScheduledStart;
    private bool _customTimerConfig;
    private bool _hasPendingTimerChanges;
    private string _timerValidationMessage = string.Empty;
    private DateTimeOffset? _lastSuccessfulTimerSaveAt;
    private bool _suppressTimerDirtyTracking;
    private bool _externalNotificationEnabled;
    private bool _externalNotificationSendWhenComplete = true;
    private bool _externalNotificationSendWhenError = true;
    private bool _externalNotificationSendWhenTimeout = true;
    private bool _externalNotificationEnableDetails;
    private string _externalNotificationStatusMessage = string.Empty;
    private string _externalNotificationWarningMessage = string.Empty;
    private string _externalNotificationErrorMessage = string.Empty;
    private string _selectedNotificationProvider = "Smtp";
    private string _notificationProviderParametersText = string.Empty;
    private string _versionUpdateProxy = string.Empty;
    private string _versionUpdateProxyType = "http";
    private string _versionUpdateVersionType = "Stable";
    private string _versionUpdateResourceSource = "Github";
    private bool _versionUpdateForceGithubSource;
    private string _versionUpdateMirrorChyanCdk = string.Empty;
    private string _versionUpdateMirrorChyanCdkExpired = string.Empty;
    private bool _versionUpdateStartupCheck = true;
    private bool _versionUpdateScheduledCheck;
    private string _versionUpdateResourceApi = string.Empty;
    private bool _versionUpdateAllowNightly;
    private bool _versionUpdateAcknowledgedNightlyWarning;
    private bool _versionUpdateUseAria2;
    private bool _versionUpdateAutoDownload = true;
    private bool _versionUpdateAutoInstall;
    private string _versionUpdateName = string.Empty;
    private string _versionUpdateBody = string.Empty;
    private bool _versionUpdateIsFirstBoot;
    private string _versionUpdatePackage = string.Empty;
    private bool _versionUpdateDoNotShow;
    private bool _versionUpdateStartupCheckTriggered;
    private string _versionUpdateLastScheduledMinuteKey = string.Empty;
    private int _versionUpdateScheduledCheckRunning;
    private string _versionUpdateActivityMessage = string.Empty;
    private string _versionUpdateStatusMessage = string.Empty;
    private string _versionUpdateErrorMessage = string.Empty;
    private bool _hasPendingVersionUpdateAvailability;
    private bool _hasPendingResourceUpdateAvailability;
    private string _pendingResourceUpdateDisplayVersion = string.Empty;
    private string _pendingResourceUpdateReleaseNote = string.Empty;
    private DateTimeOffset? _pendingResourceUpdateVersionTimestamp;
    private IReadOnlyList<DisplayValueOption> _themeOptions = [];
    private IReadOnlyList<DisplayValueOption> _supportedLanguages = [];
    private IReadOnlyList<DisplayValueOption> _backgroundStretchModes = [];
    private IReadOnlyList<DisplayValueOption> _operNameLanguageOptions = [];
    private IReadOnlyList<DisplayValueOption> _inverseClearModeOptions = [];
    private IReadOnlyList<DisplayValueOption> _versionUpdateVersionTypeOptions = [];
    private IReadOnlyList<DisplayValueOption> _versionUpdateProxyTypeOptions = [];
    private IReadOnlyList<DisplayValueOption> _versionUpdateResourceSourceOptions = [];
    private string _updatePanelUiVersion = "unknown";
    private string _updatePanelCoreVersion = "unknown";
    private string _updatePanelBuildTime = "unknown";
    private string _updatePanelResourceVersion = string.Empty;
    private string _updatePanelResourceTime = string.Empty;
    private bool _isVersionUpdateActionRunning;
    private string _configurationManagerSelectedProfile = string.Empty;
    private string _configurationManagerNewProfileName = string.Empty;
    private bool _suppressConfigurationManagerSaveAsNewSuccessReset;
    private string _configurationManagerSaveAsNewSucceededText = string.Empty;
    private string _configurationManagerSaveAsNewFailedText = string.Empty;
    private string _configurationManagerImportSucceededText = string.Empty;
    private string _configurationManagerStatusMessage = string.Empty;
    private string _configurationManagerErrorMessage = string.Empty;
    private bool _achievementPopupDisabled;
    private bool _achievementPopupAutoClose = AchievementPolicy.Default.PopupAutoClose;
    private int _achievementUnlockedCount;
    private int _achievementTotalCount;
    private bool _achievementDebugEnabled;
    private int _achievementDebugClickCount;
    private string _achievementDebugMedalColor = "#B0B0B0";
    private string _achievementDebugTip = "MAA";
    private string _achievementStatusMessage = string.Empty;
    private string _achievementErrorMessage = string.Empty;
    private string _achievementPolicySummary = string.Empty;
    private string _aboutVersionInfo = string.Empty;
    private string _aboutStatusMessage = string.Empty;
    private string _aboutErrorMessage = string.Empty;
    private readonly Dictionary<string, string> _notificationProviderParameters =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _providerParameterValues =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressProviderParameterDirtyTracking;
    private bool _isApplyingProviderParametersText;
    private readonly Func<string, CancellationToken, Task<UiOperationResult>> _openExternalTargetAsync;
    private readonly IAppDialogService _dialogService;
    private readonly IUiLanguageCoordinator _uiLanguageCoordinator;
    private readonly Func<string?> _coreVersionResolver;
    private readonly HttpClient _aboutAnnouncementHttpClient;
    private readonly TimeSpan _aboutAnnouncementTimeout;
    private readonly DispatcherTimer _versionUpdateSchedulerTimer = new()
    {
        Interval = TimeSpan.FromSeconds(10),
    };
    private string? _pendingLanguageChangeTarget;
    private Task _pendingUnifiedLanguageApplyTask = Task.CompletedTask;
    private int _blockingOperationOverlayDepth;
    private bool _isBlockingOperationOverlayVisible;

    public SettingsPageViewModel(
        MAAUnifiedRuntime runtime,
        ConnectionGameSharedStateViewModel connectionGameSharedState,
        Action<LocalizationFallbackInfo>? localizationFallbackReporter = null,
        Func<string, CancellationToken, Task<UiOperationResult>>? openExternalTargetAsync = null,
        IAppDialogService? dialogService = null,
        Func<string?>? coreVersionResolver = null,
        HttpClient? aboutAnnouncementHttpClient = null,
        TimeSpan? aboutAnnouncementTimeout = null)
        : base(runtime)
    {
        _localizationFallbackReporter = localizationFallbackReporter;
        _openExternalTargetAsync = openExternalTargetAsync ?? OpenExternalTargetAsync;
        _dialogService = dialogService ?? NoOpAppDialogService.Instance;
        _uiLanguageCoordinator = runtime.UiLanguageCoordinator;
        _coreVersionResolver = coreVersionResolver ?? ResolveCurrentCoreVersion;
        _aboutAnnouncementHttpClient = aboutAnnouncementHttpClient ?? SharedAboutAnnouncementHttpClient;
        _aboutAnnouncementTimeout = aboutAnnouncementTimeout is { } timeout && timeout > TimeSpan.Zero
            ? timeout
            : DefaultAboutAnnouncementTimeout;
        _uiLanguageCoordinator.LanguageChanged += OnUnifiedLanguageChanged;
        _versionUpdateSchedulerTimer.Tick += OnVersionUpdateSchedulerTick;
        RootTexts = new RootLocalizationTextMap("Root.Localization.Settings");
        RootTexts.FallbackReported += info => _localizationFallbackReporter?.Invoke(info);
        RootTexts.Language = _language;
        InitializeNotificationTemplateDefaults();
        ConnectionGameSharedState = connectionGameSharedState;
        ConnectionGameSharedState.SetLanguage(_language);
        runtime.AchievementTrackerService.SetCurrentLanguage(_language);
        SupportedLanguages = SettingsOptionCatalog.BuildLanguageOptions();
        runtime.AchievementTrackerService.StateChanged += OnAchievementTrackerStateChanged;
        (_updatePanelUiVersion, _updatePanelBuildTime) = BuildVersionUpdateUiMetadata();
        _updatePanelCoreVersion = ResolveCoreVersionOrUnknown();
        RebuildGuiOptionLists();
        RebuildVersionUpdateOptionLists();
        _aboutVersionInfo = BuildAboutVersionInfo();
        _achievementDebugTip = AchievementTextCatalog.GetPallasString(1, 10);
        UpdateAchievementPolicySummary(AchievementPolicy.Default);
        Sections = new ObservableCollection<SettingsSectionViewModel>();
        CurrentSectionActions = new ObservableCollection<SettingsSectionActionItem>();
        RefreshHotkeyUiText();
        ApplyHotkeyDraft(_showGuiHotkeyState, DefaultHotkeyShowGui, nameof(HotkeyShowGui), clearFeedback: false);
        ApplyHotkeyDraft(_linkStartHotkeyState, DefaultHotkeyLinkStart, nameof(HotkeyLinkStart), clearFeedback: false);
        RebuildSections();

        Timers = new ObservableCollection<TimerSlotViewModel>(
            Enumerable.Range(1, TimerSlotCount).Select(i => new TimerSlotViewModel(i)));
        foreach (var slot in Timers)
        {
            slot.PropertyChanged += OnTimerSlotPropertyChanged;
        }

        ApplyGpuUiStateBeforeProbe();
        SelectedSection = Sections[0];
        PropertyChanged += OnSettingsPropertyChanged;
        ConnectionGameSharedState.PropertyChanged += OnConnectionGameSharedStateChanged;
    }

    public RootLocalizationTextMap RootTexts { get; }

    public event EventHandler<GuiSettingsAppliedEventArgs>? GuiSettingsApplied;
    public event EventHandler<GuiSettingsPreviewChangedEventArgs>? GuiSettingsPreviewChanged;
    public event EventHandler? ResourceVersionUpdated;
    public event EventHandler? UpdateAvailabilityChanged;
    public event EventHandler<ConfigurationContextChangedEventArgs>? ConfigurationContextChanged;
    public Func<CancellationToken, Task<UiOperationResult>>? BeforeConfigurationProfileSwitchAsync { private get; set; }
    public Func<ConfigurationContextChangedEventArgs, CancellationToken, Task>? ApplyConfigurationContextChangedAsync { private get; set; }

    public ObservableCollection<SettingsSectionViewModel> Sections { get; }

    public ObservableCollection<SettingsSectionActionItem> CurrentSectionActions { get; }

    public ObservableCollection<TimerSlotViewModel> Timers { get; }

    public ObservableCollection<string> ConfigurationProfiles { get; } = new();

    public ConnectionGameSharedStateViewModel ConnectionGameSharedState { get; }

    public IReadOnlyList<DisplayValueOption> ThemeOptions
    {
        get => _themeOptions;
        private set => SetProperty(ref _themeOptions, value);
    }

    public IReadOnlyList<DisplayValueOption> SupportedLanguages
    {
        get => _supportedLanguages;
        private set => SetProperty(ref _supportedLanguages, value);
    }

    public IReadOnlyList<DisplayValueOption> BackgroundStretchModes
    {
        get => _backgroundStretchModes;
        private set => SetProperty(ref _backgroundStretchModes, value);
    }

    public IReadOnlyList<DisplayValueOption> OperNameLanguageOptions
    {
        get => _operNameLanguageOptions;
        private set => SetProperty(ref _operNameLanguageOptions, value);
    }

    public IReadOnlyList<DisplayValueOption> InverseClearModeOptions
    {
        get => _inverseClearModeOptions;
        private set => SetProperty(ref _inverseClearModeOptions, value);
    }

    public IReadOnlyList<string> LogItemDateFormatOptions { get; } = SettingsOptionCatalog.GetLogItemDateFormatOptions();

    public DisplayValueOption? SelectedThemeOption
    {
        get => ThemeOptions.FirstOrDefault(
            option => string.Equals(option.Value, Theme, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null)
            {
                return;
            }

            Theme = value.Value;
        }
    }

    public DisplayValueOption? SelectedLanguageOption
    {
        get => SupportedLanguages.FirstOrDefault(
            option => string.Equals(option.Value, SelectedLanguageValue, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null)
            {
                return;
            }

            SelectedLanguageValue = value.Value;
        }
    }

    public string SelectedLanguageValue
    {
        get => _pendingLanguageChangeTarget ?? _selectedLanguageValue;
        set => SetSelectedLanguageValue(value, requestLanguageChange: true);
    }

    public bool IsBlockingOperationOverlayVisible
    {
        get => _isBlockingOperationOverlayVisible;
        private set => SetProperty(ref _isBlockingOperationOverlayVisible, value);
    }

    public DisplayValueOption? SelectedBackgroundStretchModeOption
    {
        get => BackgroundStretchModes.FirstOrDefault(
            option => string.Equals(option.Value, BackgroundStretchMode, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null)
            {
                return;
            }

            BackgroundStretchMode = value.Value;
        }
    }

    public DisplayValueOption? SelectedOperNameLanguageOption
    {
        get => OperNameLanguageOptions.FirstOrDefault(
            option => string.Equals(option.Value, OperNameLanguage, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null)
            {
                return;
            }

            OperNameLanguage = value.Value;
        }
    }

    public DisplayValueOption? SelectedInverseClearModeOption
    {
        get => InverseClearModeOptions.FirstOrDefault(
            option => string.Equals(option.Value, InverseClearMode, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null)
            {
                return;
            }

            InverseClearMode = value.Value;
        }
    }

    public IReadOnlyList<DisplayValueOption> VersionUpdateVersionTypeOptions
    {
        get => _versionUpdateVersionTypeOptions;
        private set => SetProperty(ref _versionUpdateVersionTypeOptions, value);
    }

    public IReadOnlyList<DisplayValueOption> VersionUpdateProxyTypeOptions
    {
        get => _versionUpdateProxyTypeOptions;
        private set => SetProperty(ref _versionUpdateProxyTypeOptions, value);
    }

    public IReadOnlyList<DisplayValueOption> VersionUpdateResourceSourceOptions
    {
        get => _versionUpdateResourceSourceOptions;
        private set => SetProperty(ref _versionUpdateResourceSourceOptions, value);
    }

    public DisplayValueOption? SelectedVersionUpdateVersionTypeOption
    {
        get => VersionUpdateVersionTypeOptions.FirstOrDefault(
            option => string.Equals(option.Value, VersionUpdateVersionType, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null)
            {
                return;
            }

            VersionUpdateVersionType = value.Value;
        }
    }

    public DisplayValueOption? SelectedVersionUpdateProxyTypeOption
    {
        get => VersionUpdateProxyTypeOptions.FirstOrDefault(
            option => string.Equals(option.Value, VersionUpdateProxyType, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null)
            {
                return;
            }

            VersionUpdateProxyType = value.Value;
        }
    }

    public DisplayValueOption? SelectedVersionUpdateResourceSourceOption
    {
        get => VersionUpdateResourceSourceOptions.FirstOrDefault(
            option => string.Equals(option.Value, VersionUpdateResourceSource, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null)
            {
                return;
            }

            VersionUpdateResourceSource = value.Value;
        }
    }

    public ObservableCollection<string> AvailableNotificationProviders { get; } = new();

    public Task WaitForStartupAnnouncementCompletionAsync(CancellationToken cancellationToken = default)
        => _startupAnnouncementCompletionSource.Task.WaitAsync(cancellationToken);

    internal Task PrepareStartupAnnouncementCompletionTask()
    {
        EnsureStartupAnnouncementCompletionPending();
        return _startupAnnouncementCompletionSource.Task;
    }

    public SettingsSectionViewModel? SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (!SetProperty(ref _selectedSection, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedSectionTitle));
            OnPropertyChanged(nameof(IsConfigurationManagerSelected));
            OnPropertyChanged(nameof(IsTimerSelected));
            OnPropertyChanged(nameof(IsPerformanceSelected));
            OnPropertyChanged(nameof(IsGameSelected));
            OnPropertyChanged(nameof(IsConnectSelected));
            OnPropertyChanged(nameof(IsStartSelected));
            OnPropertyChanged(nameof(IsRemoteControlSelected));
            OnPropertyChanged(nameof(IsGuiSelected));
            OnPropertyChanged(nameof(IsBackgroundSelected));
            OnPropertyChanged(nameof(IsExternalNotificationSelected));
            OnPropertyChanged(nameof(IsHotkeySelected));
            OnPropertyChanged(nameof(IsAchievementSelected));
            OnPropertyChanged(nameof(IsVersionUpdateSelected));
            OnPropertyChanged(nameof(IsIssueReportSelected));
            OnPropertyChanged(nameof(IsAboutSelected));
            RefreshCurrentSectionActions();
            if (_deferredSectionDataLoadEnabled && value is not null)
            {
                _ = EnsureSectionDataLoadedAsync(value.Key, CancellationToken.None);
            }
        }
    }

    public string SelectedSectionTitle => SelectedSection?.DisplayName ?? string.Empty;

    private static TaskCompletionSource<bool> CreateCompletedTaskCompletionSource()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult(true);
        return source;
    }

    private void EnsureStartupAnnouncementCompletionPending()
    {
        if (!_startupAnnouncementCompletionSource.Task.IsCompleted)
        {
            return;
        }

        ResetStartupAnnouncementCompletion();
    }

    private void ResetStartupAnnouncementCompletion()
    {
        _startupAnnouncementCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void CompleteStartupAnnouncementCompletion()
    {
        _startupAnnouncementCompletionSource.TrySetResult(true);
    }

    public bool IsConfigurationManagerSelected => IsSelectedSection("ConfigurationManager");

    public bool IsTimerSelected => IsSelectedSection("Timer");

    public bool IsPerformanceSelected => IsSelectedSection("Performance");

    public bool IsGameSelected => IsSelectedSection("Game");

    public bool IsConnectSelected => IsSelectedSection("Connect");

    public bool IsStartSelected => IsSelectedSection("Start");

    public bool IsRemoteControlSelected => IsSelectedSection("RemoteControl");

    public bool IsGuiSelected => IsSelectedSection("GUI");

    public bool IsBackgroundSelected => IsSelectedSection("Background");

    public bool IsExternalNotificationSelected => IsSelectedSection("ExternalNotification");

    public bool IsHotkeySelected => IsSelectedSection("HotKey");

    public bool IsAchievementSelected => IsSelectedSection("Achievement");

    public bool IsVersionUpdateSelected => IsSelectedSection("VersionUpdate");

    public bool IsIssueReportSelected => IsSelectedSection("IssueReport");

    public bool IsAboutSelected => IsSelectedSection("About");

    public string Theme
    {
        get => _theme;
        set
        {
            var normalized = NormalizeTheme(value);
            if (SetProperty(ref _theme, normalized))
            {
                OnPropertyChanged(nameof(SelectedThemeOption));
                MarkGuiSettingsDirty();
                NotifyGuiSettingsPreviewChanged();
            }
        }
    }

    public string Language
    {
        get => _language;
        set
        {
            var normalized = NormalizeLanguage(value);
            var previousLanguage = _language;
            if (SetProperty(ref _language, normalized))
            {
                var previousSuppressSelectedLanguageChangeRequest = _suppressSelectedLanguageChangeRequest;
                try
                {
                    _suppressSelectedLanguageChangeRequest = true;
                    ApplyLanguageSideEffectsImmediately(previousLanguage, normalized);
                }
                finally
                {
                    _suppressSelectedLanguageChangeRequest = previousSuppressSelectedLanguageChangeRequest;
                }
            }
        }
    }

    public string LogItemDateFormatString
    {
        get => _logItemDateFormatString;
        set
        {
            var normalized = NormalizeLogItemDateFormat(value);
            if (SetProperty(ref _logItemDateFormatString, normalized))
            {
                MarkGuiSettingsDirty();
                OnPropertyChanged(nameof(GuiLogItemDateFormatPreview));
                NotifyGuiSettingsPreviewChanged();
            }
        }
    }

    public string GuiLogItemDateFormatPreview
        => FormatGuiLogTimestampPreview(LogItemDateFormatString);

    public string OperNameLanguage
    {
        get => _operNameLanguage;
        set
        {
            var normalized = NormalizeOperNameLanguage(value);
            if (SetProperty(ref _operNameLanguage, normalized))
            {
                OnPropertyChanged(nameof(SelectedOperNameLanguageOption));
                MarkGuiSettingsDirty();
                NotifyGuiSettingsPreviewChanged();
            }
        }
    }

    public string InverseClearMode
    {
        get => _inverseClearMode;
        set
        {
            var normalized = NormalizeInverseClearMode(value);
            if (SetProperty(ref _inverseClearMode, normalized))
            {
                OnPropertyChanged(nameof(SelectedInverseClearModeOption));
                MarkGuiSettingsDirty();
                NotifyGuiSettingsPreviewChanged();
            }
        }
    }

    public bool UseTray
    {
        get => _useTray;
        set
        {
            if (SetProperty(ref _useTray, value))
            {
                if (!value && _minimizeToTray)
                {
                    _minimizeToTray = false;
                    OnPropertyChanged(nameof(MinimizeToTray));
                }

                OnPropertyChanged(nameof(CanMinimizeToTray));
                MarkGuiSettingsDirty();
                NotifyGuiSettingsPreviewChanged();
            }
        }
    }

    public bool CanMinimizeToTray => UseTray;

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            var normalized = UseTray && value;
            if (SetProperty(ref _minimizeToTray, normalized))
            {
                MarkGuiSettingsDirty();
                NotifyGuiSettingsPreviewChanged();
            }
        }
    }

    public bool UseNotify
    {
        get => _useNotify;
        set
        {
            if (SetProperty(ref _useNotify, value))
            {
                MarkGuiSettingsDirty();
                NotifyGuiSettingsPreviewChanged();
            }
        }
    }

    public bool WindowTitleScrollable
    {
        get => _windowTitleScrollable;
        set
        {
            if (SetProperty(ref _windowTitleScrollable, value))
            {
                MarkGuiSettingsDirty();
                NotifyGuiSettingsPreviewChanged();
            }
        }
    }

    public int UiScalePercent
    {
        get => _uiScalePercent;
        set
        {
            var clamped = Math.Clamp(value, UiScalePercentMin, UiScalePercentMax);
            if (SetProperty(ref _uiScalePercent, clamped))
            {
                MarkGuiSettingsDirty();
                NotifyGuiSettingsPreviewChanged();
            }
        }
    }

    public bool UseSoftwareRendering
    {
        get => _useSoftwareRendering;
        set
        {
            if (SetProperty(ref _useSoftwareRendering, value))
            {
                MarkGuiSettingsDirty();
                NotifyGuiSettingsPreviewChanged();
            }
        }
    }

    public bool DeveloperModeEnabled
    {
        get => _developerModeEnabled;
        set
        {
            var normalized = global::MAAUnified.Platform.MaaUnifiedBuildFlavor.ExposesDeveloperTools && value;
            if (SetProperty(ref _developerModeEnabled, normalized))
            {
                Runtime.LogService.SetVerboseEnabled(normalized);
                MarkGuiSettingsDirty();
            }
        }
    }

    public bool CanUseDeveloperMode => global::MAAUnified.Platform.MaaUnifiedBuildFlavor.ExposesDeveloperTools;

    public bool CanOpenRuntimeLogWindow => true;

    public bool CanUseIssueReportMaintenanceTools => true;

    public bool StartSelf
    {
        get => _startSelf;
        set => SetProperty(ref _startSelf, value);
    }

    public string AutostartStatus
    {
        get => _autostartStatus;
        set => SetProperty(ref _autostartStatus, value);
    }

    public string AutostartWarningMessage
    {
        get => _autostartWarningMessage;
        private set
        {
            if (SetProperty(ref _autostartWarningMessage, value))
            {
                OnPropertyChanged(nameof(HasAutostartWarningMessage));
            }
        }
    }

    public bool HasAutostartWarningMessage => !string.IsNullOrWhiteSpace(AutostartWarningMessage);

    public string AutostartErrorMessage
    {
        get => _autostartErrorMessage;
        private set
        {
            if (SetProperty(ref _autostartErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasAutostartErrorMessage));
            }
        }
    }

    public bool HasAutostartErrorMessage => !string.IsNullOrWhiteSpace(AutostartErrorMessage);

    public HotkeySettingItemViewModel ShowGuiHotkeyState => _showGuiHotkeyState;

    public HotkeySettingItemViewModel LinkStartHotkeyState => _linkStartHotkeyState;

    public string HotkeyCaptureGuideText => GetHotkeyCaptureGuideText();

    public string HotkeyShowGui
    {
        get => _hotkeyShowGui;
        set => ApplyHotkeyDraft(_showGuiHotkeyState, value, nameof(HotkeyShowGui));
    }

    public string HotkeyLinkStart
    {
        get => _hotkeyLinkStart;
        set => ApplyHotkeyDraft(_linkStartHotkeyState, value, nameof(HotkeyLinkStart));
    }

    public string HotkeyStatusMessage
    {
        get => _hotkeyStatusMessage;
        private set => SetProperty(ref _hotkeyStatusMessage, value);
    }

    public string HotkeyWarningMessage
    {
        get => _hotkeyWarningMessage;
        private set
        {
            if (SetProperty(ref _hotkeyWarningMessage, value))
            {
                OnPropertyChanged(nameof(HasHotkeyWarningMessage));
            }
        }
    }

    public string HotkeyErrorMessage
    {
        get => _hotkeyErrorMessage;
        private set
        {
            if (SetProperty(ref _hotkeyErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasHotkeyErrorMessage));
            }
        }
    }

    public bool HasHotkeyWarningMessage => !string.IsNullOrWhiteSpace(HotkeyWarningMessage);

    public bool HasHotkeyErrorMessage => !string.IsNullOrWhiteSpace(HotkeyErrorMessage);

    public void BeginHotkeyCapture(string hotkeyName)
    {
        _showGuiHotkeyState.EndCapture();
        _linkStartHotkeyState.EndCapture();
        GetHotkeyState(hotkeyName)?.BeginCapture();
    }

    public void ClearHotkeyBinding(string hotkeyName)
    {
        var state = GetHotkeyState(hotkeyName);
        if (state is null)
        {
            return;
        }

        state.EndCapture();
        if (string.Equals(hotkeyName, ShowGuiHotkeyName, StringComparison.OrdinalIgnoreCase))
        {
            HotkeyShowGui = string.Empty;
        }
        else if (string.Equals(hotkeyName, LinkStartHotkeyName, StringComparison.OrdinalIgnoreCase))
        {
            HotkeyLinkStart = string.Empty;
        }

        state.SetWarning(GetHotkeyDraftPendingText(cleared: true));
        HotkeyStatusMessage = GetHotkeyDraftPendingText(cleared: false);
        HotkeyErrorMessage = string.Empty;
        StatusMessage = HotkeyStatusMessage;
    }

    public void HandleHotkeyCapture(string hotkeyName, HotkeyCaptureResult capture)
    {
        var state = GetHotkeyState(hotkeyName);
        if (state is null)
        {
            return;
        }

        switch (capture.Kind)
        {
            case HotkeyCaptureResultKind.Pending:
                state.SetWarning(string.Empty);
                break;
            case HotkeyCaptureResultKind.Cancelled:
                state.EndCapture();
                state.SetWarning(GetHotkeyCaptureCancelledText());
                break;
            case HotkeyCaptureResultKind.Cleared:
                ClearHotkeyBinding(hotkeyName);
                break;
            case HotkeyCaptureResultKind.Captured:
                state.EndCapture();
                if (capture.Gesture is not null)
                {
                    if (string.Equals(hotkeyName, ShowGuiHotkeyName, StringComparison.OrdinalIgnoreCase))
                    {
                        HotkeyShowGui = capture.Gesture.ToStorageString();
                    }
                    else
                    {
                        HotkeyLinkStart = capture.Gesture.ToStorageString();
                    }
                }

                state.SetWarning(GetHotkeyDraftPendingText(cleared: false));
                HotkeyStatusMessage = GetHotkeyDraftPendingText(cleared: false);
                HotkeyErrorMessage = string.Empty;
                StatusMessage = HotkeyStatusMessage;
                break;
            case HotkeyCaptureResultKind.Rejected:
                state.SetWarning(LocalizeHotkeyCaptureMessage(capture.Message));
                break;
            default:
                break;
        }
    }

    public string NotificationTitle
    {
        get => _notificationTitle;
        set => SetProperty(ref _notificationTitle, value);
    }

    public string NotificationMessage
    {
        get => _notificationMessage;
        set => SetProperty(ref _notificationMessage, value);
    }

    public string IssueReportPath
    {
        get => _issueReportPath;
        set
        {
            if (SetProperty(ref _issueReportPath, value))
            {
                OnPropertyChanged(nameof(HasIssueReportPath));
            }
        }
    }

    public string IssueReportStatusMessage
    {
        get => _issueReportStatusMessage;
        private set
        {
            if (SetProperty(ref _issueReportStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasIssueReportStatusMessage));
            }
        }
    }

    public string IssueReportErrorMessage
    {
        get => _issueReportErrorMessage;
        private set
        {
            if (SetProperty(ref _issueReportErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasIssueReportErrorMessage));
            }
        }
    }

    public bool HasIssueReportPath => !string.IsNullOrWhiteSpace(IssueReportPath);

    public bool HasIssueReportStatusMessage => !string.IsNullOrWhiteSpace(IssueReportStatusMessage);

    public bool HasIssueReportErrorMessage => !string.IsNullOrWhiteSpace(IssueReportErrorMessage);

    public string RemoteGetTaskEndpoint
    {
        get => _remoteGetTaskEndpoint;
        set => SetProperty(ref _remoteGetTaskEndpoint, value);
    }

    public string RemoteReportEndpoint
    {
        get => _remoteReportEndpoint;
        set => SetProperty(ref _remoteReportEndpoint, value);
    }

    public string RemoteUserIdentity
    {
        get => _remoteUserIdentity;
        set => SetProperty(ref _remoteUserIdentity, value);
    }

    public string RemoteDeviceIdentity
    {
        get => _remoteDeviceIdentity;
        set => SetProperty(ref _remoteDeviceIdentity, value);
    }

    public int RemotePollInterval
    {
        get => _remotePollInterval;
        set => SetProperty(ref _remotePollInterval, Math.Max(500, value));
    }

    public string RemoteControlStatusMessage
    {
        get => _remoteControlStatusMessage;
        private set => SetProperty(ref _remoteControlStatusMessage, value);
    }

    public string RemoteControlWarningMessage
    {
        get => _remoteControlWarningMessage;
        private set
        {
            if (SetProperty(ref _remoteControlWarningMessage, value))
            {
                OnPropertyChanged(nameof(HasRemoteControlWarningMessage));
            }
        }
    }

    public string RemoteControlErrorMessage
    {
        get => _remoteControlErrorMessage;
        private set
        {
            if (SetProperty(ref _remoteControlErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasRemoteControlErrorMessage));
            }
        }
    }

    public bool HasRemoteControlWarningMessage => !string.IsNullOrWhiteSpace(RemoteControlWarningMessage);

    public bool HasRemoteControlErrorMessage => !string.IsNullOrWhiteSpace(RemoteControlErrorMessage);

    public bool ExternalNotificationEnabled
    {
        get => _externalNotificationEnabled;
        set
        {
            if (SetProperty(ref _externalNotificationEnabled, value))
            {
                if (!value)
                {
                    ClearExternalNotificationStatus();
                }

                OnPropertyChanged(nameof(CanEditExternalNotification));
                OnPropertyChanged(nameof(CanEditExternalNotificationDetails));
                OnPropertyChanged(nameof(HasExternalNotificationStatusMessage));
                OnPropertyChanged(nameof(HasExternalNotificationWarningMessage));
                OnPropertyChanged(nameof(HasExternalNotificationErrorMessage));
            }
        }
    }

    public bool ExternalNotificationSendWhenComplete
    {
        get => _externalNotificationSendWhenComplete;
        set
        {
            if (SetProperty(ref _externalNotificationSendWhenComplete, value))
            {
                if (!value && _externalNotificationEnableDetails)
                {
                    _externalNotificationEnableDetails = false;
                    OnPropertyChanged(nameof(ExternalNotificationEnableDetails));
                }

                OnPropertyChanged(nameof(CanEditExternalNotificationDetails));
            }
        }
    }

    public bool ExternalNotificationSendWhenError
    {
        get => _externalNotificationSendWhenError;
        set => SetProperty(ref _externalNotificationSendWhenError, value);
    }

    public bool ExternalNotificationSendWhenTimeout
    {
        get => _externalNotificationSendWhenTimeout;
        set => SetProperty(ref _externalNotificationSendWhenTimeout, value);
    }

    public bool ExternalNotificationEnableDetails
    {
        get => _externalNotificationEnableDetails;
        set => SetProperty(ref _externalNotificationEnableDetails, CanEditExternalNotificationDetails && value);
    }

    public bool CanEditExternalNotification => ExternalNotificationEnabled;

    public bool CanSelectExternalNotificationProvider => NotificationProviderSelections.Count > 0;

    public bool CanEditExternalNotificationDetails =>
        ExternalNotificationEnabled && ExternalNotificationSendWhenComplete;

    public string SelectedNotificationProvider
    {
        get => _selectedNotificationProvider;
        set
        {
            var normalized = NormalizeNotificationProvider(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            if (string.Equals(_selectedNotificationProvider, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_selectedNotificationProvider))
            {
                _notificationProviderParameters[_selectedNotificationProvider] = NotificationProviderParametersText;
            }

            if (SetProperty(ref _selectedNotificationProvider, normalized))
            {
                RefreshSelectedNotificationProviderText(normalized);
            }
        }
    }

    public string NotificationProviderParametersText
    {
        get => _notificationProviderParametersText;
        set
        {
            var normalized = value ?? string.Empty;
            if (SetProperty(ref _notificationProviderParametersText, normalized))
            {
                UpdateProviderParameterMapFromText(_selectedNotificationProvider, normalized, markDirty: false);
                if (!_suppressProviderParameterDirtyTracking)
                {
                    MarkExternalNotificationDirty();
                }
            }
        }
    }

    public string ExternalNotificationStatusMessage
    {
        get => _externalNotificationStatusMessage;
        private set
        {
            if (SetProperty(ref _externalNotificationStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasExternalNotificationStatusMessage));
            }
        }
    }

    public string ExternalNotificationWarningMessage
    {
        get => _externalNotificationWarningMessage;
        private set
        {
            if (SetProperty(ref _externalNotificationWarningMessage, value))
            {
                OnPropertyChanged(nameof(HasExternalNotificationWarningMessage));
            }
        }
    }

    public string ExternalNotificationErrorMessage
    {
        get => _externalNotificationErrorMessage;
        private set
        {
            if (SetProperty(ref _externalNotificationErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasExternalNotificationErrorMessage));
            }
        }
    }

    public bool HasExternalNotificationStatusMessage =>
        ExternalNotificationEnabled && !string.IsNullOrWhiteSpace(ExternalNotificationStatusMessage);

    public bool HasExternalNotificationWarningMessage =>
        ExternalNotificationEnabled && !string.IsNullOrWhiteSpace(ExternalNotificationWarningMessage);

    public bool HasExternalNotificationErrorMessage =>
        ExternalNotificationEnabled && !string.IsNullOrWhiteSpace(ExternalNotificationErrorMessage);

    public bool ServerChanSectionVisible => _enabledNotificationProviders.Contains("ServerChan");

    public bool TelegramSectionVisible => _enabledNotificationProviders.Contains("Telegram");

    public bool DiscordSectionVisible => _enabledNotificationProviders.Contains("Discord");

    public bool DiscordWebhookSectionVisible => DiscordSectionVisible;

    public bool DingTalkSectionVisible => _enabledNotificationProviders.Contains("DingTalk");

    public bool SmtpSectionVisible => _enabledNotificationProviders.Contains("Smtp");

    public bool BarkSectionVisible => _enabledNotificationProviders.Contains("Bark");

    public bool QmsgSectionVisible => _enabledNotificationProviders.Contains("Qmsg");

    public bool GotifySectionVisible => _enabledNotificationProviders.Contains("Gotify");

    public bool CustomWebhookSectionVisible => _enabledNotificationProviders.Contains("CustomWebhook");

    public string ServerChanSendKey
    {
        get => GetProviderParameterValue("ServerChan", "sendKey");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("ServerChan", "sendKey"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("ServerChan", "sendKey", normalized);
            OnPropertyChanged(nameof(ServerChanSendKey));
            MarkExternalNotificationDirty();
        }
    }

    public string TelegramBotToken
    {
        get => GetProviderParameterValue("Telegram", "botToken");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Telegram", "botToken"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Telegram", "botToken", normalized);
            OnPropertyChanged(nameof(TelegramBotToken));
            MarkExternalNotificationDirty();
        }
    }

    public string TelegramChatId
    {
        get => GetProviderParameterValue("Telegram", "chatId");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Telegram", "chatId"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Telegram", "chatId", normalized);
            OnPropertyChanged(nameof(TelegramChatId));
            MarkExternalNotificationDirty();
        }
    }

    public string TelegramTopicId
    {
        get => GetProviderParameterValue("Telegram", "topicId");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Telegram", "topicId"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Telegram", "topicId", normalized);
            OnPropertyChanged(nameof(TelegramTopicId));
            MarkExternalNotificationDirty();
        }
    }

    public string DiscordBotToken
    {
        get => GetProviderParameterValue("Discord", "botToken");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Discord", "botToken"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Discord", "botToken", normalized);
            OnPropertyChanged(nameof(DiscordBotToken));
            MarkExternalNotificationDirty();
        }
    }

    public string DiscordUserId
    {
        get => GetProviderParameterValue("Discord", "userId");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Discord", "userId"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Discord", "userId", normalized);
            OnPropertyChanged(nameof(DiscordUserId));
            MarkExternalNotificationDirty();
        }
    }

    public string DiscordWebhookUrl
    {
        get => GetProviderParameterValue("Discord", "webhookUrl");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Discord", "webhookUrl"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Discord", "webhookUrl", normalized);
            OnPropertyChanged(nameof(DiscordWebhookUrl));
            MarkExternalNotificationDirty();
        }
    }

    public string DingTalkAccessToken
    {
        get => GetProviderParameterValue("DingTalk", "accessToken");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("DingTalk", "accessToken"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("DingTalk", "accessToken", normalized);
            OnPropertyChanged(nameof(DingTalkAccessToken));
            MarkExternalNotificationDirty();
        }
    }

    public string DingTalkSecret
    {
        get => GetProviderParameterValue("DingTalk", "secret");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("DingTalk", "secret"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("DingTalk", "secret", normalized);
            OnPropertyChanged(nameof(DingTalkSecret));
            MarkExternalNotificationDirty();
        }
    }

    public string SmtpServer
    {
        get => GetProviderParameterValue("Smtp", "server");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Smtp", "server"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Smtp", "server", normalized);
            OnPropertyChanged(nameof(SmtpServer));
            MarkExternalNotificationDirty();
        }
    }

    public string SmtpPort
    {
        get => GetProviderParameterValue("Smtp", "port");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Smtp", "port"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Smtp", "port", normalized);
            OnPropertyChanged(nameof(SmtpPort));
            MarkExternalNotificationDirty();
        }
    }

    public bool SmtpUseSsl
    {
        get => TryGetProviderParameterBool("Smtp", "useSsl", out var value) && value;
        set
        {
            if (SmtpUseSsl == value)
            {
                return;
            }

            SetProviderParameterValue("Smtp", "useSsl", value ? bool.TrueString : bool.FalseString);
            OnPropertyChanged(nameof(SmtpUseSsl));
            MarkExternalNotificationDirty();
        }
    }

    public bool SmtpRequireAuthentication
    {
        get => TryGetProviderParameterBool("Smtp", "requiresAuthentication", out var value) && value;
        set
        {
            if (SmtpRequireAuthentication == value)
            {
                return;
            }

            SetProviderParameterValue("Smtp", "requiresAuthentication", value ? bool.TrueString : bool.FalseString);
            OnPropertyChanged(nameof(SmtpRequireAuthentication));
            MarkExternalNotificationDirty();
        }
    }

    public string SmtpUser
    {
        get => GetProviderParameterValue("Smtp", "user");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Smtp", "user"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Smtp", "user", normalized);
            OnPropertyChanged(nameof(SmtpUser));
            MarkExternalNotificationDirty();
        }
    }

    public string SmtpPassword
    {
        get => GetProviderParameterValue("Smtp", "password");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Smtp", "password"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Smtp", "password", normalized);
            OnPropertyChanged(nameof(SmtpPassword));
            MarkExternalNotificationDirty();
        }
    }

    public string SmtpFrom
    {
        get => GetProviderParameterValue("Smtp", "from");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Smtp", "from"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Smtp", "from", normalized);
            OnPropertyChanged(nameof(SmtpFrom));
            MarkExternalNotificationDirty();
        }
    }

    public string SmtpTo
    {
        get => GetProviderParameterValue("Smtp", "to");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Smtp", "to"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Smtp", "to", normalized);
            OnPropertyChanged(nameof(SmtpTo));
            MarkExternalNotificationDirty();
        }
    }

    public string BarkServer
    {
        get => GetProviderParameterValue("Bark", "server");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Bark", "server"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Bark", "server", normalized);
            OnPropertyChanged(nameof(BarkServer));
            MarkExternalNotificationDirty();
        }
    }

    public string BarkSendKey
    {
        get => GetProviderParameterValue("Bark", "sendKey");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Bark", "sendKey"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Bark", "sendKey", normalized);
            OnPropertyChanged(nameof(BarkSendKey));
            MarkExternalNotificationDirty();
        }
    }

    public string QmsgServer
    {
        get => GetProviderParameterValue("Qmsg", "server");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Qmsg", "server"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Qmsg", "server", normalized);
            OnPropertyChanged(nameof(QmsgServer));
            MarkExternalNotificationDirty();
        }
    }

    public string QmsgKey
    {
        get => GetProviderParameterValue("Qmsg", "key");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Qmsg", "key"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Qmsg", "key", normalized);
            OnPropertyChanged(nameof(QmsgKey));
            MarkExternalNotificationDirty();
        }
    }

    public string QmsgUser
    {
        get => GetProviderParameterValue("Qmsg", "user");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Qmsg", "user"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Qmsg", "user", normalized);
            OnPropertyChanged(nameof(QmsgUser));
            MarkExternalNotificationDirty();
        }
    }

    public string QmsgBot
    {
        get => GetProviderParameterValue("Qmsg", "bot");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Qmsg", "bot"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Qmsg", "bot", normalized);
            OnPropertyChanged(nameof(QmsgBot));
            MarkExternalNotificationDirty();
        }
    }

    public string GotifyServer
    {
        get => GetProviderParameterValue("Gotify", "server");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Gotify", "server"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Gotify", "server", normalized);
            OnPropertyChanged(nameof(GotifyServer));
            MarkExternalNotificationDirty();
        }
    }

    public string GotifyToken
    {
        get => GetProviderParameterValue("Gotify", "token");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("Gotify", "token"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("Gotify", "token", normalized);
            OnPropertyChanged(nameof(GotifyToken));
            MarkExternalNotificationDirty();
        }
    }

    public string CustomWebhookUrl
    {
        get => GetProviderParameterValue("CustomWebhook", "url");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("CustomWebhook", "url"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("CustomWebhook", "url", normalized);
            OnPropertyChanged(nameof(CustomWebhookUrl));
            MarkExternalNotificationDirty();
        }
    }

    public string CustomWebhookBody
    {
        get => GetProviderParameterValue("CustomWebhook", "body");
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(GetProviderParameterValue("CustomWebhook", "body"), normalized, StringComparison.Ordinal))
            {
                return;
            }

            SetProviderParameterValue("CustomWebhook", "body", normalized);
            OnPropertyChanged(nameof(CustomWebhookBody));
            MarkExternalNotificationDirty();
        }
    }

    public string ConfigurationManagerSelectedProfile
    {
        get => _configurationManagerSelectedProfile;
        set => SetProperty(ref _configurationManagerSelectedProfile, value?.Trim() ?? string.Empty);
    }

    public string ConfigurationManagerNewProfileName
    {
        get => _configurationManagerNewProfileName;
        set
        {
            if (SetProperty(ref _configurationManagerNewProfileName, value ?? string.Empty))
            {
                if (!_suppressConfigurationManagerSaveAsNewSuccessReset)
                {
                    ConfigurationManagerSaveAsNewSucceededText = string.Empty;
                    ConfigurationManagerSaveAsNewFailedText = string.Empty;
                }
            }
        }
    }

    public string ConfigurationManagerSaveAsNewSucceededText
    {
        get => _configurationManagerSaveAsNewSucceededText;
        private set
        {
            if (SetProperty(ref _configurationManagerSaveAsNewSucceededText, value))
            {
                OnPropertyChanged(nameof(HasConfigurationManagerSaveAsNewSucceeded));
            }
        }
    }

    public string ConfigurationManagerSaveAsNewFailedText
    {
        get => _configurationManagerSaveAsNewFailedText;
        private set
        {
            if (SetProperty(ref _configurationManagerSaveAsNewFailedText, value))
            {
                OnPropertyChanged(nameof(HasConfigurationManagerSaveAsNewFailed));
            }
        }
    }

    public string ConfigurationManagerImportSucceededText
    {
        get => _configurationManagerImportSucceededText;
        private set
        {
            if (SetProperty(ref _configurationManagerImportSucceededText, value))
            {
                OnPropertyChanged(nameof(HasConfigurationManagerImportSucceeded));
            }
        }
    }

    public string ConfigurationManagerStatusMessage
    {
        get => _configurationManagerStatusMessage;
        private set => SetProperty(ref _configurationManagerStatusMessage, value);
    }

    public string ConfigurationManagerErrorMessage
    {
        get => _configurationManagerErrorMessage;
        private set
        {
            if (SetProperty(ref _configurationManagerErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasConfigurationManagerErrorMessage));
            }
        }
    }

    public bool HasConfigurationManagerErrorMessage => !string.IsNullOrWhiteSpace(ConfigurationManagerErrorMessage);

    public bool HasConfigurationManagerSaveAsNewSucceeded =>
        !string.IsNullOrWhiteSpace(ConfigurationManagerSaveAsNewSucceededText);

    public bool HasConfigurationManagerSaveAsNewFailed =>
        !string.IsNullOrWhiteSpace(ConfigurationManagerSaveAsNewFailedText);

    public bool HasConfigurationManagerImportSucceeded =>
        !string.IsNullOrWhiteSpace(ConfigurationManagerImportSucceededText);

    public string VersionUpdateProxy
    {
        get => _versionUpdateProxy;
        set => SetProperty(ref _versionUpdateProxy, value?.Trim() ?? string.Empty);
    }

    public string VersionUpdateProxyType
    {
        get => _versionUpdateProxyType;
        set
        {
            if (SetProperty(ref _versionUpdateProxyType, NormalizeVersionUpdateProxyType(value)))
            {
                OnPropertyChanged(nameof(SelectedVersionUpdateProxyTypeOption));
            }
        }
    }

    public string VersionUpdateVersionType
    {
        get => _versionUpdateVersionType;
        set
        {
            if (SetProperty(ref _versionUpdateVersionType, value?.Trim() ?? "Stable"))
            {
                OnPropertyChanged(nameof(SelectedVersionUpdateVersionTypeOption));
            }
        }
    }

    public string VersionUpdateResourceSource
    {
        get => _versionUpdateResourceSource;
        set
        {
            var normalized = NormalizeVersionUpdateResourceSource(value);
            if (!SetProperty(ref _versionUpdateResourceSource, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(IsVersionUpdateMirrorChyanSource));
            OnPropertyChanged(nameof(IsVersionUpdateGithubSource));
            OnPropertyChanged(nameof(SelectedVersionUpdateResourceSourceOption));
        }
    }

    public bool VersionUpdateForceGithubSource
    {
        get => _versionUpdateForceGithubSource;
        set => SetProperty(ref _versionUpdateForceGithubSource, value);
    }

    public string VersionUpdateMirrorChyanCdk
    {
        get => _versionUpdateMirrorChyanCdk;
        set => SetProperty(ref _versionUpdateMirrorChyanCdk, value?.Trim() ?? string.Empty);
    }

    public string VersionUpdateMirrorChyanCdkExpired
    {
        get => _versionUpdateMirrorChyanCdkExpired;
        set
        {
            if (!SetProperty(ref _versionUpdateMirrorChyanCdkExpired, value?.Trim() ?? string.Empty))
            {
                return;
            }

            OnPropertyChanged(nameof(VersionUpdateMirrorChyanCdkExpiryText));
        }
    }

    public bool VersionUpdateStartupCheck
    {
        get => _versionUpdateStartupCheck;
        set => SetProperty(ref _versionUpdateStartupCheck, value);
    }

    public bool VersionUpdateScheduledCheck
    {
        get => _versionUpdateScheduledCheck;
        set => SetProperty(ref _versionUpdateScheduledCheck, value);
    }

    public string VersionUpdateResourceApi
    {
        get => _versionUpdateResourceApi;
        set => SetProperty(ref _versionUpdateResourceApi, value?.Trim() ?? string.Empty);
    }

    public bool VersionUpdateAllowNightly
    {
        get => _versionUpdateAllowNightly;
        set
        {
            if (!SetProperty(ref _versionUpdateAllowNightly, value))
            {
                return;
            }

            RebuildVersionUpdateOptionLists();
            if (!value && string.Equals(VersionUpdateVersionType, "Nightly", StringComparison.OrdinalIgnoreCase))
            {
                VersionUpdateVersionType = "Beta";
            }
        }
    }

    public bool VersionUpdateAcknowledgedNightlyWarning
    {
        get => _versionUpdateAcknowledgedNightlyWarning;
        set => SetProperty(ref _versionUpdateAcknowledgedNightlyWarning, value);
    }

    public bool VersionUpdateUseAria2
    {
        get => _versionUpdateUseAria2;
        set => SetProperty(ref _versionUpdateUseAria2, value);
    }

    public bool VersionUpdateAutoDownload
    {
        get => _versionUpdateAutoDownload;
        set => SetProperty(ref _versionUpdateAutoDownload, value);
    }

    public bool VersionUpdateAutoInstall
    {
        get => _versionUpdateAutoInstall;
        set => SetProperty(ref _versionUpdateAutoInstall, value);
    }

    public string VersionUpdateName
    {
        get => _versionUpdateName;
        set => SetProperty(ref _versionUpdateName, value ?? string.Empty);
    }

    public string VersionUpdateBody
    {
        get => _versionUpdateBody;
        set => SetProperty(ref _versionUpdateBody, value ?? string.Empty);
    }

    public bool VersionUpdateIsFirstBoot
    {
        get => _versionUpdateIsFirstBoot;
        set => SetProperty(ref _versionUpdateIsFirstBoot, value);
    }

    public string VersionUpdatePackage
    {
        get => _versionUpdatePackage;
        set => SetProperty(ref _versionUpdatePackage, value ?? string.Empty);
    }

    public bool VersionUpdateDoNotShow
    {
        get => _versionUpdateDoNotShow;
        set => SetProperty(ref _versionUpdateDoNotShow, value);
    }

    public string VersionUpdateStatusMessage
    {
        get => _versionUpdateStatusMessage;
        private set
        {
            if (SetProperty(ref _versionUpdateStatusMessage, value))
            {
                OnPropertyChanged(nameof(VersionUpdateInlineMessage));
                OnPropertyChanged(nameof(HasVersionUpdateInlineMessage));
            }
        }
    }

    public string VersionUpdateActivityMessage
    {
        get => _versionUpdateActivityMessage;
        private set
        {
            if (SetProperty(ref _versionUpdateActivityMessage, value))
            {
                OnPropertyChanged(nameof(HasVersionUpdateActivityMessage));
                OnPropertyChanged(nameof(VersionUpdateInlineMessage));
                OnPropertyChanged(nameof(HasVersionUpdateInlineMessage));
            }
        }
    }

    public bool HasVersionUpdateActivityMessage => !string.IsNullOrWhiteSpace(VersionUpdateActivityMessage);

    public string VersionUpdateInlineMessage
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(VersionUpdateActivityMessage))
            {
                return VersionUpdateActivityMessage;
            }

            if (!string.IsNullOrWhiteSpace(VersionUpdateStatusMessage))
            {
                return VersionUpdateStatusMessage;
            }

            return VersionUpdateErrorMessage;
        }
    }

    public bool HasVersionUpdateInlineMessage => !string.IsNullOrWhiteSpace(VersionUpdateInlineMessage);

    public string VersionUpdateErrorMessage
    {
        get => _versionUpdateErrorMessage;
        private set
        {
            if (SetProperty(ref _versionUpdateErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasVersionUpdateErrorMessage));
                OnPropertyChanged(nameof(VersionUpdateInlineMessage));
                OnPropertyChanged(nameof(HasVersionUpdateInlineMessage));
            }
        }
    }

    public bool HasVersionUpdateErrorMessage => !string.IsNullOrWhiteSpace(VersionUpdateErrorMessage);

    public bool HasPendingVersionUpdateAvailability => _hasPendingVersionUpdateAvailability;

    public bool HasPendingResourceUpdateAvailability => _hasPendingResourceUpdateAvailability;

    public string PendingResourceUpdateSummary => BuildPendingResourceUpdateSummary();

    public bool HasIssueReportUpdateAvailability =>
        HasPendingVersionUpdateAvailability || HasPendingResourceUpdateAvailability;

    public bool ShowIssueReportPreflightNote => !HasIssueReportUpdateAvailability;

    public string IssueReportVersionUpdateSummary => HasPendingVersionUpdateAvailability
        ? RootTexts.GetOrDefault(
            "Main.Update.VersionAvailable",
            "版本更新可用，点击设置 > Version Update")
        : string.Empty;

    public string IssueReportUpdateNotice => BuildIssueReportUpdateNoticeText();

    public string IssueReportClearImageCacheTip => LocalizeSettingsText(
        "Settings.IssueReport.ClearImageCacheTip",
        "通常无需手动清理图片缓存，仅建议排障时使用。");

    public bool IsVersionUpdateMirrorChyanSource =>
        string.Equals(VersionUpdateResourceSource, "MirrorChyan", StringComparison.OrdinalIgnoreCase);

    public bool IsVersionUpdateGithubSource => !IsVersionUpdateMirrorChyanSource;

    public string VersionUpdateMirrorChyanCdkExpiryText =>
        BuildMirrorChyanExpiryText(VersionUpdateMirrorChyanCdkExpired);

    public string UpdatePanelUiVersion
    {
        get => _updatePanelUiVersion;
        private set => SetProperty(ref _updatePanelUiVersion, value);
    }

    public string UpdatePanelCoreVersion
    {
        get => _updatePanelCoreVersion;
        private set => SetProperty(ref _updatePanelCoreVersion, value);
    }

    public string UpdatePanelBuildTime
    {
        get => _updatePanelBuildTime;
        private set => SetProperty(ref _updatePanelBuildTime, value);
    }

    public string UpdatePanelResourceVersion
    {
        get => _updatePanelResourceVersion;
        private set => SetProperty(ref _updatePanelResourceVersion, value);
    }

    public string UpdatePanelResourceTime
    {
        get => _updatePanelResourceTime;
        private set => SetProperty(ref _updatePanelResourceTime, value);
    }

    public bool IsVersionUpdateActionRunning
    {
        get => _isVersionUpdateActionRunning;
        private set
        {
            if (SetProperty(ref _isVersionUpdateActionRunning, value))
            {
                OnPropertyChanged(nameof(CanRunVersionUpdateActions));
                UpdateAvailabilityChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool CanRunVersionUpdateActions => !IsVersionUpdateActionRunning;

    public bool AchievementPopupDisabled
    {
        get => _achievementPopupDisabled;
        set
        {
            if (SetProperty(ref _achievementPopupDisabled, value))
            {
                OnPropertyChanged(nameof(CanEditAchievementPopupAutoClose));
                UpdateAchievementPolicySummary(new AchievementPolicy(_achievementPopupDisabled, _achievementPopupAutoClose));
            }
        }
    }

    public bool AchievementPopupAutoClose
    {
        get => _achievementPopupAutoClose;
        set
        {
            if (SetProperty(ref _achievementPopupAutoClose, value))
            {
                UpdateAchievementPolicySummary(new AchievementPolicy(_achievementPopupDisabled, _achievementPopupAutoClose));
            }
        }
    }

    public bool CanEditAchievementPopupAutoClose => !AchievementPopupDisabled;

    public int AchievementUnlockedCount
    {
        get => _achievementUnlockedCount;
        private set
        {
            if (SetProperty(ref _achievementUnlockedCount, value))
            {
                OnPropertyChanged(nameof(AchievementLevelText));
            }
        }
    }

    public int AchievementTotalCount
    {
        get => _achievementTotalCount;
        private set
        {
            if (SetProperty(ref _achievementTotalCount, value))
            {
                OnPropertyChanged(nameof(AchievementLevelText));
            }
        }
    }

    public string AchievementLevelText
        => $"{AchievementTextCatalog.GetString("AchievementLevel", Language, "成就数量：")}{AchievementUnlockedCount}/{AchievementTotalCount}";

    public bool AchievementDebugEnabled
    {
        get => _achievementDebugEnabled;
        private set
        {
            if (SetProperty(ref _achievementDebugEnabled, value))
            {
                OnPropertyChanged(nameof(CanUseAchievementDebugActions));
            }
        }
    }

    public bool CanUseAchievementDebugEntry => true;

    public bool CanUseAchievementDebugActions => AchievementDebugEnabled;

    public string AchievementDebugMedalColor
    {
        get => _achievementDebugMedalColor;
        private set => SetProperty(ref _achievementDebugMedalColor, value);
    }

    public string AchievementDebugTip
    {
        get => _achievementDebugTip;
        private set => SetProperty(ref _achievementDebugTip, value);
    }

    public string AchievementStatusMessage
    {
        get => _achievementStatusMessage;
        private set
        {
            if (SetProperty(ref _achievementStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasAchievementStatusMessage));
                OnPropertyChanged(nameof(HasAchievementStatusBlockMessage));
            }
        }
    }

    public string AchievementErrorMessage
    {
        get => _achievementErrorMessage;
        private set
        {
            if (SetProperty(ref _achievementErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasAchievementErrorMessage));
                OnPropertyChanged(nameof(HasAchievementStatusBlockMessage));
            }
        }
    }

    public bool HasAchievementStatusMessage => !string.IsNullOrWhiteSpace(AchievementStatusMessage);

    public bool HasAchievementErrorMessage => !string.IsNullOrWhiteSpace(AchievementErrorMessage);

    public bool HasAchievementStatusBlockMessage => HasAchievementStatusMessage || HasAchievementErrorMessage;

    public string AchievementPolicySummary
    {
        get => _achievementPolicySummary;
        private set => SetProperty(ref _achievementPolicySummary, value);
    }

    public string AboutVersionInfo
    {
        get => _aboutVersionInfo;
        private set => SetProperty(ref _aboutVersionInfo, value);
    }

    public string AboutStatusMessage
    {
        get => _aboutStatusMessage;
        private set
        {
            if (SetProperty(ref _aboutStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasAboutStatusMessage));
                OnPropertyChanged(nameof(HasAboutStatusBlockMessage));
            }
        }
    }

    public string AboutErrorMessage
    {
        get => _aboutErrorMessage;
        private set
        {
            if (SetProperty(ref _aboutErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasAboutErrorMessage));
                OnPropertyChanged(nameof(HasAboutStatusBlockMessage));
            }
        }
    }

    public bool HasAboutStatusMessage => !string.IsNullOrWhiteSpace(AboutStatusMessage);

    public bool HasAboutErrorMessage => !string.IsNullOrWhiteSpace(AboutErrorMessage);

    public bool HasAboutStatusBlockMessage => HasAboutStatusMessage || HasAboutErrorMessage;

    public async Task ExecuteSectionActionAsync(SettingsSectionActionItem? action, CancellationToken cancellationToken = default)
    {
        if (action is null)
        {
            return;
        }

        await ExecuteSectionActionAsync(action.ActionId, cancellationToken);
    }

    public async Task ExecuteSectionActionAsync(string? actionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(actionId))
        {
            return;
        }

        switch (actionId)
        {
            case "settings.save-gui":
                await SaveGuiSettingsAsync(cancellationToken);
                break;
            case "settings.save-connection-game":
                await SaveConnectionGameSettingsAsync(cancellationToken);
                break;
            case "settings.save-start-performance":
                await SaveStartPerformanceSettingsAsync(cancellationToken);
                break;
            case "settings.save-timer":
                await SaveTimerSettingsAsync(cancellationToken);
                break;
            case "settings.save-remote":
                await SaveRemoteControlAsync(cancellationToken);
                break;
            case "settings.test-remote":
                await TestRemoteControlConnectivityAsync(cancellationToken);
                break;
            case "settings.register-hotkeys":
                await RegisterHotkeysAsync(cancellationToken: cancellationToken);
                break;
            case "settings.validate-notification":
                await ValidateExternalNotificationParametersAsync(cancellationToken);
                break;
            case "settings.test-notification":
                await TestExternalNotificationAsync(cancellationToken);
                break;
            case "settings.save-notification":
                await SaveExternalNotificationAsync(cancellationToken);
                break;
            case "settings.save-version-update":
                await SaveVersionUpdateSettingsAsync(cancellationToken);
                break;
            case "settings.check-version-update":
                await CheckVersionUpdateAsync(cancellationToken);
                break;
            case "settings.save-achievement":
                await SaveAchievementSettingsAsync(cancellationToken);
                break;
            case "settings.refresh-achievement":
                await RefreshAchievementPolicyAsync(cancellationToken);
                break;
            case "settings.show-achievement":
                await ShowAchievementListDialogAsync(cancellationToken);
                break;
            case "settings.open-achievement-guide":
                await OpenAchievementGuideAsync(cancellationToken);
                break;
            case "settings.build-issue-report":
                await BuildIssueReportAsync(cancellationToken);
                break;
            case "settings.open-debug-directory":
                await OpenIssueReportDebugDirectoryAsync(cancellationToken);
                break;
            case "settings.clear-image-cache":
                await ClearIssueReportImageCacheAsync(cancellationToken);
                break;
            case "settings.check-announcement":
                await CheckAboutAnnouncementWithDialogAsync(cancellationToken);
                break;
            case "settings.open-official":
                await OpenAboutOfficialWebsiteAsync(cancellationToken);
                break;
            case "settings.open-community":
                await OpenAboutCommunityAsync(cancellationToken);
                break;
            case "settings.open-download":
                await OpenAboutDownloadAsync(cancellationToken);
                break;
            case "settings.refresh-profiles":
                await RefreshConfigurationProfilesAsync(cancellationToken);
                break;
            default:
                await RecordEventAsync("Settings.SectionAction.Unknown", actionId, cancellationToken);
                break;
        }
    }

    public string BackgroundImagePath
    {
        get => _backgroundImagePath;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (SetProperty(ref _backgroundImagePath, normalized))
            {
                MarkGuiSettingsDirty(saveImmediately: false);
            }
        }
    }

    public int BackgroundOpacity
    {
        get => _backgroundOpacity;
        set
        {
            var clamped = Math.Clamp(value, BackgroundOpacityMin, BackgroundOpacityMax);
            if (SetProperty(ref _backgroundOpacity, clamped))
            {
                MarkGuiSettingsDirty();
            }
        }
    }

    public int BackgroundBlur
    {
        get => _backgroundBlur;
        set
        {
            var clamped = Math.Clamp(value, BackgroundBlurMin, BackgroundBlurMax);
            if (SetProperty(ref _backgroundBlur, clamped))
            {
                MarkGuiSettingsDirty();
            }
        }
    }

    public string BackgroundStretchMode
    {
        get => _backgroundStretchMode;
        set
        {
            var normalized = NormalizeBackgroundStretchMode(value);
            if (SetProperty(ref _backgroundStretchMode, normalized))
            {
                OnPropertyChanged(nameof(SelectedBackgroundStretchModeOption));
                MarkGuiSettingsDirty();
            }
        }
    }

    public bool HasPendingGuiChanges
    {
        get => _hasPendingGuiChanges;
        private set
        {
            if (SetProperty(ref _hasPendingGuiChanges, value))
            {
                OnPropertyChanged(nameof(IsGuiSaveInProgress));
                OnPropertyChanged(nameof(HasGuiSaveSucceeded));
            }
        }
    }

    public string GuiValidationMessage
    {
        get => _guiValidationMessage;
        private set
        {
            if (SetProperty(ref _guiValidationMessage, value))
            {
                OnPropertyChanged(nameof(HasGuiValidationMessage));
            }
        }
    }

    public bool HasGuiValidationMessage => !string.IsNullOrWhiteSpace(GuiValidationMessage);

    public string GuiSectionValidationMessage
    {
        get => _guiSectionValidationMessage;
        private set
        {
            if (SetProperty(ref _guiSectionValidationMessage, value))
            {
                OnPropertyChanged(nameof(HasGuiSectionValidationMessage));
                UpdateCombinedGuiValidationMessage();
            }
        }
    }

    public bool HasGuiSectionValidationMessage => !string.IsNullOrWhiteSpace(GuiSectionValidationMessage);

    public string BackgroundValidationMessage
    {
        get => _backgroundValidationMessage;
        private set
        {
            if (SetProperty(ref _backgroundValidationMessage, value))
            {
                OnPropertyChanged(nameof(HasBackgroundValidationMessage));
                UpdateCombinedGuiValidationMessage();
            }
        }
    }

    public bool HasBackgroundValidationMessage => !string.IsNullOrWhiteSpace(BackgroundValidationMessage);

    public DateTimeOffset? LastSuccessfulGuiSaveAt
    {
        get => _lastSuccessfulGuiSaveAt;
        private set
        {
            if (SetProperty(ref _lastSuccessfulGuiSaveAt, value))
            {
                OnPropertyChanged(nameof(HasGuiSaveSucceeded));
            }
        }
    }

    public bool IsGuiSaveInProgress => HasPendingGuiChanges;

    public bool HasGuiSaveSucceeded => !HasPendingGuiChanges && LastSuccessfulGuiSaveAt.HasValue;

    public bool RunDirectly
    {
        get => _runDirectly;
        set
        {
            if (SetProperty(ref _runDirectly, value))
            {
                MarkStartPerformanceDirty();
            }
        }
    }

    public bool MinimizeDirectly
    {
        get => _minimizeDirectly;
        set
        {
            if (SetProperty(ref _minimizeDirectly, value))
            {
                MarkStartPerformanceDirty();
            }
        }
    }

    public bool OpenEmulatorAfterLaunch
    {
        get => _openEmulatorAfterLaunch;
        set
        {
            if (SetProperty(ref _openEmulatorAfterLaunch, value))
            {
                if (!value)
                {
                    StartPerformanceValidationMessage = string.Empty;
                }

                OnPropertyChanged(nameof(CanEditEmulatorLaunchSettings));
                MarkStartPerformanceDirty();
            }
        }
    }

    public bool CanEditEmulatorLaunchSettings => OpenEmulatorAfterLaunch;

    public string EmulatorPath
    {
        get => _emulatorPath;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (SetProperty(ref _emulatorPath, normalized))
            {
                MarkStartPerformanceDirty();
            }
        }
    }

    public string EmulatorAddCommand
    {
        get => _emulatorAddCommand;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (SetProperty(ref _emulatorAddCommand, normalized))
            {
                MarkStartPerformanceDirty();
            }
        }
    }

    public int EmulatorWaitSeconds
    {
        get => _emulatorWaitSeconds;
        set
        {
            if (SetProperty(ref _emulatorWaitSeconds, value))
            {
                MarkStartPerformanceDirty();
            }
        }
    }

    public bool PerformanceUseGpu
    {
        get => _performanceUseGpu;
        set
        {
            if (SetProperty(ref _performanceUseGpu, value))
            {
                if (!_suppressGpuUiRefresh)
                {
                    RefreshGpuUiState();
                }

                MarkStartPerformanceDirty();
            }
        }
    }

    public bool PerformanceAllowDeprecatedGpu
    {
        get => _performanceAllowDeprecatedGpu;
        set
        {
            if (SetProperty(ref _performanceAllowDeprecatedGpu, value))
            {
                if (!_suppressGpuUiRefresh)
                {
                    RefreshGpuUiState();
                }

                MarkStartPerformanceDirty();
            }
        }
    }

    public string PerformancePreferredGpuDescription
    {
        get => _performancePreferredGpuDescription;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (SetProperty(ref _performancePreferredGpuDescription, normalized))
            {
                if (!_suppressGpuUiRefresh)
                {
                    RefreshGpuUiState();
                }

                MarkStartPerformanceDirty();
            }
        }
    }

    public string PerformancePreferredGpuInstancePath
    {
        get => _performancePreferredGpuInstancePath;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (SetProperty(ref _performancePreferredGpuInstancePath, normalized))
            {
                if (!_suppressGpuUiRefresh)
                {
                    RefreshGpuUiState();
                }

                MarkStartPerformanceDirty();
            }
        }
    }

    public IReadOnlyList<GpuOptionDisplayItem> AvailableGpuOptions
    {
        get => _availableGpuOptions;
        private set => SetProperty(ref _availableGpuOptions, value);
    }

    public GpuOptionDisplayItem? SelectedGpuOption
    {
        get => _selectedGpuOption;
        set
        {
            if (!SetProperty(ref _selectedGpuOption, value) || _suppressGpuSelectionChange || value is null)
            {
                return;
            }

            ApplyGpuSelection(value.Descriptor);
        }
    }

    public string? SelectedGpuOptionId
    {
        get => SelectedGpuOption?.Descriptor.Id;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                SelectedGpuOption = null;
                return;
            }

            SelectedGpuOption = AvailableGpuOptions.FirstOrDefault(
                option => string.Equals(option.Descriptor.Id, value, StringComparison.Ordinal));
        }
    }

    public string GpuSupportMessage
    {
        get => _gpuSupportMessage;
        private set
        {
            if (SetProperty(ref _gpuSupportMessage, value))
            {
                OnPropertyChanged(nameof(HasGpuSupportMessage));
            }
        }
    }

    public bool HasGpuSupportMessage => !string.IsNullOrWhiteSpace(GpuSupportMessage);

    public string GpuWarningMessage
    {
        get => _gpuWarningMessage;
        private set
        {
            if (SetProperty(ref _gpuWarningMessage, value))
            {
                OnPropertyChanged(nameof(HasGpuWarningMessage));
            }
        }
    }

    public bool HasGpuWarningMessage => !string.IsNullOrWhiteSpace(GpuWarningMessage);

    public string GpuCustomDescription
    {
        get => _gpuCustomDescription;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!SetProperty(ref _gpuCustomDescription, normalized)
                || _suppressGpuUiRefresh
                || SelectedGpuOption?.Descriptor.IsCustomEntry != true)
            {
                return;
            }

            ApplyCustomGpuFields();
        }
    }

    public string GpuCustomInstancePath
    {
        get => _gpuCustomInstancePath;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!SetProperty(ref _gpuCustomInstancePath, normalized)
                || _suppressGpuUiRefresh
                || SelectedGpuOption?.Descriptor.IsCustomEntry != true)
            {
                return;
            }

            ApplyCustomGpuFields();
        }
    }

    public bool IsGpuSelectionEnabled
    {
        get => _isGpuSelectionEnabled;
        private set => SetProperty(ref _isGpuSelectionEnabled, value);
    }

    public bool IsGpuDeprecatedToggleEnabled
    {
        get => _isGpuDeprecatedToggleEnabled;
        private set => SetProperty(ref _isGpuDeprecatedToggleEnabled, value);
    }

    public bool IsGpuCustomSelectionFieldsVisible
    {
        get => _isGpuCustomSelectionFieldsVisible;
        private set => SetProperty(ref _isGpuCustomSelectionFieldsVisible, value);
    }

    public bool ShowGpuRestartRequiredHint
    {
        get => _showGpuRestartRequiredHint;
        private set => SetProperty(ref _showGpuRestartRequiredHint, value);
    }

    public bool DeploymentWithPause
    {
        get => _deploymentWithPause;
        set
        {
            if (SetProperty(ref _deploymentWithPause, value))
            {
                MarkStartPerformanceDirty();
            }
        }
    }

    public string StartsWithScript
    {
        get => _startsWithScript;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (SetProperty(ref _startsWithScript, normalized))
            {
                MarkStartPerformanceDirty();
            }
        }
    }

    public string EndsWithScript
    {
        get => _endsWithScript;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (SetProperty(ref _endsWithScript, normalized))
            {
                MarkStartPerformanceDirty();
            }
        }
    }

    public bool CopilotWithScript
    {
        get => _copilotWithScript;
        set
        {
            if (SetProperty(ref _copilotWithScript, value))
            {
                MarkStartPerformanceDirty();
            }
        }
    }

    public bool ManualStopWithScript
    {
        get => _manualStopWithScript;
        set
        {
            if (SetProperty(ref _manualStopWithScript, value))
            {
                MarkStartPerformanceDirty();
            }
        }
    }

    public bool BlockSleep
    {
        get => _blockSleep;
        set
        {
            if (SetProperty(ref _blockSleep, value))
            {
                OnPropertyChanged(nameof(ShowBlockSleepWithScreenOnOption));
                MarkStartPerformanceDirty();
            }
        }
    }

    public bool ShowBlockSleepWithScreenOnOption => BlockSleep;

    public bool BlockSleepWithScreenOn
    {
        get => _blockSleepWithScreenOn;
        set
        {
            if (SetProperty(ref _blockSleepWithScreenOn, value))
            {
                MarkStartPerformanceDirty();
            }
        }
    }

    public bool EnablePenguin
    {
        get => _enablePenguin;
        set
        {
            if (SetProperty(ref _enablePenguin, value))
            {
                OnPropertyChanged(nameof(ShowPenguinIdField));
                MarkStartPerformanceDirty();
            }
        }
    }

    public bool ShowPenguinIdField => EnablePenguin;

    public bool EnableYituliu
    {
        get => _enableYituliu;
        set
        {
            if (SetProperty(ref _enableYituliu, value))
            {
                MarkStartPerformanceDirty();
            }
        }
    }

    public string PenguinId
    {
        get => _penguinId;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (SetProperty(ref _penguinId, normalized))
            {
                MarkStartPerformanceDirty();
            }
        }
    }

    public int TaskTimeoutMinutes
    {
        get => _taskTimeoutMinutes;
        set
        {
            var normalized = Math.Max(0, value);
            if (SetProperty(ref _taskTimeoutMinutes, normalized))
            {
                MarkStartPerformanceDirty();
            }
        }
    }

    public int ReminderIntervalMinutes
    {
        get => _reminderIntervalMinutes;
        set
        {
            var normalized = Math.Max(1, value);
            if (SetProperty(ref _reminderIntervalMinutes, normalized))
            {
                MarkStartPerformanceDirty();
            }
        }
    }

    public bool HasPendingStartPerformanceChanges
    {
        get => _hasPendingStartPerformanceChanges;
        private set
        {
            if (SetProperty(ref _hasPendingStartPerformanceChanges, value))
            {
                OnPropertyChanged(nameof(IsStartPerformanceSaveInProgress));
                OnPropertyChanged(nameof(HasStartPerformanceSaveSucceeded));
            }
        }
    }

    public string StartPerformanceValidationMessage
    {
        get => _startPerformanceValidationMessage;
        private set
        {
            if (SetProperty(ref _startPerformanceValidationMessage, value))
            {
                OnPropertyChanged(nameof(HasStartPerformanceValidationMessage));
            }
        }
    }

    public bool HasStartPerformanceValidationMessage => !string.IsNullOrWhiteSpace(StartPerformanceValidationMessage);

    public DateTimeOffset? LastSuccessfulStartPerformanceSaveAt
    {
        get => _lastSuccessfulStartPerformanceSaveAt;
        private set
        {
            if (SetProperty(ref _lastSuccessfulStartPerformanceSaveAt, value))
            {
                OnPropertyChanged(nameof(HasStartPerformanceSaveSucceeded));
            }
        }
    }

    public bool IsStartPerformanceSaveInProgress => HasPendingStartPerformanceChanges;

    public bool HasStartPerformanceSaveSucceeded =>
        !HasPendingStartPerformanceChanges && LastSuccessfulStartPerformanceSaveAt.HasValue;

    public bool ForceScheduledStart
    {
        get => _forceScheduledStart;
        set
        {
            if (SetProperty(ref _forceScheduledStart, value))
            {
                MarkTimerDirty();
            }
        }
    }

    public bool ShowWindowBeforeForceScheduledStart
    {
        get => _showWindowBeforeForceScheduledStart;
        set
        {
            if (SetProperty(ref _showWindowBeforeForceScheduledStart, value))
            {
                MarkTimerDirty();
            }
        }
    }

    public bool CustomTimerConfig
    {
        get => _customTimerConfig;
        set
        {
            if (SetProperty(ref _customTimerConfig, value))
            {
                if (!value)
                {
                    TimerValidationMessage = string.Empty;
                }

                MarkTimerDirty();
            }
        }
    }

    public bool HasPendingTimerChanges
    {
        get => _hasPendingTimerChanges;
        private set
        {
            if (SetProperty(ref _hasPendingTimerChanges, value))
            {
                OnPropertyChanged(nameof(IsTimerSaveInProgress));
                OnPropertyChanged(nameof(HasTimerSaveSucceeded));
            }
        }
    }

    public string TimerValidationMessage
    {
        get => _timerValidationMessage;
        private set
        {
            if (SetProperty(ref _timerValidationMessage, value))
            {
                OnPropertyChanged(nameof(HasTimerValidationMessage));
            }
        }
    }

    public bool HasTimerValidationMessage => !string.IsNullOrWhiteSpace(TimerValidationMessage);

    public DateTimeOffset? LastSuccessfulTimerSaveAt
    {
        get => _lastSuccessfulTimerSaveAt;
        private set
        {
            if (SetProperty(ref _lastSuccessfulTimerSaveAt, value))
            {
                OnPropertyChanged(nameof(HasTimerSaveSucceeded));
            }
        }
    }

    public bool IsTimerSaveInProgress => HasPendingTimerChanges;

    public bool HasTimerSaveSucceeded => !HasPendingTimerChanges && LastSuccessfulTimerSaveAt.HasValue;

    public GuiSettingsSnapshot CurrentGuiSnapshot => BuildNormalizedGuiSnapshot();

    public void ApplyStartupSnapshot(StartupShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _suppressPageAutoSave = true;
        _suppressGuiAutoSave = true;
        _suppressGuiPreview = true;
        try
        {
            Theme = snapshot.Theme;
            Language = snapshot.Language;
            UseTray = snapshot.UseTray;
            MinimizeToTray = snapshot.MinimizeToTray;
            WindowTitleScrollable = snapshot.WindowTitleScrollable;
            UiScalePercent = snapshot.UiScalePercent;
            UseSoftwareRendering = snapshot.UseSoftwareRendering;
            LogItemDateFormatString = snapshot.LogItemDateFormatString;
            DeveloperModeEnabled = snapshot.DeveloperModeEnabled;
            BackgroundImagePath = snapshot.BackgroundImagePath;
            BackgroundOpacity = snapshot.BackgroundOpacity;
            BackgroundBlur = snapshot.BackgroundBlur;
            BackgroundStretchMode = snapshot.BackgroundStretchMode;
            HotkeyShowGui = snapshot.HotkeyShowGui;
            HotkeyLinkStart = snapshot.HotkeyLinkStart;
            _persistedHotkeyShowGui = snapshot.HotkeyShowGui;
            _persistedHotkeyLinkStart = snapshot.HotkeyLinkStart;
            HasPendingGuiChanges = false;
        }
        finally
        {
            _suppressGuiPreview = false;
            _suppressGuiAutoSave = false;
            _suppressPageAutoSave = false;
        }
    }

    private bool IsSelectedSection(string key)
    {
        return string.Equals(SelectedSection?.Key, key, StringComparison.OrdinalIgnoreCase);
    }

    public bool SelectSection(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var target = Sections.FirstOrDefault(
            section => string.Equals(section.Key, key, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        SelectedSection = target;
        return true;
    }

    private void RebuildSections(string? preferredKey = null)
    {
        var target = preferredKey ?? SelectedSection?.Key ?? SectionOrder[0];
        Sections.Clear();
        foreach (var key in SectionOrder)
        {
            Sections.Add(new SettingsSectionViewModel(key, RootTexts[$"Settings.Section.{key}"]));
        }

        var selected = Sections.FirstOrDefault(section => string.Equals(section.Key, target, StringComparison.OrdinalIgnoreCase))
            ?? Sections.First();
        SelectedSection = selected;
    }

    private void RefreshCurrentSectionActions()
    {
        CurrentSectionActions.Clear();
        var key = SelectedSection?.Key;
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        switch (key)
        {
            case "GUI":
            case "Background":
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.save-gui", RootTexts["Settings.Action.SaveGui"], IsPrimary: true));
                break;
            case "Connect":
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.save-connection-game", RootTexts["Settings.Action.SaveConnectionGame"], IsPrimary: true));
                break;
            case "Game":
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.save-start-performance", RootTexts["Settings.Action.SaveStartPerformance"], IsPrimary: true));
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.save-connection-game", RootTexts["Settings.Action.SaveConnectionGame"]));
                break;
            case "Start":
            case "Performance":
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.save-start-performance", RootTexts["Settings.Action.SaveStartPerformance"], IsPrimary: true));
                break;
            case "Timer":
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.save-timer", RootTexts["Settings.Action.SaveTimer"], IsPrimary: true));
                break;
            case "RemoteControl":
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.save-remote", RootTexts["Settings.Action.SaveRemote"], IsPrimary: true));
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.test-remote", RootTexts["Settings.Action.TestRemote"]));
                break;
            case "HotKey":
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.register-hotkeys", RootTexts["Settings.Action.RegisterHotkeys"], IsPrimary: true));
                break;
            case "ExternalNotification":
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.save-notification", RootTexts["Settings.Action.SaveNotification"], IsPrimary: true));
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.validate-notification", RootTexts["Settings.Action.ValidateNotification"]));
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.test-notification", RootTexts["Settings.Action.TestNotification"]));
                break;
            case "VersionUpdate":
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.save-version-update", RootTexts["Settings.Action.SaveVersionUpdate"], IsPrimary: true));
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.check-version-update", RootTexts["Settings.Action.CheckVersionUpdate"]));
                break;
            case "Achievement":
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.save-achievement", RootTexts["Settings.Action.SaveAchievement"], IsPrimary: true));
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.refresh-achievement", RootTexts["Settings.Action.RefreshAchievement"]));
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.show-achievement", RootTexts["Settings.Action.ShowAchievement"]));
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.open-achievement-guide", RootTexts["Settings.Action.OpenAchievementGuide"], IsSubtle: true));
                break;
            case "IssueReport":
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.build-issue-report", RootTexts["Settings.Action.BuildIssueReport"], IsPrimary: true));
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.open-debug-directory", RootTexts["Settings.Action.OpenDebugDirectory"]));
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.clear-image-cache", RootTexts["Settings.Action.ClearImageCache"]));
                break;
            case "About":
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.check-announcement", RootTexts["Settings.Action.CheckAnnouncement"], IsPrimary: true));
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.open-official", RootTexts["Settings.Action.OpenOfficial"], IsSubtle: true));
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.open-community", RootTexts["Settings.Action.OpenCommunity"], IsSubtle: true));
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.open-download", RootTexts["Settings.Action.OpenDownload"], IsSubtle: true));
                break;
            case "ConfigurationManager":
                CurrentSectionActions.Add(new SettingsSectionActionItem("settings.refresh-profiles", RootTexts["Settings.Action.RefreshProfiles"], IsPrimary: true));
                break;
            default:
                break;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ResetAutoSaveLifecycle();
        SuspendAutoSave(repairTimerProfilesOnResume: true);

        var initialized = false;
        try
        {
            await PersistPlatformHotkeyDefaultsMigrationIfNeededAsync(cancellationToken);
            await LoadInitialSettingsAsync(cancellationToken);
            await RefreshConfigurationProfilesAsync(cancellationToken);
            _deferredSectionDataLoadEnabled = _deferredSectionDataLoadRequested;
            if (_deferredSectionDataLoadEnabled)
            {
                await EnsureSectionDataLoadedAsync(SelectedSection?.Key, cancellationToken);
            }
            else
            {
                await WarmupDeferredSectionDataAsync(cancellationToken);
            }

            RestoreExternalNotificationStatusSummaryIfIdle();
            await RecordEventAsync("Settings", "Settings page initialized.", cancellationToken);
            initialized = true;
        }
        finally
        {
            _autoSaveReady = initialized;
            ResumeAutoSave();
        }
    }

    private async Task PersistPlatformHotkeyDefaultsMigrationIfNeededAsync(CancellationToken cancellationToken)
    {
        if (!HotkeyConfigurationCodec.ApplyPlatformDefaultsMigration(Runtime.ConfigurationService.CurrentConfig))
        {
            return;
        }

        try
        {
            await Runtime.ConfigurationService.SaveAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Runtime.LogService.Warn($"Failed to persist platform hotkey defaults migration: {ex.Message}");
        }
    }

    public void BeginViewComposition()
    {
        _viewCompositionDepth++;
        if (_viewCompositionDepth != 1)
        {
            return;
        }

        EnableDeferredSectionDataLoad();
        SuspendAutoSave(repairTimerProfilesOnResume: true);
    }

    public void EndViewComposition()
    {
        if (_viewCompositionDepth <= 0)
        {
            _viewCompositionDepth = 0;
            return;
        }

        _viewCompositionDepth--;
        if (_viewCompositionDepth != 0)
        {
            return;
        }

        DisableDeferredSectionDataLoad();
        ResumeAutoSave();
    }

    public void EnableDeferredSectionDataLoad()
    {
        _deferredSectionDataLoadRequested = true;
        _deferredSectionDataLoadEnabled = true;
    }

    public void DisableDeferredSectionDataLoad()
    {
        _deferredSectionDataLoadRequested = false;
        _deferredSectionDataLoadEnabled = false;
    }

    public async Task ChangeLanguageAsync(string targetLanguage, CancellationToken cancellationToken = default)
    {
        var total = Stopwatch.StartNew();
        var step = Stopwatch.StartNew();
        var normalized = NormalizeLanguage(targetLanguage);
        var previousLanguage = Language;
        BeginBlockingOperationOverlay();
        try
        {
            await WaitForBlockingOperationOverlayRenderAsync();
            _ = RecordLanguageSwitchTimingAsync(
                "Settings.ChangeLanguage.OverlayRendered",
                step,
                previousLanguage,
                normalized,
                cancellationToken);

            step.Restart();
            var changeResult = await ChangeLanguageCoordinatorAsync(normalized, cancellationToken);
            _ = RecordLanguageSwitchTimingAsync(
                "Settings.ChangeLanguage.Coordinator",
                step,
                previousLanguage,
                normalized,
                cancellationToken,
                ("success", changeResult.Success));

            step.Restart();
            var appliedLanguage = await ApplyLanguageChangeResultAsync(changeResult, cancellationToken);
            _ = RecordLanguageSwitchTimingAsync(
                "Settings.ChangeLanguage.ApplyResult",
                step,
                previousLanguage,
                normalized,
                cancellationToken,
                ("appliedLanguage", appliedLanguage));
            if (appliedLanguage is null)
            {
                SetSelectedLanguageValue(Language, requestLanguageChange: false);
                return;
            }

            step.Restart();
            await _pendingUnifiedLanguageApplyTask;
            _ = RecordLanguageSwitchTimingAsync(
                "Settings.ChangeLanguage.WaitPostedApply",
                step,
                previousLanguage,
                appliedLanguage,
                cancellationToken);

            step.Restart();
            await WaitForPostedLanguageRefreshAsync(previousLanguage, appliedLanguage, cancellationToken);
            _ = RecordLanguageSwitchTimingAsync(
                "Settings.ChangeLanguage.WaitViewRefresh",
                step,
                previousLanguage,
                appliedLanguage,
                cancellationToken);

            step.Restart();
            if (!string.Equals(Language, appliedLanguage, StringComparison.OrdinalIgnoreCase))
            {
                ApplyUnifiedLanguage(appliedLanguage);
            }
            else
            {
                SetSelectedLanguageValue(appliedLanguage, requestLanguageChange: false);
            }

            if (!string.Equals(previousLanguage, appliedLanguage, StringComparison.OrdinalIgnoreCase))
            {
                _ = Runtime.AchievementTrackerService.Unlock("Linguist");
            }

            _ = RecordLanguageSwitchTimingAsync(
                "Settings.ChangeLanguage.FinalSync",
                step,
                previousLanguage,
                appliedLanguage,
                cancellationToken);
        }
        finally
        {
            EndBlockingOperationOverlay();
            _ = RecordTemporaryTimingAsync(
                "Settings.ChangeLanguage.Total",
                total.Elapsed.TotalMilliseconds,
                cancellationToken,
                ("from", previousLanguage),
                ("target", normalized),
                ("current", Language));
        }
    }

    public async Task SaveGuiSettingsAsync(CancellationToken cancellationToken = default)
    {
        await RunSettingsSaveTargetAsync(
            "Settings.AutoSave.Gui",
            ct => SaveGuiSettingsCoreAsync(triggeredByAutoSave: false, cancellationToken: ct),
            cancellationToken);
    }

    public async Task RefreshConfigurationProfilesAsync(CancellationToken cancellationToken = default)
    {
        await LoadConfigurationProfilesAsync("Settings.ConfigurationManager.Load", cancellationToken);
    }

    public async Task AddConfigurationProfileAsync(CancellationToken cancellationToken = default)
    {
        ClearConfigurationManagerStatus();
        var profileName = (ConfigurationManagerNewProfileName ?? string.Empty).Trim();
        var copyFrom = string.IsNullOrWhiteSpace(ConfigurationManagerSelectedProfile)
            ? null
            : ConfigurationManagerSelectedProfile;
        var result = await Runtime.ConfigurationProfileFeatureService.AddProfileAsync(
            profileName,
            copyFrom,
            cancellationToken);
        await HandleConfigurationProfileResultAsync(
            result,
            "Settings.ConfigurationManager.Add",
            successMessage: FormatSettingsText(
                "Settings.ConfigurationManager.Status.AddSucceeded",
                "配置 `{0}` 已新增。",
                profileName),
            failureMessage: LocalizeSettingsText(
                "Settings.ConfigurationManager.Status.AddFailed",
                "配置新增失败。"),
            cancellationToken: cancellationToken,
            suppressFailureDialog: true,
            onFailure: SetConfigurationManagerSaveAsNewFailure);
        if (result.Success)
        {
            _suppressConfigurationManagerSaveAsNewSuccessReset = true;
            try
            {
                ConfigurationManagerNewProfileName = string.Empty;
            }
            finally
            {
                _suppressConfigurationManagerSaveAsNewSuccessReset = false;
            }

            ConfigurationManagerSaveAsNewFailedText = string.Empty;
            ConfigurationManagerSaveAsNewSucceededText = LocalizeSettingsText(
                "Settings.ConfigurationManager.SaveAsNewSucceededInline",
                "保存成功");
        }
    }

    public async Task DeleteConfigurationProfileAsync(CancellationToken cancellationToken = default)
    {
        ClearConfigurationManagerStatus();
        var target = ConfigurationManagerSelectedProfile;
        var previousCurrent = Runtime.ConfigurationService.CurrentConfig.CurrentProfile;
        var result = await Runtime.ConfigurationProfileFeatureService.DeleteProfileAsync(target, cancellationToken);
        var deleted = await HandleConfigurationProfileResultAsync(
            result,
            "Settings.ConfigurationManager.Delete",
            successMessage: FormatSettingsText(
                "Settings.ConfigurationManager.Status.DeleteSucceeded",
                "配置 `{0}` 已删除。",
                target),
            failureMessage: LocalizeSettingsText(
                "Settings.ConfigurationManager.Status.DeleteFailed",
                "配置删除失败。"),
            cancellationToken);
        if (!deleted)
        {
            return;
        }

        var current = Runtime.ConfigurationService.CurrentConfig.CurrentProfile;
        if (string.IsNullOrWhiteSpace(current))
        {
            return;
        }

        var message = string.Equals(previousCurrent, current, StringComparison.OrdinalIgnoreCase)
            ? FormatSettingsText(
                "Settings.ConfigurationManager.Status.DeleteReloadedCurrent",
                "配置 `{0}` 已删除，已重新加载配置 `{1}`。",
                target,
                current)
            : FormatSettingsText(
                "Settings.ConfigurationManager.Status.DeleteSwitchedCurrent",
                "配置 `{0}` 已删除，已切换至配置 `{1}`。",
                target,
                current);
        await LoadFromConfigAsync(Runtime.ConfigurationService.CurrentConfig, cancellationToken);
        LoadConnectionSharedStateFromConfig();
        ConfigurationManagerStatusMessage = message;
        ConfigurationManagerErrorMessage = string.Empty;
        StatusMessage = message;
        LastErrorMessage = string.Empty;
        await RaiseConfigurationContextChangedAsync(
            ConfigurationContextChangeReason.ProfileSwitched,
            message,
            cancellationToken);
    }

    public async Task MoveConfigurationProfileUpAsync(CancellationToken cancellationToken = default)
    {
        ClearConfigurationManagerStatus();
        var target = ConfigurationManagerSelectedProfile;
        var result = await Runtime.ConfigurationProfileFeatureService.MoveProfileAsync(target, -1, cancellationToken);
        await HandleConfigurationProfileResultAsync(
            result,
            "Settings.ConfigurationManager.MoveUp",
            successMessage: FormatSettingsText(
                "Settings.ConfigurationManager.Status.MoveUpSucceeded",
                "配置 `{0}` 已上移。",
                target),
            failureMessage: LocalizeSettingsText(
                "Settings.ConfigurationManager.Status.MoveUpFailed",
                "配置上移失败。"),
            cancellationToken);
    }

    public async Task MoveConfigurationProfileDownAsync(CancellationToken cancellationToken = default)
    {
        ClearConfigurationManagerStatus();
        var target = ConfigurationManagerSelectedProfile;
        var result = await Runtime.ConfigurationProfileFeatureService.MoveProfileAsync(target, 1, cancellationToken);
        await HandleConfigurationProfileResultAsync(
            result,
            "Settings.ConfigurationManager.MoveDown",
            successMessage: FormatSettingsText(
                "Settings.ConfigurationManager.Status.MoveDownSucceeded",
                "配置 `{0}` 已下移。",
                target),
            failureMessage: LocalizeSettingsText(
                "Settings.ConfigurationManager.Status.MoveDownFailed",
                "配置下移失败。"),
            cancellationToken);
    }

    public async Task SwitchConfigurationProfileAsync(CancellationToken cancellationToken = default)
    {
        if (_suppressConfigurationProfileSelectionHandling)
        {
            return;
        }

        await _configurationProfileSwitchSemaphore.WaitAsync(cancellationToken);
        try
        {
            ClearConfigurationManagerStatus();
            var target = ConfigurationManagerSelectedProfile;
            var current = Runtime.ConfigurationService.CurrentConfig.CurrentProfile;
            if (string.IsNullOrWhiteSpace(target)
                || string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!await FlushConfigurationSavesForCloseAsync(cancellationToken))
            {
                ConfigurationManagerErrorMessage = string.IsNullOrWhiteSpace(LastErrorMessage)
                    ? "Failed to save pending settings before switching profiles."
                    : LastErrorMessage;
                ConfigurationManagerStatusMessage = LocalizeSettingsText(
                    "Settings.ConfigurationManager.Status.SwitchFailed",
                    "配置切换失败。");
                await LoadConfigurationProfilesAsync(
                    "Settings.ConfigurationManager.ReloadAfterFlushFailure",
                    cancellationToken,
                    updateStatus: false);
                return;
            }

            if (BeforeConfigurationProfileSwitchAsync is not null)
            {
                var flushResult = await BeforeConfigurationProfileSwitchAsync(cancellationToken);
                if (!flushResult.Success)
                {
                    ConfigurationManagerErrorMessage = flushResult.Message;
                    ConfigurationManagerStatusMessage = LocalizeSettingsText(
                        "Settings.ConfigurationManager.Status.SwitchFailed",
                        "配置切换失败。");
                    await LoadConfigurationProfilesAsync(
                        "Settings.ConfigurationManager.ReloadAfterFlushFailure",
                        cancellationToken,
                        updateStatus: false);
                    return;
                }
            }

            var result = await Runtime.ConfigurationProfileFeatureService.SwitchProfileAsync(target, cancellationToken);
            var payload = await ApplyResultAsync(result, "Settings.ConfigurationManager.Switch", cancellationToken);
            if (payload is null)
            {
                ConfigurationManagerErrorMessage = result.Message;
                ConfigurationManagerStatusMessage = LocalizeSettingsText(
                    "Settings.ConfigurationManager.Status.SwitchFailed",
                    "配置切换失败。");
                await LoadConfigurationProfilesAsync(
                    "Settings.ConfigurationManager.ReloadAfterFailure",
                    cancellationToken,
                    updateStatus: false);
                return;
            }

            var switchMessage = FormatSettingsText(
                "Settings.ConfigurationManager.Status.SwitchSucceeded",
                "已切换至配置 `{0}`。",
                target);
            await LoadFromConfigAsync(Runtime.ConfigurationService.CurrentConfig, cancellationToken);
            ApplyConfigurationProfileState(payload);
            LoadConnectionSharedStateFromConfig();
            ConfigurationManagerStatusMessage = switchMessage;
            ConfigurationManagerErrorMessage = string.Empty;
            StatusMessage = switchMessage;
            LastErrorMessage = string.Empty;
            await RaiseConfigurationContextChangedAsync(
                ConfigurationContextChangeReason.ProfileSwitched,
                switchMessage,
                cancellationToken);
        }
        finally
        {
            _configurationProfileSwitchSemaphore.Release();
        }
    }

    public async Task SaveCurrentConfigurationAsync(CancellationToken cancellationToken = default)
    {
        ClearConfigurationManagerStatus();
        var saved = await ConfigurationSaveTracker.Instance.RunTrackedAsync(
            "Settings.ConfigurationManager.Current",
            LocalizeSettingsText("Settings.ConfigurationManager.Title", "配置管理"),
            "Settings.ConfigurationManager.SaveCurrent",
            Runtime.DiagnosticsService,
            async ct =>
            {
                await Runtime.ConfigurationService.SaveAsync(ct);
                return true;
            },
            cancellationToken);
        if (saved)
        {
            await LoadConfigurationProfilesAsync(
                "Settings.ConfigurationManager.ReloadAfterSave",
                cancellationToken,
                updateStatus: false);
            ConfigurationManagerStatusMessage = string.Empty;
            ConfigurationManagerErrorMessage = string.Empty;
            LastErrorMessage = string.Empty;
            await RecordEventAsync(
                "Settings.ConfigurationManager.SaveCurrent",
                "Current configuration saved.",
                cancellationToken);
        }
    }

    public async Task ExportAllConfigurationsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ClearConfigurationManagerStatus();
        var normalizedPath = NormalizeConfigPath(filePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            ConfigurationManagerStatusMessage = LocalizeSettingsText(
                "Settings.ConfigurationManager.Status.ExportAllFailed",
                "导出所有配置失败。");
            ConfigurationManagerErrorMessage = LocalizeSettingsText(
                "Settings.ConfigurationManager.Error.ExportPathEmpty",
                "导出路径为空。");
            return;
        }

        try
        {
            var exportConfig = CloneConfig(Runtime.ConfigurationService.CurrentConfig);
            await WriteConfigFileAsync(exportConfig, normalizedPath, cancellationToken);
            ConfigurationManagerStatusMessage = FormatSettingsText(
                "Settings.ConfigurationManager.Status.ExportAllSucceeded",
                "全部配置已导出到 `{0}`。",
                normalizedPath);
            ConfigurationManagerErrorMessage = string.Empty;
            StatusMessage = ConfigurationManagerStatusMessage;
            LastErrorMessage = string.Empty;
            await RecordEventAsync("Settings.ConfigurationManager.ExportAll", ConfigurationManagerStatusMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            ConfigurationManagerStatusMessage = LocalizeSettingsText(
                "Settings.ConfigurationManager.Status.ExportAllFailed",
                "导出所有配置失败。");
            ConfigurationManagerErrorMessage = ex.Message;
            await RecordUnhandledExceptionAsync(
                "Settings.ConfigurationManager.ExportAll",
                ex,
                UiErrorCode.ConfigurationProfileSaveFailed,
                "Failed to export all configuration profiles.",
                cancellationToken);
        }
    }

    public async Task ExportCurrentConfigurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ClearConfigurationManagerStatus();
        var normalizedPath = NormalizeConfigPath(filePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            ConfigurationManagerStatusMessage = LocalizeSettingsText(
                "Settings.ConfigurationManager.Status.ExportCurrentFailed",
                "导出当前配置失败。");
            ConfigurationManagerErrorMessage = LocalizeSettingsText(
                "Settings.ConfigurationManager.Error.ExportPathEmpty",
                "导出路径为空。");
            return;
        }

        try
        {
            var exportConfig = BuildCurrentProfileOnlyConfig(Runtime.ConfigurationService.CurrentConfig);
            await WriteConfigFileAsync(exportConfig, normalizedPath, cancellationToken);
            ConfigurationManagerStatusMessage = FormatSettingsText(
                "Settings.ConfigurationManager.Status.ExportCurrentSucceeded",
                "当前配置已导出到 `{0}`。",
                normalizedPath);
            ConfigurationManagerErrorMessage = string.Empty;
            StatusMessage = ConfigurationManagerStatusMessage;
            LastErrorMessage = string.Empty;
            await RecordEventAsync("Settings.ConfigurationManager.ExportCurrent", ConfigurationManagerStatusMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            ConfigurationManagerStatusMessage = LocalizeSettingsText(
                "Settings.ConfigurationManager.Status.ExportCurrentFailed",
                "导出当前配置失败。");
            ConfigurationManagerErrorMessage = ex.Message;
            await RecordUnhandledExceptionAsync(
                "Settings.ConfigurationManager.ExportCurrent",
                ex,
                UiErrorCode.ConfigurationProfileSaveFailed,
                "Failed to export current configuration profile.",
                cancellationToken);
        }
    }

    public async Task ImportConfigurationsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ClearConfigurationManagerStatus();
        var normalizedPath = NormalizeConfigPath(filePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            ConfigurationManagerStatusMessage = LocalizeSettingsText(
                "Settings.ConfigurationManager.Status.ImportFailed",
                "导入配置失败。");
            ConfigurationManagerErrorMessage = LocalizeSettingsText(
                "Settings.ConfigurationManager.Error.ImportPathEmpty",
                "导入路径为空。");
            return;
        }

        if (!File.Exists(normalizedPath))
        {
            ConfigurationManagerStatusMessage = LocalizeSettingsText(
                "Settings.ConfigurationManager.Status.ImportFailed",
                "导入配置失败。");
            ConfigurationManagerErrorMessage = LocalizeSettingsText(
                "Settings.ConfigurationManager.Error.ImportFileNotFound",
                "导入文件不存在。");
            return;
        }

        try
        {
            await using var stream = File.OpenRead(normalizedPath);
            var imported = await JsonSerializer.DeserializeAsync<UnifiedConfig>(stream, cancellationToken: cancellationToken);
            if (imported is null || imported.Profiles.Count == 0)
            {
                ConfigurationManagerStatusMessage = LocalizeSettingsText(
                    "Settings.ConfigurationManager.Status.ImportFailed",
                    "导入配置失败。");
                ConfigurationManagerErrorMessage = LocalizeSettingsText(
                    "Settings.ConfigurationManager.Error.ImportNoValidProfile",
                    "导入文件中未找到有效配置。");
                return;
            }

            var currentConfig = Runtime.ConfigurationService.CurrentConfig;
            var existingNames = new HashSet<string>(currentConfig.Profiles.Keys, StringComparer.OrdinalIgnoreCase);
            var importedCount = 0;
            var renamedCount = 0;

            foreach (var (name, profile) in imported.Profiles)
            {
                if (string.IsNullOrWhiteSpace(name) || profile is null)
                {
                    continue;
                }

                var normalizedName = name.Trim();
                var targetName = AllocateUniqueProfileName(existingNames, normalizedName);
                if (!string.Equals(targetName, normalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    renamedCount++;
                }

                currentConfig.Profiles[targetName] = CloneProfile(profile);
                existingNames.Add(targetName);
                importedCount++;
            }

            if (importedCount == 0)
            {
                ConfigurationManagerStatusMessage = LocalizeSettingsText(
                    "Settings.ConfigurationManager.Status.ImportFailed",
                    "导入配置失败。");
                ConfigurationManagerErrorMessage = LocalizeSettingsText(
                    "Settings.ConfigurationManager.Error.ImportNoUsableEntries",
                    "导入文件中未找到可用配置项。");
                return;
            }

            if (string.IsNullOrWhiteSpace(currentConfig.CurrentProfile)
                || !currentConfig.Profiles.ContainsKey(currentConfig.CurrentProfile))
            {
                currentConfig.CurrentProfile = currentConfig.Profiles.Keys.First();
            }

            var saved = await ConfigurationSaveTracker.Instance.RunTrackedAsync(
                "Settings.ConfigurationManager.Import",
                LocalizeSettingsText("Settings.ConfigurationManager.Title", "配置管理"),
                "Settings.ConfigurationManager.Import.Save",
                Runtime.DiagnosticsService,
                async ct =>
                {
                    await Runtime.ConfigurationService.SaveAsync(ct);
                    return true;
                },
                cancellationToken);
            if (!saved)
            {
                return;
            }

            var importSuccessMessage = renamedCount > 0
                ? FormatSettingsText(
                    "Settings.ConfigurationManager.Status.ImportSucceededWithRename",
                    "已导入 {0} 个配置（{1} 个重命名以避免冲突）。",
                    importedCount,
                    renamedCount)
                : FormatSettingsText(
                    "Settings.ConfigurationManager.Status.ImportSucceeded",
                    "已导入 {0} 个配置。",
                    importedCount);
            ConfigurationManagerImportSucceededText = LocalizeSettingsText(
                "Settings.ConfigurationManager.ImportSucceededInline",
                "导入成功");
            ConfigurationManagerStatusMessage = importSuccessMessage;
            ConfigurationManagerErrorMessage = string.Empty;
            StatusMessage = ConfigurationManagerStatusMessage;
            LastErrorMessage = string.Empty;
            await RefreshAfterConfigurationImportAsync(
                ConfigurationContextChangeReason.UnifiedImport,
                "Settings.ConfigurationManager.Import.ContextRefresh",
                ConfigurationManagerStatusMessage,
                report: null,
                cancellationToken);
            ConfigurationManagerStatusMessage = importSuccessMessage;
            ConfigurationManagerErrorMessage = string.Empty;
            StatusMessage = ConfigurationManagerStatusMessage;
            LastErrorMessage = string.Empty;
            await RecordEventAsync("Settings.ConfigurationManager.Import", ConfigurationManagerStatusMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            ConfigurationManagerStatusMessage = LocalizeSettingsText(
                "Settings.ConfigurationManager.Status.ImportFailed",
                "导入配置失败。");
            ConfigurationManagerErrorMessage = ex.Message;
            await RecordUnhandledExceptionAsync(
                "Settings.ConfigurationManager.Import",
                ex,
                UiErrorCode.ImportFailed,
                "Failed to import configuration profiles.",
                cancellationToken);
        }
    }

    public async Task<ImportReport> ImportLegacyConfigurationsAsync(
        LegacyImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ClearConfigurationManagerStatus();
        var report = await Runtime.ConfigurationService.ImportLegacyAsync(request, cancellationToken);
        var statusMessage = ImportReportTextFormatter.BuildStatusMessage(report, manualImport: request.ManualImport);
        var errorMessage = report.Errors.Count == 0
            ? string.Empty
            : string.Join("; ", report.Errors);

        ConfigurationManagerStatusMessage = statusMessage;
        ConfigurationManagerErrorMessage = errorMessage;
        ConfigurationManagerImportSucceededText = report.AppliedConfig
            ? LocalizeSettingsText(
                "Settings.ConfigurationManager.ImportSucceededInline",
                "导入成功")
            : string.Empty;
        StatusMessage = statusMessage;
        LastErrorMessage = report.AppliedConfig ? errorMessage : (errorMessage.Length == 0 ? statusMessage : errorMessage);

        if (report.AppliedConfig)
        {
            await RefreshAfterConfigurationImportAsync(
                ConfigurationContextChangeReason.LegacyImport,
                "Settings.ConfigurationManager.ImportLegacy.ContextRefresh",
                statusMessage,
                report,
                cancellationToken);
            ConfigurationManagerStatusMessage = statusMessage;
            ConfigurationManagerErrorMessage = errorMessage;
            StatusMessage = statusMessage;
            LastErrorMessage = errorMessage;
            await RecordEventAsync(
                "Settings.ConfigurationManager.ImportLegacy",
                $"{statusMessage} {report.Summary}",
                cancellationToken);
            return report;
        }

        if (report.DamagedFiles.Count > 0 && report.ImportedFiles.Count > 0 && !request.AllowPartialImport)
        {
            await RecordEventAsync(
                "Settings.ConfigurationManager.ImportLegacy.PendingConfirmation",
                $"{statusMessage} {report.Summary}",
                cancellationToken);
            return report;
        }

        var failureMessage = errorMessage.Length == 0 ? statusMessage : errorMessage;
        await RecordFailedResultAsync(
            "Settings.ConfigurationManager.ImportLegacy",
            UiOperationResult.Fail(UiErrorCode.ImportFailed, failureMessage),
            cancellationToken);
        return report;
    }

    public async Task SaveAchievementSettingsAsync(CancellationToken cancellationToken = default)
    {
        await RunSettingsSaveTargetAsync(
            "Settings.AutoSave.Achievement",
            SaveAchievementSettingsCoreAsync,
            cancellationToken);
    }

    private async Task SaveAchievementSettingsCoreAsync(CancellationToken cancellationToken = default)
    {
        AchievementStatusMessage = string.Empty;
        AchievementErrorMessage = string.Empty;

        var policy = new AchievementPolicy(
            PopupDisabled: AchievementPopupDisabled,
            PopupAutoClose: AchievementPopupAutoClose);
        var saveResult = await Runtime.AchievementFeatureService.SavePolicyAsync(policy, cancellationToken);
        if (!await ApplyResultAsync(saveResult, "Settings.Achievement.Save", cancellationToken))
        {
            AchievementErrorMessage = saveResult.Message;
            AchievementStatusMessage = LocalizeSettingsText(
                "Settings.Achievement.Status.SaveFailed",
                "成就配置保存失败。");
            return;
        }

        UpdateAchievementPolicySummary(policy);
        AchievementStatusMessage = LocalizeSettingsText(
            "Settings.Achievement.Status.SaveSucceeded",
            "成就配置保存成功。");
        AchievementErrorMessage = string.Empty;
        await RefreshAchievementSnapshotAsync("Settings.Achievement.Save.Refresh", cancellationToken);
    }

    public async Task RefreshAchievementPolicyAsync(CancellationToken cancellationToken = default)
    {
        AchievementStatusMessage = string.Empty;
        AchievementErrorMessage = string.Empty;
        var result = await Runtime.AchievementFeatureService.LoadPolicyAsync(cancellationToken);
        var policy = await ApplyResultAsync(result, "Settings.Achievement.Refresh", cancellationToken);
        if (policy is null)
        {
            AchievementErrorMessage = result.Message;
            AchievementStatusMessage = LocalizeSettingsText(
                "Settings.Achievement.Status.RefreshFailed",
                "成就配置刷新失败。");
            return;
        }

        ApplyAchievementPolicy(policy);
        UpdateAchievementPolicySummary(policy);
        AchievementStatusMessage = LocalizeSettingsText(
            "Settings.Achievement.Status.RefreshSucceeded",
            "成就配置已刷新。");
        AchievementErrorMessage = string.Empty;
        await RefreshAchievementSnapshotAsync("Settings.Achievement.Refresh.Snapshot", cancellationToken);
    }

    public async Task OpenAchievementGuideAsync(CancellationToken cancellationToken = default)
    {
        var result = await _openExternalTargetAsync(AchievementGuideUrl, cancellationToken);
        if (!await ApplyResultAsync(result, "Settings.Achievement.OpenGuide", cancellationToken))
        {
            AchievementStatusMessage = LocalizeSettingsText(
                "Settings.Achievement.Status.OpenGuideFailed",
                "打开成就说明失败。");
            AchievementErrorMessage = result.Message;
            return;
        }

        AchievementStatusMessage = LocalizeSettingsText(
            "Settings.Achievement.Status.OpenGuideSucceeded",
            "已打开成就说明。");
        AchievementErrorMessage = string.Empty;
    }

    public async Task ShowAchievementListDialogAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await RefreshAchievementSnapshotAsync("Settings.Achievement.Dialog.Snapshot", cancellationToken);
        if (snapshot is null)
        {
            AchievementStatusMessage = LocalizeSettingsText(
                "Settings.Achievement.Status.DialogOpenFailed",
                "成就列表弹窗打开失败。");
            AchievementErrorMessage = LastErrorMessage;
            return;
        }

        var chrome = DialogTextCatalog.CreateCatalog(
            Language,
            language =>
            {
                var filterWatermark = GetSettingsTextForLanguage(
                    language,
                    "Settings.Achievement.Dialog.FilterWatermark",
                    DialogTextCatalog.Select(language, "搜索成就", "Filter achievements"));
                return new DialogChromeSnapshot(
                    title: AchievementTextCatalog.GetString("AchievementList", language, "成就列表"),
                    confirmText: GetSettingsTextForLanguage(
                        language,
                        "Settings.Action.Close",
                        DialogTextCatalog.ErrorDialogCloseButton(language)),
                    cancelText: DialogTextCatalog.WarningDialogCancelButton(language),
                    namedTexts: DialogTextCatalog.CreateNamedTexts(
                        (DialogTextCatalog.ChromeKeys.FilterWatermark, filterWatermark)));
            });
        var chromeSnapshot = chrome.GetSnapshot();
        var request = new AchievementListDialogRequest(
            Title: chromeSnapshot.Title,
            Items: snapshot.Items,
            InitialFilter: string.Empty,
            ConfirmText: chromeSnapshot.ConfirmText ?? RootTexts.GetOrDefault(
                "Settings.Action.Close",
                DialogTextCatalog.ErrorDialogCloseButton(Language)),
            CancelText: chromeSnapshot.CancelText ?? DialogTextCatalog.WarningDialogCancelButton(Language),
            FilterWatermark: chromeSnapshot.GetNamedTextOrDefault(
                DialogTextCatalog.ChromeKeys.FilterWatermark,
                RootTexts.GetOrDefault(
                    "Settings.Achievement.Dialog.FilterWatermark",
                    DialogTextCatalog.Select(Language, "搜索成就", "Filter achievements"))),
            UnlockedCount: snapshot.UnlockedCount,
            TotalCount: snapshot.TotalCount,
            Chrome: chrome);
        var dialogResult = await _dialogService.ShowAchievementListAsync(request, "Settings.Achievement.Dialog", cancellationToken);
        AchievementStatusMessage = dialogResult.Return switch
        {
            DialogReturnSemantic.Confirm => LocalizeSettingsText(
                "Settings.Achievement.Status.DialogConfirmed",
                "成就列表弹窗确认完成。"),
            DialogReturnSemantic.Cancel => LocalizeSettingsText(
                "Settings.Achievement.Status.DialogCancelled",
                "成就列表弹窗已取消。"),
            _ => LocalizeSettingsText(
                "Settings.Achievement.Status.DialogClosed",
                "成就列表弹窗已关闭。"),
        };
        AchievementErrorMessage = string.Empty;
    }

    public async Task ExportAchievementsAsync(string path, CancellationToken cancellationToken = default)
    {
        AchievementStatusMessage = string.Empty;
        AchievementErrorMessage = string.Empty;

        var result = await Runtime.AchievementTrackerService.BackupAsync(path, cancellationToken);
        if (!await ApplyResultAsync(result, "Settings.Achievement.Backup", cancellationToken))
        {
            AchievementStatusMessage = LocalizeSettingsText(
                "Settings.Achievement.Status.BackupFailed",
                "成就备份失败。");
            AchievementErrorMessage = result.Message;
            return;
        }

        AchievementStatusMessage = $"{AchievementTextCatalog.GetString("AchievementBackupSuccess", Language, "已备份到")} {path}";
        AchievementErrorMessage = string.Empty;
    }

    public async Task ImportAchievementsAsync(string path, CancellationToken cancellationToken = default)
    {
        AchievementStatusMessage = string.Empty;
        AchievementErrorMessage = string.Empty;

        var result = await Runtime.AchievementTrackerService.RestoreAsync(path, cancellationToken);
        if (!await ApplyResultAsync(result, "Settings.Achievement.Restore", cancellationToken))
        {
            AchievementStatusMessage = LocalizeSettingsText(
                "Settings.Achievement.Status.RestoreFailed",
                "成就恢复失败。");
            AchievementErrorMessage = result.Message;
            return;
        }

        AchievementStatusMessage = AchievementTextCatalog.GetString("AchievementRestoreSuccess", Language, "已恢复成就进度");
        AchievementErrorMessage = string.Empty;
        await RefreshAchievementSnapshotAsync("Settings.Achievement.Restore.Refresh", cancellationToken);
    }

    public async Task UnlockAllAchievementsAsync(CancellationToken cancellationToken = default)
    {
        AchievementStatusMessage = string.Empty;
        AchievementErrorMessage = string.Empty;

        var result = await Runtime.AchievementTrackerService.UnlockAllAsync(cancellationToken);
        if (!await ApplyResultAsync(result, "Settings.Achievement.UnlockAll", cancellationToken))
        {
            AchievementStatusMessage = LocalizeSettingsText(
                "Settings.Achievement.Status.UnlockAllFailed",
                "批量解锁成就失败。");
            AchievementErrorMessage = result.Message;
            return;
        }

        AchievementStatusMessage = LocalizeSettingsText(
            "Settings.Achievement.Status.UnlockAllSucceeded",
            "已批量解锁成就。");
        AchievementErrorMessage = string.Empty;
        await RefreshAchievementSnapshotAsync("Settings.Achievement.UnlockAll.Refresh", cancellationToken);
    }

    public async Task LockAllAchievementsAsync(CancellationToken cancellationToken = default)
    {
        AchievementStatusMessage = string.Empty;
        AchievementErrorMessage = string.Empty;

        var result = await Runtime.AchievementTrackerService.LockAllAsync(cancellationToken);
        if (!await ApplyResultAsync(result, "Settings.Achievement.LockAll", cancellationToken))
        {
            AchievementStatusMessage = LocalizeSettingsText(
                "Settings.Achievement.Status.LockAllFailed",
                "批量重置成就失败。");
            AchievementErrorMessage = result.Message;
            return;
        }

        AchievementStatusMessage = LocalizeSettingsText(
            "Settings.Achievement.Status.LockAllSucceeded",
            "已重置全部成就。");
        AchievementErrorMessage = string.Empty;
        await RefreshAchievementSnapshotAsync("Settings.Achievement.LockAll.Refresh", cancellationToken);
    }

    public void HandleAchievementDebugClick()
    {
        AchievementDebugTip = AchievementTextCatalog.GetPallasString(1, 10);
        if (AchievementDebugEnabled)
        {
            AchievementDebugEnabled = false;
            _achievementDebugClickCount = 0;
            AchievementDebugMedalColor = "#B0B0B0";
            return;
        }

        _achievementDebugClickCount += 1;
        if (_achievementDebugClickCount < 10)
        {
            return;
        }

        AchievementDebugEnabled = true;
        _achievementDebugClickCount = 0;
        AchievementDebugMedalColor = "#D4AF37";
    }

    public async Task RegisterHotkeysAsync(
        HotkeyRegistrationSource source = HotkeyRegistrationSource.Manual,
        CancellationToken cancellationToken = default)
    {
        ClearHotkeyStatus();

        var requests = new[]
        {
            new HotkeyBindingRequest(ShowGuiHotkeyName, HotkeyConfigurationCodec.NormalizeDraftGesture(HotkeyShowGui)),
            new HotkeyBindingRequest(LinkStartHotkeyName, HotkeyConfigurationCodec.NormalizeDraftGesture(HotkeyLinkStart)),
        };

        HotkeyShowGui = requests[0].Gesture;
        HotkeyLinkStart = requests[1].Gesture;

        var batchResult = await Runtime.PlatformCapabilityService.RegisterGlobalHotkeysAsync(requests, cancellationToken);
        if (!batchResult.Success || batchResult.Value is null)
        {
            HotkeyErrorMessage = batchResult.Message;
            HotkeyStatusMessage = FormatSettingsText(
                "Settings.Hotkey.Status.RegisterFailed",
                "{0}: 热键注册失败。",
                GetHotkeySourceText(source));
            LastErrorMessage = HotkeyErrorMessage;
            StatusMessage = HotkeyStatusMessage;
            await RecordFailedResultAsync(
                "Settings.Hotkey.Batch",
                UiOperationResult.Fail(
                    batchResult.Error?.Code ?? UiErrorCode.HotkeyRegistrationFailed,
                    HotkeyStatusMessage,
                    batchResult.Error?.Details),
                cancellationToken);
            await RefreshHotkeyRuntimeStateAsync(cancellationToken);
            await RefreshHotkeyFallbackWarningAsync(source, cancellationToken);
            return;
        }

        var outcomesByName = batchResult.Value.ToDictionary(
            outcome => outcome.Name,
            StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            if (!outcomesByName.TryGetValue(request.Name, out var outcome))
            {
                GetHotkeyState(request.Name)?.SetError("Skipped because the platform provider returned no result.");
                continue;
            }

            await RecordHotkeyRegistrationResultAsync(
                request.Name,
                request.Gesture,
                ToUiOperationResult(outcome.Result),
                cancellationToken);
            ApplyHotkeyOutcome(request.Name, request.Gesture, outcome);
        }

        var persistedShowGui = outcomesByName.TryGetValue(ShowGuiHotkeyName, out var showOutcome) && showOutcome.Result.Success
            ? requests[0].Gesture
            : _persistedHotkeyShowGui;
        var persistedLinkStart = outcomesByName.TryGetValue(LinkStartHotkeyName, out var linkOutcome) && linkOutcome.Result.Success
            ? requests[1].Gesture
            : _persistedHotkeyLinkStart;
        var serializedHotkeys = HotkeyConfigurationCodec.Serialize(
            persistedShowGui,
            persistedLinkStart);
        var saveSucceeded = await RunTrackedConfigurationSaveAsync(
            "Settings.Hotkey",
            FormatSettingsText("Settings.Section.HotKey", "热键设置"),
            "Settings.Hotkey.Save",
            ct => Runtime.SettingsFeatureService.SaveGlobalSettingsAsync(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ConfigurationKeys.HotKeys] = serializedHotkeys,
                },
                ct),
            cancellationToken);

        if (!saveSucceeded)
        {
            await RefreshHotkeyRuntimeStateAsync(cancellationToken);
            await RefreshHotkeyFallbackWarningAsync(source, cancellationToken);
            return;
        }

        _persistedHotkeyShowGui = persistedShowGui;
        _persistedHotkeyLinkStart = persistedLinkStart;

        var failedOutcomes = batchResult.Value.Where(static outcome => !outcome.Result.Success).ToArray();
        var successCount = batchResult.Value.Count - failedOutcomes.Length;
        if (failedOutcomes.Length == 0)
        {
            HotkeyStatusMessage = FormatSettingsText(
                "Settings.Hotkey.Status.AppliedAll",
                "{0}: {1} 个热键已应用。",
                GetHotkeySourceText(source),
                successCount);
            HotkeyErrorMessage = string.Empty;
            LastErrorMessage = string.Empty;
            await RecordEventAsync("Settings.Hotkey.Batch", HotkeyStatusMessage, cancellationToken);
        }
        else
        {
            HotkeyStatusMessage = FormatSettingsText(
                "Settings.Hotkey.Status.AppliedPartial",
                "{0}: {1}/{2} 个热键已应用。",
                GetHotkeySourceText(source),
                successCount,
                batchResult.Value.Count);
            HotkeyErrorMessage = string.Join(
                " ",
                failedOutcomes.Select(outcome => BuildHotkeyErrorMessage(outcome.Name, ToUiOperationResult(outcome.Result))));
            LastErrorMessage = HotkeyErrorMessage;
            await RecordFailedResultAsync(
                "Settings.Hotkey.Batch",
                UiOperationResult.Fail(
                    failedOutcomes[0].Result.ErrorCode ?? UiErrorCode.HotkeyRegistrationFailed,
                    HotkeyStatusMessage),
                cancellationToken);
        }

        StatusMessage = HotkeyStatusMessage;
        await RefreshHotkeyRuntimeStateAsync(cancellationToken);
        await RefreshHotkeyFallbackWarningAsync(source, cancellationToken);
    }

    public async Task TestNotificationAsync(CancellationToken cancellationToken = default)
    {
        await ApplyResultAsync(
            await Runtime.SettingsFeatureService.TestNotificationAsync(NotificationTitle, NotificationMessage, cancellationToken),
            "Settings.Notification.Test",
            cancellationToken);
    }

    public async Task BuildIssueReportAsync(CancellationToken cancellationToken = default)
    {
        ClearIssueReportStatus();
        var outputPath = await ApplyResultAsync(
            await Runtime.SettingsFeatureService.BuildIssueReportAsync(cancellationToken),
            "Settings.IssueReport.Build",
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            IssueReportPath = outputPath;
            IssueReportStatusMessage = FormatSettingsText(
                "Settings.IssueReport.Status.BuildSucceeded",
                "支持包已生成：{0}",
                outputPath);
            IssueReportErrorMessage = string.Empty;
            return;
        }

        IssueReportStatusMessage = LocalizeSettingsText(
            "Settings.IssueReport.Status.BuildFailed",
            "生成支持包失败。");
        IssueReportErrorMessage = LastErrorMessage;
    }

    public async Task OpenIssueReportHelpAsync(CancellationToken cancellationToken = default)
    {
        ClearIssueReportStatus();
        var result = await _openExternalTargetAsync(IssueReportHelpUrl, cancellationToken);
        if (!await ApplyResultAsync(result, "Settings.IssueReport.OpenHelp", cancellationToken))
        {
            IssueReportStatusMessage = LocalizeSettingsText(
                "Settings.IssueReport.Status.OpenHelpFailed",
                "打开帮助文档失败。");
            IssueReportErrorMessage = result.Message;
            return;
        }

        IssueReportStatusMessage = LocalizeSettingsText(
            "Settings.IssueReport.Status.OpenHelpSucceeded",
            "已打开帮助文档。");
        IssueReportErrorMessage = string.Empty;
    }

    public async Task OpenIssueReportEntryAsync(CancellationToken cancellationToken = default)
    {
        ClearIssueReportStatus();
        var result = await OpenIssueReportEntryForDialogAsync(cancellationToken);
        if (!await ApplyResultAsync(result, "Settings.IssueReport.OpenEntry", cancellationToken))
        {
            IssueReportStatusMessage = LocalizeSettingsText(
                "Settings.IssueReport.Status.OpenEntryFailed",
                "打开 Issue 入口失败。");
            IssueReportErrorMessage = result.Message;
            return;
        }

        IssueReportStatusMessage = LocalizeSettingsText(
            "Settings.IssueReport.Status.OpenEntrySucceeded",
            "已打开 Issue 入口。");
        IssueReportErrorMessage = string.Empty;
        _ = Runtime.AchievementTrackerService.Unlock("ProblemFeedback");
    }

    public async Task<UiOperationResult> OpenIssueReportEntryForDialogAsync(CancellationToken cancellationToken = default)
    {
        var result = await _openExternalTargetAsync(IssueReportIssueEntryUrl, cancellationToken);
        if (result.Success)
        {
            _ = Runtime.AchievementTrackerService.Unlock("ProblemFeedback");
        }

        return result;
    }

    public async Task OpenIssueReportDebugDirectoryAsync(CancellationToken cancellationToken = default)
    {
        ClearIssueReportStatus();
        var debugDirectory = ResolveDebugDirectoryPath();
        Directory.CreateDirectory(debugDirectory);
        var result = await _openExternalTargetAsync(debugDirectory, cancellationToken);
        if (!await ApplyResultAsync(result, "Settings.IssueReport.OpenDebugDirectory", cancellationToken))
        {
            IssueReportStatusMessage = LocalizeSettingsText(
                "Settings.IssueReport.Status.OpenDebugDirectoryFailed",
                "打开 debug 目录失败。");
            IssueReportErrorMessage = result.Message;
            return;
        }

        IssueReportStatusMessage = FormatSettingsText(
            "Settings.IssueReport.Status.OpenDebugDirectorySucceeded",
            "已打开目录：{0}",
            debugDirectory);
        IssueReportErrorMessage = string.Empty;
    }

    public async Task ClearIssueReportImageCacheAsync(CancellationToken cancellationToken = default)
    {
        ClearIssueReportStatus();
        var imageCacheDirectory = ResolveImageCacheDirectoryPath();
        try
        {
            var removedFiles = 0;
            var removedDirectories = 0;
            if (Directory.Exists(imageCacheDirectory))
            {
                var files = Directory.EnumerateFiles(imageCacheDirectory, "*", SearchOption.AllDirectories).ToList();
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Delete(file);
                    removedFiles++;
                }

                var directories = Directory.EnumerateDirectories(imageCacheDirectory, "*", SearchOption.AllDirectories)
                    .OrderByDescending(static path => path.Length)
                    .ToList();
                foreach (var directory in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.Delete(directory, recursive: false);
                    removedDirectories++;
                }
            }

            Directory.CreateDirectory(imageCacheDirectory);
            var result = UiOperationResult.Ok(
                removedFiles == 0 && removedDirectories == 0
                    ? FormatSettingsText(
                        "Settings.IssueReport.Status.ImageCacheAlreadyEmpty",
                        "图像缓存目录为空：{0}",
                        imageCacheDirectory)
                    : FormatSettingsText(
                        "Settings.IssueReport.Status.ImageCacheCleared",
                        "图像缓存已清理：文件 {0} 个，目录 {1} 个。",
                        removedFiles,
                        removedDirectories));
            if (!await ApplyResultAsync(result, "Settings.IssueReport.ClearImageCache", cancellationToken))
            {
                IssueReportStatusMessage = LocalizeSettingsText(
                    "Settings.IssueReport.Status.ImageCacheClearFailed",
                    "清理图像缓存失败。");
                IssueReportErrorMessage = result.Message;
                return;
            }

            IssueReportStatusMessage = result.Message;
            IssueReportErrorMessage = string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = UiOperationResult.Fail(
                UiErrorCode.IssueReportImageCacheClearFailed,
                FormatSettingsText(
                    "Settings.IssueReport.Status.ImageCacheClearFailedWithReason",
                    "清理图像缓存失败：{0}",
                    ex.Message),
                ex.Message);
            _ = await ApplyResultAsync(failure, "Settings.IssueReport.ClearImageCache", cancellationToken);
            IssueReportStatusMessage = LocalizeSettingsText(
                "Settings.IssueReport.Status.ImageCacheClearFailed",
                "清理图像缓存失败。");
            IssueReportErrorMessage = failure.Message;
        }
    }

    public async Task OpenAboutOfficialWebsiteAsync(CancellationToken cancellationToken = default)
    {
        await OpenAboutExternalTargetAsync(
            AboutOfficialWebsiteUrl,
            "Settings.About.OpenOfficialWebsite",
            "Settings.About.Status.OpenOfficialWebsiteSucceeded",
            "已打开官网。",
            cancellationToken);
    }

    public async Task OpenAboutCommunityAsync(CancellationToken cancellationToken = default)
    {
        await OpenAboutExternalTargetAsync(
            AboutCommunityUrl,
            "Settings.About.OpenCommunity",
            "Settings.About.Status.OpenCommunitySucceeded",
            "已打开社区入口。",
            cancellationToken);
    }

    public async Task OpenAboutDownloadAsync(CancellationToken cancellationToken = default)
    {
        await OpenAboutExternalTargetAsync(
            AboutDownloadUrl,
            "Settings.About.OpenDownload",
            "Settings.About.Status.OpenDownloadSucceeded",
            "已打开下载页。",
            cancellationToken);
    }

    public async Task CheckAboutAnnouncementAsync(CancellationToken cancellationToken = default)
    {
        ClearAboutStatus();
        var loadResult = await Runtime.AnnouncementFeatureService.LoadStateAsync(cancellationToken);
        var state = await ApplyResultAsync(loadResult, "Settings.About.CheckAnnouncement", cancellationToken);
        if (state is null)
        {
            AboutStatusMessage = LocalizeSettingsText(
                "Settings.About.Status.AnnouncementLoadFailed",
                "公告读取失败。");
            AboutErrorMessage = loadResult.Message;
            return;
        }

        var fetchResult = await FetchLatestAnnouncementInfoAsync(cancellationToken);
        if (fetchResult.Success && fetchResult.Value is not null && !string.IsNullOrWhiteSpace(fetchResult.Value.Content))
        {
            var latestInfo = fetchResult.Value.Content.Trim();
            if (!string.Equals(latestInfo, state.AnnouncementInfo, StringComparison.Ordinal))
            {
                state = state with
                {
                    AnnouncementInfo = latestInfo,
                    DoNotRemindThisAnnouncementAgain = false,
                };
                var saveFetchedState = await Runtime.AnnouncementFeatureService.SaveStateAsync(state, cancellationToken);
                if (!await ApplyResultAsync(saveFetchedState, "Settings.About.Announcement.SaveFetched", cancellationToken))
                {
                    AboutStatusMessage = LocalizeSettingsText(
                        "Settings.About.Status.AnnouncementSaveFailed",
                        "公告状态保存失败。");
                    AboutErrorMessage = saveFetchedState.Message;
                    return;
                }
            }
        }

        var info = string.IsNullOrWhiteSpace(state.AnnouncementInfo)
            ? LocalizeSettingsText(
                "Settings.About.Status.AnnouncementInfoEmpty",
                "当前没有公告内容。")
            : state.AnnouncementInfo;
        var flag = FormatSettingsText(
            "Settings.About.Status.AnnouncementFlags",
            "不再提醒={0}; 不显示={1}",
            state.DoNotRemindThisAnnouncementAgain,
            state.DoNotShowAnnouncement);
        AboutStatusMessage = FormatSettingsText(
            "Settings.About.Status.AnnouncementSummary",
            "公告状态：{0}。{1}",
            flag,
            info);
        AboutErrorMessage = string.Empty;
    }

    public Task CheckAboutAnnouncementWithDialogAsync(CancellationToken cancellationToken = default)
        => CheckAndDownloadAboutAnnouncementWithDialogAsync(cancellationToken);

    public async Task ApplyAutostartAsync(CancellationToken cancellationToken = default)
    {
        var desired = StartSelf;
        var setResult = await Runtime.SettingsFeatureService.SetAutostartAsync(desired, cancellationToken);
        if (!setResult.Success)
        {
            LastErrorMessage = setResult.Message;
            await RecordFailedResultAsync("Settings.Autostart.Set", setResult, cancellationToken);
            await ShowAutostartErrorWithDelayAsync(
                BuildAutostartSetErrorMessage(setResult.Error?.Code, setResult.Message),
                cancellationToken);
            return;
        }

        StatusMessage = setResult.Message;
        LastErrorMessage = string.Empty;
        _ = Runtime.AchievementTrackerService.Unlock("StartupBoot");
        await RecordEventAsync("Settings.Autostart.Set", setResult.Message, cancellationToken);
        await RefreshAutostartStatusAsync(
            cancellationToken,
            syncDesiredState: false,
            delayMismatchHint: true,
            desiredState: desired);
    }

    public async Task RefreshAutostartStatusAsync(
        CancellationToken cancellationToken = default,
        bool syncDesiredState = true,
        bool delayMismatchHint = false,
        bool? desiredState = null)
    {
        var result = await Runtime.SettingsFeatureService.GetAutostartStatusAsync(cancellationToken);
        if (!result.Success)
        {
            AutostartStatus = result.Message;
            LastErrorMessage = result.Message;
            await RecordFailedResultAsync(
                "Settings.Autostart.Query",
                UiOperationResult.Fail(result.Error?.Code ?? UiErrorCode.AutostartQueryFailed, result.Message, result.Error?.Details),
                cancellationToken);

            if (delayMismatchHint)
            {
                await ShowAutostartErrorWithDelayAsync(
                    BuildAutostartSetErrorMessage(result.Error?.Code, result.Message),
                    cancellationToken);
            }

            return;
        }

        var enabled = result.Value;
        if (syncDesiredState)
        {
            _suppressPageAutoSave = true;
            try
            {
                StartSelf = enabled;
            }
            finally
            {
                _suppressPageAutoSave = false;
            }
        }

        AutostartStatus = PlatformCapabilityTextMap.FormatAutostartStatus(
            Language,
            enabled,
            _localizationFallbackReporter);

        if (syncDesiredState)
        {
            ClearAutostartFeedback();
            return;
        }

        var expected = desiredState ?? StartSelf;
        if (enabled == expected)
        {
            ClearAutostartFeedback();
            LastErrorMessage = string.Empty;
            return;
        }

        var warningMessage = BuildAutostartMismatchMessage(enabled);
        LastErrorMessage = warningMessage;
        await RecordFailedResultAsync(
            "Settings.Autostart.Verify",
            UiOperationResult.Fail(PlatformErrorCodes.AutostartVerificationFailed, warningMessage),
            cancellationToken);

        if (delayMismatchHint)
        {
            await ShowAutostartWarningWithDelayAsync(warningMessage, cancellationToken);
        }
    }

    private async Task SaveGuiSettingsCoreAsync(bool triggeredByAutoSave, CancellationToken cancellationToken = default)
    {
        var lockAcquired = false;
        try
        {
            await _guiSaveSemaphore.WaitAsync(cancellationToken);
            lockAcquired = true;

            var snapshot = BuildNormalizedGuiSnapshot();
            var previousSoftwareRendering = ReadGlobalBool(Runtime.ConfigurationService.CurrentConfig, SoftwareRenderingConfigKey, false);
            var previousBackgroundImagePath = ReadGlobalString(Runtime.ConfigurationService.CurrentConfig, ConfigurationKeys.BackgroundImagePath, string.Empty);
            ApplyGuiSnapshotWithoutAutoSave(snapshot);

            var validation = ValidateGuiSnapshot(snapshot);
            if (!validation.Success)
            {
                HasPendingGuiChanges = true;
                SetGuiValidationMessageForResult(validation);
                LastErrorMessage = validation.Message;
                await RecordFailedResultAsync("Settings.Save.GuiBatch.Validation", validation, cancellationToken);
                return;
            }

            var saveResult = await SaveScopedSettingsAsync(
                globalUpdates: snapshot.ToGlobalSettingUpdates(),
                profileUpdates: snapshot.ToProfileSettingUpdates(),
                successScope: "Settings.Save.GuiBatch",
                cancellationToken: cancellationToken);
            if (!await ApplyResultAsync(saveResult, "Settings.Save.GuiBatch", cancellationToken))
            {
                HasPendingGuiChanges = true;
                MarkSettingsSaveFailure("Settings.AutoSave.Gui");
                return;
            }

            HasPendingGuiChanges = false;
            ClearGuiValidationMessages();
            LastSuccessfulGuiSaveAt = DateTimeOffset.Now;
            RaiseGuiSettingsApplied(snapshot);

            if (!string.IsNullOrWhiteSpace(snapshot.BackgroundImagePath)
                && !string.Equals(previousBackgroundImagePath, snapshot.BackgroundImagePath, StringComparison.OrdinalIgnoreCase))
            {
                _ = Runtime.AchievementTrackerService.Unlock("CustomizationMaster");
            }

            if (previousSoftwareRendering != snapshot.UseSoftwareRendering)
            {
                await PromptForSoftwareRenderingRestartAsync(cancellationToken);
            }

            if (triggeredByAutoSave)
            {
                await RecordEventAsync(
                    "Settings.Save.GuiBatch.Auto",
                    "GUI settings auto-save succeeded.",
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // No-op for canceled save requests.
        }
        catch (Exception ex)
        {
            HasPendingGuiChanges = true;
            MarkSettingsSaveFailure("Settings.AutoSave.Gui");
            await RecordUnhandledExceptionAsync(
                "Settings.Save.GuiBatch",
                ex,
                UiErrorCode.SettingsSaveFailed,
                string.Format(
                    RootTexts.GetOrDefault("Settings.SaveScoped.Error.SaveFailed", "Failed to save settings: {0}"),
                    ex.Message),
                cancellationToken);
        }
        finally
        {
            if (lockAcquired)
            {
                _guiSaveSemaphore.Release();
            }
        }
    }

    private bool IsPageAutoSaveSuppressed => _suppressPageAutoSave || !_autoSaveReady || _autoSaveSuspensionCount > 0;

    private void ResetAutoSaveLifecycle()
    {
        _autoSaveReady = false;
        _repairTimerProfilesWhenAutoSaveResumes = false;
        _autoSaveSuspensionCount = _viewCompositionDepth > 0 ? 1 : 0;
        CancelPendingAutoSaveRequests();
    }

    private void SuspendAutoSave(bool repairTimerProfilesOnResume = false)
    {
        _autoSaveSuspensionCount++;
        _repairTimerProfilesWhenAutoSaveResumes |= repairTimerProfilesOnResume;
        CancelPendingAutoSaveRequests();
    }

    private void ResumeAutoSave()
    {
        if (_autoSaveSuspensionCount > 0)
        {
            _autoSaveSuspensionCount--;
        }

        if (IsPageAutoSaveSuppressed)
        {
            return;
        }

        if (_repairTimerProfilesWhenAutoSaveResumes)
        {
            _repairTimerProfilesWhenAutoSaveResumes = false;
            RepairEnabledTimerProfilesFromConfig();
        }

        ResumePendingAutoSaves();
    }

    private void CancelPendingAutoSaveRequests()
    {
        CancelAutoSaveCts(ref _guiAutoSaveCts);
        CancelAutoSaveCts(ref _startPerformanceAutoSaveCts);
        CancelAutoSaveCts(ref _timerAutoSaveCts);
        CancelAutoSaveCts(ref _connectionGameAutoSaveCts);
        CancelAutoSaveCts(ref _remoteControlAutoSaveCts);
        CancelAutoSaveCts(ref _externalNotificationAutoSaveCts);
        CancelAutoSaveCts(ref _versionUpdateAutoSaveCts);
        CancelAutoSaveCts(ref _achievementAutoSaveCts);
        CancelAutoSaveCts(ref _autostartAutoApplyCts);
    }

    private void ResumePendingAutoSaves()
    {
        if (HasPendingGuiChanges && !_suppressGuiAutoSave)
        {
            ScheduleGuiAutoSave();
        }

        if (HasPendingStartPerformanceChanges && !_suppressStartPerformanceDirtyTracking)
        {
            ScheduleAutoSave(
                ref _startPerformanceAutoSaveCts,
                "Settings.AutoSave.StartPerformance",
                550,
                SaveStartPerformanceSettingsCoreAsync);
        }

        if (HasPendingTimerChanges && !_suppressTimerDirtyTracking)
        {
            ScheduleAutoSave(
                ref _timerAutoSaveCts,
                "Settings.AutoSave.Timer",
                550,
                SaveTimerSettingsCoreAsync);
        }
    }

    private void RepairEnabledTimerProfilesFromConfig()
    {
        if (!CustomTimerConfig || Timers.Count == 0)
        {
            return;
        }

        var warnings = new List<string>();
        var persistedSnapshot = ReadTimerSnapshot(Runtime.ConfigurationService.CurrentConfig, warnings);
        if (!persistedSnapshot.CustomTimerConfig)
        {
            return;
        }

        var persistedSlots = persistedSnapshot.Slots.ToDictionary(static slot => slot.Index);
        var previousSuppressPageAutoSave = _suppressPageAutoSave;
        var previousSuppressTimerDirtyTracking = _suppressTimerDirtyTracking;
        _suppressPageAutoSave = true;
        _suppressTimerDirtyTracking = true;
        try
        {
            foreach (var slot in Timers)
            {
                if (!slot.Enabled
                    || !string.IsNullOrWhiteSpace(slot.Profile)
                    || !persistedSlots.TryGetValue(slot.Index, out var persistedSlot)
                    || !persistedSlot.Enabled
                    || string.IsNullOrWhiteSpace(persistedSlot.Profile))
                {
                    continue;
                }

                slot.Profile = persistedSlot.Profile;
            }
        }
        finally
        {
            _suppressTimerDirtyTracking = previousSuppressTimerDirtyTracking;
            _suppressPageAutoSave = previousSuppressPageAutoSave;
        }
    }

    private static void CancelAutoSaveCts(ref CancellationTokenSource? cts)
    {
        var current = cts;
        cts = null;
        if (current is null)
        {
            return;
        }

        current.Cancel();
        current.Dispose();
    }

    private void MarkGuiSettingsDirty(bool saveImmediately = true)
    {
        HasPendingGuiChanges = true;
        if (_suppressGuiAutoSave || IsPageAutoSaveSuppressed || !saveImmediately)
        {
            return;
        }

        ScheduleGuiAutoSave();
    }

    private void ScheduleGuiAutoSave()
    {
        ScheduleAutoSave(
            ref _guiAutoSaveCts,
            "Settings.AutoSave.Gui",
            500,
            ct => SaveGuiSettingsCoreAsync(triggeredByAutoSave: true, cancellationToken: ct));
    }

    private void MarkStartPerformanceDirty()
    {
        if (_suppressStartPerformanceDirtyTracking || IsPageAutoSaveSuppressed)
        {
            return;
        }

        HasPendingStartPerformanceChanges = true;
        StartPerformanceValidationMessage = string.Empty;
        ScheduleAutoSave(
            ref _startPerformanceAutoSaveCts,
            "Settings.AutoSave.StartPerformance",
            550,
            SaveStartPerformanceSettingsCoreAsync);
    }

    private void MarkTimerDirty()
    {
        if (_suppressTimerDirtyTracking || IsPageAutoSaveSuppressed)
        {
            return;
        }

        HasPendingTimerChanges = true;
        TimerValidationMessage = string.Empty;
        ScheduleAutoSave(
            ref _timerAutoSaveCts,
            "Settings.AutoSave.Timer",
            550,
            SaveTimerSettingsCoreAsync);
    }

    private void MarkConnectionGameDirty()
    {
        if (IsPageAutoSaveSuppressed)
        {
            return;
        }

        ScheduleAutoSave(
            ref _connectionGameAutoSaveCts,
            "Settings.AutoSave.ConnectionGame",
            500,
            SaveConnectionGameSettingsCoreAsync);
    }

    private void MarkRemoteControlDirty()
    {
        if (IsPageAutoSaveSuppressed)
        {
            return;
        }

        ScheduleAutoSave(
            ref _remoteControlAutoSaveCts,
            "Settings.AutoSave.Remote",
            650,
            SaveRemoteControlCoreAsync);
    }

    private void MarkExternalNotificationDirty()
    {
        if (IsPageAutoSaveSuppressed)
        {
            return;
        }

        ScheduleAutoSave(
            ref _externalNotificationAutoSaveCts,
            "Settings.AutoSave.Notification",
            700,
            SaveExternalNotificationCoreAsync);
    }

    private void MarkVersionUpdateDirty()
    {
        if (IsPageAutoSaveSuppressed)
        {
            return;
        }

        ScheduleAutoSave(
            ref _versionUpdateAutoSaveCts,
            "Settings.AutoSave.VersionUpdate",
            700,
            SaveVersionUpdateSettingsCoreAsync);
    }

    private void MarkAchievementDirty()
    {
        if (IsPageAutoSaveSuppressed)
        {
            return;
        }

        ScheduleAutoSave(
            ref _achievementAutoSaveCts,
            "Settings.AutoSave.Achievement",
            500,
            SaveAchievementSettingsCoreAsync);
    }

    private void MarkAutostartDirty()
    {
        if (IsPageAutoSaveSuppressed)
        {
            return;
        }

        BeginAutostartInteraction();
        ScheduleAutoSave(
            ref _autostartAutoApplyCts,
            "Settings.AutoSave.Autostart",
            300,
            ApplyAutostartAsync);
    }

    private void ScheduleAutoSave(
        ref CancellationTokenSource? debounceCts,
        string scope,
        int delayMs,
        Func<CancellationToken, Task> saveAsync)
    {
        CancelAutoSaveCts(ref debounceCts);
        ConfigurationSaveTracker.Instance.MarkPending(
            scope,
            ResolveSettingsSaveDisplayName(scope),
            scope,
            Runtime.DiagnosticsService,
            CreateSettingsSaveRetry(scope, saveAsync));
        debounceCts = new CancellationTokenSource();
        var token = debounceCts.Token;

        _ = RunDebouncedSaveAsync(scope, delayMs, saveAsync, token);
    }

    private async Task RunDebouncedSaveAsync(
        string scope,
        int delayMs,
        Func<CancellationToken, Task> saveAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delayMs, cancellationToken);
            if (IsPageAutoSaveSuppressed)
            {
                ConfigurationSaveTracker.Instance.ClearPending(scope);
                return;
            }

            await RunSettingsSaveTargetAsync(scope, saveAsync, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Newer input superseded previous autosave request.
        }
        catch (Exception ex)
        {
            await RecordUnhandledExceptionAsync(
                scope,
                ex,
                UiErrorCode.SettingsSaveFailed,
                $"{scope} failed.");
        }
    }

    private async Task<bool> RunSettingsSaveTargetAsync(
        string scope,
        Func<CancellationToken, Task> saveAsync,
        CancellationToken cancellationToken)
    {
        return await ConfigurationSaveTracker.Instance.RunTrackedAsync(
            scope,
            ResolveSettingsSaveDisplayName(scope),
            scope,
            Runtime.DiagnosticsService,
            CreateSettingsSaveRetry(scope, saveAsync),
            cancellationToken);
    }

    private Func<CancellationToken, Task<bool>> CreateSettingsSaveRetry(
        string scope,
        Func<CancellationToken, Task> saveAsync)
    {
        return async ct =>
        {
            ClearSettingsSaveFailure(scope);
            await saveAsync(ct);
            return !HasSettingsSaveFailure(scope);
        };
    }

    private void RegisterSettingsSaveTarget(string scope)
    {
        var saveAsync = ResolveSettingsSaveAction(scope);
        if (saveAsync is null)
        {
            return;
        }

        ConfigurationSaveTracker.Instance.MarkPending(
            scope,
            ResolveSettingsSaveDisplayName(scope),
            scope,
            Runtime.DiagnosticsService,
            CreateSettingsSaveRetry(scope, saveAsync));
    }

    private void MarkSettingsSaveFailure(string scope)
    {
        if (!string.IsNullOrWhiteSpace(scope))
        {
            lock (_settingsSaveFailureGate)
            {
                _settingsSaveFailureScopes.Add(scope);
            }
        }
    }

    private void ClearSettingsSaveFailure(string scope)
    {
        if (!string.IsNullOrWhiteSpace(scope))
        {
            lock (_settingsSaveFailureGate)
            {
                _settingsSaveFailureScopes.Remove(scope);
            }
        }
    }

    private bool HasSettingsSaveFailure(string scope)
    {
        lock (_settingsSaveFailureGate)
        {
            if (_settingsSaveFailureScopes.Contains(scope))
            {
                return true;
            }
        }

        return scope switch
        {
            "Settings.AutoSave.Gui" => !string.IsNullOrWhiteSpace(GuiValidationMessage),
            "Settings.AutoSave.StartPerformance" => !string.IsNullOrWhiteSpace(StartPerformanceValidationMessage),
            "Settings.AutoSave.Timer" => !string.IsNullOrWhiteSpace(TimerValidationMessage),
            "Settings.AutoSave.ConnectionGame" => !string.IsNullOrWhiteSpace(LastErrorMessage),
            "Settings.AutoSave.Remote" => !string.IsNullOrWhiteSpace(RemoteControlErrorMessage),
            "Settings.AutoSave.Notification" => !string.IsNullOrWhiteSpace(ExternalNotificationErrorMessage),
            "Settings.AutoSave.VersionUpdate" => !string.IsNullOrWhiteSpace(VersionUpdateErrorMessage),
            "Settings.AutoSave.Achievement" => !string.IsNullOrWhiteSpace(AchievementErrorMessage),
            "Settings.AutoSave.Autostart" => !string.IsNullOrWhiteSpace(AutostartErrorMessage),
            _ => !string.IsNullOrWhiteSpace(LastErrorMessage),
        };
    }

    private string ResolveSettingsSaveDisplayName(string scope)
    {
        var sectionKey = scope switch
        {
            "Settings.AutoSave.Gui" => "GUI",
            "Settings.AutoSave.StartPerformance" => "Performance",
            "Settings.AutoSave.Timer" => "Timer",
            "Settings.AutoSave.ConnectionGame" => "Connect",
            "Settings.AutoSave.Remote" => "RemoteControl",
            "Settings.AutoSave.Notification" => "ExternalNotification",
            "Settings.AutoSave.VersionUpdate" => "VersionUpdate",
            "Settings.AutoSave.Achievement" => "Achievement",
            "Settings.AutoSave.Autostart" => "Start",
            _ => string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(sectionKey))
        {
            var displayName = Sections.FirstOrDefault(section => string.Equals(section.Key, sectionKey, StringComparison.Ordinal))?.DisplayName;
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }
        }

        return scope switch
        {
            "Settings.AutoSave.Gui" => "界面设置",
            "Settings.AutoSave.StartPerformance" => "运行设置",
            "Settings.AutoSave.Timer" => "定时执行",
            "Settings.AutoSave.ConnectionGame" => "连接设置",
            "Settings.AutoSave.Remote" => "远程控制",
            "Settings.AutoSave.Notification" => "外部通知",
            "Settings.AutoSave.VersionUpdate" => "版本更新",
            "Settings.AutoSave.Achievement" => "成就设置",
            "Settings.AutoSave.Autostart" => "启动设置",
            _ => "设置",
        };
    }

    public async Task<bool> FlushConfigurationSavesForCloseAsync(CancellationToken cancellationToken = default)
    {
        var scopes = new HashSet<string>(
            ConfigurationSaveTracker.Instance.GetPendingOrFailedKeys(Runtime.DiagnosticsService)
                .Where(static key => key.StartsWith("Settings.AutoSave.", StringComparison.Ordinal)),
            StringComparer.Ordinal);

        if (HasPendingGuiChanges && !_suppressGuiAutoSave)
        {
            RegisterSettingsSaveTarget("Settings.AutoSave.Gui");
        }

        if (HasPendingStartPerformanceChanges && !_suppressStartPerformanceDirtyTracking)
        {
            RegisterSettingsSaveTarget("Settings.AutoSave.StartPerformance");
        }

        if (HasPendingTimerChanges && !_suppressTimerDirtyTracking)
        {
            RegisterSettingsSaveTarget("Settings.AutoSave.Timer");
        }

        CancelPendingAutoSaveRequests();
        foreach (var scope in scopes)
        {
            RegisterSettingsSaveTarget(scope);
        }

        var failedNames = await ConfigurationSaveTracker.Instance.RetryPendingOrFailedAsync(
            static key => key.StartsWith("Settings.AutoSave.", StringComparison.Ordinal),
            cancellationToken,
            Runtime.DiagnosticsService);
        return failedNames.Count == 0;
    }

    private Func<CancellationToken, Task>? ResolveSettingsSaveAction(string scope)
    {
        return scope switch
        {
            "Settings.AutoSave.Gui" => ct => SaveGuiSettingsCoreAsync(triggeredByAutoSave: true, cancellationToken: ct),
            "Settings.AutoSave.StartPerformance" => SaveStartPerformanceSettingsCoreAsync,
            "Settings.AutoSave.Timer" => SaveTimerSettingsCoreAsync,
            "Settings.AutoSave.ConnectionGame" => SaveConnectionGameSettingsCoreAsync,
            "Settings.AutoSave.Remote" => SaveRemoteControlCoreAsync,
            "Settings.AutoSave.Notification" => SaveExternalNotificationCoreAsync,
            "Settings.AutoSave.VersionUpdate" => SaveVersionUpdateSettingsCoreAsync,
            "Settings.AutoSave.Achievement" => SaveAchievementSettingsCoreAsync,
            "Settings.AutoSave.Autostart" => ApplyAutostartAsync,
            _ => null,
        };
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsPageAutoSaveSuppressed || string.IsNullOrWhiteSpace(e.PropertyName))
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(StartSelf):
                MarkAutostartDirty();
                break;
            case nameof(RemoteGetTaskEndpoint):
            case nameof(RemoteReportEndpoint):
            case nameof(RemoteUserIdentity):
            case nameof(RemoteDeviceIdentity):
            case nameof(RemotePollInterval):
                MarkRemoteControlDirty();
                break;
            case nameof(ExternalNotificationEnabled):
            case nameof(ExternalNotificationSendWhenComplete):
            case nameof(ExternalNotificationSendWhenError):
            case nameof(ExternalNotificationSendWhenTimeout):
            case nameof(ExternalNotificationEnableDetails):
            case nameof(SelectedNotificationProvider):
            case nameof(NotificationProviderParametersText):
                MarkExternalNotificationDirty();
                break;
            case nameof(AchievementPopupDisabled):
            case nameof(AchievementPopupAutoClose):
                MarkAchievementDirty();
                break;
            default:
                if (!IsPageAutoSaveSuppressed && IsVersionUpdateAutoSaveProperty(e.PropertyName))
                {
                    MarkVersionUpdateDirty();
                }

                break;
        }
    }

    private static bool IsVersionUpdateAutoSaveProperty(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        return propertyName switch
        {
            nameof(VersionUpdateProxy) => true,
            nameof(VersionUpdateProxyType) => true,
            nameof(VersionUpdateVersionType) => true,
            nameof(VersionUpdateResourceSource) => true,
            nameof(VersionUpdateForceGithubSource) => true,
            nameof(VersionUpdateMirrorChyanCdk) => true,
            nameof(VersionUpdateStartupCheck) => true,
            nameof(VersionUpdateScheduledCheck) => true,
            nameof(VersionUpdateResourceApi) => true,
            nameof(VersionUpdateAllowNightly) => true,
            nameof(VersionUpdateAcknowledgedNightlyWarning) => true,
            nameof(VersionUpdateUseAria2) => true,
            nameof(VersionUpdateAutoDownload) => true,
            nameof(VersionUpdateAutoInstall) => true,
            _ => false,
        };
    }

    private void OnConnectionGameSharedStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PropertyName))
        {
            return;
        }

        if (ConnectionGameProfileSync.ShouldSyncProperty(e.PropertyName))
        {
            MarkConnectionGameDirty();
        }

        if (string.Equals(
                e.PropertyName,
                nameof(ConnectionGameSharedStateViewModel.ClientType),
                StringComparison.Ordinal))
        {
            if (_suppressVersionUpdateResourceRefresh)
            {
                return;
            }

            _ = RefreshVersionUpdateResourceInfoAsync();
        }
    }

    private void OnUnifiedLanguageChanged(object? sender, UiLanguageChangedEventArgs e)
    {
        if (Avalonia.Application.Current is null)
        {
            ApplyUnifiedLanguage(e.CurrentLanguage);
            _pendingUnifiedLanguageApplyTask = Task.CompletedTask;
            return;
        }

        _pendingUnifiedLanguageApplyTask = ScheduleUnifiedLanguageApplyAsync(e.CurrentLanguage);
    }

    private void ApplyUnifiedLanguage(string? language)
    {
        var hadPendingGuiChanges = HasPendingGuiChanges;
        var previousSuppressPageAutoSave = _suppressPageAutoSave;
        var previousSuppressGuiAutoSave = _suppressGuiAutoSave;
        try
        {
            _suppressPageAutoSave = true;
            _suppressGuiAutoSave = true;
            Language = NormalizeLanguage(language);
        }
        finally
        {
            _suppressGuiAutoSave = previousSuppressGuiAutoSave;
            _suppressPageAutoSave = previousSuppressPageAutoSave;
            HasPendingGuiChanges = hadPendingGuiChanges;
        }
    }

    private Task ScheduleUnifiedLanguageApplyAsync(string? language)
    {
        return Dispatcher.UIThread.InvokeAsync(
            () => ApplyUnifiedLanguageAsync(language),
            DispatcherPriority.Send);
    }

    private async Task ApplyUnifiedLanguageAsync(string? language)
    {
        var hadPendingGuiChanges = HasPendingGuiChanges;
        var previousSuppressPageAutoSave = _suppressPageAutoSave;
        var previousSuppressGuiAutoSave = _suppressGuiAutoSave;
        try
        {
            _suppressPageAutoSave = true;
            _suppressGuiAutoSave = true;
            await SetLanguageAsync(NormalizeLanguage(language), allowRenderYields: true);
        }
        finally
        {
            _suppressGuiAutoSave = previousSuppressGuiAutoSave;
            _suppressPageAutoSave = previousSuppressPageAutoSave;
            HasPendingGuiChanges = hadPendingGuiChanges;
        }
    }

    private async Task SetLanguageAsync(string normalized, bool allowRenderYields)
    {
        var previousLanguage = _language;
        if (!SetProperty(ref _language, normalized))
        {
            return;
        }

        var previousSuppressSelectedLanguageChangeRequest = _suppressSelectedLanguageChangeRequest;
        try
        {
            _suppressSelectedLanguageChangeRequest = true;
            await ApplyLanguageSideEffectsAsync(previousLanguage, normalized, allowRenderYields);
        }
        finally
        {
            _suppressSelectedLanguageChangeRequest = previousSuppressSelectedLanguageChangeRequest;
        }
    }

    private async Task ApplyLanguageSideEffectsAsync(
        string previousLanguage,
        string normalized,
        bool allowRenderYields)
    {
        var total = Stopwatch.StartNew();
        var step = Stopwatch.StartNew();
        SetSelectedLanguageValue(normalized, requestLanguageChange: false);
        RootTexts.Language = normalized;
        OnPropertyChanged(nameof(RootTexts));
        RefreshNotificationTemplateLocalization(previousLanguage, normalized);
        ConnectionGameSharedState.SetLanguage(normalized);
        Runtime.AchievementTrackerService.SetCurrentLanguage(normalized);
        RefreshHotkeyUiText();
        OnPropertyChanged(nameof(HotkeyCaptureGuideText));
        _ = RecordLanguageApplyTimingAsync(
            "Settings.ApplyLanguage.CoreText",
            step,
            previousLanguage,
            normalized,
            allowRenderYields);
        await YieldForBlockingOperationOverlayFrameAsync(allowRenderYields);

        step.Restart();
        RebuildGuiOptionLists();
        _ = RecordLanguageApplyTimingAsync(
            "Settings.ApplyLanguage.GuiOptions",
            step,
            previousLanguage,
            normalized,
            allowRenderYields);
        await YieldForBlockingOperationOverlayFrameAsync(allowRenderYields);

        step.Restart();
        RebuildSections(SelectedSection?.Key);
        _ = RecordLanguageApplyTimingAsync(
            "Settings.ApplyLanguage.Sections",
            step,
            previousLanguage,
            normalized,
            allowRenderYields,
            ("sectionCount", Sections.Count));
        await YieldForBlockingOperationOverlayFrameAsync(allowRenderYields);

        step.Restart();
        RebuildVersionUpdateOptionLists();
        if (IsSettingsDataBucketLoaded(SettingsDataBucketStartPerformance))
        {
            RefreshGpuUiState();
        }

        _ = RecordLanguageApplyTimingAsync(
            "Settings.ApplyLanguage.VersionUpdateAndGpu",
            step,
            previousLanguage,
            normalized,
            allowRenderYields);
        await YieldForBlockingOperationOverlayFrameAsync(allowRenderYields);

        step.Restart();
        RefreshAchievementUiState();
        UpdateAchievementPolicySummary(new AchievementPolicy(AchievementPopupDisabled, AchievementPopupAutoClose));
        OnPropertyChanged(nameof(VersionUpdateMirrorChyanCdkExpiryText));
        OnPropertyChanged(nameof(PendingResourceUpdateSummary));
        MarkGuiSettingsDirty();
        NotifyGuiSettingsPreviewChanged();
        RefreshRightPaneLocalization();
        _ = RecordLanguageApplyTimingAsync(
            "Settings.ApplyLanguage.AchievementValidationAndPreview",
            step,
            previousLanguage,
            normalized,
            allowRenderYields);
        _ = RecordLanguageApplyTimingAsync(
            "Settings.ApplyLanguage.Total",
            total,
            previousLanguage,
            normalized,
            allowRenderYields);
    }

    private void ApplyLanguageSideEffectsImmediately(string previousLanguage, string normalized)
    {
        SetSelectedLanguageValue(normalized, requestLanguageChange: false);
        RootTexts.Language = normalized;
        OnPropertyChanged(nameof(RootTexts));
        RefreshNotificationTemplateLocalization(previousLanguage, normalized);
        ConnectionGameSharedState.SetLanguage(normalized);
        Runtime.AchievementTrackerService.SetCurrentLanguage(normalized);
        RefreshHotkeyUiText();
        OnPropertyChanged(nameof(HotkeyCaptureGuideText));

        RebuildGuiOptionLists();
        RebuildSections(SelectedSection?.Key);
        RebuildVersionUpdateOptionLists();
        if (IsSettingsDataBucketLoaded(SettingsDataBucketStartPerformance))
        {
            RefreshGpuUiState();
        }

        RefreshAchievementUiState();
        UpdateAchievementPolicySummary(new AchievementPolicy(AchievementPopupDisabled, AchievementPopupAutoClose));
        OnPropertyChanged(nameof(VersionUpdateMirrorChyanCdkExpiryText));
        OnPropertyChanged(nameof(PendingResourceUpdateSummary));
        MarkGuiSettingsDirty();
        NotifyGuiSettingsPreviewChanged();
        RefreshRightPaneLocalization();
    }

    private void OnTimerSlotPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressTimerDirtyTracking || string.IsNullOrEmpty(e.PropertyName))
        {
            return;
        }

        if (e.PropertyName == nameof(TimerSlotViewModel.Profile)
            && sender is TimerSlotViewModel slot
            && TryRepairTransientEmptyTimerProfile(slot))
        {
            return;
        }

        if (e.PropertyName == nameof(TimerSlotViewModel.Enabled)
            || e.PropertyName == nameof(TimerSlotViewModel.Time)
            || e.PropertyName == nameof(TimerSlotViewModel.Hour)
            || e.PropertyName == nameof(TimerSlotViewModel.Minute)
            || e.PropertyName == nameof(TimerSlotViewModel.Profile))
        {
            MarkTimerDirty();
        }
    }

    private bool TryRepairTransientEmptyTimerProfile(TimerSlotViewModel slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        if (!CustomTimerConfig || !slot.Enabled || !string.IsNullOrWhiteSpace(slot.Profile))
        {
            return false;
        }

        var repairedProfile = ResolvePreferredTimerProfile(slot.Index);
        if (string.IsNullOrWhiteSpace(repairedProfile))
        {
            return false;
        }

        var previousSuppressPageAutoSave = _suppressPageAutoSave;
        var previousSuppressTimerDirtyTracking = _suppressTimerDirtyTracking;
        _suppressPageAutoSave = true;
        _suppressTimerDirtyTracking = true;
        try
        {
            slot.Profile = repairedProfile;
            HasPendingTimerChanges = false;
            if (string.IsNullOrWhiteSpace(TimerValidationMessage)
                || TimerValidationMessage.Contains(UiErrorCode.TimerProfileMissing, StringComparison.Ordinal)
                || TimerValidationMessage.Contains("profile cannot be empty", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    TimerValidationMessage,
                    BuildTimerProfileRequiredMessage(slot.Index),
                    StringComparison.Ordinal))
            {
                TimerValidationMessage = string.Empty;
            }
        }
        finally
        {
            _suppressTimerDirtyTracking = previousSuppressTimerDirtyTracking;
            _suppressPageAutoSave = previousSuppressPageAutoSave;
        }

        return true;
    }

    private string ResolvePreferredTimerProfile(int slotIndex)
    {
        var persistedProfile = string.Empty;
        if (slotIndex >= 1 && slotIndex <= TimerSlotCount)
        {
            var key = BuildTimerProfileKey(slotIndex);
            persistedProfile = ReadGlobalString(Runtime.ConfigurationService.CurrentConfig, key, string.Empty).Trim();
        }

        if (!string.IsNullOrWhiteSpace(persistedProfile)
            && Runtime.ConfigurationService.CurrentConfig.Profiles.ContainsKey(persistedProfile))
        {
            return persistedProfile;
        }

        var currentProfile = Runtime.ConfigurationService.CurrentConfig.CurrentProfile?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(currentProfile)
            && Runtime.ConfigurationService.CurrentConfig.Profiles.ContainsKey(currentProfile))
        {
            return currentProfile;
        }

        return ConfigurationProfiles.FirstOrDefault(static profile => !string.IsNullOrWhiteSpace(profile)) ?? string.Empty;
    }

    private void ClearHotkeyStatus()
    {
        HotkeyStatusMessage = string.Empty;
        HotkeyWarningMessage = string.Empty;
        HotkeyErrorMessage = string.Empty;
        _showGuiHotkeyState.ClearFeedback();
        _linkStartHotkeyState.ClearFeedback();
    }

    private void ClearRemoteControlStatus()
    {
        RemoteControlStatusMessage = string.Empty;
        RemoteControlWarningMessage = string.Empty;
        RemoteControlErrorMessage = string.Empty;
    }

    private void ClearExternalNotificationStatus()
    {
        ExternalNotificationStatusMessage = string.Empty;
        ExternalNotificationWarningMessage = string.Empty;
        ExternalNotificationErrorMessage = string.Empty;
    }

    private void ClearConfigurationManagerStatus()
    {
        ConfigurationManagerSaveAsNewSucceededText = string.Empty;
        ConfigurationManagerSaveAsNewFailedText = string.Empty;
        ConfigurationManagerImportSucceededText = string.Empty;
        ConfigurationManagerStatusMessage = string.Empty;
        ConfigurationManagerErrorMessage = string.Empty;
    }

    private void ClearIssueReportStatus()
    {
        IssueReportStatusMessage = string.Empty;
        IssueReportErrorMessage = string.Empty;
    }

    private void ClearAboutStatus()
    {
        AboutStatusMessage = string.Empty;
        AboutErrorMessage = string.Empty;
    }

    private async Task LoadConfigurationProfilesAsync(string scope, CancellationToken cancellationToken, bool updateStatus = true)
    {
        SuspendAutoSave();
        try
        {
            var stateResult = await Runtime.ConfigurationProfileFeatureService.LoadStateAsync(cancellationToken);
            if (!stateResult.Success || stateResult.Value is null)
            {
                if (updateStatus)
                {
                    await ApplyResultAsync(stateResult, scope, cancellationToken);
                }
                else
                {
                    await RecordFailedResultAsync(
                        scope,
                        UiOperationResult.Fail(
                            stateResult.Error?.Code ?? UiErrorCode.ConfigurationProfileLoadFailed,
                            stateResult.Message,
                            stateResult.Error?.Details),
                        cancellationToken);
            }

                if (updateStatus)
                {
                    ConfigurationManagerErrorMessage = stateResult.Message;
                    ConfigurationManagerStatusMessage = LocalizeSettingsText(
                        "Settings.ConfigurationManager.Status.ProfileListLoadFailed",
                        "配置列表加载失败。");
                }

                return;
            }

            if (updateStatus)
            {
                await ApplyResultAsync(stateResult, scope, cancellationToken);
            }
            else
            {
                await RecordEventAsync(scope, stateResult.Message, cancellationToken);
            }

            ApplyConfigurationProfileState(stateResult.Value);
            if (updateStatus)
            {
                ConfigurationManagerErrorMessage = string.Empty;
                ConfigurationManagerStatusMessage = LocalizeSettingsText(
                    "Settings.ConfigurationManager.Status.ProfileListSynced",
                    "配置列表已同步。");
            }
        }
        finally
        {
            ResumeAutoSave();
        }
    }

    private async Task<bool> HandleConfigurationProfileResultAsync(
        UiOperationResult<ConfigurationProfileState> result,
        string scope,
        string successMessage,
        string failureMessage,
        CancellationToken cancellationToken,
        bool suppressFailureDialog = false,
        Action<UiOperationResult<ConfigurationProfileState>>? onFailure = null)
    {
        if (!result.Success || result.Value is null)
        {
            var failed = UiOperationResult.Fail(
                result.Error?.Code ?? UiErrorCode.UiOperationFailed,
                result.Message,
                result.Error?.Details);
            if (suppressFailureDialog)
            {
                await RecordFailedResultAsync(scope, failed, cancellationToken);
                LastErrorMessage = result.Message;
            }
            else
            {
                _ = await ApplyResultAsync(result, scope, cancellationToken);
            }

            ConfigurationManagerErrorMessage = result.Message;
            ConfigurationManagerStatusMessage = failureMessage;
            onFailure?.Invoke(result);
            await LoadConfigurationProfilesAsync(
                "Settings.ConfigurationManager.ReloadAfterFailure",
                cancellationToken,
                updateStatus: false);
            return false;
        }

        var payload = await ApplyResultAsync(result, scope, cancellationToken);
        if (payload is null)
        {
            return false;
        }

        ApplyConfigurationProfileState(payload);
        LoadConnectionSharedStateFromConfig();
        ConfigurationManagerStatusMessage = successMessage;
        ConfigurationManagerErrorMessage = string.Empty;
        return true;
    }

    private void SetConfigurationManagerSaveAsNewFailure(UiOperationResult<ConfigurationProfileState> result)
    {
        var reason = result.Error?.Code switch
        {
            UiErrorCode.ConfigurationProfileAlreadyExists => LocalizeSettingsText(
                "Settings.ConfigurationManager.SaveAsNewFailedReason.ProfileAlreadyExists",
                "请换一个未使用的配置名称。"),
            UiErrorCode.ConfigurationProfileInvalidName when result.Message.Contains("cannot be empty", StringComparison.OrdinalIgnoreCase) => LocalizeSettingsText(
                "Settings.ConfigurationManager.SaveAsNewFailedReason.ProfileNameEmpty",
                "请输入配置名称。"),
            UiErrorCode.ConfigurationProfileInvalidName => LocalizeSettingsText(
                "Settings.ConfigurationManager.SaveAsNewFailedReason.ProfileNameInvalid",
                "配置名称无效。"),
            UiErrorCode.ConfigurationProfileNotFound => LocalizeSettingsText(
                "Settings.ConfigurationManager.SaveAsNewFailedReason.SourceProfileMissing",
                "当前配置不存在，请刷新后重试。"),
            _ => LocalizeSettingsText(
                "Settings.ConfigurationManager.SaveAsNewFailedReason.Generic",
                "请稍后重试。"),
        };

        ConfigurationManagerSaveAsNewSucceededText = string.Empty;
        ConfigurationManagerSaveAsNewFailedText = FormatSettingsText(
            "Settings.ConfigurationManager.SaveAsNewFailedInline",
            "保存失败：{0}",
            reason);
    }

    private void ApplyConfigurationProfileState(ConfigurationProfileState state)
    {
        var normalizedProfiles = state.OrderedProfiles
            .Where(static profile => !string.IsNullOrWhiteSpace(profile))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var previousSuppressSelectionHandling = _suppressConfigurationProfileSelectionHandling;
        _suppressConfigurationProfileSelectionHandling = true;
        try
        {
            SynchronizeConfigurationProfiles(normalizedProfiles);

            if (ConfigurationProfiles.Count == 0)
            {
                ConfigurationManagerSelectedProfile = string.Empty;
                return;
            }

            var selected = state.CurrentProfile;
            if (string.IsNullOrWhiteSpace(selected)
                || !ConfigurationProfiles.Contains(selected, StringComparer.OrdinalIgnoreCase))
            {
                selected = ConfigurationProfiles[0];
            }

            ConfigurationManagerSelectedProfile = selected;
        }
        finally
        {
            _suppressConfigurationProfileSelectionHandling = previousSuppressSelectionHandling;
        }
    }

    private void SynchronizeConfigurationProfiles(IReadOnlyList<string> orderedProfiles)
    {
        for (var index = 0; index < orderedProfiles.Count; index++)
        {
            var desired = orderedProfiles[index];

            if (index < ConfigurationProfiles.Count
                && string.Equals(ConfigurationProfiles[index], desired, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(ConfigurationProfiles[index], desired, StringComparison.Ordinal))
                {
                    ConfigurationProfiles[index] = desired;
                }

                continue;
            }

            var existingIndex = FindConfigurationProfileIndex(desired, index);
            if (existingIndex >= 0)
            {
                ConfigurationProfiles.Move(existingIndex, index);
                if (!string.Equals(ConfigurationProfiles[index], desired, StringComparison.Ordinal))
                {
                    ConfigurationProfiles[index] = desired;
                }
            }
            else
            {
                ConfigurationProfiles.Insert(index, desired);
            }
        }

        while (ConfigurationProfiles.Count > orderedProfiles.Count)
        {
            ConfigurationProfiles.RemoveAt(ConfigurationProfiles.Count - 1);
        }
    }

    private int FindConfigurationProfileIndex(string profile, int startIndex)
    {
        for (var index = startIndex; index < ConfigurationProfiles.Count; index++)
        {
            if (string.Equals(ConfigurationProfiles[index], profile, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string NormalizeConfigPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }

        return Path.GetFullPath(filePath.Trim());
    }

    private async Task RefreshAfterConfigurationImportAsync(
        ConfigurationContextChangeReason reason,
        string scope,
        string message,
        ImportReport? report,
        CancellationToken cancellationToken)
    {
        await LoadConfigurationProfilesAsync(scope, cancellationToken, updateStatus: false);
        await LoadFromConfigAsync(Runtime.ConfigurationService.CurrentConfig, cancellationToken);
        LoadConnectionSharedStateFromConfig();
        await RaiseConfigurationContextChangedAsync(reason, message, cancellationToken, report);
    }

    private async Task RaiseConfigurationContextChangedAsync(
        ConfigurationContextChangeReason reason,
        string message,
        CancellationToken cancellationToken = default,
        ImportReport? report = null)
    {
        var args = new ConfigurationContextChangedEventArgs(reason, message, report);
        if (ApplyConfigurationContextChangedAsync is not null)
        {
            await ApplyConfigurationContextChangedAsync(args, cancellationToken);
        }

        ConfigurationContextChanged?.Invoke(this, args);
    }

    private static async Task WriteConfigFileAsync(UnifiedConfig config, string filePath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, config, ConfigExportSerializerOptions, cancellationToken);
    }

    private static UnifiedConfig BuildCurrentProfileOnlyConfig(UnifiedConfig source)
    {
        var normalizedCurrent = source.CurrentProfile;
        if (string.IsNullOrWhiteSpace(normalizedCurrent) || !source.Profiles.ContainsKey(normalizedCurrent))
        {
            normalizedCurrent = source.Profiles.Keys.FirstOrDefault() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(normalizedCurrent) || !source.Profiles.TryGetValue(normalizedCurrent, out var currentProfile))
        {
            throw new InvalidOperationException("No available profile to export.");
        }

        var config = new UnifiedConfig
        {
            SchemaVersion = source.SchemaVersion,
            CurrentProfile = normalizedCurrent,
            Profiles = new Dictionary<string, UnifiedProfile>(StringComparer.OrdinalIgnoreCase)
            {
                [normalizedCurrent] = CloneProfile(currentProfile),
            },
            GlobalValues = CloneJsonNodeMap(source.GlobalValues),
            Migration = CloneMigration(source.Migration),
        };
        return config;
    }

    private static UnifiedConfig CloneConfig(UnifiedConfig source)
    {
        var profiles = new Dictionary<string, UnifiedProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, profile) in source.Profiles)
        {
            profiles[name] = CloneProfile(profile);
        }

        return new UnifiedConfig
        {
            SchemaVersion = source.SchemaVersion,
            CurrentProfile = source.CurrentProfile,
            Profiles = profiles,
            GlobalValues = CloneJsonNodeMap(source.GlobalValues),
            Migration = CloneMigration(source.Migration),
        };
    }

    private static UnifiedProfile CloneProfile(UnifiedProfile source)
    {
        var values = CloneJsonNodeMap(source.Values);
        var tasks = source.TaskQueue
            .Select(task => new UnifiedTaskItem
            {
                Type = task.Type,
                Name = task.Name,
                IsEnabled = task.IsEnabled,
                Params = task.Params?.DeepClone() as JsonObject ?? [],
                LegacyRawTask = task.LegacyRawTask?.DeepClone() as JsonObject,
            })
            .ToList();
        return new UnifiedProfile
        {
            Values = values,
            TaskQueue = tasks,
        };
    }

    private static Dictionary<string, JsonNode?> CloneJsonNodeMap(Dictionary<string, JsonNode?> source)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            result[key] = value?.DeepClone();
        }

        return result;
    }

    private static UnifiedMigrationMetadata CloneMigration(UnifiedMigrationMetadata source)
    {
        return new UnifiedMigrationMetadata
        {
            ImportedAt = source.ImportedAt,
            ImportedBy = source.ImportedBy,
            ImportedFromGui = source.ImportedFromGui,
            ImportedFromGuiNew = source.ImportedFromGuiNew,
            Warnings = [.. source.Warnings],
        };
    }

    private static string AllocateUniqueProfileName(HashSet<string> existingNames, string preferredName)
    {
        var baseName = string.IsNullOrWhiteSpace(preferredName)
            ? "Imported"
            : preferredName.Trim();
        if (!existingNames.Contains(baseName))
        {
            return baseName;
        }

        var suffix = 1;
        string candidate;
        do
        {
            candidate = $"{baseName}_{suffix}";
            suffix++;
        }
        while (existingNames.Contains(candidate));

        return candidate;
    }

    private async Task RecordHotkeyRegistrationResultAsync(
        string hotkeyName,
        string gesture,
        UiOperationResult result,
        CancellationToken cancellationToken)
    {
        var scope = $"Settings.Hotkey.Register.{hotkeyName}";
        if (result.Success)
        {
            await RecordEventAsync(
                scope,
                $"source={hotkeyName} gesture={gesture} message={result.Message}",
                cancellationToken);
            return;
        }

        await RecordFailedResultAsync(
            scope,
            UiOperationResult.Fail(result.Error?.Code ?? UiErrorCode.HotkeyRegistrationFailed, result.Message, result.Error?.Details),
            cancellationToken);
    }

    private string BuildHotkeyErrorMessage(string hotkeyName, UiOperationResult result)
    {
        var localized = PlatformCapabilityTextMap.FormatErrorCode(
            Language,
            result.Error?.Code,
            result.Message,
            _localizationFallbackReporter);
        return string.IsNullOrWhiteSpace(result.Error?.Code)
            ? $"{hotkeyName}: {localized}"
            : $"{hotkeyName}: {localized} ({result.Error.Code})";
    }

    private string GetHotkeySourceText(HotkeyRegistrationSource source)
    {
        return source == HotkeyRegistrationSource.Startup
            ? LocalizeSettingsText("Settings.Hotkey.Source.Startup", "启动自动注册")
            : LocalizeSettingsText("Settings.Hotkey.Source.Manual", "手动注册");
    }

    private async Task RefreshHotkeyFallbackWarningAsync(
        HotkeyRegistrationSource source,
        CancellationToken cancellationToken)
    {
        var snapshotResult = await Runtime.PlatformCapabilityService.GetSnapshotAsync(cancellationToken);
        if (!snapshotResult.Success || snapshotResult.Value is null)
        {
            HotkeyWarningMessage = string.Empty;
            return;
        }

        var hotkeyCapability = snapshotResult.Value.Hotkey;
        if (hotkeyCapability.Supported || !hotkeyCapability.HasFallback)
        {
            HotkeyWarningMessage = string.Empty;
            return;
        }

        var localizedFallback = PlatformCapabilityTextMap.FormatErrorCode(
            Language,
            PlatformErrorCodes.HotkeyFallback,
            hotkeyCapability.Message,
            _localizationFallbackReporter);
        HotkeyWarningMessage =
            $"{localizedFallback} provider={hotkeyCapability.Provider}, mode={hotkeyCapability.FallbackMode ?? "unknown"}";
        await RecordEventAsync(
            "Settings.Hotkey.Fallback",
            $"source={source} provider={hotkeyCapability.Provider} mode={hotkeyCapability.FallbackMode ?? "unknown"} message={hotkeyCapability.Message}",
            cancellationToken);
    }

    private void ApplyHotkeyDraft(
        HotkeySettingItemViewModel state,
        string? gesture,
        string propertyName,
        bool clearFeedback = true)
    {
        var normalized = HotkeyConfigurationCodec.NormalizeDraftGesture(gesture);
        var changed = false;
        if (state == _showGuiHotkeyState)
        {
            changed = SetProperty(ref _hotkeyShowGui, normalized, propertyName);
        }
        else if (state == _linkStartHotkeyState)
        {
            changed = SetProperty(ref _hotkeyLinkStart, normalized, propertyName);
        }
        else
        {
            return;
        }

        state.SetGesture(normalized, FormatHotkeyDisplay(normalized));
        if (clearFeedback)
        {
            state.ClearFeedback();
        }

        if (!changed)
        {
            return;
        }
    }

    private HotkeySettingItemViewModel? GetHotkeyState(string hotkeyName)
    {
        return string.Equals(hotkeyName, ShowGuiHotkeyName, StringComparison.OrdinalIgnoreCase)
            ? _showGuiHotkeyState
            : string.Equals(hotkeyName, LinkStartHotkeyName, StringComparison.OrdinalIgnoreCase)
                ? _linkStartHotkeyState
                : null;
    }

    private void ApplyHotkeyOutcome(string hotkeyName, string requestedGesture, HotkeyRegistrationOutcome outcome)
    {
        var state = GetHotkeyState(hotkeyName);
        if (state is null)
        {
            return;
        }

        if (outcome.Result.Success)
        {
            state.SetGesture(
                requestedGesture,
                string.IsNullOrWhiteSpace(outcome.EffectiveGestureDisplay)
                    ? FormatHotkeyDisplay(requestedGesture)
                    : outcome.EffectiveGestureDisplay);
            state.SetError(string.Empty);
            return;
        }

        state.SetError(BuildHotkeyErrorMessage(hotkeyName, ToUiOperationResult(outcome.Result)));
    }

    private UiOperationResult ToUiOperationResult(PlatformOperationResult result)
    {
        return result.Success
            ? UiOperationResult.Ok(result.Message)
            : UiOperationResult.Fail(
                result.ErrorCode ?? UiErrorCode.HotkeyRegistrationFailed,
                result.Message);
    }

    private async Task RefreshHotkeyRuntimeStateAsync(CancellationToken cancellationToken)
    {
        var snapshotResult = await Runtime.PlatformCapabilityService.GetSnapshotAsync(cancellationToken);
        if (!snapshotResult.Success || snapshotResult.Value is null)
        {
            return;
        }

        ApplyRuntimeHotkeyState(_showGuiHotkeyState, _hotkeyShowGui, snapshotResult.Value.Hotkey);
        ApplyRuntimeHotkeyState(_linkStartHotkeyState, _hotkeyLinkStart, snapshotResult.Value.Hotkey);
    }

    private void ApplyRuntimeHotkeyState(
        HotkeySettingItemViewModel state,
        string configuredGesture,
        PlatformCapabilityStatus capability)
    {
        if (Runtime.Platform.HotkeyService.TryGetRegisteredHotkey(state.Name, out var registered))
        {
            state.SetGesture(
                configuredGesture,
                string.IsNullOrWhiteSpace(registered.DisplayGesture)
                    ? FormatHotkeyDisplay(configuredGesture)
                    : registered.DisplayGesture);
            state.SetScope(
                registered.ExecutionMode == PlatformExecutionMode.Fallback
                    ? HotkeyScopePresentation.WindowScoped
                    : HotkeyScopePresentation.Global,
                GetHotkeyScopeText(
                    registered.ExecutionMode == PlatformExecutionMode.Fallback
                        ? HotkeyScopePresentation.WindowScoped
                        : HotkeyScopePresentation.Global));
            return;
        }

        var scope = capability.Supported
            ? HotkeyScopePresentation.Global
            : capability.HasFallback
                ? HotkeyScopePresentation.WindowScoped
                : HotkeyScopePresentation.Unsupported;
        state.SetScope(scope, GetHotkeyScopeText(scope));
    }

    private void RefreshHotkeyUiText()
    {
        _showGuiHotkeyState.UpdateLocalization(
            GetHotkeyTitleText(ShowGuiHotkeyName),
            GetHotkeyUnboundText(),
            GetHotkeyCapturePromptText(),
            GetHotkeyRecordText(),
            GetHotkeyReRecordText(),
            GetHotkeyCapturingText(),
            GetHotkeyClearText(),
            GetHotkeyScopeText(_showGuiHotkeyState.ScopeKind));
        _linkStartHotkeyState.UpdateLocalization(
            GetHotkeyTitleText(LinkStartHotkeyName),
            GetHotkeyUnboundText(),
            GetHotkeyCapturePromptText(),
            GetHotkeyRecordText(),
            GetHotkeyReRecordText(),
            GetHotkeyCapturingText(),
            GetHotkeyClearText(),
            GetHotkeyScopeText(_linkStartHotkeyState.ScopeKind));
    }

    private string GetHotkeyTitleText(string hotkeyName)
    {
        return hotkeyName switch
        {
            ShowGuiHotkeyName => LocalizeSettingsText("Settings.Hotkey.Title.ShowGui", "[热键] 显示/收起 MAA"),
            _ => LocalizeSettingsText("Settings.Hotkey.Title.LinkStart", "[热键] Link start/stop"),
        };
    }

    private string GetHotkeyCaptureGuideText()
    {
        return LocalizeSettingsText(
            "Settings.Hotkey.Capture.Guide",
            "录入规则：至少按下一个修饰键与一个普通键才会提交；Esc 取消；Backspace/Delete 清空绑定。");
    }

    private string GetHotkeyUnboundText()
    {
        return LocalizeSettingsText("Settings.Hotkey.Capture.Unbound", "未绑定");
    }

    private string GetHotkeyCapturePromptText()
    {
        return LocalizeSettingsText("Settings.Hotkey.Capture.Prompt", "请按下快捷键...");
    }

    private string GetHotkeyRecordText()
    {
        return LocalizeSettingsText("Settings.Hotkey.Capture.Record", "录入");
    }

    private string GetHotkeyReRecordText()
    {
        return LocalizeSettingsText("Settings.Hotkey.Capture.ReRecord", "重新录入");
    }

    private string GetHotkeyCapturingText()
    {
        return LocalizeSettingsText("Settings.Hotkey.Capture.Capturing", "等待录入...");
    }

    private string GetHotkeyClearText()
    {
        return LocalizeSettingsText("Settings.Hotkey.Capture.Clear", "清除");
    }

    private string GetHotkeyScopeText(HotkeyScopePresentation scope)
    {
        return scope switch
        {
            HotkeyScopePresentation.Global => LocalizeSettingsText("Settings.Hotkey.Scope.Global", "全局"),
            HotkeyScopePresentation.WindowScoped => LocalizeSettingsText("Settings.Hotkey.Scope.WindowScoped", "窗口级"),
            _ => LocalizeSettingsText("Settings.Hotkey.Scope.Unsupported", "不支持"),
        };
    }

    private string GetHotkeyDraftPendingText(bool cleared)
    {
        return cleared
            ? LocalizeSettingsText(
                "Settings.Hotkey.Capture.Status.ClearedPending",
                "已清空绑定，点击“注册热键”后生效。")
            : LocalizeSettingsText(
                "Settings.Hotkey.Capture.Status.UpdatedPending",
                "已更新快捷键，点击“注册热键”后生效。");
    }

    private string GetHotkeyCaptureCancelledText()
    {
        return LocalizeSettingsText("Settings.Hotkey.Capture.Status.Cancelled", "已取消录入。");
    }

    private string LocalizeHotkeyCaptureMessage(string? message)
    {
        if (string.Equals(message, "At least one modifier key is required.", StringComparison.Ordinal))
        {
            return LocalizeSettingsText(
                "Settings.Hotkey.Capture.Error.RequireModifier",
                "至少需要一个修饰键。");
        }

        if (!string.IsNullOrWhiteSpace(message)
            && message.StartsWith("Unsupported key `", StringComparison.Ordinal))
        {
            return FormatSettingsText(
                "Settings.Hotkey.Capture.Error.UnsupportedKey",
                "暂不支持该按键：{0}",
                message[17..]);
        }

        return string.IsNullOrWhiteSpace(message)
            ? GetHotkeyCaptureCancelledText()
            : message;
    }

    private static string FormatHotkeyDisplay(string gesture)
    {
        return HotkeyGestureCodec.FormatDisplay(gesture);
    }

    private UiOperationResult ValidateGuiSnapshot(GuiSettingsSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.BackgroundImagePath) && !File.Exists(snapshot.BackgroundImagePath))
        {
            return UiOperationResult.Fail(
                UiErrorCode.BackgroundImagePathNotFound,
                $"Background image path does not exist: {snapshot.BackgroundImagePath}");
        }

        return UiOperationResult.Ok("GUI settings validation passed.");
    }

    private GuiSettingsSnapshot BuildNormalizedGuiSnapshot()
    {
        return new GuiSettingsSnapshot(
            Theme: NormalizeTheme(Theme),
            UseTray: UseTray,
            UseNotify: UseNotify,
            MinimizeToTray: UseTray && MinimizeToTray,
            WindowTitleScrollable: WindowTitleScrollable,
            UiScalePercent: Math.Clamp(UiScalePercent, UiScalePercentMin, UiScalePercentMax),
            UseSoftwareRendering: UseSoftwareRendering,
            DeveloperModeEnabled: DeveloperModeEnabled,
            LogItemDateFormatString: NormalizeLogItemDateFormat(LogItemDateFormatString),
            OperNameLanguage: NormalizeOperNameLanguage(OperNameLanguage),
            InverseClearMode: NormalizeInverseClearMode(InverseClearMode),
            BackgroundImagePath: NormalizeBackgroundPath(BackgroundImagePath),
            BackgroundOpacity: Math.Clamp(BackgroundOpacity, BackgroundOpacityMin, BackgroundOpacityMax),
            BackgroundBlur: Math.Clamp(BackgroundBlur, BackgroundBlurMin, BackgroundBlurMax),
            BackgroundStretchMode: NormalizeBackgroundStretchMode(BackgroundStretchMode));
    }

    private void ApplyGuiSnapshotWithoutAutoSave(GuiSettingsSnapshot snapshot)
    {
        _suppressGuiAutoSave = true;
        _suppressGuiPreview = true;
        try
        {
            Theme = snapshot.Theme;
            UseTray = snapshot.UseTray;
            UseNotify = snapshot.UseNotify;
            MinimizeToTray = snapshot.MinimizeToTray;
            WindowTitleScrollable = snapshot.WindowTitleScrollable;
            UiScalePercent = snapshot.UiScalePercent;
            UseSoftwareRendering = snapshot.UseSoftwareRendering;
            DeveloperModeEnabled = snapshot.DeveloperModeEnabled;
            LogItemDateFormatString = snapshot.LogItemDateFormatString;
            OperNameLanguage = snapshot.OperNameLanguage;
            InverseClearMode = snapshot.InverseClearMode;
            BackgroundImagePath = snapshot.BackgroundImagePath;
            BackgroundOpacity = snapshot.BackgroundOpacity;
            BackgroundBlur = snapshot.BackgroundBlur;
            BackgroundStretchMode = snapshot.BackgroundStretchMode;
        }
        finally
        {
            _suppressGuiPreview = false;
            _suppressGuiAutoSave = false;
        }
    }

    private void NotifyGuiSettingsPreviewChanged()
    {
        if (_suppressGuiPreview || _suppressPageAutoSave)
        {
            return;
        }

        RaiseGuiSettingsPreviewChanged(BuildNormalizedGuiSnapshot());
    }

    private void RaiseGuiSettingsApplied(GuiSettingsSnapshot snapshot)
    {
        GuiSettingsApplied?.Invoke(this, new GuiSettingsAppliedEventArgs(snapshot));
    }

    private void RaiseGuiSettingsPreviewChanged(GuiSettingsSnapshot snapshot)
    {
        GuiSettingsPreviewChanged?.Invoke(this, new GuiSettingsPreviewChangedEventArgs(snapshot));
    }

    private void ApplyAchievementPolicy(AchievementPolicy policy)
    {
        RunWithSuppressedSettingsBackfill(() =>
        {
            AchievementPopupDisabled = policy.PopupDisabled;
            AchievementPopupAutoClose = policy.PopupAutoClose;
        });
    }

    private void UpdateAchievementPolicySummary(AchievementPolicy policy)
    {
        AchievementPolicySummary = FormatSettingsText(
            "Settings.Achievement.Status.PolicySummary",
            "当前策略：禁用弹窗={0}；自动关闭={1}；已解锁 {2}/{3}",
            policy.PopupDisabled,
            policy.PopupAutoClose,
            AchievementUnlockedCount,
            AchievementTotalCount);
    }

    private async Task<AchievementTrackerSnapshot?> RefreshAchievementSnapshotAsync(string scope, CancellationToken cancellationToken)
    {
        Runtime.AchievementTrackerService.SetCurrentLanguage(Language);
        var result = await Runtime.AchievementTrackerService.GetSnapshotAsync(Language, cancellationToken);
        var snapshot = await ApplyResultAsync(result, scope, cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        ApplyAchievementSnapshot(snapshot);
        return snapshot;
    }

    private void ApplyAchievementSnapshot(AchievementTrackerSnapshot snapshot)
    {
        AchievementUnlockedCount = snapshot.UnlockedCount;
        AchievementTotalCount = snapshot.TotalCount;
        ApplyAchievementPolicy(snapshot.Policy);
        UpdateAchievementPolicySummary(snapshot.Policy);
    }

    private void RefreshAchievementUiState()
    {
        OnPropertyChanged(nameof(AchievementLevelText));
        UpdateAchievementPolicySummary(new AchievementPolicy(AchievementPopupDisabled, AchievementPopupAutoClose));
    }

    private void OnAchievementTrackerStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() => _ = RefreshAchievementSnapshotAsync("Settings.Achievement.StateChanged", CancellationToken.None));
    }

    private static string NormalizeVersionUpdateProxyType(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.Equals(normalized, "socks5", StringComparison.OrdinalIgnoreCase))
        {
            return "socks5";
        }

        return "http";
    }

    private static string NormalizeVersionUpdateResourceSource(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.Equals(normalized, "Mirror", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "MirrorChyan", StringComparison.OrdinalIgnoreCase))
        {
            return "MirrorChyan";
        }

        return "Github";
    }

    private void RebuildGuiOptionLists()
    {
        ThemeOptions = SettingsOptionCatalog.BuildThemeOptions(Language);
        BackgroundStretchModes = SettingsOptionCatalog.BuildBackgroundStretchOptions(Language);
        OperNameLanguageOptions = SettingsOptionCatalog.BuildOperNameLanguageOptions(Language);
        InverseClearModeOptions = SettingsOptionCatalog.BuildInverseClearModeOptions(Language);

        OnPropertyChanged(nameof(SelectedThemeOption));
        OnPropertyChanged(nameof(SelectedBackgroundStretchModeOption));
        OnPropertyChanged(nameof(SelectedOperNameLanguageOption));
        OnPropertyChanged(nameof(SelectedInverseClearModeOption));
    }

    private void RebuildVersionUpdateOptionLists()
    {
        VersionUpdateVersionTypeOptions = SettingsOptionCatalog.BuildVersionTypeOptions(
            Language,
            VersionUpdateAllowNightly);

        VersionUpdateProxyTypeOptions =
        [
            new DisplayValueOption("HTTP Proxy", "http"),
            new DisplayValueOption("SOCKS5 Proxy", "socks5"),
        ];

        VersionUpdateResourceSourceOptions = SettingsOptionCatalog.BuildVersionResourceSourceOptions(Language);

        OnPropertyChanged(nameof(SelectedVersionUpdateVersionTypeOption));
        OnPropertyChanged(nameof(SelectedVersionUpdateProxyTypeOption));
        OnPropertyChanged(nameof(SelectedVersionUpdateResourceSourceOption));
    }

    private static (string UiVersion, string BuildTime) BuildVersionUpdateUiMetadata()
    {
        var assembly = typeof(SettingsPageViewModel).Assembly;
        var uiVersion = ResolveDisplayVersion(assembly);

        var buildTime = "unknown";
        try
        {
            if (!string.IsNullOrWhiteSpace(assembly.Location) && File.Exists(assembly.Location))
            {
                buildTime = File.GetLastWriteTime(assembly.Location)
                    .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            buildTime = "unknown";
        }

        return (uiVersion, buildTime);
    }

    private static string ResolveDisplayVersion(Assembly assembly)
    {
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString() ?? "unknown"
            : informationalVersion.Split('+')[0];
    }

    private async Task<T?> ApplyResultNoDialogAsync<T>(
        UiOperationResult<T> result,
        string scope,
        CancellationToken cancellationToken = default)
    {
        if (result.Success)
        {
            await RecordEventAsync(scope, result.Message, cancellationToken);
            LastErrorMessage = string.Empty;
            return result.Value;
        }

        LastErrorMessage = result.Message;
        var failed = UiOperationResult.Fail(
            result.Error?.Code ?? UiErrorCode.UiOperationFailed,
            result.Message,
            result.Error?.Details);
        await RecordFailedResultAsync(scope, failed, cancellationToken);
        return default;
    }

    private async Task<string?> ApplyLanguageChangeResultAsync(
        UiOperationResult<string> result,
        CancellationToken cancellationToken)
    {
        const string scope = "Settings.Gui.Language.Change";
        if (result.Success)
        {
            LastErrorMessage = string.Empty;
            await RecordEventAsync(scope, result.Message, cancellationToken);
            return result.Value;
        }

        return await ApplyResultAsync(result, scope, cancellationToken);
    }

    private string BuildMirrorChyanExpiryText(string? rawValue)
    {
        if (!long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds)
            || unixSeconds <= 0)
        {
            return string.Empty;
        }

        if (unixSeconds == 1)
        {
            return LocalizeSettingsText(
                "Settings.VersionUpdate.Status.MirrorCdkExpired",
                "MirrorChyan CDK 已过期。");
        }

        var expiry = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
        var remaining = expiry - DateTime.Now;
        if (remaining.TotalSeconds <= 0)
        {
            return FormatSettingsText(
                "Settings.VersionUpdate.Status.MirrorCdkExpiredAt",
                "MirrorChyan CDK 已过期（{0:yyyy-MM-dd HH:mm}）。",
                expiry);
        }

        return FormatSettingsText(
            "Settings.VersionUpdate.Status.MirrorCdkRemainingDays",
            "MirrorChyan CDK 剩余 {0:F1} 天（至 {1:yyyy-MM-dd HH:mm}）。",
            remaining.TotalDays,
            expiry);
    }

    private static string BuildAboutVersionInfo()
    {
        var assembly = typeof(SettingsPageViewModel).Assembly;
        var assemblyName = assembly.GetName();
        var version = ResolveDisplayVersion(assembly);
        return
            $"{assemblyName.Name} {version} | .NET {Environment.Version} | {RuntimeInformation.OSDescription}";
    }

    private string ResolveCoreVersionOrUnknown()
    {
        var version = _coreVersionResolver.Invoke()?.Trim();
        return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
    }

    private string? ResolveCurrentCoreVersion()
    {
        var baseDirectories = new[]
        {
            ResolveRuntimeBaseDirectory(),
            AppContext.BaseDirectory,
        };

        foreach (var baseDirectory in baseDirectories
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var version = TryResolveInstalledCoreVersion(baseDirectory);
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version;
            }
        }

        return null;
    }

    private static string? TryResolveInstalledCoreVersion(string baseDirectory)
    {
        var libraryName = ResolveCoreLibraryName();
        if (string.IsNullOrWhiteSpace(libraryName))
        {
            return null;
        }

        var libraryPath = Path.Combine(baseDirectory, libraryName);
        if (!File.Exists(libraryPath))
        {
            return null;
        }

        nint library = nint.Zero;
        try
        {
            library = NativeLibrary.Load(libraryPath);
            if (!NativeLibrary.TryGetExport(library, "AsstGetVersion", out var export) || export == nint.Zero)
            {
                return null;
            }

            var getVersion = Marshal.GetDelegateForFunctionPointer<AsstGetVersionExport>(export);
            return Marshal.PtrToStringUTF8(getVersion())?.Trim();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (library != nint.Zero)
            {
                NativeLibrary.Free(library);
            }
        }
    }

    private static string? ResolveCoreLibraryName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "MaaCore.dll";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "libMaaCore.so";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "libMaaCore.dylib";
        }

        return null;
    }

    private delegate nint AsstGetVersionExport();

    private string ResolveDebugDirectoryPath()
    {
        var debugDirectory = Path.GetDirectoryName(Runtime.DiagnosticsService.EventLogPath);
        if (!string.IsNullOrWhiteSpace(debugDirectory))
        {
            return debugDirectory;
        }

        return Path.Combine(ResolveRuntimeBaseDirectory(), "debug");
    }

    private string ResolveImageCacheDirectoryPath()
    {
        return Path.Combine(ResolveRuntimeBaseDirectory(), "cache", "images");
    }

    private IReadOnlyList<string> BuildEmulatorPathDialogCandidates()
    {
        var candidates = new List<string>();
        void AddCandidate(string? value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return;
            }

            if (candidates.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            candidates.Add(normalized);
        }

        AddCandidate(EmulatorPath);
        var config = Runtime.ConfigurationService.CurrentConfig;
        AddCandidate(ReadConfigValue(config, LegacyConfigurationKeys.EmulatorPath));
        AddCandidate(ReadConfigValue(config, LegacyConfigurationKeys.MuMu12EmulatorPath));
        AddCandidate(ReadConfigValue(config, LegacyConfigurationKeys.LdPlayerEmulatorPath));
        return candidates;
    }

    private static string ReadConfigValue(UnifiedConfig config, string key)
    {
        if (!TryGetConfigNode(config, key, ConfigValuePreference.ProfileFirst, out var node) || node is null)
        {
            return string.Empty;
        }

        return node.ToString().Trim();
    }

    private string ResolveRuntimeBaseDirectory()
    {
        var debugDirectory = Path.GetDirectoryName(Runtime.DiagnosticsService.EventLogPath);
        if (!string.IsNullOrWhiteSpace(debugDirectory))
        {
            var parent = Directory.GetParent(debugDirectory);
            if (parent is not null)
            {
                return parent.FullName;
            }
        }

        return RuntimeLayout.ResolveRuntimeBaseDirectory();
    }

    private async Task OpenAboutExternalTargetAsync(
        string target,
        string scope,
        string successMessageKey,
        string successMessageFallback,
        CancellationToken cancellationToken)
    {
        ClearAboutStatus();
        var result = await _openExternalTargetAsync(target, cancellationToken);
        if (!await ApplyResultAsync(result, scope, cancellationToken))
        {
            AboutStatusMessage = LocalizeSettingsText(
                "Settings.About.Status.OpenExternalFailed",
                "外链打开失败。");
            AboutErrorMessage = result.Message;
            return;
        }

        AboutStatusMessage = LocalizeSettingsText(successMessageKey, successMessageFallback);
        AboutErrorMessage = string.Empty;
    }

    private static Task<UiOperationResult> OpenExternalTargetAsync(
        string target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(target))
        {
            return Task.FromResult(
                UiOperationResult.Fail(
                    UiErrorCode.ExternalTargetMissing,
                    "External target cannot be empty."));
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
            return Task.FromResult(UiOperationResult.Ok($"Opened target: {target}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(UiOperationResult.Fail(
                UiErrorCode.ExternalTargetOpenFailed,
                $"Failed to open target `{target}`: {ex.Message}",
                ex.Message));
        }
    }

    private async Task LoadFromConfigAsync(UnifiedConfig config, CancellationToken cancellationToken)
    {
        SuspendAutoSave(repairTimerProfilesOnResume: true);
        var previousSuppressPageAutoSave = _suppressPageAutoSave;
        _suppressPageAutoSave = true;
        _loadedSettingsDataBuckets.Clear();
        try
        {
            ClearHotkeyStatus();
            ClearRemoteControlStatus();
            ClearExternalNotificationStatus();
            ClearAutostartFeedback();
            await EnsureNotificationProvidersLoadedAsync(cancellationToken);
            var guiWarnings = new List<string>();
            var backgroundWarnings = new List<string>();
            var hotkeyWarnings = new List<string>();

        var rawTheme = ReadGlobalString(config, ThemeModeKey, DefaultTheme);
        var rawLanguage = ReadGlobalString(config, ConfigurationKeys.Localization, DefaultLanguage);
        var rawBackgroundPath = ReadGlobalString(config, ConfigurationKeys.BackgroundImagePath, string.Empty);
        var rawOpacity = ReadGlobalInt(config, ConfigurationKeys.BackgroundOpacity, _backgroundOpacity);
        var rawBlur = ReadGlobalInt(config, ConfigurationKeys.BackgroundBlurEffectRadius, _backgroundBlur);
        var rawStretchMode = ReadGlobalString(config, ConfigurationKeys.BackgroundImageStretchMode, DefaultBackgroundStretchMode);
        var rawLogItemDateFormat = ReadGlobalString(config, ConfigurationKeys.LogItemDateFormat, DefaultLogItemDateFormat);
        var rawOperNameLanguage = ReadGlobalString(config, ConfigurationKeys.OperNameLanguage, DefaultOperNameLanguage);
        var rawInverseClearMode = ReadProfileString(config, ConfigurationKeys.InverseClearMode, DefaultInverseClearMode);
        var rawUseSoftwareRendering = ReadGlobalBool(config, SoftwareRenderingConfigKey, false);
        var rawHotkeys = ReadGlobalString(config, ConfigurationKeys.HotKeys, string.Empty);
        var parsedHotkeys = HotkeyConfigurationCodec.Parse(rawHotkeys);
        hotkeyWarnings.AddRange(parsedHotkeys.Warnings);
        var loadedShowGui = parsedHotkeys.ShowGui;
        var loadedLinkStart = parsedHotkeys.LinkStart;

        var theme = NormalizeTheme(rawTheme);
        if (!string.Equals(rawTheme, theme, StringComparison.Ordinal))
        {
            guiWarnings.Add($"Theme normalized to `{theme}` from `{rawTheme}`.");
        }

        var language = NormalizeLanguage(rawLanguage);
        if (!string.Equals(rawLanguage, language, StringComparison.OrdinalIgnoreCase))
        {
            guiWarnings.Add($"Language normalized to `{language}` from `{rawLanguage}`.");
        }

        var backgroundPath = NormalizeBackgroundPath(rawBackgroundPath);
        if (!string.IsNullOrWhiteSpace(backgroundPath) && !File.Exists(backgroundPath))
        {
            backgroundWarnings.Add($"Background path not found and reset: {backgroundPath}");
            backgroundPath = string.Empty;
        }

        var opacity = Math.Clamp(rawOpacity, BackgroundOpacityMin, BackgroundOpacityMax);
        if (opacity != rawOpacity)
        {
            backgroundWarnings.Add($"Background opacity clamped to {opacity} from {rawOpacity}.");
        }

        var blur = Math.Clamp(rawBlur, BackgroundBlurMin, BackgroundBlurMax);
        if (blur != rawBlur)
        {
            backgroundWarnings.Add($"Background blur clamped to {blur} from {rawBlur}.");
        }

        var stretch = NormalizeBackgroundStretchMode(rawStretchMode);
        if (!string.Equals(rawStretchMode, stretch, StringComparison.OrdinalIgnoreCase))
        {
            backgroundWarnings.Add($"Background stretch mode normalized to `{stretch}` from `{rawStretchMode}`.");
        }

        var logItemDateFormat = NormalizeLogItemDateFormat(rawLogItemDateFormat);
        if (!string.Equals(rawLogItemDateFormat, logItemDateFormat, StringComparison.Ordinal))
        {
            guiWarnings.Add(
                $"Log item date format normalized to `{logItemDateFormat}` from `{rawLogItemDateFormat}`.");
        }

        var operNameLanguage = NormalizeOperNameLanguage(rawOperNameLanguage);
        if (!string.Equals(rawOperNameLanguage, operNameLanguage, StringComparison.OrdinalIgnoreCase))
        {
            guiWarnings.Add(
                $"Oper name language normalized to `{operNameLanguage}` from `{rawOperNameLanguage}`.");
        }

        var inverseClearMode = NormalizeInverseClearMode(rawInverseClearMode);
        if (!string.Equals(rawInverseClearMode, inverseClearMode, StringComparison.OrdinalIgnoreCase))
        {
            guiWarnings.Add(
                $"Inverse clear mode normalized to `{inverseClearMode}` from `{rawInverseClearMode}`.");
        }

        _suppressGuiAutoSave = true;
        _suppressGuiPreview = true;
        try
        {
            Theme = theme;
            var previousSuppress = _suppressPageAutoSave;
            _suppressPageAutoSave = true;
            try
            {
                Language = language;
            }
            finally
            {
                _suppressPageAutoSave = previousSuppress;
            }
            UseTray = ReadGlobalBool(config, ConfigurationKeys.UseTray, true);
            UseNotify = ReadGlobalBool(config, ConfigurationKeys.UseNotify, true);
            MinimizeToTray = ReadGlobalBool(config, ConfigurationKeys.MinimizeToTray, false);
            WindowTitleScrollable = ReadGlobalBool(config, ConfigurationKeys.WindowTitleScrollable, false);
            UiScalePercent = ReadGlobalInt(config, ConfigurationKeys.UiScalePercent, DefaultUiScalePercent);
            UseSoftwareRendering = rawUseSoftwareRendering;
            DeveloperModeEnabled = ReadGlobalBool(config, DeveloperModeConfigKey, false);
            LogItemDateFormatString = logItemDateFormat;
            OperNameLanguage = operNameLanguage;
            InverseClearMode = inverseClearMode;
            BackgroundImagePath = backgroundPath;
            BackgroundOpacity = opacity;
            BackgroundBlur = blur;
            BackgroundStretchMode = stretch;
            RemoteGetTaskEndpoint = ReadProfileString(config, ConfigurationKeys.RemoteControlGetTaskEndpointUri, string.Empty);
            RemoteReportEndpoint = ReadProfileString(config, ConfigurationKeys.RemoteControlReportStatusUri, string.Empty);
            RemoteUserIdentity = ReadProfileString(config, ConfigurationKeys.RemoteControlUserIdentity, string.Empty).Trim();
            RemoteDeviceIdentity = ReadProfileString(config, ConfigurationKeys.RemoteControlDeviceIdentity, string.Empty).Trim();
            RemotePollInterval = ReadProfileInt(config, ConfigurationKeys.RemoteControlPollIntervalMs, DefaultRemotePollIntervalMs);
            ExternalNotificationEnabled = false;
            ExternalNotificationSendWhenComplete = ReadProfileBool(config, ConfigurationKeys.ExternalNotificationSendWhenComplete, true);
            ExternalNotificationSendWhenError = ReadProfileBool(config, ConfigurationKeys.ExternalNotificationSendWhenError, true);
            ExternalNotificationSendWhenTimeout = ReadProfileBool(config, ConfigurationKeys.ExternalNotificationSendWhenTimeout, true);
            ExternalNotificationEnableDetails = ReadProfileBool(config, ConfigurationKeys.ExternalNotificationEnableDetails, false);
            HotkeyShowGui = loadedShowGui;
            HotkeyLinkStart = loadedLinkStart;
            _persistedHotkeyShowGui = loadedShowGui;
            _persistedHotkeyLinkStart = loadedLinkStart;
            LoadExternalNotificationProviderParametersFromConfig(config);
            HasPendingGuiChanges = false;
        }
        finally
        {
            _suppressGuiPreview = false;
            _suppressGuiAutoSave = false;
        }

        var startPerformanceWarnings = new List<string>();
        NormalizeUnsupportedGpuSettingsInConfig(config, startPerformanceWarnings);
        var startPerformanceSnapshot = ReadStartPerformanceSnapshot(config, startPerformanceWarnings);
        ApplyStartPerformanceSnapshotWithoutDirtyTracking(startPerformanceSnapshot, refreshGpuUi: false);
        HasPendingStartPerformanceChanges = false;

        var timerWarnings = new List<string>();
        var timerSnapshot = ReadTimerSnapshot(config, timerWarnings);
        ApplyTimerSnapshot(timerSnapshot);
        HasPendingTimerChanges = false;

        var versionPolicyResult = await Runtime.VersionUpdateFeatureService.LoadPolicyAsync(cancellationToken);
        if (versionPolicyResult.Success && versionPolicyResult.Value is not null)
        {
            ApplyVersionUpdatePolicy(versionPolicyResult.Value);
            SyncVersionUpdateAvailabilityFromState();
            SetPendingResourceUpdateState(null);
            VersionUpdateErrorMessage = string.Empty;
        }
        else
        {
            SetPendingVersionUpdateAvailability(false);
            SetPendingResourceUpdateState(null);
            VersionUpdateErrorMessage = versionPolicyResult.Message;
        }

        UpdatePanelCoreVersion = ResolveCoreVersionOrUnknown();

        await RefreshVersionUpdateResourceInfoAsync(cancellationToken);

        var achievementPolicyResult = await Runtime.AchievementFeatureService.LoadPolicyAsync(cancellationToken);
        if (achievementPolicyResult.Success && achievementPolicyResult.Value is not null)
        {
            ApplyAchievementPolicy(achievementPolicyResult.Value);
            UpdateAchievementPolicySummary(achievementPolicyResult.Value);
            AchievementErrorMessage = string.Empty;
        }
        else
        {
            UpdateAchievementPolicySummary(AchievementPolicy.Default);
            AchievementErrorMessage = achievementPolicyResult.Message;
        }

        _ = await RefreshAchievementSnapshotAsync("Settings.Achievement.Initialize", cancellationToken);

        var warnings = guiWarnings.Concat(backgroundWarnings).ToArray();
        if (warnings.Length > 0)
        {
            GuiSectionValidationMessage = string.Join(" ", guiWarnings);
            BackgroundValidationMessage = string.Join(" ", backgroundWarnings);
            StatusMessage = GuiValidationMessage;
            await RecordEventAsync(
                "Settings.Gui.Normalize",
                string.Join(" | ", warnings),
                cancellationToken);
        }
        else
        {
            ClearGuiValidationMessages();
        }

        if (hotkeyWarnings.Count > 0)
        {
            HotkeyWarningMessage = string.Join(" ", hotkeyWarnings);
            await RecordEventAsync(
                "Settings.Hotkey.Normalize",
                string.Join(" | ", hotkeyWarnings),
                cancellationToken);
        }
        else
        {
            HotkeyWarningMessage = string.Empty;
        }

        await RefreshHotkeyRuntimeStateAsync(cancellationToken);

        if (startPerformanceWarnings.Count > 0)
        {
            StartPerformanceValidationMessage = string.Join(" ", startPerformanceWarnings);
            await RecordEventAsync(
                "Settings.StartPerformance.Normalize",
                string.Join(" | ", startPerformanceWarnings),
                cancellationToken);
        }
        else
        {
            StartPerformanceValidationMessage = string.Empty;
        }

            if (timerWarnings.Count > 0)
            {
                TimerValidationMessage = string.Join(" ", timerWarnings);
                await RecordEventAsync(
                    "Settings.Timer.Normalize",
                    string.Join(" | ", timerWarnings),
                    cancellationToken);
            }
            else
            {
                TimerValidationMessage = string.Empty;
            }

            RestoreExternalNotificationStatusSummaryIfIdle();
            MarkAllSettingsDataBucketsLoaded();
        }
        finally
        {
            _suppressPageAutoSave = previousSuppressPageAutoSave;
            ResumeAutoSave();
        }
    }

    private void RestoreExternalNotificationStatusSummaryIfIdle()
    {
        if (!ExternalNotificationEnabled
            || HasGuiValidationMessage
            || HasHotkeyWarningMessage
            || HasStartPerformanceValidationMessage
            || HasTimerValidationMessage
            || !string.IsNullOrWhiteSpace(LastErrorMessage))
        {
            return;
        }

        var summary = BuildExternalNotificationConfigurationSummary(
            AvailableNotificationProviders.Where(provider => _enabledNotificationProviders.Contains(provider)));
        if (!string.IsNullOrWhiteSpace(summary))
        {
            StatusMessage = summary;
        }
    }

    private void BeginAutostartInteraction()
    {
        _lastAutostartToggleAt = DateTimeOffset.UtcNow;
        ClearAutostartFeedback();
    }

    private void ClearAutostartFeedback()
    {
        var pendingFeedback = _autostartFeedbackCts;
        _autostartFeedbackCts = null;
        pendingFeedback?.Cancel();
        AutostartWarningMessage = string.Empty;
        AutostartErrorMessage = string.Empty;
    }

    private async Task ShowAutostartWarningWithDelayAsync(string message, CancellationToken cancellationToken)
    {
        await ShowAutostartFeedbackWithDelayAsync(
            warningMessage: message,
            errorMessage: string.Empty,
            cancellationToken);
    }

    private async Task ShowAutostartErrorWithDelayAsync(string message, CancellationToken cancellationToken)
    {
        await ShowAutostartFeedbackWithDelayAsync(
            warningMessage: string.Empty,
            errorMessage: message,
            cancellationToken);
    }

    private async Task ShowAutostartFeedbackWithDelayAsync(
        string warningMessage,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var pendingFeedback = _autostartFeedbackCts;
        pendingFeedback?.Cancel();
        _autostartFeedbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var feedbackCts = _autostartFeedbackCts;

        try
        {
            var remainingDelay = GetRemainingAutostartFeedbackDelay();
            if (remainingDelay > TimeSpan.Zero)
            {
                await Task.Delay(remainingDelay, feedbackCts.Token);
            }

            if (feedbackCts.Token.IsCancellationRequested)
            {
                return;
            }

            AutostartWarningMessage = warningMessage;
            AutostartErrorMessage = errorMessage;
        }
        catch (OperationCanceledException) when (feedbackCts.IsCancellationRequested)
        {
            // Newer toggle state superseded the pending feedback.
        }
        finally
        {
            if (ReferenceEquals(_autostartFeedbackCts, feedbackCts))
            {
                feedbackCts.Dispose();
                _autostartFeedbackCts = null;
            }
            else
            {
                feedbackCts.Dispose();
            }
        }
    }

    private TimeSpan GetRemainingAutostartFeedbackDelay()
    {
        if (!_lastAutostartToggleAt.HasValue)
        {
            return TimeSpan.Zero;
        }

        var remaining = TimeSpan.FromMilliseconds(AutostartFeedbackDelayMs)
            - (DateTimeOffset.UtcNow - _lastAutostartToggleAt.Value);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private string BuildAutostartSetErrorMessage(string? errorCode, string fallbackMessage)
    {
        var localized = PlatformCapabilityTextMap.FormatErrorCode(
            Language,
            errorCode,
            fallbackMessage,
            _localizationFallbackReporter);

        return string.Equals(localized, fallbackMessage, StringComparison.Ordinal)
            ? fallbackMessage
            : $"{localized}：{fallbackMessage}";
    }

    private string BuildAutostartMismatchMessage(bool actualEnabled)
    {
        var verificationFailed = PlatformCapabilityTextMap.FormatErrorCode(
            Language,
            PlatformErrorCodes.AutostartVerificationFailed,
            "Autostart verification failed",
            _localizationFallbackReporter);
        var actualStatus = PlatformCapabilityTextMap.FormatAutostartStatus(
            Language,
            actualEnabled,
            _localizationFallbackReporter);
        return $"{verificationFailed}，{actualStatus}";
    }

    private void UpdateCombinedGuiValidationMessage()
    {
        GuiValidationMessage = CombineValidationMessages(GuiSectionValidationMessage, BackgroundValidationMessage);
    }

    private void RefreshRightPaneLocalization()
    {
        RefreshCurrentSectionActions();
        UpdateAchievementPolicySummary(new AchievementPolicy(AchievementPopupDisabled, AchievementPopupAutoClose));
        NotifyValidationStateChanged();
    }

    private void NotifyValidationStateChanged()
    {
        OnPropertyChanged(nameof(AchievementLevelText));
        OnPropertyChanged(nameof(GuiSectionValidationMessage));
        OnPropertyChanged(nameof(HasGuiSectionValidationMessage));
        OnPropertyChanged(nameof(BackgroundValidationMessage));
        OnPropertyChanged(nameof(HasBackgroundValidationMessage));
        OnPropertyChanged(nameof(GuiValidationMessage));
        OnPropertyChanged(nameof(HasGuiValidationMessage));
        OnPropertyChanged(nameof(VersionUpdateStatusMessage));
        OnPropertyChanged(nameof(VersionUpdateInlineMessage));
        OnPropertyChanged(nameof(HasVersionUpdateInlineMessage));
        OnPropertyChanged(nameof(RemoteControlStatusMessage));
        OnPropertyChanged(nameof(ConfigurationManagerStatusMessage));
        OnPropertyChanged(nameof(HotkeyStatusMessage));
        OnPropertyChanged(nameof(IssueReportStatusMessage));
        OnPropertyChanged(nameof(AboutStatusMessage));
        OnPropertyChanged(nameof(HasAboutStatusMessage));
        OnPropertyChanged(nameof(HasAboutStatusBlockMessage));
        OnPropertyChanged(nameof(GpuSupportMessage));
        OnPropertyChanged(nameof(HasGpuSupportMessage));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(HasIssueReportUpdateAvailability));
        OnPropertyChanged(nameof(ShowIssueReportPreflightNote));
        OnPropertyChanged(nameof(IssueReportVersionUpdateSummary));
        OnPropertyChanged(nameof(IssueReportUpdateNotice));
        OnPropertyChanged(nameof(IssueReportClearImageCacheTip));
    }

    private string BuildIssueReportUpdateNoticeText()
    {
        if (HasPendingVersionUpdateAvailability && HasPendingResourceUpdateAvailability)
        {
            return LocalizeSettingsText(
                "Settings.IssueReport.UpdateAvailable.Both",
                "检测到软件和资源更新可用，建议先更新后再提交 Issue。");
        }

        if (HasPendingVersionUpdateAvailability)
        {
            return LocalizeSettingsText(
                "Settings.IssueReport.UpdateAvailable.Software",
                "检测到软件更新可用，建议先更新后再提交 Issue。");
        }

        return LocalizeSettingsText(
            "Settings.IssueReport.UpdateAvailable.Resource",
            "检测到资源更新可用，建议先更新资源后再提交 Issue。");
    }

    private void ClearGuiValidationMessages()
    {
        GuiSectionValidationMessage = string.Empty;
        BackgroundValidationMessage = string.Empty;
    }

    private void SetGuiValidationMessageForCurrentSection(string message)
    {
        if (IsBackgroundSelected)
        {
            GuiSectionValidationMessage = string.Empty;
            BackgroundValidationMessage = message;
            return;
        }

        GuiSectionValidationMessage = message;
        BackgroundValidationMessage = string.Empty;
    }

    private void SetGuiValidationMessageForResult(UiOperationResult result)
    {
        if (string.Equals(result.Error?.Code, UiErrorCode.BackgroundImagePathNotFound, StringComparison.Ordinal))
        {
            GuiSectionValidationMessage = string.Empty;
            BackgroundValidationMessage = result.Message;
            return;
        }

        SetGuiValidationMessageForCurrentSection(result.Message);
    }

    private static string CombineValidationMessages(params string[] messages)
    {
        return string.Join(
            " ",
            messages.Where(static message => !string.IsNullOrWhiteSpace(message)));
    }

    private void RunWithSuppressedSettingsBackfill(
        Action apply,
        bool suppressGuiAutoSave = false,
        bool suppressGuiPreview = false,
        bool suppressStartPerformanceDirtyTracking = false,
        bool suppressGpuUiRefresh = false,
        bool suppressGpuSelectionChange = false,
        bool suppressVersionUpdateResourceRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(apply);

        var previousSuppressPageAutoSave = _suppressPageAutoSave;
        var previousSuppressGuiAutoSave = _suppressGuiAutoSave;
        var previousSuppressGuiPreview = _suppressGuiPreview;
        var previousSuppressStartPerformanceDirtyTracking = _suppressStartPerformanceDirtyTracking;
        var previousSuppressGpuUiRefresh = _suppressGpuUiRefresh;
        var previousSuppressGpuSelectionChange = _suppressGpuSelectionChange;
        var previousSuppressVersionUpdateResourceRefresh = _suppressVersionUpdateResourceRefresh;

        _suppressPageAutoSave = true;
        if (suppressGuiAutoSave)
        {
            _suppressGuiAutoSave = true;
        }

        if (suppressGuiPreview)
        {
            _suppressGuiPreview = true;
        }

        if (suppressStartPerformanceDirtyTracking)
        {
            _suppressStartPerformanceDirtyTracking = true;
        }

        if (suppressGpuUiRefresh)
        {
            _suppressGpuUiRefresh = true;
        }

        if (suppressGpuSelectionChange)
        {
            _suppressGpuSelectionChange = true;
        }

        if (suppressVersionUpdateResourceRefresh)
        {
            _suppressVersionUpdateResourceRefresh = true;
        }

        try
        {
            apply();
        }
        finally
        {
            _suppressVersionUpdateResourceRefresh = previousSuppressVersionUpdateResourceRefresh;
            _suppressGpuSelectionChange = previousSuppressGpuSelectionChange;
            _suppressGpuUiRefresh = previousSuppressGpuUiRefresh;
            _suppressStartPerformanceDirtyTracking = previousSuppressStartPerformanceDirtyTracking;
            _suppressGuiPreview = previousSuppressGuiPreview;
            _suppressGuiAutoSave = previousSuppressGuiAutoSave;
            _suppressPageAutoSave = previousSuppressPageAutoSave;
        }
    }

    private bool IsSettingsDataBucketLoaded(string bucket)
        => _loadedSettingsDataBuckets.Contains(bucket);

    private void MarkSettingsDataBucketsLoaded(params string[] buckets)
    {
        foreach (var bucket in buckets)
        {
            if (!string.IsNullOrWhiteSpace(bucket))
            {
                _loadedSettingsDataBuckets.Add(bucket);
            }
        }
    }

    private void MarkAllSettingsDataBucketsLoaded()
    {
        _loadedSettingsDataBuckets.Clear();
        MarkSettingsDataBucketsLoaded(AllSettingsDataBuckets);
    }

    private static IReadOnlyList<string> GetSettingsDataBucketsForSection(string? sectionKey)
    {
        if (string.IsNullOrWhiteSpace(sectionKey))
        {
            return [];
        }

        return sectionKey.Trim() switch
        {
            "HotKey" => [SettingsDataBucketHotKey],
            "Timer" => [SettingsDataBucketTimer],
            "Performance" => [SettingsDataBucketStartPerformance],
            "Start" => [SettingsDataBucketStartPerformance, SettingsDataBucketAutostart, SettingsDataBucketConnectionGame],
            "Game" => [SettingsDataBucketConnectionGame, SettingsDataBucketStartPerformance],
            "Connect" => [SettingsDataBucketConnectionGame],
            "RemoteControl" => [SettingsDataBucketRemoteControl],
            "ExternalNotification" => [SettingsDataBucketExternalNotification],
            "Achievement" => [SettingsDataBucketAchievement],
            "VersionUpdate" => [SettingsDataBucketConnectionGame, SettingsDataBucketVersionUpdate],
            _ => [],
        };
    }

    public async Task EnsureSectionDataLoadedAsync(string? sectionKey, CancellationToken cancellationToken = default)
    {
        foreach (var bucket in GetSettingsDataBucketsForSection(sectionKey))
        {
            await EnsureSettingsDataBucketLoadedAsync(bucket, cancellationToken);
        }
    }

    public async Task WarmupDeferredSectionDataAsync(CancellationToken cancellationToken = default)
    {
        foreach (var bucket in AllSettingsDataBuckets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureSettingsDataBucketLoadedAsync(bucket, cancellationToken);
        }
    }

    private async Task EnsureSettingsDataBucketLoadedAsync(string bucket, CancellationToken cancellationToken)
    {
        if (IsSettingsDataBucketLoaded(bucket))
        {
            return;
        }

        await _settingsDataLoadSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (IsSettingsDataBucketLoaded(bucket))
            {
                return;
            }

            await LoadSettingsDataBucketCoreAsync(bucket, Runtime.ConfigurationService.CurrentConfig, cancellationToken);
            MarkSettingsDataBucketsLoaded(bucket);
        }
        finally
        {
            _settingsDataLoadSemaphore.Release();
        }
    }

    private async Task LoadSettingsDataBucketCoreAsync(
        string bucket,
        UnifiedConfig config,
        CancellationToken cancellationToken)
    {
        switch (bucket)
        {
            case SettingsDataBucketHotKey:
                await RefreshHotkeyRuntimeStateAsync(cancellationToken);
                break;
            case SettingsDataBucketTimer:
                await LoadTimerDataFromConfigAsync(config, cancellationToken);
                break;
            case SettingsDataBucketStartPerformance:
                await LoadStartPerformanceDataFromConfigAsync(config, cancellationToken);
                break;
            case SettingsDataBucketAutostart:
                await RefreshAutostartStatusAsync(cancellationToken);
                break;
            case SettingsDataBucketConnectionGame:
                LoadConnectionSharedStateFromConfig();
                break;
            case SettingsDataBucketRemoteControl:
                LoadRemoteControlDataFromConfig(config);
                break;
            case SettingsDataBucketExternalNotification:
                await LoadExternalNotificationDataFromConfigAsync(config, cancellationToken);
                break;
            case SettingsDataBucketVersionUpdate:
                await LoadVersionUpdateDataFromConfigAsync(cancellationToken);
                break;
            case SettingsDataBucketAchievement:
                await LoadAchievementDataFromConfigAsync(cancellationToken);
                break;
            default:
                break;
        }
    }

    private async Task LoadInitialSettingsAsync(CancellationToken cancellationToken)
    {
        ClearHotkeyStatus();
        ClearRemoteControlStatus();
        ClearExternalNotificationStatus();
        ClearAutostartFeedback();
        _deferredSectionDataLoadEnabled = false;
        _loadedSettingsDataBuckets.Clear();
        await LoadGuiBackgroundBaselineFromConfigAsync(Runtime.ConfigurationService.CurrentConfig, cancellationToken);
        MarkSettingsDataBucketsLoaded(SettingsDataBucketGuiBackground);
    }

    private async Task LoadGuiBackgroundBaselineFromConfigAsync(
        UnifiedConfig config,
        CancellationToken cancellationToken)
    {
        var guiWarnings = new List<string>();
        var backgroundWarnings = new List<string>();
        var hotkeyWarnings = new List<string>();

        var rawTheme = ReadGlobalString(config, ThemeModeKey, DefaultTheme);
        var rawLanguage = ReadGlobalString(config, ConfigurationKeys.Localization, DefaultLanguage);
        var rawBackgroundPath = ReadGlobalString(config, ConfigurationKeys.BackgroundImagePath, string.Empty);
        var rawOpacity = ReadGlobalInt(config, ConfigurationKeys.BackgroundOpacity, _backgroundOpacity);
        var rawBlur = ReadGlobalInt(config, ConfigurationKeys.BackgroundBlurEffectRadius, _backgroundBlur);
        var rawStretchMode = ReadGlobalString(config, ConfigurationKeys.BackgroundImageStretchMode, DefaultBackgroundStretchMode);
        var rawLogItemDateFormat = ReadGlobalString(config, ConfigurationKeys.LogItemDateFormat, DefaultLogItemDateFormat);
        var rawOperNameLanguage = ReadGlobalString(config, ConfigurationKeys.OperNameLanguage, DefaultOperNameLanguage);
        var rawInverseClearMode = ReadProfileString(config, ConfigurationKeys.InverseClearMode, DefaultInverseClearMode);
        var rawUseSoftwareRendering = ReadGlobalBool(config, SoftwareRenderingConfigKey, false);
        var rawHotkeys = ReadGlobalString(config, ConfigurationKeys.HotKeys, string.Empty);
        var parsedHotkeys = HotkeyConfigurationCodec.Parse(rawHotkeys);
        hotkeyWarnings.AddRange(parsedHotkeys.Warnings);
        var loadedShowGui = parsedHotkeys.ShowGui;
        var loadedLinkStart = parsedHotkeys.LinkStart;

        var theme = NormalizeTheme(rawTheme);
        if (!string.Equals(rawTheme, theme, StringComparison.Ordinal))
        {
            guiWarnings.Add($"Theme normalized to `{theme}` from `{rawTheme}`.");
        }

        var language = NormalizeLanguage(rawLanguage);
        if (!string.Equals(rawLanguage, language, StringComparison.OrdinalIgnoreCase))
        {
            guiWarnings.Add($"Language normalized to `{language}` from `{rawLanguage}`.");
        }

        var backgroundPath = NormalizeBackgroundPath(rawBackgroundPath);
        if (!string.IsNullOrWhiteSpace(backgroundPath) && !File.Exists(backgroundPath))
        {
            backgroundWarnings.Add($"Background path not found and reset: {backgroundPath}");
            backgroundPath = string.Empty;
        }

        var opacity = Math.Clamp(rawOpacity, BackgroundOpacityMin, BackgroundOpacityMax);
        if (opacity != rawOpacity)
        {
            backgroundWarnings.Add($"Background opacity clamped to {opacity} from {rawOpacity}.");
        }

        var blur = Math.Clamp(rawBlur, BackgroundBlurMin, BackgroundBlurMax);
        if (blur != rawBlur)
        {
            backgroundWarnings.Add($"Background blur clamped to {blur} from {rawBlur}.");
        }

        var stretch = NormalizeBackgroundStretchMode(rawStretchMode);
        if (!string.Equals(rawStretchMode, stretch, StringComparison.OrdinalIgnoreCase))
        {
            backgroundWarnings.Add($"Background stretch mode normalized to `{stretch}` from `{rawStretchMode}`.");
        }

        var logItemDateFormat = NormalizeLogItemDateFormat(rawLogItemDateFormat);
        if (!string.Equals(rawLogItemDateFormat, logItemDateFormat, StringComparison.Ordinal))
        {
            guiWarnings.Add(
                $"Log item date format normalized to `{logItemDateFormat}` from `{rawLogItemDateFormat}`.");
        }

        var operNameLanguage = NormalizeOperNameLanguage(rawOperNameLanguage);
        if (!string.Equals(rawOperNameLanguage, operNameLanguage, StringComparison.OrdinalIgnoreCase))
        {
            guiWarnings.Add(
                $"Oper name language normalized to `{operNameLanguage}` from `{rawOperNameLanguage}`.");
        }

        var inverseClearMode = NormalizeInverseClearMode(rawInverseClearMode);
        if (!string.Equals(rawInverseClearMode, inverseClearMode, StringComparison.OrdinalIgnoreCase))
        {
            guiWarnings.Add(
                $"Inverse clear mode normalized to `{inverseClearMode}` from `{rawInverseClearMode}`.");
        }

        RunWithSuppressedSettingsBackfill(
            () =>
            {
                Theme = theme;
                Language = language;
                UseTray = ReadGlobalBool(config, ConfigurationKeys.UseTray, true);
                MinimizeToTray = ReadGlobalBool(config, ConfigurationKeys.MinimizeToTray, false);
                WindowTitleScrollable = ReadGlobalBool(config, ConfigurationKeys.WindowTitleScrollable, false);
                UiScalePercent = ReadGlobalInt(config, ConfigurationKeys.UiScalePercent, DefaultUiScalePercent);
                UseSoftwareRendering = rawUseSoftwareRendering;
                DeveloperModeEnabled = ReadGlobalBool(config, DeveloperModeConfigKey, false);
                LogItemDateFormatString = logItemDateFormat;
                OperNameLanguage = operNameLanguage;
                InverseClearMode = inverseClearMode;
                BackgroundImagePath = backgroundPath;
                BackgroundOpacity = opacity;
                BackgroundBlur = blur;
                BackgroundStretchMode = stretch;
                HotkeyShowGui = loadedShowGui;
                HotkeyLinkStart = loadedLinkStart;
                _persistedHotkeyShowGui = loadedShowGui;
                _persistedHotkeyLinkStart = loadedLinkStart;
                HasPendingGuiChanges = false;
            },
            suppressGuiAutoSave: true,
            suppressGuiPreview: true,
            suppressGpuUiRefresh: true,
            suppressStartPerformanceDirtyTracking: true);

        var warnings = guiWarnings.Concat(backgroundWarnings).ToArray();
        if (warnings.Length > 0)
        {
            GuiSectionValidationMessage = string.Join(" ", guiWarnings);
            BackgroundValidationMessage = string.Join(" ", backgroundWarnings);
            StatusMessage = GuiValidationMessage;
            await RecordEventAsync(
                "Settings.Gui.Normalize",
                string.Join(" | ", warnings),
                cancellationToken);
        }
        else
        {
            ClearGuiValidationMessages();
        }

        if (hotkeyWarnings.Count > 0)
        {
            HotkeyWarningMessage = string.Join(" ", hotkeyWarnings);
            await RecordEventAsync(
                "Settings.Hotkey.Normalize",
                string.Join(" | ", hotkeyWarnings),
                cancellationToken);
        }
        else
        {
            HotkeyWarningMessage = string.Empty;
        }
    }

    private async Task LoadStartPerformanceDataFromConfigAsync(
        UnifiedConfig config,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        NormalizeUnsupportedGpuSettingsInConfig(config, warnings);
        var startPerformanceSnapshot = ReadStartPerformanceSnapshot(config, warnings);
        ApplyStartPerformanceSnapshotWithoutDirtyTracking(startPerformanceSnapshot, refreshGpuUi: false);
        HasPendingStartPerformanceChanges = false;

        if (warnings.Count > 0)
        {
            StartPerformanceValidationMessage = string.Join(" ", warnings);
            await RecordEventAsync(
                "Settings.StartPerformance.Normalize",
                string.Join(" | ", warnings),
                cancellationToken);
        }
        else
        {
            StartPerformanceValidationMessage = string.Empty;
        }

        await RefreshGpuUiStateAsync(cancellationToken);
    }

    private async Task LoadTimerDataFromConfigAsync(
        UnifiedConfig config,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var timerSnapshot = ReadTimerSnapshot(config, warnings);
        ApplyTimerSnapshot(timerSnapshot);
        HasPendingTimerChanges = false;

        if (warnings.Count > 0)
        {
            TimerValidationMessage = string.Join(" ", warnings);
            await RecordEventAsync(
                "Settings.Timer.Normalize",
                string.Join(" | ", warnings),
                cancellationToken);
        }
        else
        {
            TimerValidationMessage = string.Empty;
        }
    }

    private void LoadRemoteControlDataFromConfig(UnifiedConfig config)
    {
        RunWithSuppressedSettingsBackfill(() =>
        {
            RemoteGetTaskEndpoint = ReadProfileString(config, ConfigurationKeys.RemoteControlGetTaskEndpointUri, string.Empty);
            RemoteReportEndpoint = ReadProfileString(config, ConfigurationKeys.RemoteControlReportStatusUri, string.Empty);
            RemoteUserIdentity = ReadProfileString(config, ConfigurationKeys.RemoteControlUserIdentity, string.Empty).Trim();
            RemoteDeviceIdentity = ReadProfileString(config, ConfigurationKeys.RemoteControlDeviceIdentity, string.Empty).Trim();
            RemotePollInterval = ReadProfileInt(config, ConfigurationKeys.RemoteControlPollIntervalMs, DefaultRemotePollIntervalMs);
        });
    }

    private async Task LoadExternalNotificationDataFromConfigAsync(
        UnifiedConfig config,
        CancellationToken cancellationToken)
    {
        await EnsureNotificationProvidersLoadedAsync(cancellationToken);
        var configurationSummary = string.Empty;
        RunWithSuppressedSettingsBackfill(() =>
        {
            ExternalNotificationEnabled = false;
            ExternalNotificationSendWhenComplete = ReadProfileBool(config, ConfigurationKeys.ExternalNotificationSendWhenComplete, true);
            ExternalNotificationSendWhenError = ReadProfileBool(config, ConfigurationKeys.ExternalNotificationSendWhenError, true);
            ExternalNotificationSendWhenTimeout = ReadProfileBool(config, ConfigurationKeys.ExternalNotificationSendWhenTimeout, true);
            ExternalNotificationEnableDetails = ReadProfileBool(config, ConfigurationKeys.ExternalNotificationEnableDetails, false);
            LoadExternalNotificationProviderParametersFromConfig(config);
            configurationSummary = BuildExternalNotificationConfigurationSummary(
                AvailableNotificationProviders.Where(provider => _enabledNotificationProviders.Contains(provider)));
        });

        if (!string.IsNullOrWhiteSpace(configurationSummary)
            && string.IsNullOrWhiteSpace(StatusMessage))
        {
            StatusMessage = configurationSummary;
        }
    }

    private async Task LoadVersionUpdateDataFromConfigAsync(CancellationToken cancellationToken)
    {
        var versionPolicyResult = await Runtime.VersionUpdateFeatureService.LoadPolicyAsync(cancellationToken);
        if (versionPolicyResult.Success && versionPolicyResult.Value is not null)
        {
            ApplyVersionUpdatePolicy(versionPolicyResult.Value);
            VersionUpdateErrorMessage = string.Empty;
        }
        else
        {
            VersionUpdateErrorMessage = versionPolicyResult.Message;
        }

        UpdatePanelCoreVersion = ResolveCoreVersionOrUnknown();

        await RefreshVersionUpdateResourceInfoAsync(cancellationToken);
    }

    private async Task LoadAchievementDataFromConfigAsync(CancellationToken cancellationToken)
    {
        var achievementPolicyResult = await Runtime.AchievementFeatureService.LoadPolicyAsync(cancellationToken);
        if (achievementPolicyResult.Success && achievementPolicyResult.Value is not null)
        {
            ApplyAchievementPolicy(achievementPolicyResult.Value);
            UpdateAchievementPolicySummary(achievementPolicyResult.Value);
            AchievementErrorMessage = string.Empty;
        }
        else
        {
            UpdateAchievementPolicySummary(AchievementPolicy.Default);
            AchievementErrorMessage = achievementPolicyResult.Message;
        }

        _ = await RefreshAchievementSnapshotAsync("Settings.Achievement.Initialize", cancellationToken);
    }

    private void LoadConnectionSharedStateFromConfig()
    {
        if (!Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
        {
            return;
        }

        RunWithSuppressedSettingsBackfill(
            () => ConnectionGameProfileSync.ReadFromProfile(profile, ConnectionGameSharedState, tolerateMissing: false),
            suppressVersionUpdateResourceRefresh: true);
    }

    private static string ReadGlobalString(UnifiedConfig config, string key, string fallback)
        => ReadString(config, key, fallback, ConfigValuePreference.GlobalFirst);

    private static string ReadProfileString(UnifiedConfig config, string key, string fallback)
        => ReadString(config, key, fallback, ConfigValuePreference.ProfileFirst);

    private static bool ReadGlobalBool(UnifiedConfig config, string key, bool fallback)
        => ReadBool(config, key, fallback, ConfigValuePreference.GlobalFirst);

    private static bool ReadProfileBool(UnifiedConfig config, string key, bool fallback)
        => ReadBool(config, key, fallback, ConfigValuePreference.ProfileFirst);

    private static int ReadGlobalInt(UnifiedConfig config, string key, int fallback)
        => ReadInt(config, key, fallback, ConfigValuePreference.GlobalFirst);

    private static int ReadProfileInt(UnifiedConfig config, string key, int fallback)
        => ReadInt(config, key, fallback, ConfigValuePreference.ProfileFirst);

    private static string ReadString(
        UnifiedConfig config,
        string key,
        string fallback,
        ConfigValuePreference preference)
    {
        if (TryGetConfigNode(config, key, preference, out var node) && node is not null)
        {
            if (node is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            var raw = node.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }
        }

        return fallback;
    }

    private static bool ReadBool(
        UnifiedConfig config,
        string key,
        bool fallback,
        ConfigValuePreference preference)
    {
        if (!TryGetConfigNode(config, key, preference, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool parsedBool))
            {
                return parsedBool;
            }

            if (value.TryGetValue(out int parsedInt))
            {
                return parsedInt != 0;
            }

            if (value.TryGetValue(out string? text))
            {
                if (bool.TryParse(text, out var parsedTextBool))
                {
                    return parsedTextBool;
                }

                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTextInt))
                {
                    return parsedTextInt != 0;
                }
            }
        }

        if (bool.TryParse(node.ToString(), out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static int ReadInt(
        UnifiedConfig config,
        string key,
        int fallback,
        ConfigValuePreference preference)
    {
        if (TryGetConfigNode(config, key, preference, out var node) && node is not null)
        {
            return int.TryParse(node.ToString(), out var parsed) ? parsed : fallback;
        }

        return fallback;
    }

    private string NormalizeTheme(string? value)
    {
        var normalized = value?.Trim();
        if (string.Equals(normalized, "System", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return "SyncWithOs";
        }

        return ThemeOptions.FirstOrDefault(
                option => string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?? DefaultTheme;
    }

    private string NormalizeLanguage(string? value)
    {
        return UiLanguageCatalog.Normalize(value);
    }

    private string NormalizeBackgroundStretchMode(string? value)
    {
        var normalized = value?.Trim();
        return BackgroundStretchModes.FirstOrDefault(
                option => string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?? DefaultBackgroundStretchMode;
    }

    private string NormalizeLogItemDateFormat(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return LogItemDateFormatOptions.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : DefaultLogItemDateFormat;
    }

    private static string FormatGuiLogTimestampPreview(string? format)
    {
        var effectiveFormat = string.IsNullOrWhiteSpace(format)
            ? DefaultLogItemDateFormat
            : format.Trim();

        try
        {
            return DateTimeOffset.Now.ToString(effectiveFormat, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return DateTimeOffset.Now.ToString(DefaultLogItemDateFormat, CultureInfo.InvariantCulture);
        }
    }

    private string NormalizeOperNameLanguage(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return OperNameLanguageOptions.FirstOrDefault(
                option => string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?? DefaultOperNameLanguage;
    }

    private string NormalizeInverseClearMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return InverseClearModeOptions.FirstOrDefault(
                option => string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?? DefaultInverseClearMode;
    }

    private static string NormalizeBackgroundPath(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private GpuPreference BuildCurrentGpuPreference()
    {
        return new GpuPreference(
            UseGpu: PerformanceUseGpu,
            AllowDeprecatedGpu: PerformanceAllowDeprecatedGpu,
            PreferredGpuDescription: PerformancePreferredGpuDescription,
            PreferredGpuInstancePath: PerformancePreferredGpuInstancePath);
    }

    private static bool IsSameGpuPreference(
        GpuPreference left,
        GpuPreference right)
    {
        return left.UseGpu == right.UseGpu
               && left.AllowDeprecatedGpu == right.AllowDeprecatedGpu
               && string.Equals(
                   left.PreferredGpuDescription?.Trim(),
                   right.PreferredGpuDescription?.Trim(),
                   StringComparison.Ordinal)
               && string.Equals(
                   left.PreferredGpuInstancePath?.Trim(),
                   right.PreferredGpuInstancePath?.Trim(),
                   StringComparison.Ordinal);
    }

    private static bool IsNonUiThreadContext()
    {
        if (Avalonia.Application.Current is null)
        {
            return true;
        }

        return !Dispatcher.UIThread.CheckAccess();
    }

    private bool SetSelectedLanguageValue(string? value, bool requestLanguageChange)
    {
        if (requestLanguageChange && _suppressSelectedLanguageChangeRequest)
        {
            return false;
        }

        var normalized = NormalizeLanguage(value);
        if (!SetProperty(ref _selectedLanguageValue, normalized, nameof(SelectedLanguageValue)))
        {
            return false;
        }

        NotifySelectedLanguageBindingChanged();
        if (requestLanguageChange && !_suppressSelectedLanguageChangeRequest)
        {
            RequestLanguageChangeFromSelection(normalized);
        }

        return true;
    }

    private void BeginBlockingOperationOverlay()
    {
        _blockingOperationOverlayDepth++;
        if (_blockingOperationOverlayDepth == 1)
        {
            IsBlockingOperationOverlayVisible = true;
        }
    }

    private void EndBlockingOperationOverlay()
    {
        if (_blockingOperationOverlayDepth <= 0)
        {
            _blockingOperationOverlayDepth = 0;
            IsBlockingOperationOverlayVisible = false;
            return;
        }

        _blockingOperationOverlayDepth--;
        if (_blockingOperationOverlayDepth == 0)
        {
            IsBlockingOperationOverlayVisible = false;
        }
    }

    private static Task WaitForBlockingOperationOverlayRenderAsync()
    {
        if (Avalonia.Application.Current is null || !Dispatcher.UIThread.CheckAccess())
        {
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Loaded).GetTask();
    }

    private async Task WaitForPostedLanguageRefreshAsync(
        string fromLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (Avalonia.Application.Current is null || !Dispatcher.UIThread.CheckAccess())
        {
            return;
        }

        var total = Stopwatch.StartNew();
        var loaded = Stopwatch.StartNew();
        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Loaded).GetTask();
        _ = RecordLanguageSwitchTimingAsync(
            "Settings.ChangeLanguage.WaitViewRefresh.Loaded",
            loaded,
            fromLanguage,
            targetLanguage,
            cancellationToken);

        var background = Stopwatch.StartNew();
        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Background).GetTask();
        _ = RecordLanguageSwitchTimingAsync(
            "Settings.ChangeLanguage.WaitViewRefresh.Background",
            background,
            fromLanguage,
            targetLanguage,
            cancellationToken);
        _ = RecordLanguageSwitchTimingAsync(
            "Settings.ChangeLanguage.WaitViewRefresh.ProbeTotal",
            total,
            fromLanguage,
            targetLanguage,
            cancellationToken);
    }

    private static Task YieldForBlockingOperationOverlayFrameAsync(bool allowRenderYields)
    {
        if (!allowRenderYields || Avalonia.Application.Current is null || !Dispatcher.UIThread.CheckAccess())
        {
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Loaded).GetTask();
    }

    private Task RecordLanguageSwitchTimingAsync(
        string scope,
        Stopwatch stopwatch,
        string fromLanguage,
        string targetLanguage,
        CancellationToken cancellationToken,
        params (string Key, object? Value)[] fields)
    {
        return RecordTemporaryTimingAsync(
            scope,
            stopwatch.Elapsed.TotalMilliseconds,
            cancellationToken,
            [("from", fromLanguage), ("target", targetLanguage), .. fields]);
    }

    private Task RecordLanguageApplyTimingAsync(
        string scope,
        Stopwatch stopwatch,
        string fromLanguage,
        string targetLanguage,
        bool allowRenderYields,
        params (string Key, object? Value)[] fields)
    {
        return RecordTemporaryTimingAsync(
            scope,
            stopwatch.Elapsed.TotalMilliseconds,
            CancellationToken.None,
            [("from", fromLanguage), ("target", targetLanguage), ("renderYields", allowRenderYields), .. fields]);
    }

    private Task RecordTemporaryTimingAsync(
        string scope,
        double elapsedMs,
        CancellationToken cancellationToken,
        params (string Key, object? Value)[] fields)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fields)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                payload[key] = value;
            }
        }

        return RecordTemporaryTimingAsync(scope, elapsedMs, payload, cancellationToken);
    }

    private Task<UiOperationResult<string>> ChangeLanguageCoordinatorAsync(
        string normalized,
        CancellationToken cancellationToken)
    {
        if (Avalonia.Application.Current is null || !Dispatcher.UIThread.CheckAccess())
        {
            return _uiLanguageCoordinator.ChangeLanguageAsync(normalized, cancellationToken);
        }

        return Task.Run(
            () => _uiLanguageCoordinator.ChangeLanguageAsync(normalized, cancellationToken),
            cancellationToken);
    }

    private void RequestLanguageChangeFromSelection(string targetLanguage)
    {
        var normalized = NormalizeLanguage(targetLanguage);
        if (string.Equals(normalized, Language, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, _pendingLanguageChangeTarget, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        async Task ChangeLanguageCoreAsync()
        {
            var previousPendingTarget = _pendingLanguageChangeTarget;
            _pendingLanguageChangeTarget = normalized;
            if (!string.Equals(previousPendingTarget, normalized, StringComparison.OrdinalIgnoreCase))
            {
                NotifySelectedLanguageBindingChanged();
            }

            try
            {
                await ChangeLanguageAsync(normalized);
            }
            finally
            {
                if (string.Equals(_pendingLanguageChangeTarget, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingLanguageChangeTarget = null;
                    NotifySelectedLanguageBindingChanged();
                }

                SetSelectedLanguageValue(Language, requestLanguageChange: false);
            }
        }

        // Non-UI contexts (test host/background callers) should observe language and config state immediately.
        if (IsNonUiThreadContext())
        {
            Task.Run(ChangeLanguageCoreAsync).GetAwaiter().GetResult();
            return;
        }

        _ = ChangeLanguageCoreAsync();
    }

    private void NotifySelectedLanguageBindingChanged()
    {
        var previousSuppressState = _suppressSelectedLanguageChangeRequest;
        _suppressSelectedLanguageChangeRequest = true;
        try
        {
            OnPropertyChanged(nameof(SelectedLanguageOption));
            OnPropertyChanged(nameof(SelectedLanguageValue));
        }
        finally
        {
            _suppressSelectedLanguageChangeRequest = previousSuppressState;
        }
    }

    private void InvalidatePendingGpuRefresh()
    {
        Interlocked.Increment(ref _gpuRefreshSequence);
    }

    private void RefreshGpuUiState()
    {
        _ = RefreshGpuUiStateAsync(CancellationToken.None);
    }

    private async Task RefreshGpuUiStateAsync(CancellationToken cancellationToken = default)
    {
        if (_suppressGpuUiRefresh)
        {
            return;
        }

        var preference = BuildCurrentGpuPreference();
        var refreshSequence = Interlocked.Increment(ref _gpuRefreshSequence);
        try
        {
            var resolution = await Task.Run(
                () => Runtime.Platform.GpuCapabilityService.Resolve(preference),
                cancellationToken).ConfigureAwait(false);

            void ApplyResolutionIfCurrent()
            {
                if (_suppressGpuUiRefresh
                    || cancellationToken.IsCancellationRequested
                    || refreshSequence != Interlocked.Read(ref _gpuRefreshSequence))
                {
                    return;
                }

                // Drop stale probe results to avoid reverting an explicit GPU selection
                // when preference fields changed while this probe was running.
                if (!IsSameGpuPreference(preference, BuildCurrentGpuPreference()))
                {
                    return;
                }

                ApplyGpuResolution(resolution);
            }

            if (Avalonia.Application.Current is null)
            {
                ApplyResolutionIfCurrent();
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(
                    ApplyResolutionIfCurrent,
                    DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is expected when a newer refresh supersedes an older request.
        }
        catch (Exception ex)
        {
            void ApplyFailureIfCurrent()
            {
                if (refreshSequence != Interlocked.Read(ref _gpuRefreshSequence))
                {
                    return;
                }

                ApplyGpuProbeFailureState(ex);
            }

            if (Avalonia.Application.Current is null)
            {
                ApplyFailureIfCurrent();
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(
                    ApplyFailureIfCurrent,
                    DispatcherPriority.Background);
            }
        }
    }

    private void ApplyGpuResolution(GpuSelectionResolution resolution)
    {
        var selectedOption = resolution.SelectedOption;

        var previousSuppressUi = _suppressGpuUiRefresh;
        var previousSuppressSelection = _suppressGpuSelectionChange;
        var previousSuppressDirty = _suppressStartPerformanceDirtyTracking;
        _suppressGpuUiRefresh = true;
        _suppressGpuSelectionChange = true;
        _suppressStartPerformanceDirtyTracking = true;

        try
        {
            SetGpuLegacyPropertiesSilently(
                selectedOption,
                selectedOption.IsCustomEntry ? PerformancePreferredGpuDescription : null,
                selectedOption.IsCustomEntry ? PerformancePreferredGpuInstancePath : null);

            AvailableGpuOptions = resolution.Snapshot.Options
                .Select(BuildGpuOptionDisplayItem)
                .ToArray();

            SelectedGpuOption = AvailableGpuOptions.FirstOrDefault(
                option => string.Equals(option.Descriptor.Id, selectedOption.Id, StringComparison.Ordinal))
                ?? AvailableGpuOptions.FirstOrDefault();

            GpuCustomDescription = PerformancePreferredGpuDescription;
            GpuCustomInstancePath = PerformancePreferredGpuInstancePath;
            GpuSupportMessage = LocalizeRootText(resolution.Snapshot.StatusTextKey);
            GpuWarningMessage = BuildGpuWarningMessage(resolution);
            IsGpuSelectionEnabled = resolution.Snapshot.IsEditable;
            IsGpuDeprecatedToggleEnabled = resolution.Snapshot.IsEditable && resolution.Snapshot.SupportsDeprecatedToggle;
            IsGpuCustomSelectionFieldsVisible = resolution.Snapshot.IsEditable && selectedOption.IsCustomEntry;
            ShowGpuRestartRequiredHint = resolution.Snapshot.AppliesToCore;
        }
        finally
        {
            _suppressStartPerformanceDirtyTracking = previousSuppressDirty;
            _suppressGpuSelectionChange = previousSuppressSelection;
            _suppressGpuUiRefresh = previousSuppressUi;
        }
    }

    private void ApplyGpuSelection(GpuOptionDescriptor descriptor)
    {
        InvalidatePendingGpuRefresh();
        SetGpuLegacyPropertiesSilently(
            descriptor,
            descriptor.IsCustomEntry ? GpuCustomDescription : null,
            descriptor.IsCustomEntry ? GpuCustomInstancePath : null);

        if (descriptor.IsCustomEntry)
        {
            IsGpuCustomSelectionFieldsVisible = true;
            RefreshGpuUiState();
            MarkStartPerformanceDirty();
            return;
        }

        RefreshGpuUiState();
        MarkStartPerformanceDirty();
    }

    private void ApplyCustomGpuFields()
    {
        if (SelectedGpuOption?.Descriptor.IsCustomEntry != true)
        {
            return;
        }

        InvalidatePendingGpuRefresh();
        SetGpuLegacyPropertiesSilently(
            SelectedGpuOption.Descriptor,
            GpuCustomDescription,
            GpuCustomInstancePath);
        RefreshGpuUiState();
        MarkStartPerformanceDirty();
    }

    private void SetGpuLegacyPropertiesSilently(
        GpuOptionDescriptor descriptor,
        string? descriptionOverride = null,
        string? instancePathOverride = null)
    {
        var previousSuppressUi = _suppressGpuUiRefresh;
        var previousSuppressDirty = _suppressStartPerformanceDirtyTracking;
        _suppressGpuUiRefresh = true;
        _suppressStartPerformanceDirtyTracking = true;

        try
        {
            switch (descriptor.Kind)
            {
                case GpuOptionKind.Disabled:
                    PerformanceUseGpu = false;
                    PerformancePreferredGpuDescription = string.Empty;
                    PerformancePreferredGpuInstancePath = string.Empty;
                    break;

                case GpuOptionKind.SystemDefault:
                    PerformanceUseGpu = true;
                    PerformancePreferredGpuDescription = string.Empty;
                    PerformancePreferredGpuInstancePath = string.Empty;
                    break;

                case GpuOptionKind.SpecificGpu:
                    PerformanceUseGpu = true;
                    PerformancePreferredGpuDescription = (descriptionOverride ?? descriptor.Description ?? string.Empty).Trim();
                    PerformancePreferredGpuInstancePath = (instancePathOverride ?? descriptor.InstancePath ?? string.Empty).Trim();
                    break;
            }
        }
        finally
        {
            _suppressStartPerformanceDirtyTracking = previousSuppressDirty;
            _suppressGpuUiRefresh = previousSuppressUi;
        }
    }

    private GpuOptionDisplayItem BuildGpuOptionDisplayItem(GpuOptionDescriptor descriptor)
    {
        return new GpuOptionDisplayItem(descriptor, FormatGpuOptionDisplay(descriptor));
    }

    private string FormatGpuOptionDisplay(GpuOptionDescriptor descriptor)
    {
        return descriptor.Kind switch
        {
            GpuOptionKind.Disabled => RootTexts["Settings.Performance.Gpu.Option.Disabled"],
            GpuOptionKind.SystemDefault => string.IsNullOrWhiteSpace(descriptor.DisplayName)
                && string.IsNullOrWhiteSpace(descriptor.Description)
                ? RootTexts["Settings.Performance.Gpu.Option.SystemDefault"]
                : $"{RootTexts["Settings.Performance.Gpu.Option.SystemDefault"]} ({(string.IsNullOrWhiteSpace(descriptor.DisplayName) ? descriptor.Description : descriptor.DisplayName)})",
            GpuOptionKind.SpecificGpu when descriptor.IsCustomEntry
                => !string.IsNullOrWhiteSpace(descriptor.DisplayName)
                    ? descriptor.DisplayName
                    : !string.IsNullOrWhiteSpace(descriptor.InstancePath)
                        ? descriptor.InstancePath
                        : RootTexts["Settings.Performance.Gpu.Option.Custom"],
            _ => !string.IsNullOrWhiteSpace(descriptor.DisplayName)
                ? descriptor.DisplayName
                : descriptor.Description,
        };
    }

    private string BuildGpuWarningMessage(GpuSelectionResolution resolution)
    {
        var warnings = new List<string>();

        if (!string.IsNullOrWhiteSpace(resolution.Snapshot.WarningTextKey))
        {
            warnings.Add(LocalizeRootText(resolution.Snapshot.WarningTextKey));
        }

        if (!string.IsNullOrWhiteSpace(resolution.SelectionWarningTextKey))
        {
            warnings.Add(LocalizeRootText(resolution.SelectionWarningTextKey));
        }

        if (resolution.Snapshot.SupportMode == GpuPlatformSupportMode.WindowsSupported)
        {
            if (resolution.SelectedOption.IsDeprecated)
            {
                warnings.Add(LocalizeRootText("Settings.Performance.Gpu.Warning.Deprecated"));
            }

            if (resolution.SelectedOption.DriverDate.HasValue
                && resolution.SelectedOption.DriverDate.Value < GpuCapabilityConstants.DirectMlDriverMinimumDate)
            {
                warnings.Add(LocalizeRootText("Settings.Performance.Gpu.Warning.OutdatedDriver"));
            }
        }

        return string.Join(
            " ",
            warnings
                .Where(static warning => !string.IsNullOrWhiteSpace(warning))
                .Distinct(StringComparer.Ordinal));
    }

    private void InitializeNotificationTemplateDefaults()
    {
        _notificationTitle = GetNotificationTemplateTitle(_language);
        _notificationMessage = GetNotificationTemplateMessage(_language);
    }

    private void RefreshNotificationTemplateLocalization(string previousLanguage, string currentLanguage)
    {
        var previousTitle = GetNotificationTemplateTitle(previousLanguage);
        var previousMessage = GetNotificationTemplateMessage(previousLanguage);
        if (string.IsNullOrWhiteSpace(NotificationTitle)
            || string.Equals(NotificationTitle, previousTitle, StringComparison.Ordinal))
        {
            NotificationTitle = GetNotificationTemplateTitle(currentLanguage);
        }

        if (string.IsNullOrWhiteSpace(NotificationMessage)
            || string.Equals(NotificationMessage, previousMessage, StringComparison.Ordinal))
        {
            NotificationMessage = GetNotificationTemplateMessage(currentLanguage);
        }
    }

    private string GetNotificationTemplateTitle(string language)
        => GetSettingsTextForLanguage(
            language,
            "Settings.ExternalNotification.DefaultTitle",
            "MAA 外部通知测试");

    private string GetNotificationTemplateMessage(string language)
        => GetSettingsTextForLanguage(
            language,
            "Settings.ExternalNotification.DefaultMessage",
            "这是 MAA 外部通知测试信息。如果你看到了这段内容，就说明通知发送成功了！");

    private static string GetSettingsTextForLanguage(string language, string key, string fallback)
    {
        var textMap = new RootLocalizationTextMap("Root.Localization.Settings")
        {
            Language = UiLanguageCatalog.Normalize(language),
        };
        return textMap.GetOrDefault(key, fallback);
    }

    private DialogChromeCatalog CreateSettingsDialogChrome(Func<RootLocalizationTextMap, DialogChromeSnapshot> snapshotFactory)
    {
        return DialogTextCatalog.CreateRootCatalog(
            Language,
            "Root.Localization.Settings",
            snapshotFactory,
            _localizationFallbackReporter);
    }

    private string LocalizeSettingsText(string key, string fallback)
    {
        return RootTexts.GetOrDefault(key, fallback);
    }

    private string FormatSettingsText(string key, string fallback, params object[] args)
    {
        var template = LocalizeSettingsText(key, fallback);
        return string.Format(CultureInfo.CurrentCulture, template, args);
    }

    private string LocalizeRootText(string? key)
    {
        return string.IsNullOrWhiteSpace(key) ? string.Empty : RootTexts[key];
    }

    private string BuildPendingResourceUpdateSummary()
    {
        if (!_hasPendingResourceUpdateAvailability)
        {
            return string.Empty;
        }

        var versionLabel = BuildPendingResourceUpdateVersionLabel();
        if (string.IsNullOrWhiteSpace(versionLabel))
        {
            return RootTexts.GetOrDefault(
                "Main.Update.ResourceAvailable",
                "资源更新可用，点击检查资源");
        }

        var template = RootTexts.GetOrDefault(
            "Main.Update.ResourceDetected",
            "检测到资源更新: {0}");
        return string.Format(CultureInfo.CurrentCulture, template, versionLabel);
    }

    private string BuildPendingResourceUpdateVersionLabel()
    {
        var releaseNote = _pendingResourceUpdateReleaseNote.Trim();
        if (_pendingResourceUpdateVersionTimestamp is DateTimeOffset timestamp
            && !string.IsNullOrWhiteSpace(releaseNote))
        {
            return FormatPendingResourceVersionLabel(releaseNote, timestamp);
        }

        if (!string.IsNullOrWhiteSpace(releaseNote))
        {
            return releaseNote;
        }

        return _pendingResourceUpdateDisplayVersion.Trim();
    }

    private string FormatPendingResourceVersionLabel(string releaseNote, DateTimeOffset timestamp)
    {
        var localTimestamp = timestamp.ToLocalTime();
        return UiLanguageCatalog.Normalize(Language) switch
        {
            "zh-cn" or "zh-tw" => $"{releaseNote}{localTimestamp:MMdd}",
            "en-us" => $"{localTimestamp:dd/MM} {releaseNote}",
            _ => $"{localTimestamp.ToString(
                CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern.Replace("yyyy", string.Empty).Trim('/', '.'),
                CultureInfo.CurrentCulture)} {releaseNote}",
        };
    }

    private void ApplyGpuProbeFailureState(Exception exception)
    {
        Runtime.LogService.Error($"GPU capability probe failed: {exception}");

        var previousSuppressUi = _suppressGpuUiRefresh;
        var previousSuppressSelection = _suppressGpuSelectionChange;
        var previousSuppressDirty = _suppressStartPerformanceDirtyTracking;
        _suppressGpuUiRefresh = true;
        _suppressGpuSelectionChange = true;
        _suppressStartPerformanceDirtyTracking = true;

        try
        {
            AvailableGpuOptions =
            [
                BuildGpuOptionDisplayItem(GpuOptionDescriptor.Disabled),
            ];
            SelectedGpuOption = AvailableGpuOptions[0];
            GpuSupportMessage = LocalizeRootText("Settings.Performance.Gpu.Status.DetectionFailed");
            GpuWarningMessage = BuildGpuProbeFailureMessage(exception);
            IsGpuSelectionEnabled = false;
            IsGpuDeprecatedToggleEnabled = false;
            IsGpuCustomSelectionFieldsVisible = false;
            ShowGpuRestartRequiredHint = false;
        }
        finally
        {
            _suppressStartPerformanceDirtyTracking = previousSuppressDirty;
            _suppressGpuSelectionChange = previousSuppressSelection;
            _suppressGpuUiRefresh = previousSuppressUi;
        }
    }

    private void ApplyGpuUiStateBeforeProbe()
    {
        var previousSuppressUi = _suppressGpuUiRefresh;
        var previousSuppressSelection = _suppressGpuSelectionChange;
        var previousSuppressDirty = _suppressStartPerformanceDirtyTracking;
        _suppressGpuUiRefresh = true;
        _suppressGpuSelectionChange = true;
        _suppressStartPerformanceDirtyTracking = true;

        try
        {
            AvailableGpuOptions =
            [
                BuildGpuOptionDisplayItem(GpuOptionDescriptor.Disabled),
            ];
            SelectedGpuOption = AvailableGpuOptions[0];
            GpuSupportMessage = string.Empty;
            GpuWarningMessage = string.Empty;
            IsGpuSelectionEnabled = false;
            IsGpuDeprecatedToggleEnabled = false;
            IsGpuCustomSelectionFieldsVisible = false;
            ShowGpuRestartRequiredHint = false;
        }
        finally
        {
            _suppressStartPerformanceDirtyTracking = previousSuppressDirty;
            _suppressGpuSelectionChange = previousSuppressSelection;
            _suppressGpuUiRefresh = previousSuppressUi;
        }
    }

    private string BuildGpuProbeFailureMessage(Exception exception)
    {
        var root = exception;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        return $"{LocalizeRootText("Settings.Performance.Gpu.Warning.DetectionFailed")} {root.GetType().Name}: {root.Message}";
    }

    private bool ShouldPromptForGpuRestart(
        StartPerformanceSettingsSnapshot previousSnapshot,
        StartPerformanceSettingsSnapshot currentSnapshot)
    {
        return ShowGpuRestartRequiredHint
               && HasGpuSettingChange(previousSnapshot, currentSnapshot);
    }

    private static bool HasGpuSettingChange(
        StartPerformanceSettingsSnapshot previousSnapshot,
        StartPerformanceSettingsSnapshot currentSnapshot)
    {
        return previousSnapshot.PerformanceUseGpu != currentSnapshot.PerformanceUseGpu
               || previousSnapshot.PerformanceAllowDeprecatedGpu != currentSnapshot.PerformanceAllowDeprecatedGpu
               || !string.Equals(
                   previousSnapshot.PerformancePreferredGpuDescription,
                   currentSnapshot.PerformancePreferredGpuDescription,
                   StringComparison.Ordinal)
               || !string.Equals(
                   previousSnapshot.PerformancePreferredGpuInstancePath,
                   currentSnapshot.PerformancePreferredGpuInstancePath,
                   StringComparison.Ordinal);
    }

    private async Task PromptForGpuRestartAsync(CancellationToken cancellationToken)
    {
        var chrome = CreateSettingsDialogChrome(
            texts => new DialogChromeSnapshot(
                title: texts["Settings.Performance.Gpu.RestartDialog.Title"],
                confirmText: texts["Settings.Performance.Gpu.RestartDialog.Confirm"],
                cancelText: texts["Settings.Performance.Gpu.RestartDialog.Cancel"]));
        var chromeSnapshot = chrome.GetSnapshot();
        var request = new WarningConfirmDialogRequest(
            Title: chromeSnapshot.Title,
            Message: RootTexts["Settings.Performance.Gpu.RestartDialog.Message"],
            ConfirmText: chromeSnapshot.ConfirmText ?? RootTexts["Settings.Performance.Gpu.RestartDialog.Confirm"],
            CancelText: chromeSnapshot.CancelText ?? RootTexts["Settings.Performance.Gpu.RestartDialog.Cancel"],
            Language: Language,
            Chrome: chrome);
        var dialogResult = await _dialogService.ShowWarningConfirmAsync(
            request,
            "Settings.Save.StartPerformance.GpuRestartPrompt",
            cancellationToken);

        if (dialogResult.Return != DialogReturnSemantic.Confirm)
        {
            StatusMessage = RootTexts["Settings.Performance.Gpu.RestartPending"];
            LastErrorMessage = string.Empty;
            await RecordEventAsync(
                "Settings.Save.StartPerformance.GpuRestartPrompt",
                $"deferred; return={dialogResult.Return}",
                cancellationToken);
            return;
        }

        var restartResult = await Runtime.AppLifecycleService.RestartAsync(cancellationToken);
        if (!await ApplyResultAsync(restartResult, "Settings.Save.StartPerformance.GpuRestart", cancellationToken))
        {
            return;
        }

        StatusMessage = RootTexts["Settings.Performance.Gpu.RestartLaunched"];
        await RecordEventAsync(
            "Settings.Save.StartPerformance.GpuRestart",
            "restart-launched",
            cancellationToken);

        if (!Runtime.AppLifecycleService.SupportsExit)
        {
            StatusMessage = RootTexts["Settings.Performance.Gpu.RestartManualClose"];
            return;
        }

        await ApplyResultAsync(
            Runtime.AppLifecycleService.ExitAsync,
            "Settings.Save.StartPerformance.GpuRestart.Exit",
            UiErrorCode.AppExitFailed,
            cancellationToken);
    }

    private async Task PromptForSoftwareRenderingRestartAsync(CancellationToken cancellationToken)
    {
        var chrome = CreateSettingsDialogChrome(
            texts => new DialogChromeSnapshot(
                title: texts["Settings.GUI.SoftwareRendering.RestartDialog.Title"],
                confirmText: texts["Settings.GUI.SoftwareRendering.RestartDialog.Confirm"],
                cancelText: texts["Settings.GUI.SoftwareRendering.RestartDialog.Cancel"]));
        var chromeSnapshot = chrome.GetSnapshot();
        var request = new WarningConfirmDialogRequest(
            Title: chromeSnapshot.Title,
            Message: RootTexts["Settings.GUI.SoftwareRendering.RestartDialog.Message"],
            ConfirmText: chromeSnapshot.ConfirmText ?? RootTexts["Settings.GUI.SoftwareRendering.RestartDialog.Confirm"],
            CancelText: chromeSnapshot.CancelText ?? RootTexts["Settings.GUI.SoftwareRendering.RestartDialog.Cancel"],
            Language: Language,
            Chrome: chrome);
        var dialogResult = await _dialogService.ShowWarningConfirmAsync(
            request,
            "Settings.Save.GuiBatch.SoftwareRenderingRestartPrompt",
            cancellationToken);

        if (dialogResult.Return != DialogReturnSemantic.Confirm)
        {
            StatusMessage = RootTexts["Settings.GUI.SoftwareRendering.RestartPending"];
            LastErrorMessage = string.Empty;
            await RecordEventAsync(
                "Settings.Save.GuiBatch.SoftwareRenderingRestartPrompt",
                $"deferred; return={dialogResult.Return}",
                cancellationToken);
            return;
        }

        var restartResult = await Runtime.AppLifecycleService.RestartAsync(cancellationToken);
        if (!await ApplyResultAsync(restartResult, "Settings.Save.GuiBatch.SoftwareRenderingRestart", cancellationToken))
        {
            return;
        }

        StatusMessage = RootTexts["Settings.GUI.SoftwareRendering.RestartLaunched"];
        await RecordEventAsync(
            "Settings.Save.GuiBatch.SoftwareRenderingRestart",
            "restart-launched",
            cancellationToken);

        if (!Runtime.AppLifecycleService.SupportsExit)
        {
            StatusMessage = RootTexts["Settings.GUI.SoftwareRendering.RestartManualClose"];
            return;
        }

        await ApplyResultAsync(
            Runtime.AppLifecycleService.ExitAsync,
            "Settings.Save.GuiBatch.SoftwareRenderingRestart.Exit",
            UiErrorCode.AppExitFailed,
            cancellationToken);
    }

    private TimerSettingsSnapshot BuildTimerSnapshot()
    {
        var slots = Timers
            .OrderBy(static s => s.Index)
            .Select(slot => new TimerSlotSettingsSnapshot(
                Index: slot.Index,
                Enabled: slot.Enabled,
                Time: NormalizeTimerTime(slot.Time),
                Profile: (slot.Profile ?? string.Empty).Trim()))
            .ToArray();

        return new TimerSettingsSnapshot(
            ForceScheduledStart: ForceScheduledStart,
            ShowWindowBeforeForceScheduledStart: ShowWindowBeforeForceScheduledStart,
            CustomTimerConfig: CustomTimerConfig,
            Slots: slots);
    }

    private UiOperationResult ValidateTimerSnapshot(TimerSettingsSnapshot snapshot)
    {
        if (snapshot.Slots.Count != TimerSlotCount)
        {
            return UiOperationResult.Fail(
                UiErrorCode.TimerSlotCountMismatch,
                BuildTimerSlotCountMismatchMessage(snapshot.Slots.Count));
        }

        foreach (var slot in snapshot.Slots.OrderBy(static s => s.Index))
        {
            if (slot.Index < 1 || slot.Index > TimerSlotCount)
            {
                return UiOperationResult.Fail(
                    UiErrorCode.TimerSlotIndexOutOfRange,
                    BuildTimerSlotIndexOutOfRangeMessage(slot.Index));
            }

            if (slot.Enabled && !TryParseTimerTime(slot.Time, out _, out _))
            {
                return UiOperationResult.Fail(
                    UiErrorCode.TimerTimeInvalid,
                    BuildTimerTimeInvalidMessage(slot.Index));
            }

            if (!snapshot.CustomTimerConfig || !slot.Enabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(slot.Profile))
            {
                return UiOperationResult.Fail(
                    UiErrorCode.TimerProfileMissing,
                    BuildTimerProfileRequiredMessage(slot.Index));
            }

            if (!Runtime.ConfigurationService.CurrentConfig.Profiles.ContainsKey(slot.Profile))
            {
                return UiOperationResult.Fail(
                    UiErrorCode.TimerProfileNotFound,
                    BuildTimerProfileNotFoundMessage(slot.Index, slot.Profile));
            }
        }

        return UiOperationResult.Ok(LocalizeSettingsText(
            "Settings.Timer.Validation.Passed",
            "定时设置校验通过。"));
    }

    private TimerSettingsSnapshot ReadTimerSnapshot(UnifiedConfig config, ICollection<string> warnings)
    {
        var currentProfile = config.CurrentProfile;
        if (string.IsNullOrWhiteSpace(currentProfile) || !config.Profiles.ContainsKey(currentProfile))
        {
            currentProfile = config.Profiles.Keys.FirstOrDefault() ?? "Default";
            warnings.Add(BuildTimerCurrentProfileFallbackWarning(currentProfile));
        }

        var slots = new List<TimerSlotSettingsSnapshot>(TimerSlotCount);
        for (var index = 1; index <= TimerSlotCount; index++)
        {
            var enabledKey = BuildTimerEnabledKey(index);
            var hourKey = BuildTimerHourKey(index);
            var minuteKey = BuildTimerMinuteKey(index);
            var profileKey = BuildTimerProfileKey(index);

            var enabled = ReadGlobalBoolFlexible(config, enabledKey, false);

            var rawHour = ReadGlobalIntFlexible(config, hourKey, DefaultTimerHour, out var parsedHour);
            if (!parsedHour && HasConfigKey(config, hourKey, ConfigValuePreference.GlobalFirst))
            {
                warnings.Add(BuildTimerHourParseFallbackWarning(index, DefaultTimerHour));
            }

            var hour = Math.Clamp(rawHour, TimerHourMin, TimerHourMax);
            if (hour != rawHour)
            {
                warnings.Add(BuildTimerHourClampedWarning(index, hour, rawHour));
            }

            var rawMinute = ReadGlobalIntFlexible(config, minuteKey, DefaultTimerMinute, out var parsedMinute);
            if (!parsedMinute && HasConfigKey(config, minuteKey, ConfigValuePreference.GlobalFirst))
            {
                warnings.Add(BuildTimerMinuteParseFallbackWarning(index, DefaultTimerMinute));
            }

            var minute = Math.Clamp(rawMinute, TimerMinuteMin, TimerMinuteMax);
            if (minute != rawMinute)
            {
                warnings.Add(BuildTimerMinuteClampedWarning(index, minute, rawMinute));
            }

            var profile = NormalizeTimerProfile(ReadGlobalString(config, profileKey, currentProfile), currentProfile);
            if (!config.Profiles.ContainsKey(profile))
            {
                warnings.Add(BuildTimerProfileFallbackWarning(index, profile, currentProfile));
                profile = currentProfile;
            }

            slots.Add(new TimerSlotSettingsSnapshot(
                Index: index,
                Enabled: enabled,
                Time: FormatTimerTime(hour, minute),
                Profile: profile));
        }

        return new TimerSettingsSnapshot(
            ForceScheduledStart: ReadGlobalBoolFlexible(config, LegacyConfigurationKeys.ForceScheduledStart, false),
            ShowWindowBeforeForceScheduledStart: ReadGlobalBoolFlexible(config, LegacyConfigurationKeys.ShowWindowBeforeForceScheduledStart, false),
            CustomTimerConfig: ReadGlobalBoolFlexible(config, LegacyConfigurationKeys.CustomConfig, false),
            Slots: slots);
    }

    private string BuildTimerSlotCountMismatchMessage(int actualCount)
    {
        return FormatSettingsText(
            "Settings.Timer.Validation.SlotCountMismatch",
            "定时任务槽数量不正确，预期 {0} 个，实际为 {1} 个。",
            TimerSlotCount,
            actualCount);
    }

    private string BuildTimerSlotIndexOutOfRangeMessage(int slotIndex)
    {
        return FormatSettingsText(
            "Settings.Timer.Validation.SlotIndexOutOfRange",
            "定时任务槽编号必须在 1-{0} 范围内，当前为 {1}。",
            TimerSlotCount,
            slotIndex);
    }

    private string BuildTimerTimeInvalidMessage(int slotIndex)
    {
        return FormatSettingsText(
            "Settings.Timer.Validation.TimeInvalid",
            "定时任务 {0} 的时间必须为 HH:mm（00:00-23:59）。",
            slotIndex);
    }

    private string BuildTimerProfileRequiredMessage(int slotIndex)
    {
        return FormatSettingsText(
            "Settings.Timer.Validation.ProfileRequired",
            "启用自定义配置时，定时任务 {0} 必须选择配置。",
            slotIndex);
    }

    private string BuildTimerProfileNotFoundMessage(int slotIndex, string profile)
    {
        return FormatSettingsText(
            "Settings.Timer.Validation.ProfileNotFound",
            "定时任务 {0} 选择的配置“{1}”不存在。",
            slotIndex,
            profile);
    }

    private string BuildTimerCurrentProfileFallbackWarning(string currentProfile)
    {
        return FormatSettingsText(
            "Settings.Timer.Warning.CurrentProfileFallback",
            "当前配置不可用，定时设置已回退到“{0}”。",
            currentProfile);
    }

    private string BuildTimerHourParseFallbackWarning(int slotIndex, int fallbackHour)
    {
        return FormatSettingsText(
            "Settings.Timer.Warning.HourParseFallback",
            "定时任务 {0} 的小时解析失败，已回退为 {1}。",
            slotIndex,
            fallbackHour);
    }

    private string BuildTimerHourClampedWarning(int slotIndex, int clampedHour, int rawHour)
    {
        return FormatSettingsText(
            "Settings.Timer.Warning.HourClamped",
            "定时任务 {0} 的小时值已从 {1} 限制为 {2}。",
            slotIndex,
            rawHour,
            clampedHour);
    }

    private string BuildTimerMinuteParseFallbackWarning(int slotIndex, int fallbackMinute)
    {
        return FormatSettingsText(
            "Settings.Timer.Warning.MinuteParseFallback",
            "定时任务 {0} 的分钟解析失败，已回退为 {1}。",
            slotIndex,
            fallbackMinute);
    }

    private string BuildTimerMinuteClampedWarning(int slotIndex, int clampedMinute, int rawMinute)
    {
        return FormatSettingsText(
            "Settings.Timer.Warning.MinuteClamped",
            "定时任务 {0} 的分钟值已从 {1} 限制为 {2}。",
            slotIndex,
            rawMinute,
            clampedMinute);
    }

    private string BuildTimerProfileFallbackWarning(int slotIndex, string missingProfile, string fallbackProfile)
    {
        return FormatSettingsText(
            "Settings.Timer.Warning.ProfileFallback",
            "定时任务 {0} 选择的配置“{1}”不存在，已回退到“{2}”。",
            slotIndex,
            missingProfile,
            fallbackProfile);
    }

    private void ApplyTimerSnapshot(TimerSettingsSnapshot snapshot)
    {
        _suppressTimerDirtyTracking = true;
        try
        {
            ForceScheduledStart = snapshot.ForceScheduledStart;
            ShowWindowBeforeForceScheduledStart = snapshot.ShowWindowBeforeForceScheduledStart;
            CustomTimerConfig = snapshot.CustomTimerConfig;

            var byIndex = snapshot.Slots.ToDictionary(static s => s.Index);
            foreach (var slot in Timers)
            {
                if (!byIndex.TryGetValue(slot.Index, out var source))
                {
                    continue;
                }

                slot.Enabled = source.Enabled;
                slot.Time = source.Time;
                slot.Profile = source.Profile;
            }
        }
        finally
        {
            _suppressTimerDirtyTracking = false;
        }
    }

    private static string BuildTimerEnabledKey(int index) => $"Timer.Timer{index}";

    private static string BuildTimerHourKey(int index) => $"Timer.Timer{index}Hour";

    private static string BuildTimerMinuteKey(int index) => $"Timer.Timer{index}Min";

    private static string BuildTimerProfileKey(int index) => $"Timer.Timer{index}.Config";

    private static string NormalizeTimerTime(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return TryParseTimerTime(trimmed, out var hour, out var minute)
            ? FormatTimerTime(hour, minute)
            : trimmed;
    }

    private static string NormalizeTimerProfile(string? value, string fallback)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized)
            ? fallback
            : normalized;
    }

    private static bool TryParseTimerTime(string? value, out int hour, out int minute)
    {
        hour = default;
        minute = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.Length != 5 || normalized[2] != ':')
        {
            return false;
        }

        if (!int.TryParse(normalized.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out hour))
        {
            return false;
        }

        if (!int.TryParse(normalized.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out minute))
        {
            return false;
        }

        if (hour < TimerHourMin || hour > TimerHourMax)
        {
            return false;
        }

        if (minute < TimerMinuteMin || minute > TimerMinuteMax)
        {
            return false;
        }

        return true;
    }

    private static string FormatTimerTime(int hour, int minute)
    {
        return FormattableString.Invariant($"{hour:00}:{minute:00}");
    }

    private static bool ReadProfileBoolFlexible(UnifiedConfig config, string key, bool fallback)
        => ReadBoolFlexible(config, key, fallback, ConfigValuePreference.ProfileFirst);

    private static bool ReadGlobalBoolFlexible(UnifiedConfig config, string key, bool fallback)
        => ReadBoolFlexible(config, key, fallback, ConfigValuePreference.GlobalFirst);

    private static bool ReadBoolFlexible(
        UnifiedConfig config,
        string key,
        bool fallback,
        ConfigValuePreference preference)
    {
        if (!TryGetConfigNode(config, key, preference, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool parsedBool))
            {
                return parsedBool;
            }

            if (value.TryGetValue(out int parsedInt))
            {
                return parsedInt != 0;
            }

            if (value.TryGetValue(out string? text))
            {
                if (bool.TryParse(text, out var parsedText))
                {
                    return parsedText;
                }

                if (int.TryParse(text, out var parsedIntText))
                {
                    return parsedIntText != 0;
                }
            }
        }

        if (bool.TryParse(node.ToString(), out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static int ReadProfileIntFlexible(UnifiedConfig config, string key, int fallback, out bool parsed)
        => ReadIntFlexible(config, key, fallback, ConfigValuePreference.ProfileFirst, out parsed);

    private static int ReadGlobalIntFlexible(UnifiedConfig config, string key, int fallback, out bool parsed)
        => ReadIntFlexible(config, key, fallback, ConfigValuePreference.GlobalFirst, out parsed);

    private static int ReadIntFlexible(
        UnifiedConfig config,
        string key,
        int fallback,
        ConfigValuePreference preference,
        out bool parsed)
    {
        parsed = false;
        if (!TryGetConfigNode(config, key, preference, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out int parsedInt))
            {
                parsed = true;
                return parsedInt;
            }

            if (value.TryGetValue(out bool parsedBool))
            {
                parsed = true;
                return parsedBool ? 1 : 0;
            }

            if (value.TryGetValue(out string? text)
                && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTextInt))
            {
                parsed = true;
                return parsedTextInt;
            }
        }

        if (int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNode))
        {
            parsed = true;
            return parsedNode;
        }

        return fallback;
    }

    private static bool HasConfigKey(UnifiedConfig config, string key, ConfigValuePreference preference)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return preference == ConfigValuePreference.ProfileFirst
            ? HasProfileValue(config, key) || config.GlobalValues.ContainsKey(key)
            : config.GlobalValues.ContainsKey(key) || HasProfileValue(config, key);
    }

    private static bool TryGetConfigNode(
        UnifiedConfig config,
        string key,
        ConfigValuePreference preference,
        out JsonNode? node)
    {
        if (preference == ConfigValuePreference.ProfileFirst)
        {
            if (TryGetProfileValue(config, key, out node))
            {
                return true;
            }

            if (config.GlobalValues.TryGetValue(key, out node) && node is not null)
            {
                return true;
            }
        }
        else
        {
            if (config.GlobalValues.TryGetValue(key, out node) && node is not null)
            {
                return true;
            }

            if (TryGetProfileValue(config, key, out node))
            {
                return true;
            }
        }

        node = null;
        return false;
    }

    private static bool HasProfileValue(UnifiedConfig config, string key)
    {
        return !string.IsNullOrWhiteSpace(config.CurrentProfile)
               && config.Profiles.TryGetValue(config.CurrentProfile, out var profile)
               && profile.Values.ContainsKey(key);
    }

    private static bool TryGetProfileValue(UnifiedConfig config, string key, out JsonNode? node)
    {
        if (!string.IsNullOrWhiteSpace(config.CurrentProfile)
            && config.Profiles.TryGetValue(config.CurrentProfile, out var profile)
            && profile.Values.TryGetValue(key, out node)
            && node is not null)
        {
            return true;
        }

        node = null;
        return false;
    }
}

internal enum ConfigValuePreference
{
    GlobalFirst = 0,
    ProfileFirst = 1,
}

public sealed record GuiSettingsSnapshot(
    string Theme,
    bool UseTray,
    bool UseNotify,
    bool MinimizeToTray,
    bool WindowTitleScrollable,
    int UiScalePercent,
    bool UseSoftwareRendering,
    string LogItemDateFormatString,
    string OperNameLanguage,
    string InverseClearMode,
    string BackgroundImagePath,
    int BackgroundOpacity,
    int BackgroundBlur,
    string BackgroundStretchMode,
    bool DeveloperModeEnabled = false)
{
    public IReadOnlyDictionary<string, string> ToGlobalSettingUpdates()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Theme.Mode"] = Theme,
            [ConfigurationKeys.UseTray] = UseTray.ToString(),
            [ConfigurationKeys.UseNotify] = UseNotify.ToString(),
            [ConfigurationKeys.MinimizeToTray] = MinimizeToTray.ToString(),
            [ConfigurationKeys.WindowTitleScrollable] = WindowTitleScrollable.ToString(),
            [ConfigurationKeys.UiScalePercent] = UiScalePercent.ToString(CultureInfo.InvariantCulture),
            [ConfigurationKeys.IgnoreBadModulesAndUseSoftwareRendering] = UseSoftwareRendering.ToString(),
            ["GUI.DeveloperMode"] = DeveloperModeEnabled.ToString(),
            [ConfigurationKeys.LogItemDateFormat] = LogItemDateFormatString,
            [ConfigurationKeys.OperNameLanguage] = OperNameLanguage,
            [ConfigurationKeys.BackgroundImagePath] = BackgroundImagePath,
            [ConfigurationKeys.BackgroundOpacity] = BackgroundOpacity.ToString(),
            [ConfigurationKeys.BackgroundBlurEffectRadius] = BackgroundBlur.ToString(),
            [ConfigurationKeys.BackgroundImageStretchMode] = BackgroundStretchMode,
        };
    }

    public IReadOnlyDictionary<string, string> ToProfileSettingUpdates()
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConfigurationKeys.InverseClearMode] = InverseClearMode,
        };

        if (string.Equals(InverseClearMode, "Clear", StringComparison.OrdinalIgnoreCase))
        {
            updates[ConfigurationKeys.MainFunctionInverseMode] = false.ToString();
        }
        else if (string.Equals(InverseClearMode, "Inverse", StringComparison.OrdinalIgnoreCase))
        {
            updates[ConfigurationKeys.MainFunctionInverseMode] = true.ToString();
        }

        return updates;
    }
}

public sealed record StartPerformanceSettingsSnapshot(
    bool RunDirectly,
    bool MinimizeDirectly,
    bool OpenEmulatorAfterLaunch,
    string EmulatorPath,
    string EmulatorAddCommand,
    int EmulatorWaitSeconds,
    bool PerformanceUseGpu,
    bool PerformanceAllowDeprecatedGpu,
    string PerformancePreferredGpuDescription,
    string PerformancePreferredGpuInstancePath,
    bool DeploymentWithPause,
    string StartsWithScript,
    string EndsWithScript,
    bool CopilotWithScript,
    bool ManualStopWithScript,
    bool BlockSleep,
    bool BlockSleepWithScreenOn,
    bool EnablePenguin,
    bool EnableYituliu,
    string PenguinId,
    int TaskTimeoutMinutes,
    int ReminderIntervalMinutes)
{
    public IReadOnlyDictionary<string, string> ToGlobalSettingUpdates()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConfigurationKeys.MinimizeDirectly] = MinimizeDirectly.ToString(),
        };
    }

    public IReadOnlyDictionary<string, string> ToProfileSettingUpdates(bool includeGpuSettings = true)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConfigurationKeys.RunDirectly] = RunDirectly.ToString(),
            [ConfigurationKeys.StartEmulator] = OpenEmulatorAfterLaunch.ToString(),
            [ConfigurationKeys.EmulatorPath] = EmulatorPath,
            [ConfigurationKeys.EmulatorAddCommand] = EmulatorAddCommand,
            [ConfigurationKeys.EmulatorWaitSeconds] = EmulatorWaitSeconds.ToString(),
            [ConfigurationKeys.RoguelikeDeploymentWithPause] = DeploymentWithPause.ToString(),
            [ConfigurationKeys.StartsWithScript] = StartsWithScript,
            [ConfigurationKeys.EndsWithScript] = EndsWithScript,
            [ConfigurationKeys.CopilotWithScript] = CopilotWithScript.ToString(),
            [ConfigurationKeys.ManualStopWithScript] = ManualStopWithScript.ToString(),
            [ConfigurationKeys.BlockSleep] = BlockSleep.ToString(),
            [ConfigurationKeys.BlockSleepWithScreenOn] = BlockSleepWithScreenOn.ToString(),
            [ConfigurationKeys.EnablePenguin] = EnablePenguin.ToString(),
            [ConfigurationKeys.EnableYituliu] = EnableYituliu.ToString(),
            [ConfigurationKeys.PenguinId] = PenguinId,
            [ConfigurationKeys.TaskTimeoutMinutes] = TaskTimeoutMinutes.ToString(),
            [ConfigurationKeys.ReminderIntervalMinutes] = ReminderIntervalMinutes.ToString(),
        };

        if (includeGpuSettings)
        {
            updates[ConfigurationKeys.PerformanceUseGpu] = PerformanceUseGpu.ToString();
            updates[ConfigurationKeys.PerformanceAllowDeprecatedGpu] = PerformanceAllowDeprecatedGpu.ToString();
            updates[ConfigurationKeys.PerformancePreferredGpuDescription] = PerformancePreferredGpuDescription;
            updates[ConfigurationKeys.PerformancePreferredGpuInstancePath] = PerformancePreferredGpuInstancePath;
        }

        return updates;
    }
}

public sealed record GpuOptionDisplayItem(
    GpuOptionDescriptor Descriptor,
    string Display);

public sealed class DisplayValueOption : IEquatable<DisplayValueOption>
{
    public DisplayValueOption(string display, string value)
    {
        Display = display;
        Value = value;
    }

    public string Display { get; }

    public string Value { get; }

    public bool Equals(DisplayValueOption? other)
    {
        return other is not null
               && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj)
    {
        return obj is DisplayValueOption other && Equals(other);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Value ?? string.Empty);
    }
}

public sealed record TimerSlotSettingsSnapshot(
    int Index,
    bool Enabled,
    string Time,
    string Profile);

public sealed record TimerSettingsSnapshot(
    bool ForceScheduledStart,
    bool ShowWindowBeforeForceScheduledStart,
    bool CustomTimerConfig,
    IReadOnlyList<TimerSlotSettingsSnapshot> Slots)
{
    public IReadOnlyDictionary<string, string> ToGlobalSettingUpdates()
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        updates[LegacyConfigurationKeys.ForceScheduledStart] = ForceScheduledStart.ToString();
        updates[LegacyConfigurationKeys.ShowWindowBeforeForceScheduledStart] = ShowWindowBeforeForceScheduledStart.ToString();
        updates[LegacyConfigurationKeys.CustomConfig] = CustomTimerConfig.ToString();

        foreach (var slot in Slots.OrderBy(static s => s.Index))
        {
            var index = Math.Clamp(slot.Index, 1, 8);
            updates[$"Timer.Timer{index}"] = slot.Enabled.ToString();

            var hour = 7;
            var minute = 0;
            if (!string.IsNullOrWhiteSpace(slot.Time))
            {
                var split = slot.Time.Split(':');
                if (split.Length == 2)
                {
                    if (int.TryParse(split[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHour))
                    {
                        hour = parsedHour;
                    }

                    if (int.TryParse(split[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMinute))
                    {
                        minute = parsedMinute;
                    }
                }
            }

            hour = Math.Clamp(hour, 0, 23);
            minute = Math.Clamp(minute, 0, 59);

            updates[$"Timer.Timer{index}Hour"] = hour.ToString(CultureInfo.InvariantCulture);
            updates[$"Timer.Timer{index}Min"] = minute.ToString(CultureInfo.InvariantCulture);
            updates[$"Timer.Timer{index}.Config"] = slot.Profile ?? string.Empty;
        }

        return updates;
    }
}

public sealed class GuiSettingsAppliedEventArgs : EventArgs
{
    public GuiSettingsAppliedEventArgs(GuiSettingsSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public GuiSettingsSnapshot Snapshot { get; }
}

public sealed class GuiSettingsPreviewChangedEventArgs : EventArgs
{
    public GuiSettingsPreviewChangedEventArgs(GuiSettingsSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public GuiSettingsSnapshot Snapshot { get; }
}

public sealed class ConfigurationContextChangedEventArgs : EventArgs
{
    public ConfigurationContextChangedEventArgs(
        ConfigurationContextChangeReason reason,
        string message,
        ImportReport? report)
    {
        Reason = reason;
        Message = message;
        Report = report;
    }

    public ConfigurationContextChangeReason Reason { get; }

    public string Message { get; }

    public ImportReport? Report { get; }
}

public enum ConfigurationContextChangeReason
{
    ProfileSwitched = 0,
    LegacyImport = 1,
    UnifiedImport = 2,
}

public enum HotkeyRegistrationSource
{
    Manual = 0,
    Startup = 1,
}

public sealed class TimerSlotViewModel : ObservableObject
{
    private const int DefaultHour = 7;
    private const int DefaultMinute = 0;
    private const int HourMin = 0;
    private const int HourMax = 23;
    private const int MinuteMin = 0;
    private const int MinuteMax = 59;

    private bool _enabled;
    private string _time = "07:00";
    private string _profile = "Default";

    public TimerSlotViewModel(int index)
    {
        Index = index;
    }

    public int Index { get; }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string Time
    {
        get => _time;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!SetProperty(ref _time, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(Hour));
            OnPropertyChanged(nameof(Minute));
        }
    }

    public int Hour
    {
        get => TryParseTime(Time, out var hour, out _) ? hour : DefaultHour;
        set => UpdateTime(value, Minute);
    }

    public int Minute
    {
        get => TryParseTime(Time, out _, out var minute) ? minute : DefaultMinute;
        set => UpdateTime(Hour, value);
    }

    public string Profile
    {
        get => _profile;
        set => SetProperty(ref _profile, value?.Trim() ?? string.Empty);
    }

    private void UpdateTime(int hour, int minute)
    {
        var normalizedHour = Math.Clamp(hour, HourMin, HourMax);
        var normalizedMinute = Math.Clamp(minute, MinuteMin, MinuteMax);
        Time = FormattableString.Invariant($"{normalizedHour:00}:{normalizedMinute:00}");
    }

    private static bool TryParseTime(string? value, out int hour, out int minute)
    {
        hour = default;
        minute = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.Length != 5 || normalized[2] != ':')
        {
            return false;
        }

        if (!int.TryParse(normalized.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out hour))
        {
            return false;
        }

        if (!int.TryParse(normalized.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out minute))
        {
            return false;
        }

        if (hour < HourMin || hour > HourMax)
        {
            return false;
        }

        if (minute < MinuteMin || minute > MinuteMax)
        {
            return false;
        }

        return true;
    }
}
