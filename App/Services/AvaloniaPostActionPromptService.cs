using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using System.Globalization;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Platform;

namespace MAAUnified.App.Services;

public sealed class AvaloniaPostActionPromptService : IPostActionPromptService
{
    private readonly IAppDialogService _dialogService;

    public AvaloniaPostActionPromptService(
        IClassicDesktopStyleApplicationLifetime desktopLifetime,
        IAppDialogService? dialogService = null)
    {
        ArgumentNullException.ThrowIfNull(desktopLifetime);
        _dialogService = dialogService ?? ResolveDialogService(desktopLifetime);
    }

    public async Task<UiOperationResult> ConfirmPowerActionAsync(
        PostActionPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Dispatcher.UIThread.CheckAccess())
        {
            return await ShowDialogAsync(request, cancellationToken);
        }

        return await await Dispatcher.UIThread.InvokeAsync(
            () => ShowDialogAsync(request, cancellationToken),
            DispatcherPriority.Normal,
            cancellationToken);
    }

    private async Task<UiOperationResult> ShowDialogAsync(
        PostActionPromptRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var seconds = Math.Max(1, (int)Math.Ceiling(request.Countdown.TotalSeconds));
        var dialogResult = await _dialogService.ShowWarningConfirmAsync(
            new WarningConfirmDialogRequest(
                Title: BuildTitle(request.Action, request.Language),
                Message: BuildMessage(request.Action, seconds, request.Language),
                ConfirmText: BuildConfirmText(request.Language),
                CancelText: DialogTextCatalog.WarningDialogCancelButton(request.Language),
                Language: request.Language ?? "en-us",
                CountdownSeconds: seconds),
            "App.PostActionPrompt.PowerAction",
            cancellationToken);

        return dialogResult.Return switch
        {
            DialogReturnSemantic.Confirm => UiOperationResult.Ok($"{request.Action} confirmed."),
            DialogReturnSemantic.Cancel => UiOperationResult.Cancelled($"{request.Action} cancelled."),
            _ when string.Equals(dialogResult.Summary, "owner-unavailable", StringComparison.OrdinalIgnoreCase)
                || string.Equals(dialogResult.Summary, "dialog-service-unavailable", StringComparison.OrdinalIgnoreCase)
                => UiOperationResult.Fail(
                    UiErrorCode.PostActionExecutionFailed,
                    "Unable to show power action confirmation dialog because no desktop window is available."),
            _ => UiOperationResult.Cancelled($"{request.Action} dismissed."),
        };
    }

    private static IAppDialogService ResolveDialogService(IClassicDesktopStyleApplicationLifetime desktopLifetime)
    {
        ArgumentNullException.ThrowIfNull(desktopLifetime);
        return Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            ? new AvaloniaDialogService(App.Runtime)
            : NoOpAppDialogService.Instance;
    }

    private static string BuildTitle(PostActionType action, string? language)
    {
        var localizer = UiLocalizer.Create(UiLanguageCatalog.Normalize(language));
        return action switch
        {
            PostActionType.Shutdown => localizer.GetOrDefault("PostAction.Dialog.Title.Shutdown", "Confirm Shutdown", "App.PostActionPrompt"),
            PostActionType.Hibernate => localizer.GetOrDefault("PostAction.Dialog.Title.Hibernate", "Confirm Hibernate", "App.PostActionPrompt"),
            PostActionType.Sleep => localizer.GetOrDefault("PostAction.Dialog.Title.Sleep", "Confirm Sleep", "App.PostActionPrompt"),
            _ => DialogTextCatalog.WarningDialogTitle(language),
        };
    }

    private static string BuildMessage(PostActionType action, int seconds, string? language)
    {
        var localizer = UiLocalizer.Create(UiLanguageCatalog.Normalize(language));
        return action switch
        {
            PostActionType.Shutdown => string.Format(
                CultureInfo.CurrentCulture,
                localizer.GetOrDefault("PostAction.Dialog.Message.Shutdown", "MAA will shut down this computer in {0} seconds unless you cancel.", "App.PostActionPrompt"),
                seconds),
            PostActionType.Hibernate => string.Format(
                CultureInfo.CurrentCulture,
                localizer.GetOrDefault("PostAction.Dialog.Message.Hibernate", "MAA will hibernate this computer in {0} seconds unless you cancel.", "App.PostActionPrompt"),
                seconds),
            PostActionType.Sleep => string.Format(
                CultureInfo.CurrentCulture,
                localizer.GetOrDefault("PostAction.Dialog.Message.Sleep", "MAA will put this computer to sleep in {0} seconds unless you cancel.", "App.PostActionPrompt"),
                seconds),
            _ => DialogTextCatalog.WarningDialogPrompt(language),
        };
    }

    private static string BuildConfirmText(string? language)
    {
        return UiLocalizer.Create(UiLanguageCatalog.Normalize(language))
            .GetOrDefault("PostAction.Dialog.ConfirmNow", "Run Now", "App.PostActionPrompt");
    }
}
