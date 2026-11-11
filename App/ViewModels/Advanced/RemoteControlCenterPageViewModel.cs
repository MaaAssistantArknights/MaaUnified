using Avalonia.Threading;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Application.Security;
using MAAUnified.Compat.Constants;

namespace MAAUnified.App.ViewModels.Advanced;

public sealed class RemoteControlCenterPageViewModel : PageViewModelBase, IDisposable
{
    private readonly ToolboxLocalizationTextMap _texts = new();
    private string _getTaskEndpoint = string.Empty;
    private string _reportEndpoint = string.Empty;
    private int _pollIntervalMs = 1000;
    private string _warningMessage = string.Empty;
    private bool _disposed;

    public RemoteControlCenterPageViewModel(MAAUnifiedRuntime runtime)
        : base(runtime)
    {
        runtime.ConfigurationService.SensitiveStorageStateChanged += HandleSensitiveStorageStateChanged;
    }

    public ToolboxLocalizationTextMap Texts => _texts;

    public string GetTaskEndpoint
    {
        get => _getTaskEndpoint;
        set => SetProperty(ref _getTaskEndpoint, value?.Trim() ?? string.Empty);
    }

    public string ReportEndpoint
    {
        get => _reportEndpoint;
        set => SetProperty(ref _reportEndpoint, value?.Trim() ?? string.Empty);
    }

    public int PollIntervalMs
    {
        get => _pollIntervalMs;
        set => SetProperty(ref _pollIntervalMs, Math.Clamp(value, 500, 60000));
    }

    public string WarningMessage
    {
        get => _warningMessage;
        private set => SetProperty(ref _warningMessage, value);
    }

    public bool HasWarningMessage => !string.IsNullOrWhiteSpace(WarningMessage);

    public bool CanUseSensitiveSettings => Runtime.ConfigurationService.SensitiveStorageState
        is SensitiveStorageState.Uninitialized or SensitiveStorageState.Available or SensitiveStorageState.PlaintextWarning;

    public bool HasSensitiveStorageWarning => !CanUseSensitiveSettings;

    public string SensitiveStorageWarningMessage => CanUseSensitiveSettings
        ? string.Empty
        : Texts.GetOrDefault(
            "Settings.SensitiveStorage.Unavailable",
            "Sensitive storage is unavailable. Remote control settings and operations are disabled until storage is restored.");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        GetTaskEndpoint = ReadString(ConfigurationKeys.RemoteControlGetTaskEndpointUri, string.Empty);
        ReportEndpoint = ReadString(ConfigurationKeys.RemoteControlReportStatusUri, string.Empty);
        PollIntervalMs = ReadInt(ConfigurationKeys.RemoteControlPollIntervalMs, 1000);
        await Runtime.DiagnosticsService.RecordEventAsync("Advanced.RemoteControlCenter", "Initialized remote control center.", cancellationToken);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanUseSensitiveSettings)
        {
            WarningMessage = SensitiveStorageWarningMessage;
            return;
        }

        WarningMessage = string.Empty;
        _ = await RunTrackedConfigurationSaveAsync(
            "Advanced.RemoteControlCenter",
            Texts.GetOrDefault("Toolbox.Advanced.RemoteControl.Title", "Remote Control"),
            "Advanced.RemoteControlCenter.Save",
            ct => Runtime.SettingsFeatureService.SaveProfileSettingsAsync(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ConfigurationKeys.RemoteControlGetTaskEndpointUri] = GetTaskEndpoint,
                    [ConfigurationKeys.RemoteControlReportStatusUri] = ReportEndpoint,
                    [ConfigurationKeys.RemoteControlPollIntervalMs] = PollIntervalMs.ToString(),
                },
                ct),
            cancellationToken);
    }

    public async Task TestConnectivityAsync(CancellationToken cancellationToken = default)
    {
        if (!CanUseSensitiveSettings)
        {
            WarningMessage = SensitiveStorageWarningMessage;
            return;
        }

        WarningMessage = string.Empty;
        var request = new RemoteControlConnectivityRequest(GetTaskEndpoint, ReportEndpoint, PollIntervalMs);
        var result = await Runtime.RemoteControlFeatureService.TestConnectivityAsync(request, cancellationToken);
        var payload = await ApplyResultAsync(result, "Advanced.RemoteControlCenter.Test", cancellationToken);
        if (payload is not null)
        {
            StatusMessage = $"GetTask={payload.GetTaskProbe.Message}; Report={payload.ReportProbe.Message}; Poll={payload.PollIntervalMs}ms";
            return;
        }

        if (string.Equals(result.Error?.Code, UiErrorCode.RemoteControlUnsupported, StringComparison.Ordinal))
        {
            WarningMessage = result.Message;
            LastErrorMessage = string.Empty;
        }
    }

    private string ReadString(string key, string fallback)
    {
        if (Runtime.ConfigurationService.TryGetCurrentProfile(out var profile)
            && profile.Values.TryGetValue(key, out var node)
            && node is not null)
        {
            var raw = node.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw.Trim();
            }
        }

        return fallback;
    }

    private int ReadInt(string key, int fallback)
    {
        if (!Runtime.ConfigurationService.TryGetCurrentProfile(out var profile)
            || !profile.Values.TryGetValue(key, out var node)
            || node is null)
        {
            return fallback;
        }

        return int.TryParse(node.ToString(), out var parsed)
            ? parsed
            : fallback;
    }

    public void SetLanguage(string language)
    {
        var normalized = UiLanguageCatalog.Normalize(language);
        if (string.Equals(_texts.Language, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _texts.Language = normalized;
        RefreshLocalizedUiState();
    }

    private void RefreshLocalizedUiState()
    {
        OnPropertyChanged(nameof(Texts));
        OnPropertyChanged(nameof(SensitiveStorageWarningMessage));
    }

    private void HandleSensitiveStorageStateChanged()
    {
        if (_disposed)
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(NotifySensitiveStorageStateChanged);
            return;
        }

        NotifySensitiveStorageStateChanged();
    }

    private void NotifySensitiveStorageStateChanged()
    {
        if (_disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(CanUseSensitiveSettings));
        OnPropertyChanged(nameof(HasSensitiveStorageWarning));
        OnPropertyChanged(nameof(SensitiveStorageWarningMessage));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Runtime.ConfigurationService.SensitiveStorageStateChanged -= HandleSensitiveStorageStateChanged;
    }
}
