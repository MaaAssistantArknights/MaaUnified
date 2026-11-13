using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Application.Services;
using MAAUnified.CoreBridge;

namespace MAAUnified.App.ViewModels.Settings;

public enum TestLinkInfoSeverity
{
    Normal,
    Warning,
    Error,
}

public sealed class ConnectionGameSharedStateViewModel : ObservableObject
{
    private const int MaxConnectAddressHistory = 5;
    private const string DefaultAttachWindowScreencapMethod = "2";
    private const string DefaultAttachWindowInputMethod = "64";
    private static readonly string[] MuMuExternalRendererCandidates =
    [
        Path.Combine("nx_device", "12.0", "shell", "sdk", "external_renderer_ipc.dll"),
        Path.Combine("nx_device", "15.0", "shell", "sdk", "external_renderer_ipc.dll"),
        Path.Combine("shell", "sdk", "external_renderer_ipc.dll"),
    ];

    private const string LdPlayerOpenGlLibrary = "ldopengl64.dll";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _defaultAddressByConnectConfig =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["General"] = [string.Empty],
            ["BlueStacks"] = ["127.0.0.1:5555", "127.0.0.1:5556", "127.0.0.1:5565", "127.0.0.1:5575", "127.0.0.1:5585", "127.0.0.1:5595", "127.0.0.1:5554"],
            ["MuMuEmulator12"] = ["127.0.0.1:16384", "127.0.0.1:16416", "127.0.0.1:16448", "127.0.0.1:16480", "127.0.0.1:16512", "127.0.0.1:16544", "127.0.0.1:16576"],
            ["LDPlayer"] = ["emulator-5554", "emulator-5556", "emulator-5558", "emulator-5560", "127.0.0.1:5555", "127.0.0.1:5557", "127.0.0.1:5559", "127.0.0.1:5561"],
            ["AVD"] = ["emulator-5554", "emulator-5556"],
            ["Nox"] = ["127.0.0.1:62001", "127.0.0.1:59865"],
            ["XYAZ"] = ["127.0.0.1:21503"],
            ["WSA"] = ["127.0.0.1:58526"],
        };

    private string _language = UiLanguageCatalog.DefaultLanguage;
    // WPF parity (ConnectSettingsUserControlModel): both default to string.Empty;
    // AutoDetect (runtime discovery / default address table) refills them at connect time.
    private string _connectAddress = string.Empty;
    private string _connectConfig = "General";
    private string _adbPath = string.Empty;
    private string _clientType = "Official";
    private bool _startGameEnabled = true;
    private string _touchMode = "minitouch";
    private bool _autoDetect = true;
    private bool _alwaysAutoDetect;
    private bool _retryOnDisconnected;
    private bool _allowAdbRestart = true;
    private bool _allowAdbHardRestart = true;
    private bool _adbLiteEnabled;
    private bool _killAdbOnExit;
    private bool _adbReplaced;
    private bool _macUseBundledAdb = true;
    private bool _muMu12ExtrasEnabled;
    private string _muMu12EmulatorPath = string.Empty;
    private bool _muMuBridgeConnection;
    private string _muMu12Index = "0";
    private bool _ldPlayerExtrasEnabled;
    private string _ldPlayerEmulatorPath = string.Empty;
    private bool _ldPlayerManualSetIndex;
    private string _ldPlayerIndex = "0";
    private string _attachWindowScreencapMethod = DefaultAttachWindowScreencapMethod;
    private string _attachWindowMouseMethod = DefaultAttachWindowInputMethod;
    private string _attachWindowKeyboardMethod = DefaultAttachWindowInputMethod;
    private string _testLinkInfo = string.Empty;
    private LocalizedTestLinkInfo? _testLinkInfoLocalization;
    private TestLinkInfoSeverity _testLinkInfoSeverity;
    private readonly object _sessionOperationGate = new();
    private IDisposable? _taskStartRequestCompatibilityLease;
    private IDisposable? _screenshotTestCompatibilityLease;
    private IDisposable? _exclusiveSessionOperationLease;
    private int _sessionOperationCount;
    private string _screencapCost = string.Empty;
    private string _fastestScreencapMethod = string.Empty;
    private long? _lastScreencapCostMin;
    private long? _lastScreencapCostAvg;
    private long? _lastScreencapCostMax;
    private DateTimeOffset? _lastScreencapCostTimestamp;
    private IReadOnlyList<ConnectionGameOptionItem> _connectConfigOptions = [];
    private IReadOnlyList<ConnectionGameOptionItem> _clientTypeOptions = [];
    private IReadOnlyList<ConnectionGameOptionItem> _touchModeOptions = [];
    private IReadOnlyList<ConnectionGameOptionItem> _attachWindowScreencapOptions = [];
    private IReadOnlyList<ConnectionGameOptionItem> _attachWindowInputOptions = [];
    private readonly ObservableCollection<string> _connectAddressHistory = [];

    public ConnectionGameSharedStateViewModel()
    {
        RootTexts.Language = _language;
        RebuildOptions();
        ScreencapCost = BuildCurrentScreencapCostText();
        UpdateConnectAddressHistory(_connectAddress);
    }

    public RootLocalizationTextMap RootTexts { get; } = new("Root.Localization.Settings");

    public IReadOnlyList<ConnectionGameOptionItem> ConnectConfigOptions
    {
        get => _connectConfigOptions;
        private set => SetProperty(ref _connectConfigOptions, value);
    }

    public IReadOnlyList<ConnectionGameOptionItem> ClientTypeOptions
    {
        get => _clientTypeOptions;
        private set => SetProperty(ref _clientTypeOptions, value);
    }

    public IReadOnlyList<ConnectionGameOptionItem> TouchModeOptions
    {
        get => _touchModeOptions;
        private set => SetProperty(ref _touchModeOptions, value);
    }

    public IReadOnlyList<ConnectionGameOptionItem> AttachWindowScreencapOptions
    {
        get => _attachWindowScreencapOptions;
        private set => SetProperty(ref _attachWindowScreencapOptions, value);
    }

    public IReadOnlyList<ConnectionGameOptionItem> AttachWindowInputOptions
    {
        get => _attachWindowInputOptions;
        private set => SetProperty(ref _attachWindowInputOptions, value);
    }

    public ObservableCollection<string> ConnectAddressHistory => _connectAddressHistory;

    public ConnectionGameOptionItem? SelectedConnectConfigOption
    {
        get => ResolveSelectedOption(ConnectConfigOptions, ConnectConfig, NormalizeConnectConfigAlias);
        set
        {
            if (value is null)
            {
                return;
            }

            ConnectConfig = value.Value;
        }
    }

    public string SelectedConnectConfigValue
    {
        get => NormalizeConnectConfigAlias(ConnectConfig);
        set => ConnectConfig = value ?? string.Empty;
    }

    public ConnectionGameOptionItem? SelectedClientTypeOption
    {
        get => ResolveSelectedOption(ClientTypeOptions, ClientType, NormalizeClientTypeAlias);
        set
        {
            if (value is null)
            {
                return;
            }

            ClientType = value.Value;
        }
    }

    public string SelectedClientTypeValue
    {
        get => NormalizeClientTypeAlias(ClientType);
        set => ClientType = value ?? string.Empty;
    }

    public ConnectionGameOptionItem? SelectedTouchModeOption
    {
        get => ResolveSelectedOption(TouchModeOptions, TouchMode, NormalizeTouchModeAlias);
        set
        {
            if (value is null)
            {
                return;
            }

            TouchMode = value.Value;
        }
    }

    public string SelectedTouchModeValue
    {
        get => NormalizeTouchModeAlias(TouchMode);
        set => TouchMode = value ?? string.Empty;
    }

    public ConnectionGameOptionItem? SelectedAttachWindowScreencapOption
    {
        get => ResolveSelectedOption(AttachWindowScreencapOptions, AttachWindowScreencapMethod, NormalizeTouchModeAlias);
        set
        {
            if (value is null)
            {
                return;
            }

            AttachWindowScreencapMethod = value.Value;
        }
    }

    public string SelectedAttachWindowScreencapValue
    {
        get => NormalizeTouchModeAlias(AttachWindowScreencapMethod);
        set => AttachWindowScreencapMethod = value ?? string.Empty;
    }

    public ConnectionGameOptionItem? SelectedAttachWindowMouseOption
    {
        get => ResolveSelectedOption(AttachWindowInputOptions, AttachWindowMouseMethod, NormalizeTouchModeAlias);
        set
        {
            if (value is null)
            {
                return;
            }

            AttachWindowMouseMethod = value.Value;
        }
    }

    public string SelectedAttachWindowMouseValue
    {
        get => NormalizeTouchModeAlias(AttachWindowMouseMethod);
        set => AttachWindowMouseMethod = value ?? string.Empty;
    }

    public ConnectionGameOptionItem? SelectedAttachWindowKeyboardOption
    {
        get => ResolveSelectedOption(AttachWindowInputOptions, AttachWindowKeyboardMethod, NormalizeTouchModeAlias);
        set
        {
            if (value is null)
            {
                return;
            }

            AttachWindowKeyboardMethod = value.Value;
        }
    }

    public string SelectedAttachWindowKeyboardValue
    {
        get => NormalizeTouchModeAlias(AttachWindowKeyboardMethod);
        set => AttachWindowKeyboardMethod = value ?? string.Empty;
    }

    public void SetLanguage(string? language)
    {
        var normalized = UiLanguageCatalog.Normalize(language);
        if (string.Equals(_language, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _language = normalized;
        RootTexts.Language = normalized;
        OnPropertyChanged(nameof(RootTexts));
        OnPropertyChanged(nameof(MacUseBundledAdbText));
        RebuildOptions();
        RefreshLocalizedConnectTexts();
    }

    public string ConnectAddress
    {
        get => _connectAddress;
        set
        {
            var normalized = (value ?? string.Empty)
                .Replace("：", ":", StringComparison.Ordinal)
                .Replace("；", ":", StringComparison.Ordinal)
                .Replace(";", ":", StringComparison.Ordinal)
                .Trim();
            if (!SetProperty(ref _connectAddress, normalized))
            {
                return;
            }

            UpdateConnectAddressHistory(normalized);
        }
    }

    public string ConnectConfig
    {
        get => _connectConfig;
        set
        {
            var normalized = (value ?? string.Empty).Trim();
            if (!SetProperty(ref _connectConfig, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(CanStartGameEnabled));
            OnPropertyChanged(nameof(SelectedConnectConfigOption));
            OnPropertyChanged(nameof(SelectedConnectConfigValue));
            OnPropertyChanged(nameof(IsAttachWindowMode));
            OnPropertyChanged(nameof(IsAdbConnectionMode));
            OnPropertyChanged(nameof(IsMuMuEmulator12Mode));
            OnPropertyChanged(nameof(IsLdPlayerMode));
            OnPropertyChanged(nameof(ShowMuMuExtrasSection));
            OnPropertyChanged(nameof(ShowLdPlayerExtrasSection));
            OnPropertyChanged(nameof(ShowEmulatorExtrasSection));
            OnPropertyChanged(nameof(CanEditAdbConnectionFields));
            // WPF parity: switching ConnectConfig only re-evaluates the StartGame read mask
            // (getter returns false while AttachWindow/PC); the stored value is preserved.
            OnPropertyChanged(nameof(StartGameEnabled));

            if (ShowMuMuExtrasSection && MuMu12ExtrasEnabled)
            {
                AutoDetectMuMu12EmulatorPathIfNeeded();
            }

            if (ShowLdPlayerExtrasSection && LdPlayerExtrasEnabled)
            {
                AutoDetectLdPlayerEmulatorPathIfNeeded();
            }
        }
    }

    public string AdbPath
    {
        get => _adbPath;
        set
        {
            var normalized = (value ?? string.Empty).Trim();
            SetProperty(ref _adbPath, normalized);
        }
    }

    public string ClientType
    {
        get => _clientType;
        set
        {
            var normalized = (value ?? string.Empty).Trim();
            if (SetProperty(ref _clientType, normalized))
            {
                OnPropertyChanged(nameof(SelectedClientTypeOption));
                OnPropertyChanged(nameof(SelectedClientTypeValue));
                OnPropertyChanged(nameof(IsYoStarEnClientType));
                OnPropertyChanged(nameof(ShowOverseasClientHint));
            }
        }
    }

    public bool StartGameEnabled
    {
        // WPF parity (GameSettingsUserControlModel.StartGame): the getter is masked while
        // AttachWindow (PC) mode is active, but the stored value is never rewritten.
        get => IsAttachWindowMode ? false : _startGameEnabled;
        set => SetProperty(ref _startGameEnabled, value);
    }

    /// <summary>
    /// Gets the persisted StartGame value without the AttachWindow (PC) read mask.
    /// Profile persistence must use this value so a PC round-trip does not erase it.
    /// </summary>
    public bool StoredStartGameEnabled => _startGameEnabled;

    public bool CanStartGameEnabled => !string.Equals(ConnectConfig, "PC", StringComparison.OrdinalIgnoreCase);

    public bool CanEditConnectionFields => !AutoDetect;

    public bool CanEditAdbConnectionFields => !AutoDetect && !IsAttachWindowMode;

    public bool IsAttachWindowMode => string.Equals(ConnectConfig, "PC", StringComparison.OrdinalIgnoreCase);

    public bool IsAdbConnectionMode => !IsAttachWindowMode;

    public bool IsMacBundledAdbSupported => MacBundledAdbPolicy.IsSupportedPlatform;

    public string MacUseBundledAdbText => UiLanguageCatalog.Normalize(_language) switch
    {
        "zh-cn" => "使用内置 ADB",
        "zh-tw" => "使用內建 ADB",
        "ja-jp" => "内蔵 ADB を使用",
        "ko-kr" => "번들 ADB 사용",
        _ => "Use bundled ADB",
    };

    public bool MacUseBundledAdb
    {
        get => _macUseBundledAdb;
        set
        {
            if (SetProperty(ref _macUseBundledAdb, value))
            {
                OnPropertyChanged(nameof(UseMacBundledAdbEffective));
                OnPropertyChanged(nameof(ShowManualAdbPathControls));
            }
        }
    }

    public bool UseMacBundledAdbEffective => MacBundledAdbPolicy.ShouldUseBundledAdb(MacUseBundledAdb);

    public bool ShowManualAdbPathControls => !UseMacBundledAdbEffective;

    public bool IsMuMuEmulator12Mode => string.Equals(ConnectConfig, "MuMuEmulator12", StringComparison.OrdinalIgnoreCase);

    public bool IsLdPlayerMode => string.Equals(ConnectConfig, "LDPlayer", StringComparison.OrdinalIgnoreCase);

    public bool ShowMuMuExtrasSection => IsAdbConnectionMode && IsMuMuEmulator12Mode;

    public bool ShowLdPlayerExtrasSection => IsAdbConnectionMode && IsLdPlayerMode;

    public bool ShowEmulatorExtrasSection => ShowMuMuExtrasSection || ShowLdPlayerExtrasSection;

    public bool IsYoStarEnClientType => string.Equals(ClientType, "YoStarEN", StringComparison.OrdinalIgnoreCase);

    public bool ShowOverseasClientHint =>
        !string.IsNullOrWhiteSpace(ClientType)
        && !string.Equals(ClientType, "Official", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(ClientType, "Bilibili", StringComparison.OrdinalIgnoreCase);

    public string TouchMode
    {
        get => _touchMode;
        set
        {
            var normalized = (value ?? string.Empty).Trim();
            if (SetProperty(ref _touchMode, normalized))
            {
                OnPropertyChanged(nameof(SelectedTouchModeOption));
                OnPropertyChanged(nameof(SelectedTouchModeValue));
            }
        }
    }

    public bool AutoDetect
    {
        get => _autoDetect;
        set
        {
            if (SetProperty(ref _autoDetect, value))
            {
                OnPropertyChanged(nameof(CanEditConnectionFields));
                OnPropertyChanged(nameof(CanEditAdbConnectionFields));
            }
        }
    }

    public bool AlwaysAutoDetect
    {
        get => _alwaysAutoDetect;
        set => SetProperty(ref _alwaysAutoDetect, value);
    }

    public bool RetryOnDisconnected
    {
        get => _retryOnDisconnected;
        set => SetProperty(ref _retryOnDisconnected, value);
    }

    public bool AllowAdbRestart
    {
        get => _allowAdbRestart;
        set => SetProperty(ref _allowAdbRestart, value);
    }

    public bool AllowAdbHardRestart
    {
        get => _allowAdbHardRestart;
        set => SetProperty(ref _allowAdbHardRestart, value);
    }

    public bool AdbLiteEnabled
    {
        get => _adbLiteEnabled;
        set => SetProperty(ref _adbLiteEnabled, value);
    }

    public bool KillAdbOnExit
    {
        get => _killAdbOnExit;
        set => SetProperty(ref _killAdbOnExit, value);
    }

    public bool AdbReplaced
    {
        get => _adbReplaced;
        set => SetProperty(ref _adbReplaced, value);
    }

    public bool MuMu12ExtrasEnabled
    {
        get => _muMu12ExtrasEnabled;
        set
        {
            if (!SetProperty(ref _muMu12ExtrasEnabled, value))
            {
                return;
            }

            if (!value)
            {
                return;
            }

            AutoDetectMuMu12EmulatorPathIfNeeded();
            _ = ValidateMuMu12EmulatorPath(out _);
        }
    }

    public string MuMu12EmulatorPath
    {
        get => _muMu12EmulatorPath;
        set
        {
            var normalized = (value ?? string.Empty).Trim();
            if (ShowMuMuExtrasSection
                && MuMu12ExtrasEnabled
                && FindMuMu12PathValidationFailure(normalized) is { } failure)
            {
                ShowPathValidationFailure(failure);
                return;
            }

            if (SetProperty(ref _muMu12EmulatorPath, normalized))
            {
                ClearTestLinkInfo();
            }
        }
    }

    public bool MuMuBridgeConnection
    {
        get => _muMuBridgeConnection;
        set => SetProperty(ref _muMuBridgeConnection, value);
    }

    public string MuMu12Index
    {
        get => _muMu12Index;
        set => SetProperty(ref _muMu12Index, (value ?? string.Empty).Trim());
    }

    public bool LdPlayerExtrasEnabled
    {
        get => _ldPlayerExtrasEnabled;
        set
        {
            if (!SetProperty(ref _ldPlayerExtrasEnabled, value))
            {
                return;
            }

            if (!value)
            {
                return;
            }

            AutoDetectLdPlayerEmulatorPathIfNeeded();
            _ = ValidateLdPlayerEmulatorPath(out _);
        }
    }

    public string LdPlayerEmulatorPath
    {
        get => _ldPlayerEmulatorPath;
        set
        {
            var normalized = (value ?? string.Empty).Trim();
            if (ShowLdPlayerExtrasSection
                && LdPlayerExtrasEnabled
                && FindLdPlayerPathValidationFailure(normalized) is { } failure)
            {
                ShowPathValidationFailure(failure);
                return;
            }

            if (SetProperty(ref _ldPlayerEmulatorPath, normalized))
            {
                ClearTestLinkInfo();
            }
        }
    }

    public bool LdPlayerManualSetIndex
    {
        get => _ldPlayerManualSetIndex;
        set => SetProperty(ref _ldPlayerManualSetIndex, value);
    }

    public string LdPlayerIndex
    {
        get => _ldPlayerIndex;
        set => SetProperty(ref _ldPlayerIndex, (value ?? string.Empty).Trim());
    }

    public string AttachWindowScreencapMethod
    {
        get => _attachWindowScreencapMethod;
        set
        {
            var normalized = (value ?? string.Empty).Trim();
            if (SetProperty(ref _attachWindowScreencapMethod, normalized))
            {
                OnPropertyChanged(nameof(SelectedAttachWindowScreencapOption));
                OnPropertyChanged(nameof(SelectedAttachWindowScreencapValue));
            }
        }
    }

    public string AttachWindowMouseMethod
    {
        get => _attachWindowMouseMethod;
        set
        {
            var normalized = (value ?? string.Empty).Trim();
            if (SetProperty(ref _attachWindowMouseMethod, normalized))
            {
                OnPropertyChanged(nameof(SelectedAttachWindowMouseOption));
                OnPropertyChanged(nameof(SelectedAttachWindowMouseValue));
            }
        }
    }

    public string AttachWindowKeyboardMethod
    {
        get => _attachWindowKeyboardMethod;
        set
        {
            var normalized = (value ?? string.Empty).Trim();
            if (SetProperty(ref _attachWindowKeyboardMethod, normalized))
            {
                OnPropertyChanged(nameof(SelectedAttachWindowKeyboardOption));
                OnPropertyChanged(nameof(SelectedAttachWindowKeyboardValue));
            }
        }
    }

    public string TestLinkInfo
    {
        get => _testLinkInfo;
        set
        {
            _testLinkInfoLocalization = null;
            ApplyTestLinkInfo(value);
        }
    }

    public void SetLocalizedTestLinkInfo(
        string key,
        string chineseFallback,
        string englishFallback,
        params object[] args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _testLinkInfoLocalization = new LocalizedTestLinkInfo(
            key,
            chineseFallback,
            englishFallback,
            args ?? []);
        ApplyTestLinkInfo(BuildLocalizedMessage(key, chineseFallback, englishFallback, _testLinkInfoLocalization.Args));
    }

    public void ClearTestLinkInfo()
    {
        _testLinkInfoLocalization = null;
        ApplyTestLinkInfo(string.Empty);
    }

    public bool IsTaskStartRequestActive
    {
        get => Volatile.Read(ref _taskStartRequestCompatibilityLease) is not null;
        set
        {
            if (value)
            {
                TryBeginTaskStartRequest();
            }
            else
            {
                EndTaskStartRequest();
            }
        }
    }

    public bool TryBeginTaskStartRequest()
    {
        if (Volatile.Read(ref _taskStartRequestCompatibilityLease) is not null)
        {
            return false;
        }

        var lease = TryBeginSessionOperation("TaskQueue.LinkStart", requireIdle: true);
        if (lease is null)
        {
            return false;
        }

        var previous = Interlocked.CompareExchange(
            ref _taskStartRequestCompatibilityLease,
            lease,
            null);
        if (previous is not null)
        {
            lease.Dispose();
            return false;
        }

        OnPropertyChanged(nameof(IsTaskStartRequestActive));
        return true;
    }

    public void EndTaskStartRequest()
    {
        var lease = Interlocked.Exchange(ref _taskStartRequestCompatibilityLease, null);
        if (lease is null)
        {
            return;
        }

        lease.Dispose();
        OnPropertyChanged(nameof(IsTaskStartRequestActive));
    }

    public bool IsScreenshotTestActive
    {
        get => Volatile.Read(ref _screenshotTestCompatibilityLease) is not null;
        set
        {
            if (value)
            {
                TryBeginScreenshotTestActivity();
            }
            else
            {
                EndScreenshotTestActivity();
            }
        }
    }

    public bool TryBeginScreenshotTestActivity()
    {
        if (Volatile.Read(ref _screenshotTestCompatibilityLease) is not null)
        {
            return false;
        }

        var lease = TryBeginSessionOperation("Settings.Connect.ScreenshotTest", requireIdle: true);
        if (lease is null)
        {
            return false;
        }

        var previous = Interlocked.CompareExchange(
            ref _screenshotTestCompatibilityLease,
            lease,
            null);
        if (previous is not null)
        {
            lease.Dispose();
            return false;
        }

        OnPropertyChanged(nameof(IsScreenshotTestActive));
        return true;
    }

    public void EndScreenshotTestActivity()
    {
        var lease = Interlocked.Exchange(ref _screenshotTestCompatibilityLease, null);
        if (lease is null)
        {
            return;
        }

        lease.Dispose();
        OnPropertyChanged(nameof(IsScreenshotTestActive));
    }

    public bool IsSessionOperationActive => Volatile.Read(ref _sessionOperationCount) > 0;

    public bool IsSessionOperationExclusiveActive =>
        Volatile.Read(ref _exclusiveSessionOperationLease) is not null;

    public bool IsTimerSessionOperationActive
    {
        get
        {
            var lease = Volatile.Read(ref _exclusiveSessionOperationLease) as SessionOperationLease;
            return lease?.OperationName.StartsWith("Timer.", StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    public IDisposable BeginSessionOperation(string operationName)
    {
        var lease = TryBeginSessionOperation(operationName);
        if (lease is null)
        {
            throw new InvalidOperationException(
                $"Session operation `{operationName.Trim()}` cannot start while an exclusive session operation is active.");
        }

        return lease;
    }

    public IDisposable? TryBeginSessionOperation(string operationName, bool requireIdle = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        var becameActive = false;
        SessionOperationLease lease;
        lock (_sessionOperationGate)
        {
            if (_exclusiveSessionOperationLease is not null)
            {
                return null;
            }

            if (requireIdle && _sessionOperationCount != 0)
            {
                return null;
            }

            _sessionOperationCount++;
            becameActive = _sessionOperationCount == 1;
            lease = new SessionOperationLease(this, operationName.Trim(), isExclusive: false);
        }

        if (becameActive)
        {
            OnPropertyChanged(nameof(IsSessionOperationActive));
        }

        return lease;
    }

    public IDisposable? TryBeginExclusiveSessionOperation(string operationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        SessionOperationLease lease;
        lock (_sessionOperationGate)
        {
            if (_sessionOperationCount != 0)
            {
                return null;
            }

            _sessionOperationCount = 1;
            lease = new SessionOperationLease(this, operationName.Trim(), isExclusive: true);
            Volatile.Write(ref _exclusiveSessionOperationLease, lease);
        }

        OnPropertyChanged(nameof(IsSessionOperationExclusiveActive));
        OnPropertyChanged(nameof(IsTimerSessionOperationActive));
        OnPropertyChanged(nameof(IsSessionOperationActive));
        return lease;
    }

    internal bool IsExclusiveSessionOperationLease(IDisposable? lease)
    {
        return lease is not null
               && ReferenceEquals(Volatile.Read(ref _exclusiveSessionOperationLease), lease);
    }

    public async Task<IDisposable> AcquireExclusiveSessionOperationAsync(
        string operationName,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var lease = TryBeginExclusiveSessionOperation(operationName);
            if (lease is not null)
            {
                return lease;
            }

            await WaitForSessionOperationsIdleAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task WaitForSessionOperationsIdleAsync(CancellationToken cancellationToken = default)
    {
        while (IsSessionOperationActive)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
    }

    private void EndSessionOperation(SessionOperationLease lease)
    {
        var becameIdle = false;
        var exclusiveChanged = false;
        lock (_sessionOperationGate)
        {
            if (_sessionOperationCount <= 0)
            {
                throw new InvalidOperationException("Session operation lease count is already zero.");
            }

            if (lease.IsExclusive)
            {
                if (!ReferenceEquals(_exclusiveSessionOperationLease, lease))
                {
                    throw new InvalidOperationException("Exclusive session operation lease is no longer active.");
                }

                if (_sessionOperationCount != 1)
                {
                    throw new InvalidOperationException("Exclusive session operation lease cannot be nested.");
                }

                Volatile.Write(ref _exclusiveSessionOperationLease, null);
                exclusiveChanged = true;
            }

            _sessionOperationCount--;
            becameIdle = _sessionOperationCount == 0;
        }

        if (exclusiveChanged)
        {
            OnPropertyChanged(nameof(IsSessionOperationExclusiveActive));
            OnPropertyChanged(nameof(IsTimerSessionOperationActive));
        }

        if (becameIdle)
        {
            OnPropertyChanged(nameof(IsSessionOperationActive));
        }
    }

    private sealed class SessionOperationLease : IDisposable
    {
        private ConnectionGameSharedStateViewModel? _owner;

        public SessionOperationLease(
            ConnectionGameSharedStateViewModel owner,
            string operationName,
            bool isExclusive)
        {
            _owner = owner;
            OperationName = operationName;
            IsExclusive = isExclusive;
        }

        public string OperationName { get; }

        public bool IsExclusive { get; }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.EndSessionOperation(this);
        }
    }

    public bool HasTestLinkInfo => !string.IsNullOrWhiteSpace(TestLinkInfo);

    public TestLinkInfoSeverity TestLinkInfoSeverity
    {
        get => _testLinkInfoSeverity;
        set
        {
            if (SetProperty(ref _testLinkInfoSeverity, value))
            {
                OnPropertyChanged(nameof(TestLinkInfoIsWarning));
                OnPropertyChanged(nameof(TestLinkInfoIsError));
            }
        }
    }

    public bool TestLinkInfoIsWarning => TestLinkInfoSeverity == TestLinkInfoSeverity.Warning;

    public bool TestLinkInfoIsError => TestLinkInfoSeverity == TestLinkInfoSeverity.Error;

    private void ApplyTestLinkInfo(string? value)
    {
        if (SetProperty(ref _testLinkInfo, value ?? string.Empty, nameof(TestLinkInfo)))
        {
            OnPropertyChanged(nameof(HasTestLinkInfo));
            TestLinkInfoSeverity = TestLinkInfoSeverity.Normal;
        }
    }

    public string ScreencapCost
    {
        get => _screencapCost;
        set => SetProperty(ref _screencapCost, string.IsNullOrWhiteSpace(value) ? BuildDefaultScreencapCostText() : value);
    }

    public string FastestScreencapMethod
    {
        get => _fastestScreencapMethod;
        set => SetProperty(ref _fastestScreencapMethod, (value ?? string.Empty).Trim());
    }

    public void UpdateScreencapCost(long min, long avg, long max, DateTimeOffset timestamp)
    {
        _lastScreencapCostMin = min;
        _lastScreencapCostAvg = avg;
        _lastScreencapCostMax = max;
        _lastScreencapCostTimestamp = timestamp;
        ScreencapCost = BuildCurrentScreencapCostText();
    }

    public static string FormatScreencapCost(long min, long avg, long max, DateTimeOffset timestamp)
    {
        return BuildScreencapCostText(UiLanguageCatalog.DefaultLanguage, min, avg, max, timestamp);
    }

    public IReadOnlyList<string> BuildConnectAddressCandidates(bool includeConfiguredAddress = true)
    {
        var candidates = new List<string>();
        if (includeConfiguredAddress)
        {
            AddAddressCandidate(candidates, ConnectAddress);
        }

        if (AutoDetect || AlwaysAutoDetect)
        {
            foreach (var fallbackAddress in GetDefaultAddressesForCurrentConfig())
            {
                AddAddressCandidate(candidates, fallbackAddress);
            }
        }

        // WPF parity: with AutoDetect off and an empty address there are no fallback
        // candidates; the connect attempt fails instead of silently trying default ports.
        return candidates;
    }

    public string? BuildConnectionSettingsHintMessage()
    {
        if (string.IsNullOrWhiteSpace(ConnectAddress) && !AutoDetect && !AlwaysAutoDetect)
        {
            return BuildLocalizedMessage(
                "Settings.Connect.Hint.ConnectionAddressEmpty",
                "连接地址为空，请填写“IP:端口”后再连接。",
                "Connection address is empty. Enter \"IP:port\" and try again.");
        }

        var adbHint = BuildAdbPathHintMessage();
        if (!string.IsNullOrWhiteSpace(adbHint))
        {
            return adbHint;
        }

        if (ValidateMuMu12EmulatorPath(out var muMuHint))
        {
            return ValidateLdPlayerEmulatorPath(out var ldHint) ? null : ldHint;
        }

        return muMuHint;
    }

    public string? BuildAdbPathHintMessage()
    {
        if (UseMacBundledAdbEffective)
        {
            var bundledPath = ResolveBundledAdbPath();
            if (File.Exists(bundledPath))
            {
                return null;
            }

            return BuildLocalizedMessage(
                "Settings.Connect.Hint.MacBundledAdbNotFound",
                "内置 ADB 不存在：{0}",
                "Bundled ADB was not found: {0}",
                bundledPath);
        }

        var normalized = (AdbPath ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (File.Exists(normalized))
        {
            return null;
        }

        if (Directory.Exists(normalized))
        {
            if (string.IsNullOrEmpty(TryFindAdbUnderDirectory(normalized)))
            {
                return BuildLocalizedMessage(
                    "Settings.Connect.Hint.AdbPathDirectoryMissingExecutable",
                    "ADB 路径是目录，但目录内未找到 adb 可执行文件：{0}",
                    "ADB path is a directory, but adb executable was not found in it: {0}",
                    normalized);
            }

            return null;
        }

        if (IsWindowsDrivePathMissingSlash(normalized))
        {
            return BuildLocalizedMessage(
                "Settings.Connect.Hint.AdbPathMissingDriveSlash",
                "ADB 路径格式可能不正确（盘符后缺少斜杠）：{0}",
                "ADB path format looks invalid (missing slash after drive letter): {0}",
                normalized);
        }

        if (!OperatingSystem.IsWindows() && LooksLikeWindowsPath(normalized))
        {
            return BuildLocalizedMessage(
                "Settings.Connect.Hint.AdbPathWindowsPathOnNonWindows",
                "当前系统是 {0}，但 ADB 路径看起来是 Windows 路径：{1}",
                "Current system is {0}, but ADB path looks like a Windows path: {1}",
                GetPlatformDisplayName(),
                normalized);
        }

        return BuildLocalizedMessage(
            "Settings.Connect.Hint.AdbPathNotFound",
            "ADB 路径不存在：{0}",
            "ADB path does not exist: {0}",
            normalized);
    }

    public string? ResolveEffectiveAdbPath(bool updateStateWhenResolved = false)
    {
        if (UseMacBundledAdbEffective)
        {
            return ResolveBundledAdbPath();
        }

        var normalized = (AdbPath ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (ShouldIgnoreCustomAdbPathForCurrentPlatform(normalized))
        {
            return null;
        }

        var resolved = ResolveAdbPathCore(normalized);
        if (string.IsNullOrEmpty(resolved))
        {
            return normalized;
        }

        if (updateStateWhenResolved
            && !string.Equals(normalized, resolved, StringComparison.Ordinal))
        {
            AdbPath = resolved;
        }

        return resolved;
    }

    public static string ResolveBundledAdbPath()
        => MacBundledAdbPolicy.ResolveBundledAdbPath();

    public CoreInstanceOptions BuildCoreInstanceOptions(bool? deploymentWithPause = null)
    {
        var touchMode = string.IsNullOrWhiteSpace(TouchMode)
            ? null
            : TouchMode.Trim();

        return new CoreInstanceOptions(
            TouchMode: touchMode,
            DeploymentWithPause: deploymentWithPause,
            AdbLiteEnabled: AdbLiteEnabled,
            KillAdbOnExit: KillAdbOnExit,
            ClientType: ResolveEffectiveCoreClientType());
    }

    public CoreConnectionInfo BuildCoreConnectionInfo(
        string? address = null,
        string? effectiveAdbPath = null,
        TimeSpan? timeout = null,
        bool discoverRuntimeCandidates = false)
    {
        var resolvedAdbPath = effectiveAdbPath ?? ResolveEffectiveAdbPath(updateStateWhenResolved: true);
        return new CoreConnectionInfo(
            (address ?? ConnectAddress ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Trim(),
            (ConnectConfig ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(resolvedAdbPath) ? null : resolvedAdbPath.Trim(),
            BuildCoreConnectionExtras(),
            timeout,
            discoverRuntimeCandidates);
    }

    public CoreConnectionExtras BuildCoreConnectionExtras()
    {
        return new CoreConnectionExtras(
            MacUseBundledAdb: UseMacBundledAdbEffective,
            TouchMode: string.IsNullOrWhiteSpace(TouchMode) ? null : TouchMode.Trim(),
            AdbLiteEnabled: AdbLiteEnabled,
            KillAdbOnExit: KillAdbOnExit,
            MuMu12ExtrasEnabled: MuMu12ExtrasEnabled,
            MuMu12EmulatorPath: string.IsNullOrWhiteSpace(MuMu12EmulatorPath) ? null : MuMu12EmulatorPath.Trim(),
            MuMuBridgeConnection: MuMuBridgeConnection,
            MuMu12Index: string.IsNullOrWhiteSpace(MuMu12Index) ? null : MuMu12Index.Trim(),
            LdPlayerExtrasEnabled: LdPlayerExtrasEnabled,
            LdPlayerEmulatorPath: string.IsNullOrWhiteSpace(LdPlayerEmulatorPath) ? null : LdPlayerEmulatorPath.Trim(),
            LdPlayerManualSetIndex: LdPlayerManualSetIndex,
            LdPlayerIndex: string.IsNullOrWhiteSpace(LdPlayerIndex) ? null : LdPlayerIndex.Trim(),
            AttachWindowScreencapMethod: string.IsNullOrWhiteSpace(AttachWindowScreencapMethod) ? null : AttachWindowScreencapMethod.Trim(),
            AttachWindowMouseMethod: string.IsNullOrWhiteSpace(AttachWindowMouseMethod) ? null : AttachWindowMouseMethod.Trim(),
            AttachWindowKeyboardMethod: string.IsNullOrWhiteSpace(AttachWindowKeyboardMethod) ? null : AttachWindowKeyboardMethod.Trim(),
            ClientType: string.IsNullOrWhiteSpace(ClientType) ? null : ClientType.Trim());
    }

    public void RemoveAddressFromHistory(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        for (var index = _connectAddressHistory.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_connectAddressHistory[index], address, StringComparison.OrdinalIgnoreCase))
            {
                _connectAddressHistory.RemoveAt(index);
            }
        }
    }

    public bool AutoDetectMuMu12EmulatorPathIfNeeded()
    {
        if (!MuMu12ExtrasEnabled || !string.IsNullOrWhiteSpace(MuMu12EmulatorPath))
        {
            return false;
        }

        var detected = DetectFirstExistingDirectory(BuildMuMu12PathCandidates());
        if (string.IsNullOrWhiteSpace(detected))
        {
            return false;
        }

        MuMu12EmulatorPath = detected;
        if (!string.Equals(MuMu12EmulatorPath, detected, StringComparison.Ordinal))
        {
            return false;
        }

        ShowMuMu12AutoDetectedPathStatus();
        return true;
    }

    public bool AutoDetectLdPlayerEmulatorPathIfNeeded()
    {
        if (!LdPlayerExtrasEnabled || !string.IsNullOrWhiteSpace(LdPlayerEmulatorPath))
        {
            return false;
        }

        var detected = DetectFirstExistingDirectory(BuildLdPlayerPathCandidates());
        if (string.IsNullOrWhiteSpace(detected))
        {
            return false;
        }

        LdPlayerEmulatorPath = detected;
        if (!string.Equals(LdPlayerEmulatorPath, detected, StringComparison.Ordinal))
        {
            return false;
        }

        ShowLdPlayerAutoDetectedPathStatus();
        return true;
    }

    public void ShowMuMu12AutoDetectedPathStatus()
    {
        SetLocalizedTestLinkInfo(
            "Settings.Connect.Status.MuMuPathAutoDetected",
            "已自动检测到 MuMu 模拟器路径：{0}",
            "MuMu emulator path auto-detected: {0}",
            MuMu12EmulatorPath);
    }

    public void ShowLdPlayerAutoDetectedPathStatus()
    {
        SetLocalizedTestLinkInfo(
            "Settings.Connect.Status.LdPlayerPathAutoDetected",
            "已自动检测到 LDPlayer 模拟器路径：{0}",
            "LDPlayer emulator path auto-detected: {0}",
            LdPlayerEmulatorPath);
    }

    public string BuildMuMu12AutoDetectedPathMessage(string? path = null)
    {
        var normalized = (path ?? MuMu12EmulatorPath ?? string.Empty).Trim();
        return BuildLocalizedMessage(
            "Settings.Connect.Status.MuMuPathAutoDetected",
            "已自动检测到 MuMu 模拟器路径：{0}",
            "MuMu emulator path auto-detected: {0}",
            normalized);
    }

    public string BuildLdPlayerAutoDetectedPathMessage(string? path = null)
    {
        var normalized = (path ?? LdPlayerEmulatorPath ?? string.Empty).Trim();
        return BuildLocalizedMessage(
            "Settings.Connect.Status.LdPlayerPathAutoDetected",
            "已自动检测到 LDPlayer 模拟器路径：{0}",
            "LDPlayer emulator path auto-detected: {0}",
            normalized);
    }

    public bool ValidateMuMu12EmulatorPath(out string? errorMessage)
    {
        if (!ShowMuMuExtrasSection || !MuMu12ExtrasEnabled)
        {
            errorMessage = null;
            return true;
        }

        return ValidateMuMu12EmulatorPath(MuMu12EmulatorPath, out errorMessage);
    }

    public bool ValidateLdPlayerEmulatorPath(out string? errorMessage)
    {
        if (!ShowLdPlayerExtrasSection || !LdPlayerExtrasEnabled)
        {
            errorMessage = null;
            return true;
        }

        return ValidateLdPlayerEmulatorPath(LdPlayerEmulatorPath, out errorMessage);
    }

    private bool ValidateMuMu12EmulatorPath(string path, out string? errorMessage)
    {
        var failure = FindMuMu12PathValidationFailure(path);
        errorMessage = failure is null ? null : BuildPathValidationMessage(failure);
        return failure is null;
    }

    private bool ValidateLdPlayerEmulatorPath(string path, out string? errorMessage)
    {
        var failure = FindLdPlayerPathValidationFailure(path);
        errorMessage = failure is null ? null : BuildPathValidationMessage(failure);
        return failure is null;
    }

    public bool ValidateAndShowMuMu12EmulatorPathStatus()
    {
        var failure = ShowMuMuExtrasSection && MuMu12ExtrasEnabled
            ? FindMuMu12PathValidationFailure(MuMu12EmulatorPath)
            : null;
        if (failure is not null)
        {
            ShowPathValidationFailure(failure);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(MuMu12EmulatorPath))
        {
            ClearPathStatusIfCurrent(
                "Settings.Connect.Hint.MuMuPathNotFound",
                "Settings.Connect.Hint.MuMuExternalRendererMissing",
                "Settings.Connect.Status.MuMuPathAutoDetected");
        }
        return true;
    }

    public bool ValidateAndShowLdPlayerEmulatorPathStatus()
    {
        var failure = ShowLdPlayerExtrasSection && LdPlayerExtrasEnabled
            ? FindLdPlayerPathValidationFailure(LdPlayerEmulatorPath)
            : null;
        if (failure is not null)
        {
            ShowPathValidationFailure(failure);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(LdPlayerEmulatorPath))
        {
            ClearPathStatusIfCurrent(
                "Settings.Connect.Hint.LdPlayerPathNotFound",
                "Settings.Connect.Hint.LdPlayerOpenGlMissing",
                "Settings.Connect.Status.LdPlayerPathAutoDetected");
        }
        return true;
    }

    private static PathValidationFailure? FindMuMu12PathValidationFailure(string path)
    {
        var normalized = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (!Directory.Exists(normalized))
        {
            return new PathValidationFailure(
                "Settings.Connect.Hint.MuMuPathNotFound",
                "MuMu 模拟器路径不存在：{0}",
                "MuMu emulator path does not exist: {0}",
                normalized);
        }

        if (MuMuExternalRendererCandidates.Any(candidate => File.Exists(Path.Combine(normalized, candidate))))
        {
            return null;
        }

        return new PathValidationFailure(
            "Settings.Connect.Hint.MuMuExternalRendererMissing",
            "MuMu 模拟器路径缺少 external_renderer_ipc.dll：{0}",
            "MuMu emulator path is missing external_renderer_ipc.dll: {0}",
            normalized);
    }

    private static PathValidationFailure? FindLdPlayerPathValidationFailure(string path)
    {
        var normalized = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (!Directory.Exists(normalized))
        {
            return new PathValidationFailure(
                "Settings.Connect.Hint.LdPlayerPathNotFound",
                "LDPlayer 模拟器路径不存在：{0}",
                "LDPlayer emulator path does not exist: {0}",
                normalized);
        }

        if (File.Exists(Path.Combine(normalized, LdPlayerOpenGlLibrary)))
        {
            return null;
        }

        return new PathValidationFailure(
            "Settings.Connect.Hint.LdPlayerOpenGlMissing",
            "LDPlayer 模拟器路径缺少 ldopengl64.dll：{0}",
            "LDPlayer emulator path is missing ldopengl64.dll: {0}",
            normalized);
    }

    private void ShowPathValidationFailure(PathValidationFailure failure)
    {
        SetLocalizedTestLinkInfo(
            failure.Key,
            failure.ChineseFallback,
            failure.EnglishFallback,
            failure.Path);
    }

    private string BuildPathValidationMessage(PathValidationFailure failure)
    {
        return BuildLocalizedMessage(
            failure.Key,
            failure.ChineseFallback,
            failure.EnglishFallback,
            failure.Path);
    }

    private void ClearPathStatusIfCurrent(params string[] keys)
    {
        if (_testLinkInfoLocalization is { } status
            && keys.Contains(status.Key, StringComparer.Ordinal))
        {
            ClearTestLinkInfo();
        }
    }

    private void UpdateConnectAddressHistory(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        for (var index = _connectAddressHistory.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_connectAddressHistory[index], address, StringComparison.OrdinalIgnoreCase))
            {
                _connectAddressHistory.RemoveAt(index);
            }
        }

        _connectAddressHistory.Insert(0, address);
        while (_connectAddressHistory.Count > MaxConnectAddressHistory)
        {
            _connectAddressHistory.RemoveAt(_connectAddressHistory.Count - 1);
        }
    }

    private static string NormalizeConnectConfigAlias(string normalized)
    {
        if (string.Equals(normalized, "Mumu", StringComparison.OrdinalIgnoreCase))
        {
            return "MuMuEmulator12";
        }

        return normalized;
    }

    private static string NormalizeClientTypeAlias(string normalized)
    {
        if (string.Equals(normalized, "Txwy", StringComparison.OrdinalIgnoreCase))
        {
            return "txwy";
        }

        return normalized;
    }

    private string ResolveEffectiveCoreClientType()
    {
        return string.Equals(ConnectConfig, "WSA", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ConnectConfig, "Androws", StringComparison.OrdinalIgnoreCase)
            ? (ClientType ?? string.Empty).Trim()
            : string.Empty;
    }

    private static string NormalizeTouchModeAlias(string normalized)
    {
        return normalized;
    }

    private void RebuildOptions()
    {
        ConnectConfigOptions = SettingsOptionCatalog.BuildConnectConfigOptions(_language);
        ClientTypeOptions = SettingsOptionCatalog.BuildClientTypeOptions(_language);
        TouchModeOptions = SettingsOptionCatalog.BuildTouchModeOptions(_language);
        AttachWindowScreencapOptions = SettingsOptionCatalog.BuildAttachWindowScreencapOptions(_language);
        AttachWindowInputOptions = SettingsOptionCatalog.BuildAttachWindowInputOptions(_language);

        OnPropertyChanged(nameof(SelectedConnectConfigOption));
        OnPropertyChanged(nameof(SelectedConnectConfigValue));
        OnPropertyChanged(nameof(SelectedClientTypeOption));
        OnPropertyChanged(nameof(SelectedClientTypeValue));
        OnPropertyChanged(nameof(SelectedTouchModeOption));
        OnPropertyChanged(nameof(SelectedTouchModeValue));
        OnPropertyChanged(nameof(SelectedAttachWindowScreencapOption));
        OnPropertyChanged(nameof(SelectedAttachWindowScreencapValue));
        OnPropertyChanged(nameof(SelectedAttachWindowMouseOption));
        OnPropertyChanged(nameof(SelectedAttachWindowMouseValue));
        OnPropertyChanged(nameof(SelectedAttachWindowKeyboardOption));
        OnPropertyChanged(nameof(SelectedAttachWindowKeyboardValue));
    }

    private static ConnectionGameOptionItem? ResolveSelectedOption(
        IReadOnlyList<ConnectionGameOptionItem> options,
        string? value,
        Func<string, string> normalizeAlias)
    {
        var normalized = normalizeAlias((value ?? string.Empty).Trim());
        if (string.IsNullOrEmpty(normalized))
        {
            return options.FirstOrDefault();
        }

        foreach (var option in options)
        {
            if (string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }
        }

        // Keep SelectedItem bound to the current ItemsSource to avoid language-switch desync.
        return options.FirstOrDefault();
    }

    private static string? ResolveAdbPathCore(string input)
    {
        if (File.Exists(input))
        {
            return input;
        }

        if (Directory.Exists(input))
        {
            var fromDirectory = TryFindAdbUnderDirectory(input);
            if (!string.IsNullOrEmpty(fromDirectory))
            {
                return fromDirectory;
            }
        }

        var directory = Path.GetDirectoryName(input);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            var fileName = Path.GetFileName(input);
            if (string.Equals(fileName, "adb", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "adb.exe", StringComparison.OrdinalIgnoreCase))
            {
                return input;
            }
        }

        return string.Empty;
    }

    private static string? TryFindAdbUnderDirectory(string directory)
    {
        var candidateDirectories = new[]
        {
            directory,
            Path.Combine(directory, "platform-tools"),
            Path.Combine(directory, "shell"),
        };
        var candidates = candidateDirectories
            .Where(Directory.Exists)
            .SelectMany(dir => GetAdbFileNameCandidates().Select(file => Path.Combine(dir, file)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private IReadOnlyList<string> GetDefaultAddressesForCurrentConfig()
    {
        return _defaultAddressByConnectConfig.TryGetValue(ConnectConfig, out var addresses)
            ? addresses
            : [string.Empty];
    }

    private static void AddAddressCandidate(ICollection<string> candidates, string? rawAddress)
    {
        var normalized = (rawAddress ?? string.Empty)
            .Replace("：", ":", StringComparison.Ordinal)
            .Replace("；", ":", StringComparison.Ordinal)
            .Replace(";", ":", StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        candidates.Add(normalized);
    }

    private static string? DetectFirstExistingDirectory(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates
                     .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                     .Select(candidate => candidate.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> BuildMuMu12PathCandidates()
    {
        var candidates = new List<string>();
        candidates.AddRange(ReadMuMuInstallDirectoriesFromRegistry());
        candidates.AddRange(
        [
            Path.Combine(GetProgramFiles(), "Netease", "MuMuPlayer-15.0"),
            Path.Combine(GetProgramFilesX86(), "Netease", "MuMuPlayer-15.0"),
            Path.Combine(GetProgramFiles(), "Netease", "MuMuPlayer-12.0"),
            Path.Combine(GetProgramFilesX86(), "Netease", "MuMuPlayer-12.0"),
            Path.Combine(GetProgramFiles(), "Netease", "MuMuPlayerGlobal-12.0"),
            Path.Combine(GetProgramFilesX86(), "Netease", "MuMuPlayerGlobal-12.0"),
            Path.Combine(GetProgramFiles(), "Netease", "YXArkNights-12.0"),
            Path.Combine(GetProgramFilesX86(), "Netease", "YXArkNights-12.0"),
            Path.Combine(GetUserProfileRoot(), "Netease", "MuMuPlayer-12.0"),
            Path.Combine(GetUserProfileRoot(), "Netease", "MuMuPlayerGlobal-12.0"),
        ]);
        return candidates;
    }

    private static IReadOnlyList<string> BuildLdPlayerPathCandidates()
    {
        var candidates = new List<string>();
        candidates.AddRange(ReadLdPlayerInstallDirectoriesFromRegistry());
        candidates.AddRange(
        [
            Path.Combine(GetProgramFiles(), "leidian", "LDPlayer14"),
            Path.Combine(GetProgramFilesX86(), "leidian", "LDPlayer14"),
            Path.Combine(GetProgramFiles(), "leidian", "LDPlayer9"),
            Path.Combine(GetProgramFilesX86(), "leidian", "LDPlayer9"),
            Path.Combine(GetProgramFiles(), "leidian", "LDPlayer4.0"),
            Path.Combine(GetProgramFilesX86(), "leidian", "LDPlayer4.0"),
            Path.Combine(GetProgramFiles(), "LDPlayer"),
            Path.Combine(GetProgramFilesX86(), "LDPlayer"),
            Path.Combine(GetUserProfileRoot(), "leidian", "LDPlayer9"),
            Path.Combine(GetUserProfileRoot(), "leidian", "LDPlayer4.0"),
        ]);
        return candidates;
    }

    private static IReadOnlyList<string> ReadMuMuInstallDirectoriesFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        string[] keys =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MuMuPlayer-15.0",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MuMuPlayer-12.0",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MuMuPlayer",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MuMuPlayerGlobal-15.0",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MuMuPlayerGlobal-12.0",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\YXArkNights-12.0",
        ];
        var paths = new List<string>();
        foreach (var keyPath in keys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                var uninstall = key?.GetValue("UninstallString") as string;
                var match = Regex.Match(uninstall ?? string.Empty, "^\\\"(.*?)\\\\uninstall\\.exe", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    paths.Add(match.Groups[1].Value);
                }
            }
            catch
            {
                // Registry discovery is best effort; fixed-path candidates remain available.
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> ReadLdPlayerInstallDirectoriesFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        string[] keys = [@"Software\leidian\ldplayer14", @"Software\leidian\ldplayer9", @"Software\mrfz\mrfz"];
        var paths = new List<string>();
        foreach (var keyPath in keys)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath);
                if (key?.GetValue("InstallDir") is string path && !string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path.Trim());
                }
            }
            catch
            {
                // Registry discovery is best effort; fixed-path candidates remain available.
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// 用户主目录。测试通过 MAAUNIFIED_USER_PROFILE_ROOT 指向临时目录，避免在沙箱/CI
    /// 里写真实 ~/Netease、~/leidian。
    /// </summary>
    private static string GetUserProfileRoot()
        => Environment.GetEnvironmentVariable("MAAUNIFIED_USER_PROFILE_ROOT") is { Length: > 0 } root
            ? root
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string GetProgramFiles()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    }

    private static string GetProgramFilesX86()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    }

    private static bool LooksLikeWindowsPath(string path)
    {
        return path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';
    }

    private static bool ShouldIgnoreCustomAdbPathForCurrentPlatform(string path)
    {
        return !OperatingSystem.IsWindows() && LooksLikeWindowsPath(path);
    }

    private static bool IsWindowsDrivePathMissingSlash(string path)
    {
        return path.Length >= 3
               && char.IsLetter(path[0])
               && path[1] == ':'
               && path[2] != '\\'
               && path[2] != '/';
    }

    private static string GetPlatformDisplayName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }

        return "Unknown";
    }

    private void RefreshLocalizedConnectTexts()
    {
        if (_testLinkInfoLocalization is { } status)
        {
            ApplyTestLinkInfo(BuildLocalizedMessage(
                status.Key,
                status.ChineseFallback,
                status.EnglishFallback,
                status.Args));
        }

        var previousScreencapCost = ScreencapCost;
        ScreencapCost = BuildCurrentScreencapCostText();
        if (_testLinkInfoLocalization is null
            && string.Equals(TestLinkInfo, previousScreencapCost, StringComparison.Ordinal))
        {
            TestLinkInfo = ScreencapCost;
        }
    }

    private string BuildCurrentScreencapCostText()
    {
        return _lastScreencapCostTimestamp.HasValue
            && _lastScreencapCostMin.HasValue
            && _lastScreencapCostAvg.HasValue
            && _lastScreencapCostMax.HasValue
            ? BuildScreencapCostText(_language, _lastScreencapCostMin.Value, _lastScreencapCostAvg.Value, _lastScreencapCostMax.Value, _lastScreencapCostTimestamp.Value)
            : BuildDefaultScreencapCostText();
    }

    private string BuildDefaultScreencapCostText()
    {
        return TryFormatRootText("Settings.Connect.Status.ScreencapCost", "---", "---", "---", "--")
            ?? BuildFallbackScreencapCostText(_language, "---", "---", "---", "--");
    }

    private string BuildLocalizedMessage(string key, string zhFormat, string enFormat, params object[] args)
    {
        return TryFormatRootText(key, args)
            ?? BuildLocalizedMessage(
                string.Format(CultureInfo.CurrentCulture, zhFormat, args),
                string.Format(CultureInfo.CurrentCulture, enFormat, args));
    }

    private string? TryFormatRootText(string key, params object[] args)
    {
        var template = RootTexts[key];
        if (string.Equals(template, key, StringComparison.Ordinal))
        {
            return null;
        }

        return args.Length == 0
            ? template
            : string.Format(CultureInfo.CurrentCulture, template, args);
    }

    private static string BuildScreencapCostText(string language, long min, long avg, long max, DateTimeOffset timestamp)
    {
        var texts = new RootLocalizationTextMap("Root.Localization.Settings")
        {
            Language = language,
        };
        var template = texts["Settings.Connect.Status.ScreencapCost"];
        if (string.Equals(template, "Settings.Connect.Status.ScreencapCost", StringComparison.Ordinal))
        {
            return BuildFallbackScreencapCostText(
                language,
                min.ToString(CultureInfo.CurrentCulture),
                avg.ToString(CultureInfo.CurrentCulture),
                max.ToString(CultureInfo.CurrentCulture),
                timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture));
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            template,
            min,
            avg,
            max,
            timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture));
    }

    private static string BuildFallbackScreencapCostText(string language, string min, string avg, string max, string timestamp)
    {
        var normalized = UiLanguageCatalog.Normalize(language);
        return normalized switch
        {
            "zh-cn" => $"截图耗时 min/avg/max(ms): {min} / {avg} / {max} ({timestamp})",
            "zh-tw" => $"截圖耗時 min/avg/max(ms): {min} / {avg} / {max} ({timestamp})",
            "ja-jp" => $"スクリーンショット時間 min/avg/max(ms): {min} / {avg} / {max} ({timestamp})",
            "ko-kr" => $"스크린샷 시간 min/avg/max(ms): {min} / {avg} / {max} ({timestamp})",
            _ => $"Screenshot time min/avg/max (ms): {min} / {avg} / {max} ({timestamp})",
        };
    }

    private string BuildLocalizedMessage(string zh, string en)
    {
        return DialogTextCatalog.Select(_language, zh, en);
    }

    private sealed record LocalizedTestLinkInfo(
        string Key,
        string ChineseFallback,
        string EnglishFallback,
        object[] Args);

    private sealed record PathValidationFailure(
        string Key,
        string ChineseFallback,
        string EnglishFallback,
        string Path);

    private static IReadOnlyList<string> GetAdbFileNameCandidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ["adb.exe", "adb"];
        }

        return ["adb", "adb.exe"];
    }
}

public sealed class ConnectionGameOptionItem : IEquatable<ConnectionGameOptionItem>
{
    public ConnectionGameOptionItem(string value, string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }

    public string Value { get; }

    public string DisplayName { get; }

    public bool Equals(ConnectionGameOptionItem? other)
    {
        return other is not null
               && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj)
    {
        return obj is ConnectionGameOptionItem other && Equals(other);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Value ?? string.Empty);
    }
}
