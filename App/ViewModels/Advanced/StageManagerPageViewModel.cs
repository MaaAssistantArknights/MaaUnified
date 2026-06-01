using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.App.ViewModels.Advanced;

public sealed class StageManagerPageViewModel : PageViewModelBase
{
    private static readonly IReadOnlyList<string> DefaultClientTypes =
    [
        "Official",
        "Bilibili",
        "YoStarEN",
        "YoStarJP",
        "YoStarKR",
        "txwy",
    ];

    private readonly ToolboxLocalizationTextMap _texts = new();
    private string _stageCodesText = string.Empty;
    private string _localStageCodesText = string.Empty;
    private string _webStageCodesText = string.Empty;
    private string _clientType = "Official";
    private bool _autoIterate;
    private string _lastSelectedStage = string.Empty;

    public StageManagerPageViewModel(MAAUnifiedRuntime runtime)
        : base(runtime)
    {
    }

    public ToolboxLocalizationTextMap Texts => _texts;

    public IReadOnlyList<string> ClientTypeOptions => DefaultClientTypes;

    public string ClientType
    {
        get => _clientType;
        set => SetProperty(ref _clientType, string.IsNullOrWhiteSpace(value) ? "Official" : value.Trim());
    }

    public string StageCodesText
    {
        get => _stageCodesText;
        set => SetProperty(ref _stageCodesText, value ?? string.Empty);
    }

    public string LocalStageCodesText
    {
        get => _localStageCodesText;
        private set => SetProperty(ref _localStageCodesText, value ?? string.Empty);
    }

    public string WebStageCodesText
    {
        get => _webStageCodesText;
        private set => SetProperty(ref _webStageCodesText, value ?? string.Empty);
    }

    public bool AutoIterate
    {
        get => _autoIterate;
        set => SetProperty(ref _autoIterate, value);
    }

    public string LastSelectedStage
    {
        get => _lastSelectedStage;
        set => SetProperty(ref _lastSelectedStage, value?.Trim() ?? string.Empty);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var state = await ApplyResultAsync(
            await Runtime.StageManagerFeatureService.LoadStateAsync(cancellationToken),
            "Advanced.StageManager.LoadState",
            cancellationToken);

        if (state is not null)
        {
            ApplyState(state);
        }

        var config = await ApplyResultAsync(
            await Runtime.StageManagerFeatureService.LoadConfigAsync(cancellationToken),
            "Advanced.StageManager.LoadConfig",
            cancellationToken);

        if (config is null)
        {
            return;
        }

        ApplyConfig(config);
    }

    public async Task RefreshLocalAsync(CancellationToken cancellationToken = default)
    {
        var result = await Runtime.StageManagerFeatureService.RefreshLocalAsync(ClientType, cancellationToken);
        var state = await ApplyResultAsync(result, "Advanced.StageManager.RefreshLocal", cancellationToken);
        if (state is null)
        {
            return;
        }

        ApplyState(state);
    }

    public async Task RefreshWebAsync(CancellationToken cancellationToken = default)
    {
        var result = await Runtime.StageManagerFeatureService.RefreshWebAsync(ClientType, cancellationToken);
        var state = await ApplyResultAsync(result, "Advanced.StageManager.RefreshWeb", cancellationToken);
        if (state is null)
        {
            return;
        }

        ApplyState(state);
    }

    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        var result = await Runtime.StageManagerFeatureService.ValidateStageCodesAsync(StageCodesText, cancellationToken);
        var values = await ApplyResultAsync(result, "Advanced.StageManager.Validate", cancellationToken);
        if (values is null)
        {
            return;
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var parse = await Runtime.StageManagerFeatureService.ValidateStageCodesAsync(StageCodesText, cancellationToken);
        if (!parse.Success || parse.Value is null)
        {
            await ApplyResultAsync(parse, "Advanced.StageManager.ValidateBeforeSave", cancellationToken);
            return;
        }

        var config = new StageManagerConfig(
            StageCodes: parse.Value,
            AutoIterate: AutoIterate,
            LastSelectedStage: LastSelectedStage,
            ClientType: ClientType);
        _ = await RunTrackedConfigurationSaveAsync(
            "Advanced.StageManager",
            Texts.GetOrDefault("Toolbox.Advanced.StageManager.Title", "Stage Manager"),
            "Advanced.StageManager.Save",
            ct => Runtime.StageManagerFeatureService.SaveConfigAsync(config, ct),
            cancellationToken);
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

    private void ApplyState(StageManagerState state)
    {
        ClientType = state.ClientType;
        LocalStageCodesText = state.LocalStageCodesText;
        WebStageCodesText = state.WebStageCodesText;
        if (string.IsNullOrWhiteSpace(StageCodesText))
        {
            StageCodesText = state.ActiveStageCodesText;
        }
    }

    private void ApplyConfig(StageManagerConfig config)
    {
        StageCodesText = string.Join(Environment.NewLine, config.StageCodes);
        AutoIterate = config.AutoIterate;
        LastSelectedStage = config.LastSelectedStage;
        ClientType = config.ClientType;
    }

    private void RefreshLocalizedUiState()
    {
        OnPropertyChanged(nameof(Texts));
    }
}
