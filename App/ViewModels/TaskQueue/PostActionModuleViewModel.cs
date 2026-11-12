using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.Services;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using System.ComponentModel;

namespace MAAUnified.App.ViewModels.TaskQueue;

public sealed class PostActionModuleViewModel : ObservableObject
{
    private readonly MAAUnifiedRuntime _runtime;
    private readonly LocalizedTextMap _texts;
    private readonly ConnectionGameSharedStateViewModel? _connectionGameSharedState;
    private CancellationTokenSource? _persistDebounceCts;
    private bool _once;
    private bool _exitArknights;
    private bool _backToAndroidHome;
    private bool _exitEmulator;
    private bool _exitSelf;
    private bool _ifNoOtherMaa;
    private bool _hibernate;
    private bool _shutdown;
    private bool _sleep;
    private bool _skipOnError;
    private bool _lockScreen;
    private bool _exitPCClient;
    private string _exitArknightsCommand = string.Empty;
    private string _backToAndroidHomeCommand = string.Empty;
    private string _exitEmulatorCommand = string.Empty;
    private string _exitSelfCommand = string.Empty;
    private string _statusMessage = string.Empty;
    private string _lastErrorMessage = string.Empty;
    private bool _suppressPersist = true;
    private bool _hasPendingCommandPersist;
    private PostActionConfig _persistentConfig = PostActionConfig.Default;

    public PostActionModuleViewModel(
        MAAUnifiedRuntime runtime,
        LocalizedTextMap texts,
        ConnectionGameSharedStateViewModel? connectionGameSharedState = null)
    {
        _runtime = runtime;
        _texts = texts;
        _connectionGameSharedState = connectionGameSharedState;
        _texts.PropertyChanged += OnTextsPropertyChanged;
        if (_connectionGameSharedState is not null)
        {
            _connectionGameSharedState.PropertyChanged += OnConnectionGameSharedStateChanged;
        }
    }

    public LocalizedTextMap Texts => _texts;

    private static bool SupportsExitEmulator => OperatingSystem.IsWindows();

    private static bool SupportsHibernate => !OperatingSystem.IsMacOS();

    /// <summary>
    /// Gets a value indicating whether the workstation lock is available on this platform.
    /// WPF parity (d58cb0825b) uses the Windows <c>LockWorkStation</c> API; macOS/Linux are
    /// explicit degradations (hidden checkbox + unsupported platform capability).
    /// </summary>
    private static bool SupportsLockScreen => OperatingSystem.IsWindows();

    private bool HasAnySystemPowerAction => Shutdown || Sleep || (SupportsHibernate && Hibernate) || (SupportsLockScreen && LockScreen);

    /// <summary>
    /// Gets a value indicating whether the current connection is the PC client (window binding),
    /// WPF <c>ConnectSettingsUserControlModel.IsPCConnectConfig</c>. Changing it never rewrites
    /// the stored exit selections (3b4f06dd86); it only filters what is shown and executed.
    /// </summary>
    public bool IsPCConnectMode => _connectionGameSharedState?.IsAttachWindowMode ?? false;

    public bool ShowExitArknights => !IsPCConnectMode;

    public bool ShowExitEmulator => SupportsExitEmulator && !IsPCConnectMode;

    public bool ShowExitPCClient => IsPCConnectMode;

    public bool ShowLockScreen => SupportsLockScreen;

    public bool ShowHibernate => SupportsHibernate;

    public bool CanUseAndroidControlPostActions => !ExitEmulator && !IsPCConnectMode;

    public bool CanUseIfNoOtherMaa => HasAnySystemPowerAction;

