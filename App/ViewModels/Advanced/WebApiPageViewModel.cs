using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.App.ViewModels.Advanced;

public sealed class WebApiPageViewModel : PageViewModelBase
{
    private readonly ToolboxLocalizationTextMap _texts = new();
    private bool _enabled;
    private string _host = "127.0.0.1";
    private int _port = 51888;
    private string _accessToken = string.Empty;
    private bool _isRunning;

    public WebApiPageViewModel(MAAUnifiedRuntime runtime)
        : base(runtime)
    {
    }

    public ToolboxLocalizationTextMap Texts => _texts;

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value?.Trim() ?? string.Empty);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, Math.Clamp(value, 1, 65535));
    }

    public string AccessToken
    {
        get => _accessToken;
        set => SetProperty(ref _accessToken, value ?? string.Empty);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value))
            {
                return;
            }

            OnPropertyChanged(nameof(RunningStatusText));
        }
    }

    public string RunningStatusText => T(
        IsRunning ? "Toolbox.Advanced.WebApi.Status.Running" : "Toolbox.Advanced.WebApi.Status.Stopped",
        IsRunning ? "Running" : "Stopped");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var config = await ApplyResultAsync(
            await Runtime.WebApiFeatureService.LoadConfigAsync(cancellationToken),
            "Advanced.WebApi.Load",
            cancellationToken);
        if (config is not null)
        {
            Enabled = config.Enabled;
            Host = config.Host;
            Port = config.Port;
            AccessToken = config.AccessToken;
        }

        await RefreshRunningStatusAsync(cancellationToken);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var config = new WebApiConfig(Enabled, Host, Port, AccessToken);
        _ = await RunTrackedConfigurationSaveAsync(
            "Advanced.WebApi",
            "Web API",
            "Advanced.WebApi.Save",
            ct => Runtime.WebApiFeatureService.SaveConfigAsync(config, ct),
            cancellationToken);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!await ApplyResultAsync(
            await Runtime.WebApiFeatureService.StartAsync(cancellationToken),
            "Advanced.WebApi.Start",
            cancellationToken))
        {
            return;
        }

        await RefreshRunningStatusAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!await ApplyResultAsync(
            await Runtime.WebApiFeatureService.StopAsync(cancellationToken),
            "Advanced.WebApi.Stop",
            cancellationToken))
        {
            return;
        }

        await RefreshRunningStatusAsync(cancellationToken);
    }

    public async Task RefreshRunningStatusAsync(CancellationToken cancellationToken = default)
    {
        var statusResult = await Runtime.WebApiFeatureService.GetRunningStatusAsync(cancellationToken);
        var status = await ApplyResultAsync(
            statusResult,
            "Advanced.WebApi.Status",
            cancellationToken);
        if (!statusResult.Success)
        {
            return;
        }

        IsRunning = status;
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
        OnPropertyChanged(nameof(RunningStatusText));
    }

    private string T(string key, string fallback)
    {
        return _texts.GetOrDefault(key, fallback);
    }
}
