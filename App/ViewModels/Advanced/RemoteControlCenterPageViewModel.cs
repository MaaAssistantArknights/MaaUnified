using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Constants;

namespace MAAUnified.App.ViewModels.Advanced;

public sealed class RemoteControlCenterPageViewModel : PageViewModelBase
{
    private readonly ToolboxLocalizationTextMap _texts = new();
    private string _getTaskEndpoint = string.Empty;
    private string _reportEndpoint = string.Empty;
    private int _pollIntervalMs = 5000;
    private string _warningMessage = string.Empty;

    public RemoteControlCenterPageViewModel(MAAUnifiedRuntime runtime)
        : base(runtime)
    {
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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        GetTaskEndpoint = ReadString(ConfigurationKeys.RemoteControlGetTaskEndpointUri, string.Empty);
        ReportEndpoint = ReadString(ConfigurationKeys.RemoteControlReportStatusUri, string.Empty);
        PollIntervalMs = ReadInt(ConfigurationKeys.RemoteControlPollIntervalMs, 5000);
        await Runtime.DiagnosticsService.RecordEventAsync("Advanced.RemoteControlCenter", "Initialized remote control center.", cancellationToken);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        WarningMessage = string.Empty;
        _ = await RunTrackedConfigurationSaveAsync(
            "Advanced.RemoteControlCenter",
            Texts.GetOrDefault("RemoteControlCenter.Title", "远程控制"),
            "Advanced.RemoteControlCenter.Save",
            ct => Runtime.SettingsFeatureService.SaveGlobalSettingsAsync(
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
        var config = Runtime.ConfigurationService.CurrentConfig;
        if (config.GlobalValues.TryGetValue(key, out var node) && node is not null)
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
        var config = Runtime.ConfigurationService.CurrentConfig;
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is null)
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
    }
}
