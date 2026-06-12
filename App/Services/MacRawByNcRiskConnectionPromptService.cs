using System.Globalization;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.App.Services;

public sealed class MacRawByNcRiskConnectionPromptService : IMacRawByNcRiskConnectionPromptService
{
    private readonly IAppDialogService _dialogService;
    private readonly IUiLanguageCoordinator? _languageCoordinator;

    public MacRawByNcRiskConnectionPromptService(
        IAppDialogService dialogService,
        IUiLanguageCoordinator? languageCoordinator = null)
    {
        _dialogService = dialogService;
        _languageCoordinator = languageCoordinator;
    }

    public async Task<MacRawByNcRiskConnectionDecision> ConfirmAsync(
        MacRawByNcRiskConnectionPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        var language = ResolveLanguage(prompt.Language);
        var localizer = UiLocalizer.Create(language);
        var configuredTouch = string.IsNullOrWhiteSpace(prompt.ConfiguredTouchMode)
            ? localizer.GetOrDefault(
                "MacRawByNcRisk.Dialog.DefaultTouchMode",
                "default touch mode",
                "App.MacRawByNcRiskPrompt")
            : prompt.ConfiguredTouchMode!.Trim();
        var configuredAdbLite = prompt.ConfiguredAdbLiteEnabled ? "true" : "false";
        var recommendedAdbLite = prompt.RecommendedAdbLiteEnabled ? "true" : "false";
        var messageTemplate = localizer.GetOrDefault(
            "MacRawByNcRisk.Dialog.Message",
            "{0} + adbLite={1} can trigger the macOS RawByNc/POSIX connection issue. We recommend setting adbLite to {2}.",
            "App.MacRawByNcRiskPrompt");
        var message = string.Format(
            CultureInfo.InvariantCulture,
            messageTemplate,
            configuredTouch,
            configuredAdbLite,
            recommendedAdbLite);
        var request = new WarningConfirmDialogRequest(
            Title: localizer.GetOrDefault(
                "MacRawByNcRisk.Dialog.Title",
                "Risky connection settings",
                "App.MacRawByNcRiskPrompt"),
            Message: message,
            ConfirmText: localizer.GetOrDefault(
                "MacRawByNcRisk.Dialog.ApplyRecommended",
                "Apply Fix",
                "App.MacRawByNcRiskPrompt"),
            CancelText: localizer.GetOrDefault(
                "MacRawByNcRisk.Dialog.ForceRun",
                "Force Run",
                "App.MacRawByNcRiskPrompt"),
            Language: language);
        var completion = await _dialogService.ShowWarningConfirmAsync(
            request,
            $"{prompt.SourceScope}.MacRawByNcRisk",
            cancellationToken).ConfigureAwait(false);

        return completion.Return switch
        {
            DialogReturnSemantic.Confirm => MacRawByNcRiskConnectionDecision.ApplyRecommended,
            DialogReturnSemantic.Cancel => MacRawByNcRiskConnectionDecision.ForceRun,
            _ => MacRawByNcRiskConnectionDecision.Cancel,
        };
    }

    private string ResolveLanguage(string? promptLanguage)
    {
        if (!string.IsNullOrWhiteSpace(_languageCoordinator?.CurrentLanguage))
        {
            return UiLanguageCatalog.Normalize(_languageCoordinator.CurrentLanguage);
        }

        if (!string.IsNullOrWhiteSpace(promptLanguage))
        {
            return UiLanguageCatalog.Normalize(promptLanguage);
        }

        return UiLanguageCatalog.DefaultLanguage;
    }
}