    private void OnTextsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName)
            && !string.Equals(e.PropertyName, nameof(LocalizedTextMap.Language), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, "Item[]", StringComparison.Ordinal))
        {
            return;
        }

        OnPropertyChanged(nameof(Texts));
        OnPropertyChanged(string.Empty);
    }

    private void OnConnectionGameSharedStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName)
            && !string.Equals(e.PropertyName, nameof(ConnectionGameSharedStateViewModel.ConnectConfig), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(ConnectionGameSharedStateViewModel.IsAttachWindowMode), StringComparison.Ordinal))
        {
            return;
        }

        // WPF parity (3b4f06dd86): switching the connection configuration re-evaluates the
        // filtered getters/visibility and never rewrites the stored selections.
        OnPropertyChanged(nameof(IsPCConnectMode));
        OnPropertyChanged(nameof(ShowExitArknights));
        OnPropertyChanged(nameof(ShowExitEmulator));
        OnPropertyChanged(nameof(ShowExitPCClient));
        OnPropertyChanged(nameof(CanUseAndroidControlPostActions));
        OnPropertyChanged(nameof(ExitArknights));
        OnPropertyChanged(nameof(ExitEmulator));
        OnPropertyChanged(nameof(ExitPCClient));
    }

    public bool Once
    {
        get => _once;
        set
        {
            if (!SetProperty(ref _once, value))
            {
                return;
            }

            if (!value)
            {
                QueuePersist();
            }
            else
            {
                _ = ValidateAndUpdateWarningAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether MAA exits Arknights after completion.
    /// Mirrors WPF <c>PostActionSetting.ExitArknights</c> (3b4f06dd86): the getter is filtered to
    /// ADB connections and hidden for the PC client, while the stored selection is preserved
    /// across connection switches.
    /// </summary>
    public bool ExitArknights
    {
        get => _exitArknights && !IsPCConnectMode;
        set
        {
            if (!SetProperty(ref _exitArknights, value))
            {
                return;
            }

            if (value)
            {
                BackToAndroidHome = false;
            }

            QueuePersist();
        }
    }

    public bool BackToAndroidHome
    {
        get => _backToAndroidHome;
        set
        {
            if (!SetProperty(ref _backToAndroidHome, value))
            {
                return;
            }

            if (value)
            {
                ExitArknights = false;
            }

            QueuePersist();
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether MAA closes the emulator after completion.
    /// WPF 3b4f06dd86 replaced the old ｢PC mode rewrites ExitEmulator｣ behavior with a filtered
    /// getter; the stored selection is kept and only hidden/skipped for PC connections.
    /// </summary>
    public bool ExitEmulator
    {
        get => SupportsExitEmulator && _exitEmulator && !IsPCConnectMode;
        set
        {
            value = SupportsExitEmulator && value;
            if (!SetProperty(ref _exitEmulator, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanUseAndroidControlPostActions));
            if (value)
            {
                ExitArknights = false;
                BackToAndroidHome = false;
            }
            else
            {
                if (SupportsExitEmulator)
                {
                    IfNoOtherMaa = false;
                }
            }

            QueuePersist();
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether MAA closes the PC client after completion.
    /// WPF parity: <c>PostActionSetting.ExitPCClient</c> (3b4f06dd86) — only shown and executed for
    /// the PC (window binding) connection, decoupled from ExitArknights/ExitEmulator: selecting it
    /// never clears them, and switching the connection never rewrites its stored value.
    /// </summary>
    public bool ExitPCClient
    {
        get => _exitPCClient && IsPCConnectMode;
        set
        {
            if (!SetProperty(ref _exitPCClient, value))
            {
                return;
            }

            QueuePersist();
        }
    }

    public bool ExitSelf
    {
        get => _exitSelf;
        set
        {
            if (!SetProperty(ref _exitSelf, value))
            {
                return;
            }

            if (!value)
            {
                IfNoOtherMaa = false;
            }

            QueuePersist();
        }
    }

    public bool IfNoOtherMaa
    {
        get => _ifNoOtherMaa;
        set
        {
            if (value && !CanUseIfNoOtherMaa)
            {
                value = false;
            }

            if (!SetProperty(ref _ifNoOtherMaa, value))
            {
                return;
            }

            if (value)
            {
                ExitEmulator = true;
                ExitSelf = true;
            }

            QueuePersist();
        }
    }

    public bool Hibernate
    {
        get => _hibernate;
        set
        {
            value = SupportsHibernate && value;
            if (!SetProperty(ref _hibernate, value))
            {
                return;
            }

            if (value)
            {
                Shutdown = false;
                Sleep = false;
                LockScreen = false;
            }
            else if (!Shutdown && !Sleep && !LockScreen)
            {
                IfNoOtherMaa = false;
            }

            OnPropertyChanged(nameof(CanUseIfNoOtherMaa));
            QueuePersist();
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether MAA locks the workstation after completion.
    /// WPF parity (d58cb0825b): LockScreen is mutually exclusive with Hibernate/Shutdown/Sleep
    /// and participates in ｢Only if no other MAA｣, but it never clears the exit selections.
    /// Windows only; on macOS/Linux the checkbox is hidden and the stored value is preserved.
    /// </summary>
    public bool LockScreen
    {
        get => SupportsLockScreen && _lockScreen;
        set
        {
            value = SupportsLockScreen && value;
            if (!SetProperty(ref _lockScreen, value))
            {
                return;
            }

            if (value)
            {
                Hibernate = false;
                Shutdown = false;
                Sleep = false;
            }
            else if (!Hibernate && !Shutdown && !Sleep)
            {
                IfNoOtherMaa = false;
            }

            OnPropertyChanged(nameof(CanUseIfNoOtherMaa));
            QueuePersist();
        }
    }

    public bool Shutdown
    {
        get => _shutdown;
        set
        {
            if (!SetProperty(ref _shutdown, value))
            {
                return;
            }

            if (value)
            {
                Hibernate = false;
                Sleep = false;
                LockScreen = false;
                ExitArknights = false;
                BackToAndroidHome = false;
            }
            else if (!Hibernate && !Sleep && !LockScreen)
            {
                IfNoOtherMaa = false;
            }

            OnPropertyChanged(nameof(CanUseIfNoOtherMaa));
            QueuePersist();
        }
    }

    public bool Sleep
    {
        get => _sleep;
        set
        {
            if (!SetProperty(ref _sleep, value))
            {
                return;
            }

            if (value)
            {
                Hibernate = false;
                Shutdown = false;
                LockScreen = false;
            }
            else if (!Hibernate && !Shutdown && !LockScreen)
            {
                IfNoOtherMaa = false;
            }

            OnPropertyChanged(nameof(CanUseIfNoOtherMaa));
            QueuePersist();
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether every post action is skipped when the run had a
    /// failed main-queue task. WPF cda1c3b07f (<c>PostActions.SkipOnError = 1 &lt;&lt; 8</c>).
    /// </summary>
    public bool SkipOnError
    {
        get => _skipOnError;
        set
        {
            if (!SetProperty(ref _skipOnError, value))
            {
                return;
            }

            QueuePersist();
        }
    }

    public string ExitArknightsCommand
    {
        get => _exitArknightsCommand;
        set
        {
            if (!SetProperty(ref _exitArknightsCommand, value))
            {
                return;
            }

            QueuePersist(forcePersist: true);
        }
    }

    public string BackToAndroidHomeCommand
    {
        get => _backToAndroidHomeCommand;
        set
        {
            if (!SetProperty(ref _backToAndroidHomeCommand, value))
            {
                return;
            }

            QueuePersist(forcePersist: true);
        }
    }

    public string ExitEmulatorCommand
    {
        get => _exitEmulatorCommand;
        set
        {
            if (!SetProperty(ref _exitEmulatorCommand, value))
            {
                return;
            }

            QueuePersist(forcePersist: true);
        }
    }

    public string ExitSelfCommand
    {
        get => _exitSelfCommand;
        set
        {
            if (!SetProperty(ref _exitSelfCommand, value))
            {
                return;
            }

            QueuePersist(forcePersist: true);
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string LastErrorMessage
    {
        get => _lastErrorMessage;
        private set => SetProperty(ref _lastErrorMessage, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        CancelPendingPersistDebounce();
        _suppressPersist = true;
        var result = await _runtime.PostActionFeatureService.LoadAsync(cancellationToken);
        if (!result.Success || result.Value is null)
        {
            LastErrorMessage = result.Message;
            _suppressPersist = false;
            return;
        }

        ApplyPersistentConfigSnapshot(result.Value);
        _hasPendingCommandPersist = false;
        _once = false;
        OnPropertyChanged(nameof(Once));

        _suppressPersist = false;
        LastErrorMessage = string.Empty;
        await ValidateAndUpdateWarningAsync(cancellationToken);
    }

    public async Task ReloadPersistentConfigAsync(CancellationToken cancellationToken = default)
    {
        CancelPendingPersistDebounce();
        _suppressPersist = true;
        ApplyPersistentConfigSnapshot(_persistentConfig);
        _hasPendingCommandPersist = false;
        _once = false;
        OnPropertyChanged(nameof(Once));
        _suppressPersist = false;
        LastErrorMessage = string.Empty;
        await ValidateAndUpdateWarningAsync(cancellationToken);
    }

    public PostActionConfig BuildRuntimeConfig()
    {
        // WPF parity (3b4f06dd86): the filtered exit getters are read once, before the post-action
        // sequence awaits anything, so a connection switch during task teardown can no longer
        // retarget the action at the wrong client/emulator.
        var isPcConnectMode = IsPCConnectMode;
        var hibernate = SupportsHibernate && Hibernate;
        var lockScreen = SupportsLockScreen && LockScreen;
        var exitEmulator = SupportsExitEmulator && !isPcConnectMode && _exitEmulator;
        var exitArknights = !isPcConnectMode && _exitArknights;
        var exitPCClient = isPcConnectMode && _exitPCClient;
        // WPF 3b4f06dd86 clears the Android-side ｢back to home｣ bit when switching to the PC
        // connection; we keep the stored value and only skip executing it for PC connections.
        var backToAndroidHome = !isPcConnectMode && BackToAndroidHome;
        var canUseIfNoOther = Shutdown || Sleep || hibernate || lockScreen;
        var ifNoOtherMaa = canUseIfNoOther && IfNoOtherMaa;
        var exitSelf = ExitSelf || ifNoOtherMaa;

        return new PostActionConfig
        {
            Once = Once,
            ExitArknights = exitArknights,
            BackToAndroidHome = backToAndroidHome,
            ExitEmulator = exitEmulator,
            ExitPCClient = exitPCClient,
            ExitSelf = exitSelf,
            IfNoOtherMaa = ifNoOtherMaa,
            Hibernate = hibernate,
            LockScreen = lockScreen,
            Shutdown = Shutdown,
            Sleep = Sleep,
            SkipOnError = SkipOnError,
            Commands = new PostActionCommandConfig
            {
                ExitArknights = ExitArknightsCommand.Trim(),
                BackToAndroidHome = BackToAndroidHomeCommand.Trim(),
                ExitEmulator = ExitEmulatorCommand.Trim(),
                ExitSelf = ExitSelfCommand.Trim(),
            },
        };
    }

    public void ClearActions()
    {
        ExitArknights = false;
        BackToAndroidHome = false;
        ExitEmulator = false;
        ExitPCClient = false;
        ExitSelf = false;
        IfNoOtherMaa = false;
        LockScreen = false;
        Hibernate = false;
        Shutdown = false;
        Sleep = false;
        SkipOnError = false;
        Once = false;
        QueuePersist();
    }

    public void ToggleActionOnce(string actionName)
    {
        var nextValue = !GetActionValue(actionName);
        Once = nextValue;
        SetActionValue(actionName, nextValue);
    }

    public void ClearActionsOnce()
    {
        Once = true;
        ExitArknights = false;
        BackToAndroidHome = false;
        ExitEmulator = false;
        ExitPCClient = false;
        ExitSelf = false;
        IfNoOtherMaa = false;
        LockScreen = false;
        Hibernate = false;
        Shutdown = false;
        Sleep = false;
        SkipOnError = false;
    }

    private bool GetActionValue(string actionName)
    {
        return actionName switch
        {
            nameof(BackToAndroidHome) => BackToAndroidHome,
            nameof(ExitArknights) => ExitArknights,
            nameof(ExitEmulator) => ExitEmulator,
            nameof(ExitPCClient) => ExitPCClient,
            nameof(ExitSelf) => ExitSelf,
            nameof(LockScreen) => LockScreen,
            nameof(Sleep) => Sleep,
            nameof(Hibernate) => Hibernate,
            nameof(Shutdown) => Shutdown,
            _ => false,
        };
    }

    private void SetActionValue(string actionName, bool value)
    {
        switch (actionName)
        {
            case nameof(BackToAndroidHome):
                BackToAndroidHome = value;
                break;
            case nameof(ExitArknights):
                ExitArknights = value;
                break;
            case nameof(ExitEmulator):
                ExitEmulator = value;
                break;
            case nameof(ExitPCClient):
                ExitPCClient = value;
                break;
            case nameof(ExitSelf):
                ExitSelf = value;
                break;
            case nameof(LockScreen):
                LockScreen = value;
                break;
            case nameof(Sleep):
                Sleep = value;
                break;
            case nameof(Hibernate):
                Hibernate = value;
                break;
            case nameof(Shutdown):
                Shutdown = value;
                break;
        }
    }

    private void QueuePersist(bool forcePersist = false)
    {
        if (_suppressPersist)
        {
            return;
        }

        _ = ValidateAndUpdateWarningAsync(CancellationToken.None);
        if (forcePersist)
        {
            _hasPendingCommandPersist = true;
        }

        if (Once && !forcePersist)
        {
            return;
        }

        var targetProfile = CaptureCurrentProfileName();
        ConfigurationSaveTracker.Instance.MarkPending(
            "TaskQueue.PostAction",
            Texts.GetOrDefault("PostAction.Title", "完成后动作"),
            "PostAction.Persist",
            _runtime.DiagnosticsService,
            ct => PersistCoreAsync(targetProfile, ct));
        _persistDebounceCts?.Cancel();
        _persistDebounceCts?.Dispose();
        _persistDebounceCts = new CancellationTokenSource();
        var token = _persistDebounceCts.Token;

        _ = PersistDebouncedAsync(targetProfile, token);
    }

    public async Task<bool> FlushPendingChangesAsync(CancellationToken cancellationToken = default)
    {
        _persistDebounceCts?.Cancel();
        if (Once && !_hasPendingCommandPersist)
        {
            await ValidateAndUpdateWarningAsync(cancellationToken);
            return true;
        }

        return await PersistNowAsync(CaptureCurrentProfileName(), cancellationToken);
    }

    private async Task PersistDebouncedAsync(string targetProfile, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(180, cancellationToken);
            await PersistNowAsync(targetProfile, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // ignore debounce cancellation
        }
    }

    private async Task<bool> PersistNowAsync(string targetProfile, CancellationToken cancellationToken)
    {
        return await ConfigurationSaveTracker.Instance.RunTrackedAsync(
            "TaskQueue.PostAction",
            Texts.GetOrDefault("PostAction.Title", "完成后动作"),
            "PostAction.Persist",
            _runtime.DiagnosticsService,
            ct => PersistCoreAsync(targetProfile, ct),
            cancellationToken);
    }

    private async Task<bool> PersistCoreAsync(string targetProfile, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsCurrentProfile(targetProfile))
            {
                return true;
            }

            var persistent = BuildPersistentConfigForSave();

            var save = await _runtime.PostActionFeatureService.SaveAsync(persistent, cancellationToken);
            if (!save.Success)
            {
                LastErrorMessage = save.Message;
                await _runtime.DiagnosticsService.RecordFailedResultAsync("PostAction.Save", save, cancellationToken);
                return false;
            }

            _persistentConfig = persistent.Clone();
            _hasPendingCommandPersist = false;
            await ValidateAndUpdateWarningAsync(cancellationToken);
            LastErrorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            LastErrorMessage = ex.Message;
            await _runtime.DiagnosticsService.RecordErrorAsync("PostAction.Persist", "Unexpected post action persistence failure.", ex, cancellationToken);
            return false;
        }
    }

    private async Task ValidateAndUpdateWarningAsync(CancellationToken cancellationToken)
    {
        var previewResult = await _runtime.PostActionFeatureService.ValidateSelectionAsync(BuildRuntimeConfig(), cancellationToken);
        if (!previewResult.Success || previewResult.Value is null)
        {
            LastErrorMessage = previewResult.Message;
            return;
        }

        if (previewResult.Value.Warnings.Count == 0)
        {
            StatusMessage = string.Empty;
            LastErrorMessage = string.Empty;
            return;
        }

        var warningText = string.Join(
            " ",
            previewResult.Value.Warnings
                .Select(LocalizeMessageToken)
                .Where(text => !string.IsNullOrWhiteSpace(text)));

        if (previewResult.Value.UnsupportedActions.Count > 0)
        {
            var unsupported = string.Join(", ", previewResult.Value.UnsupportedActions);
            warningText = $"{warningText} {_texts["PostAction.Unsupported"]} ({unsupported})".Trim();
        }

        StatusMessage = warningText;
        LastErrorMessage = string.Empty;
    }

    private PostActionConfig BuildPersistentConfigForSave()
    {
        var persistent = _persistentConfig.Clone();
        var hibernate = SupportsHibernate && Hibernate;
        var lockScreen = SupportsLockScreen && LockScreen;
        var canUseIfNoOther = Shutdown || Sleep || hibernate || lockScreen;
        var ifNoOtherMaa = canUseIfNoOther && IfNoOtherMaa;
        var exitSelf = ExitSelf || ifNoOtherMaa;

        persistent.Commands = new PostActionCommandConfig
        {
            ExitArknights = ExitArknightsCommand.Trim(),
            BackToAndroidHome = BackToAndroidHomeCommand.Trim(),
            ExitEmulator = ExitEmulatorCommand.Trim(),
            ExitSelf = ExitSelfCommand.Trim(),
        };

        if (!Once)
        {
            // Persist the raw (unfiltered) exit selections: WPF 3b4f06dd86 removed the old
            // ｢switching to PC rewrites ExitEmulator｣ behavior, so a connection switch must never
            // erase what the user picked for the other mode.
            persistent.ExitArknights = _exitArknights;
            persistent.BackToAndroidHome = BackToAndroidHome;
            persistent.ExitPCClient = _exitPCClient;
            if (SupportsExitEmulator)
            {
                persistent.ExitEmulator = _exitEmulator;
            }
            persistent.ExitSelf = exitSelf;
            persistent.IfNoOtherMaa = ifNoOtherMaa;
            persistent.Hibernate = hibernate;
            if (SupportsLockScreen)
            {
                persistent.LockScreen = _lockScreen;
            }
            persistent.Shutdown = Shutdown;
            persistent.Sleep = Sleep;
            persistent.SkipOnError = SkipOnError;
        }

        persistent.Once = false;
        return persistent;
    }

    private string LocalizeMessageToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        return _texts.GetOrDefault(token, token);
    }

    private void ApplyPersistentConfigSnapshot(PostActionConfig config)
    {
        _persistentConfig = config.Clone();
        ExitArknights = _persistentConfig.ExitArknights;
        BackToAndroidHome = _persistentConfig.BackToAndroidHome;
        ExitEmulator = _persistentConfig.ExitEmulator;
        ExitPCClient = _persistentConfig.ExitPCClient;
        ExitSelf = _persistentConfig.ExitSelf;
        LockScreen = _persistentConfig.LockScreen;
        Hibernate = _persistentConfig.Hibernate;
        Shutdown = _persistentConfig.Shutdown;
        Sleep = _persistentConfig.Sleep;
        SkipOnError = _persistentConfig.SkipOnError;
        // Mutually exclusive setters above may clear it; restore from the snapshot last (WPF
        // LoadPostActions does the same for IfNoOtherMaa).
        IfNoOtherMaa = _persistentConfig.IfNoOtherMaa;
        ExitArknightsCommand = _persistentConfig.Commands.ExitArknights;
        BackToAndroidHomeCommand = _persistentConfig.Commands.BackToAndroidHome;
        ExitEmulatorCommand = _persistentConfig.Commands.ExitEmulator;
        ExitSelfCommand = _persistentConfig.Commands.ExitSelf;
    }

    private string CaptureCurrentProfileName()
    {
        return _runtime.ConfigurationService.CurrentConfig.CurrentProfile;
    }

    private bool IsCurrentProfile(string profileName)
    {
        return string.Equals(
            _runtime.ConfigurationService.CurrentConfig.CurrentProfile,
            profileName,
            StringComparison.OrdinalIgnoreCase);
    }

    private void CancelPendingPersistDebounce()
    {
        _persistDebounceCts?.Cancel();
        _persistDebounceCts?.Dispose();
        _persistDebounceCts = null;
    }
}
