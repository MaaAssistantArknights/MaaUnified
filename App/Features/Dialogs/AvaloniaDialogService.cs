using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.App.Features.Dialogs;

public sealed class AvaloniaDialogService : IAppDialogService
{
    private const string IssueReportIssueEntryUrl = "https://github.com/MaaAssistantArknights/MaaAssistantArknights/issues/new/choose";
    // All desktop dialog service instances share owner-modal state through the same main window.
    private static readonly SemaphoreSlim DialogPresentationSemaphore = new(1, 1);
    private static int _activeOwnerModalCount;
    private readonly MAAUnifiedRuntime _runtime;

    public AvaloniaDialogService(MAAUnifiedRuntime runtime)
    {
        _runtime = runtime;
    }

    public static event EventHandler? OwnerModalStateChanged;

    public static bool HasActiveOwnerModal => Volatile.Read(ref _activeOwnerModalCount) > 0;

    public async Task<DialogCompletion<AnnouncementDialogPayload>> ShowAnnouncementAsync(
        AnnouncementDialogRequest request,
        string sourceScope,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequest = request with
        {
            Title = NormalizeDialogTitle(request.Title),
        };
        var token = await _runtime.DialogFeatureService.BeginDialogAsync(DialogType.Announcement, sourceScope, normalizedRequest.Title, cancellationToken);
        var dialog = new AnnouncementDialogView();
        dialog.ApplyRequest(normalizedRequest);
        using var chromeBinding = AttachChromeLocalization(dialog, normalizedRequest.Title, normalizedRequest.Chrome);
        var presentation = await ShowDialogWithOwnerScaleAsync<DialogReturnSemantic?>(dialog, cancellationToken);
        if (!presentation.OwnerAvailable)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "owner", "owner-unavailable", cancellationToken);
            await _runtime.DialogFeatureService.CompleteDialogAsync(token, DialogReturnSemantic.Close, "owner-unavailable", cancellationToken);
            return new DialogCompletion<AnnouncementDialogPayload>(DialogReturnSemantic.Close, null, "owner-unavailable");
        }

        var semantic = presentation.Result ?? DialogReturnSemantic.Close;
        var payload = semantic == DialogReturnSemantic.Cancel ? null : dialog.BuildPayload();
        await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "return", semantic.ToString(), cancellationToken);
        await _runtime.DialogFeatureService.CompleteDialogAsync(token, semantic, "announcement-dialog-complete", cancellationToken);
        return new DialogCompletion<AnnouncementDialogPayload>(semantic, payload, "announcement-dialog-complete");
    }

    public async Task<DialogCompletion<VersionUpdateDialogPayload>> ShowVersionUpdateAsync(
        VersionUpdateDialogRequest request,
        string sourceScope,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequest = request with
        {
            Title = NormalizeDialogTitle(request.Title),
        };
        var token = await _runtime.DialogFeatureService.BeginDialogAsync(DialogType.VersionUpdate, sourceScope, normalizedRequest.Title, cancellationToken);
        var dialog = new VersionUpdateDialogView();
        dialog.ApplyRequest(normalizedRequest);
        using var chromeBinding = AttachChromeLocalization(dialog, normalizedRequest.Title, normalizedRequest.Chrome);
        var presentation = await ShowDialogWithOwnerScaleAsync<DialogReturnSemantic?>(dialog, cancellationToken);
        if (!presentation.OwnerAvailable)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "owner", "owner-unavailable", cancellationToken);
            await _runtime.DialogFeatureService.CompleteDialogAsync(token, DialogReturnSemantic.Close, "owner-unavailable", cancellationToken);
            return new DialogCompletion<VersionUpdateDialogPayload>(DialogReturnSemantic.Close, null, "owner-unavailable");
        }

        var semantic = presentation.Result ?? DialogReturnSemantic.Close;
        var payload = semantic == DialogReturnSemantic.Confirm ? dialog.BuildPayload() : null;
        await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "return", semantic.ToString(), cancellationToken);
        await _runtime.DialogFeatureService.CompleteDialogAsync(token, semantic, "version-update-dialog-complete", cancellationToken);
        return new DialogCompletion<VersionUpdateDialogPayload>(semantic, payload, "version-update-dialog-complete");
    }

    public async Task<DialogCompletion<ProcessPickerDialogPayload>> ShowProcessPickerAsync(
        ProcessPickerDialogRequest request,
        string sourceScope,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequest = request with
        {
            Title = NormalizeDialogTitle(request.Title),
        };
        var token = await _runtime.DialogFeatureService.BeginDialogAsync(DialogType.ProcessPicker, sourceScope, normalizedRequest.Title, cancellationToken);
        var dialog = new ProcessPickerDialogView();
        dialog.ApplyRequest(normalizedRequest);
        using var chromeBinding = AttachChromeLocalization(dialog, normalizedRequest.Title, normalizedRequest.Chrome);
        var presentation = await ShowDialogWithOwnerScaleAsync<DialogReturnSemantic?>(dialog, cancellationToken);
        if (!presentation.OwnerAvailable)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "owner", "owner-unavailable", cancellationToken);
            await _runtime.DialogFeatureService.CompleteDialogAsync(token, DialogReturnSemantic.Close, "owner-unavailable", cancellationToken);
            return new DialogCompletion<ProcessPickerDialogPayload>(DialogReturnSemantic.Close, null, "owner-unavailable");
        }

        var semantic = presentation.Result ?? DialogReturnSemantic.Close;
        var payload = semantic == DialogReturnSemantic.Confirm ? dialog.BuildPayload() : null;
        await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "return", semantic.ToString(), cancellationToken);
        await _runtime.DialogFeatureService.CompleteDialogAsync(token, semantic, "process-picker-dialog-complete", cancellationToken);
        return new DialogCompletion<ProcessPickerDialogPayload>(semantic, payload, "process-picker-dialog-complete");
    }

    public async Task<DialogCompletion<EmulatorPathDialogPayload>> ShowEmulatorPathAsync(
        EmulatorPathDialogRequest request,
        string sourceScope,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequest = request with
        {
            Title = NormalizeDialogTitle(request.Title),
        };
        var token = await _runtime.DialogFeatureService.BeginDialogAsync(DialogType.EmulatorPath, sourceScope, normalizedRequest.Title, cancellationToken);
        var dialog = new EmulatorPathSelectionDialogView();
        dialog.ApplyRequest(normalizedRequest);
        using var chromeBinding = AttachChromeLocalization(dialog, normalizedRequest.Title, normalizedRequest.Chrome);
        var presentation = await ShowDialogWithOwnerScaleAsync<DialogReturnSemantic?>(dialog, cancellationToken);
        if (!presentation.OwnerAvailable)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "owner", "owner-unavailable", cancellationToken);
            await _runtime.DialogFeatureService.CompleteDialogAsync(token, DialogReturnSemantic.Close, "owner-unavailable", cancellationToken);
            return new DialogCompletion<EmulatorPathDialogPayload>(DialogReturnSemantic.Close, null, "owner-unavailable");
        }

        var semantic = presentation.Result ?? DialogReturnSemantic.Close;
        var payload = semantic == DialogReturnSemantic.Confirm ? dialog.BuildPayload() : null;
        await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "return", semantic.ToString(), cancellationToken);
        await _runtime.DialogFeatureService.CompleteDialogAsync(token, semantic, "emulator-path-dialog-complete", cancellationToken);
        return new DialogCompletion<EmulatorPathDialogPayload>(semantic, payload, "emulator-path-dialog-complete");
    }

    public async Task<DialogCompletion<ErrorDialogPayload>> ShowErrorAsync(
        ErrorDialogRequest request,
        string sourceScope,
        Func<CancellationToken, Task<UiOperationResult>>? openIssueReportAsync = null,
        Func<CancellationToken, Task<UiOperationResult>>? openSettingsAsync = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequest = request with
        {
            Title = NormalizeDialogTitle(request.Title),
        };
        var token = await _runtime.DialogFeatureService.BeginDialogAsync(DialogType.Error, sourceScope, normalizedRequest.Title, cancellationToken);
        var dialog = new ErrorDialogView();
        dialog.ApplyRequest(normalizedRequest, openIssueReportAsync ?? OpenIssueReportAsync, openSettingsAsync);
        using var chromeBinding = AttachChromeLocalization(dialog, normalizedRequest.Title, normalizedRequest.Chrome);
        var presentation = await ShowDialogWithOwnerScaleAsync<DialogReturnSemantic?>(dialog, cancellationToken);
        if (!presentation.OwnerAvailable)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "owner", "owner-unavailable", cancellationToken);
            await _runtime.DialogFeatureService.CompleteDialogAsync(token, DialogReturnSemantic.Close, "owner-unavailable", cancellationToken);
            return new DialogCompletion<ErrorDialogPayload>(DialogReturnSemantic.Close, null, "owner-unavailable");
        }

        var semantic = presentation.Result ?? DialogReturnSemantic.Close;
        var payload = dialog.BuildPayload();
        if (payload.Copied)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "copy", "clipboard", cancellationToken);
        }

        if (payload.IssueReportOpened)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "open-issue", "issue-report-entry", cancellationToken);
        }

        if (payload.SettingsOpened)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "open-settings", "connection", cancellationToken);
        }

        await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "return", semantic.ToString(), cancellationToken);
        await _runtime.DialogFeatureService.CompleteDialogAsync(token, semantic, "error-dialog-complete", cancellationToken);
        return new DialogCompletion<ErrorDialogPayload>(semantic, payload, "error-dialog-complete");
    }

    public async Task<DialogCompletion<AchievementListDialogPayload>> ShowAchievementListAsync(
        AchievementListDialogRequest request,
        string sourceScope,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequest = request with
        {
            Title = NormalizeDialogTitle(request.Title),
        };
        var token = await _runtime.DialogFeatureService.BeginDialogAsync(DialogType.AchievementList, sourceScope, normalizedRequest.Title, cancellationToken);
        var dialog = new AchievementListDialogView();
        dialog.ApplyRequest(normalizedRequest);
        using var chromeBinding = AttachChromeLocalization(dialog, normalizedRequest.Title, normalizedRequest.Chrome);
        var presentation = await ShowDialogWithOwnerScaleAsync<DialogReturnSemantic?>(dialog, cancellationToken);
        if (!presentation.OwnerAvailable)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "owner", "owner-unavailable", cancellationToken);
            await _runtime.DialogFeatureService.CompleteDialogAsync(token, DialogReturnSemantic.Close, "owner-unavailable", cancellationToken);
            return new DialogCompletion<AchievementListDialogPayload>(DialogReturnSemantic.Close, null, "owner-unavailable");
        }

        var semantic = presentation.Result ?? DialogReturnSemantic.Close;
        var payload = dialog.BuildPayload();
        await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "return", semantic.ToString(), cancellationToken);
        await _runtime.DialogFeatureService.CompleteDialogAsync(token, semantic, "achievement-list-dialog-complete", cancellationToken);
        return new DialogCompletion<AchievementListDialogPayload>(semantic, payload, "achievement-list-dialog-complete");
    }

    public async Task<DialogCompletion<TextDialogPayload>> ShowTextAsync(
        TextDialogRequest request,
        string sourceScope,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequest = request with
        {
            Title = NormalizeDialogTitle(request.Title),
        };
        var token = await _runtime.DialogFeatureService.BeginDialogAsync(DialogType.Text, sourceScope, normalizedRequest.Title, cancellationToken);
        var dialog = new TextDialogView();
        dialog.ApplyRequest(normalizedRequest);
        using var chromeBinding = AttachChromeLocalization(dialog, normalizedRequest.Title, normalizedRequest.Chrome);
        var presentation = await ShowDialogWithOwnerScaleAsync<DialogReturnSemantic?>(dialog, cancellationToken);
        if (!presentation.OwnerAvailable)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "owner", "owner-unavailable", cancellationToken);
            await _runtime.DialogFeatureService.CompleteDialogAsync(token, DialogReturnSemantic.Close, "owner-unavailable", cancellationToken);
            return new DialogCompletion<TextDialogPayload>(DialogReturnSemantic.Close, null, "owner-unavailable");
        }

        var semantic = presentation.Result ?? DialogReturnSemantic.Close;
        var payload = semantic == DialogReturnSemantic.Confirm ? dialog.BuildPayload() : null;
        await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "return", semantic.ToString(), cancellationToken);
        await _runtime.DialogFeatureService.CompleteDialogAsync(token, semantic, "text-dialog-complete", cancellationToken);
        return new DialogCompletion<TextDialogPayload>(semantic, payload, "text-dialog-complete");
    }

    public async Task<DialogCompletion<WarningConfirmDialogPayload>> ShowWarningConfirmAsync(
        WarningConfirmDialogRequest request,
        string sourceScope,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequest = request with
        {
            Title = NormalizeDialogTitle(request.Title),
        };
        var token = await _runtime.DialogFeatureService.BeginDialogAsync(DialogType.WarningConfirm, sourceScope, normalizedRequest.Title, cancellationToken);
        var dialog = new WarningConfirmDialogView();
        dialog.ApplyRequest(
            normalizedRequest.Title,
            normalizedRequest.Message,
            normalizedRequest.ConfirmText,
            normalizedRequest.CancelText,
            normalizedRequest.Language,
            normalizedRequest.CountdownSeconds);
        using var chromeBinding = AttachChromeLocalization(dialog, normalizedRequest.Title, normalizedRequest.Chrome);
        var presentation = await ShowDialogWithOwnerScaleAsync<DialogReturnSemantic?>(dialog, cancellationToken);
        if (!presentation.OwnerAvailable)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "owner", "owner-unavailable", cancellationToken);
            await _runtime.DialogFeatureService.CompleteDialogAsync(token, DialogReturnSemantic.Close, "owner-unavailable", cancellationToken);
            return new DialogCompletion<WarningConfirmDialogPayload>(DialogReturnSemantic.Close, null, "owner-unavailable");
        }

        var semantic = presentation.Result ?? DialogReturnSemantic.Close;
        var payload = semantic == DialogReturnSemantic.Confirm
            ? new WarningConfirmDialogPayload(true)
            : null;
        var summary = semantic switch
        {
            DialogReturnSemantic.Confirm => "warning-confirm-dialog-confirmed",
            DialogReturnSemantic.Cancel => "warning-confirm-dialog-cancelled",
            DialogReturnSemantic.Details => "warning-confirm-dialog-details",
            _ => "warning-confirm-dialog-closed",
        };

        await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "return", semantic.ToString(), cancellationToken);
        await _runtime.DialogFeatureService.CompleteDialogAsync(token, semantic, summary, cancellationToken);
        return new DialogCompletion<WarningConfirmDialogPayload>(semantic, payload, summary);
    }

    private static Window? ResolveOwnerWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        if (desktop.MainWindow is { IsVisible: true } mainWindow)
        {
            return mainWindow;
        }

        return desktop.Windows.LastOrDefault(static window => window.IsVisible);
    }

    private static async Task<DialogPresentationResult<TResult>> ShowDialogWithOwnerScaleAsync<TResult>(
        Window dialog,
        CancellationToken cancellationToken)
    {
        await DialogPresentationSemaphore.WaitAsync(cancellationToken);
        try
        {
            var owner = ResolveOwnerWindow();
            if (owner is null)
            {
                return new DialogPresentationResult<TResult>(false, default);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (owner.WindowState == WindowState.Minimized)
            {
                owner.WindowState = WindowState.Normal;
            }

            if (!owner.IsVisible)
            {
                owner.Show();
            }

            owner.Activate();
            dialog.Topmost = ResolveTopmost(dialog, owner);
            DialogWindowScaling.ApplyOwnerUiScale(dialog, owner);
            BeginOwnerModalPresentation();
            try
            {
                return new DialogPresentationResult<TResult>(true, await dialog.ShowDialog<TResult>(owner));
            }
            finally
            {
                EndOwnerModalPresentation();
            }
        }
        finally
        {
            DialogPresentationSemaphore.Release();
        }
    }

    private readonly record struct DialogPresentationResult<TResult>(bool OwnerAvailable, TResult? Result);

    private static void BeginOwnerModalPresentation()
    {
        if (Interlocked.Increment(ref _activeOwnerModalCount) == 1)
        {
            RaiseOwnerModalStateChanged();
        }
    }

    private static void EndOwnerModalPresentation()
    {
        if (Interlocked.Decrement(ref _activeOwnerModalCount) == 0)
        {
            RaiseOwnerModalStateChanged();
        }
    }

    private static void RaiseOwnerModalStateChanged()
    {
        var handler = OwnerModalStateChanged;
        if (handler is null)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            handler.Invoke(null, EventArgs.Empty);
            return;
        }

        Dispatcher.UIThread.Post(() => handler.Invoke(null, EventArgs.Empty), DispatcherPriority.Background);
    }

    private static bool ResolveTopmost(Window dialog, Window owner)
    {
        if (dialog.Topmost || owner.Topmost)
        {
            return true;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return false;
        }

        return desktop.Windows.Any(window => !ReferenceEquals(window, dialog) && window.IsVisible && window.Topmost);
    }

    private static Task<UiOperationResult> OpenIssueReportAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Process.Start(new ProcessStartInfo(IssueReportIssueEntryUrl)
            {
                UseShellExecute = true,
            });
            return Task.FromResult(UiOperationResult.Ok("IssueReport entry opened."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(UiOperationResult.Fail(
                UiErrorCode.PlatformOperationFailed,
                $"Failed to open IssueReport entry: {ex.Message}",
                ex.Message));
        }
    }

    private IDisposable? AttachChromeLocalization(Window dialog, string fallbackTitle, DialogChromeCatalog? chromeCatalog)
    {
        if (chromeCatalog is null)
        {
            return null;
        }

        var binding = new DialogChromeBinding(_runtime.UiLanguageCoordinator, dialog, fallbackTitle, chromeCatalog);
        binding.Attach();
        return binding;
    }

    private static string NormalizeDialogTitle(string title)
    {
        return string.IsNullOrWhiteSpace(title) ? "Dialog" : title.Trim();
    }

    private sealed class DialogChromeBinding : IDisposable
    {
        private readonly IUiLanguageCoordinator _uiLanguageCoordinator;
        private readonly Window _dialog;
        private readonly string _fallbackTitle;
        private readonly DialogChromeCatalog _chromeCatalog;
        private bool _disposed;

        public DialogChromeBinding(
            IUiLanguageCoordinator uiLanguageCoordinator,
            Window dialog,
            string fallbackTitle,
            DialogChromeCatalog chromeCatalog)
        {
            _uiLanguageCoordinator = uiLanguageCoordinator;
            _dialog = dialog;
            _fallbackTitle = fallbackTitle;
            _chromeCatalog = chromeCatalog;
        }

        public void Attach()
        {
            Apply(_uiLanguageCoordinator.CurrentLanguage);
            _uiLanguageCoordinator.LanguageChanged += OnLanguageChanged;
            _dialog.Closed += OnDialogClosed;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _uiLanguageCoordinator.LanguageChanged -= OnLanguageChanged;
            _dialog.Closed -= OnDialogClosed;
        }

        private void OnDialogClosed(object? sender, EventArgs e)
        {
            Dispose();
        }

        private void OnLanguageChanged(object? sender, UiLanguageChangedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            if (Avalonia.Application.Current is null)
            {
                Apply(e.CurrentLanguage);
                return;
            }

            Dispatcher.UIThread.Post(() => Apply(e.CurrentLanguage), DispatcherPriority.Background);
        }

        private void Apply(string? language)
        {
            if (_disposed)
            {
                return;
            }

            var chrome = _chromeCatalog.GetSnapshot(language);
            var resolvedTitle = string.IsNullOrWhiteSpace(chrome.Title) ? _fallbackTitle : chrome.Title;
            _dialog.Title = NormalizeDialogTitle(resolvedTitle);
            if (_dialog is IDialogChromeAware chromeAware)
            {
                chromeAware.ApplyDialogChrome(chrome);
            }
        }
    }
}
