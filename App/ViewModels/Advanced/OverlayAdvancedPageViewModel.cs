using System.Collections.ObjectModel;
using System.ComponentModel;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Platform;

namespace MAAUnified.App.ViewModels.Advanced;

public sealed class OverlayAdvancedPageViewModel : PageViewModelBase
{
    private readonly ToolboxLocalizationTextMap _texts = new();
    private string _capabilityProvider = string.Empty;
    private bool? _capabilitySupported;
    private string? _capabilityFallbackMode;
    private OverlayTarget? _selectedTarget;
    private bool _visible;
    private string _capabilitySummary = string.Empty;
    private readonly OverlaySharedState _overlaySharedState;

    public OverlayAdvancedPageViewModel(MAAUnifiedRuntime runtime)
        : base(runtime)
    {
        Targets = new ObservableCollection<OverlayTarget>();
        _overlaySharedState = OverlaySharedStateRegistry.Get(runtime);
        _visible = _overlaySharedState.Visible;
        _overlaySharedState.PropertyChanged += OnOverlaySharedStateChanged;
    }

    public ToolboxLocalizationTextMap Texts => _texts;

    public ObservableCollection<OverlayTarget> Targets { get; }

    public OverlayTarget? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!SetProperty(ref _selectedTarget, value))
            {
                return;
            }

            _overlaySharedState.SelectedTargetId = value?.Id ?? "preview";
        }
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (!SetProperty(ref _visible, value))
            {
                return;
            }

            _overlaySharedState.Visible = value;
        }
    }

    public string CapabilitySummary
    {
        get => _capabilitySummary;
        private set => SetProperty(ref _capabilitySummary, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ReloadTargetsAsync(cancellationToken);
    }

    public async Task ReloadTargetsAsync(CancellationToken cancellationToken = default)
    {
        var targets = await ApplyResultAsync(
            await Runtime.OverlayFeatureService.GetOverlayTargetsAsync(cancellationToken),
            "Advanced.Overlay.QueryTargets",
            cancellationToken);
        if (targets is null)
        {
            return;
        }

        Targets.Clear();
        foreach (var target in targets)
        {
            Targets.Add(target);
        }

        var resolvedSelection = OverlayTargetPersistence.ResolveSelection(
            Targets,
            Runtime.ConfigurationService.CurrentConfig.GlobalValues,
            _overlaySharedState.SelectedTargetId);
        if (OverlayTargetPersistence.ShouldDefaultToPreview(
            Runtime.ConfigurationService.CurrentConfig.GlobalValues,
            _overlaySharedState.SelectedTargetId))
        {
            resolvedSelection = Targets.FirstOrDefault(t => string.Equals(t.Id, "preview", StringComparison.Ordinal))
                ?? resolvedSelection;
        }

        SelectedTarget = resolvedSelection
            ?? Targets.FirstOrDefault(t => t.IsPrimary)
            ?? Targets.FirstOrDefault();

        var snapshot = await ApplyResultAsync(
            await Runtime.PlatformCapabilityService.GetSnapshotAsync(cancellationToken),
            "Advanced.Overlay.QueryCapability",
            cancellationToken);
        if (snapshot is not null)
        {
            var capability = snapshot.Overlay;
            UpdateCapabilitySummary(capability.Provider, capability.Supported, capability.FallbackMode);
        }
    }

    public async Task ToggleOverlayAsync(CancellationToken cancellationToken = default)
    {
        if (!await SelectAndPersistTargetAsync(SelectedTarget?.Id ?? "preview", cancellationToken))
        {
            return;
        }

        var requestedVisible = !Visible;
        var toggleResult = await Runtime.OverlayFeatureService.ToggleOverlayVisibilityAsync(requestedVisible, cancellationToken);
        if (!await ApplyResultAsync(
            toggleResult,
            "Advanced.Overlay.ToggleVisible",
            cancellationToken))
        {
            return;
        }

        Visible = requestedVisible;
    }

    private async Task<bool> SelectAndPersistTargetAsync(string targetId, CancellationToken cancellationToken)
    {
        var selectResult = await ApplyResultAsync(
            await Runtime.OverlayFeatureService.SelectOverlayTargetAsync(targetId, cancellationToken),
            "Advanced.Overlay.SelectTarget",
            cancellationToken);
        if (!selectResult)
        {
            return false;
        }

        SelectedTarget = Targets.FirstOrDefault(target => string.Equals(target.Id, targetId, StringComparison.Ordinal))
                         ?? SelectedTarget
                         ?? new OverlayTarget(targetId, targetId, false);

        await PersistSelectedTargetBestEffortAsync(SelectedTarget, cancellationToken);
        return true;
    }

    private async Task PersistSelectedTargetBestEffortAsync(OverlayTarget? selectedTarget, CancellationToken cancellationToken)
    {
        if (selectedTarget is null)
        {
            return;
        }

        _ = await RunTrackedConfigurationSaveAsync(
            "Advanced.Overlay.TargetSelection",
            Texts.GetOrDefault("Overlay.Title", "悬浮窗"),
            "Advanced.Overlay.SaveTarget",
            ct => Runtime.SettingsFeatureService.SaveGlobalSettingsAsync(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [Compat.Constants.ConfigurationKeys.OverlayTarget] = OverlayTargetPersistence.Serialize(selectedTarget),
                    [Compat.Constants.ConfigurationKeys.OverlayPreviewPinned] = OverlayTargetPersistence.SerializePreviewPreference(selectedTarget),
                },
                ct),
            cancellationToken);
    }

    private void OnOverlaySharedStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(OverlaySharedState.Visible), StringComparison.Ordinal))
        {
            if (_visible == _overlaySharedState.Visible)
            {
                return;
            }

            _visible = _overlaySharedState.Visible;
            OnPropertyChanged(nameof(Visible));
            return;
        }

        if (!string.Equals(e.PropertyName, nameof(OverlaySharedState.SelectedTargetId), StringComparison.Ordinal)
            || Targets.Count == 0)
        {
            return;
        }

        var selected = Targets.FirstOrDefault(target =>
            string.Equals(target.Id, _overlaySharedState.SelectedTargetId, StringComparison.Ordinal));
        if (selected is null || Equals(_selectedTarget, selected))
        {
            return;
        }

        _selectedTarget = selected;
        OnPropertyChanged(nameof(SelectedTarget));
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
        if (_capabilitySupported.HasValue)
        {
            CapabilitySummary = string.Format(
                T("Toolbox.Advanced.Capability.Summary", "Provider: {0}; Supported: {1}; Fallback: {2}"),
                string.IsNullOrWhiteSpace(_capabilityProvider)
                    ? T("Toolbox.Advanced.Capability.Provider.Unknown", "unknown")
                    : _capabilityProvider,
                _capabilitySupported.Value
                    ? T("Toolbox.Advanced.Capability.Supported.True", "Yes")
                    : T("Toolbox.Advanced.Capability.Supported.False", "No"),
                string.IsNullOrWhiteSpace(_capabilityFallbackMode)
                    ? T("Toolbox.Advanced.Capability.Fallback.None", "None")
                    : _capabilityFallbackMode);
        }
    }

    private void UpdateCapabilitySummary(string provider, bool supported, string? fallbackMode)
    {
        _capabilityProvider = provider ?? string.Empty;
        _capabilitySupported = supported;
        _capabilityFallbackMode = fallbackMode;
        RefreshLocalizedUiState();
    }

    private string T(string key, string fallback)
    {
        return _texts.GetOrDefault(key, fallback);
    }
}
