using System.Collections.ObjectModel;
using Avalonia.Threading;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Application.Security;

namespace MAAUnified.App.ViewModels.Advanced;

public sealed class ExternalNotificationProvidersPageViewModel : PageViewModelBase, IDisposable
{
    private readonly ToolboxLocalizationTextMap _texts = new();
    private string _defaultTitle = string.Empty;
    private string _defaultMessage = string.Empty;
    private string _selectedProvider = string.Empty;
    private string _parametersText = string.Empty;
    private string _title = string.Empty;
    private string _message = string.Empty;
    private bool _disposed;

    public ExternalNotificationProvidersPageViewModel(MAAUnifiedRuntime runtime)
        : base(runtime)
    {
        Providers = new ObservableCollection<string>();
        runtime.ConfigurationService.SensitiveStorageStateChanged += HandleSensitiveStorageStateChanged;
        RefreshLocalizedUiState();
    }

    public ToolboxLocalizationTextMap Texts => _texts;

    public ObservableCollection<string> Providers { get; }

    public bool CanUseSensitiveSettings => Runtime.ConfigurationService.SensitiveStorageState
        is SensitiveStorageState.Uninitialized or SensitiveStorageState.Available or SensitiveStorageState.PlaintextWarning;

    public bool HasSensitiveStorageWarning => !CanUseSensitiveSettings;

    public string SensitiveStorageWarningMessage => CanUseSensitiveSettings
        ? string.Empty
        : T(
            "Settings.SensitiveStorage.Unavailable",
            "Sensitive storage is unavailable. External notification settings and operations are disabled until storage is restored.");

    public string SelectedProvider
    {
        get => _selectedProvider;
        set => SetProperty(ref _selectedProvider, value ?? string.Empty);
    }

    public string ParametersText
    {
        get => _parametersText;
        set => SetProperty(ref _parametersText, value ?? string.Empty);
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value ?? string.Empty);
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value ?? string.Empty);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshProvidersAsync(cancellationToken);
    }

    public async Task RefreshProvidersAsync(CancellationToken cancellationToken = default)
    {
        if (!CanUseSensitiveSettings)
        {
            LastErrorMessage = SensitiveStorageWarningMessage;
            return;
        }

        try
        {
            var providers = await Runtime.NotificationProviderFeatureService.GetAvailableProvidersAsync(cancellationToken);
            Providers.Clear();
            foreach (var provider in providers)
            {
                Providers.Add(provider);
            }

            if (Providers.Count == 0)
            {
                SelectedProvider = string.Empty;
                StatusMessage = T(
                    "Toolbox.Advanced.ExternalNotification.Status.NoProviders",
                    "No external notification provider available.");
                LastErrorMessage = string.Empty;
                await RecordEventAsync("Advanced.ExternalNotificationProviders.Query", StatusMessage, cancellationToken);
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedProvider) || !Providers.Contains(SelectedProvider))
            {
                SelectedProvider = Providers[0];
            }

            StatusMessage = string.Format(
                T(
                    "Toolbox.Advanced.ExternalNotification.Status.LoadedProviders",
                    "Loaded {0} external notification provider(s)."),
                Providers.Count);
            LastErrorMessage = string.Empty;
            await RecordEventAsync("Advanced.ExternalNotificationProviders.Query", StatusMessage, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RecordUnhandledExceptionAsync(
                "Advanced.ExternalNotificationProviders.Query",
                ex,
                UiErrorCode.NotificationProviderFailed,
                T(
                    "Toolbox.Advanced.ExternalNotification.Error.QueryFailed",
                    "Failed to query external notification providers."),
                cancellationToken);
        }
    }

    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (!CanUseSensitiveSettings)
        {
            LastErrorMessage = SensitiveStorageWarningMessage;
            return;
        }

        var result = await Runtime.NotificationProviderFeatureService.ValidateProviderParametersAsync(
            new NotificationProviderRequest(SelectedProvider, ParametersText),
            cancellationToken);
        await ApplyResultAsync(result, "Advanced.ExternalNotificationProviders.Validate", cancellationToken);
    }

    public async Task SendTestAsync(CancellationToken cancellationToken = default)
    {
        if (!CanUseSensitiveSettings)
        {
            LastErrorMessage = SensitiveStorageWarningMessage;
            return;
        }

        var result = await Runtime.NotificationProviderFeatureService.SendTestAsync(
            new NotificationProviderTestRequest(SelectedProvider, ParametersText, Title, Message),
            cancellationToken);
        await ApplyResultAsync(result, "Advanced.ExternalNotificationProviders.SendTest", cancellationToken);
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

        var previousTitle = _defaultTitle;
        var previousMessage = _defaultMessage;
        _defaultTitle = T("Toolbox.Advanced.ExternalNotification.DefaultTitle", "MAA Test");
        _defaultMessage = T("Toolbox.Advanced.ExternalNotification.DefaultMessage", "Advanced external notification test");

        if (string.IsNullOrWhiteSpace(Title)
            || string.Equals(Title, previousTitle, StringComparison.Ordinal))
        {
            Title = _defaultTitle;
        }

        if (string.IsNullOrWhiteSpace(Message)
            || string.Equals(Message, previousMessage, StringComparison.Ordinal))
        {
            Message = _defaultMessage;
        }
    }

    private string T(string key, string fallback)
    {
        return _texts.GetOrDefault(key, fallback);
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
