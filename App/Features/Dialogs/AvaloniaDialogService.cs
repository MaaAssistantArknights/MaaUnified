using System.Diagnostics;
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
    private readonly MAAUnifiedRuntime _runtime;

    public AvaloniaDialogService(MAAUnifiedRuntime runtime)
    {
        _runtime = runtime;
    }

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
        var owner = ResolveOwnerWindow();
        if (owner is null)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "owner", "owner-unavailable", cancellationToken);
            await _runtime.DialogFeatureService.CompleteDialogAsync(token, DialogReturnSemantic.Close, "owner-unavailable", cancellationToken);
            return new DialogCompletion<AnnouncementDialogPayload>(DialogReturnSemantic.Close, null, "owner-unavailable");
        }

        var dialog = new AnnouncementDialogView();
        dialog.ApplyRequest(normalizedRequest);
        using var chromeBinding = AttachChromeLocalization(dialog, normalizedRequest.Title, normalizedRequest.Chrome);
        var semantic = await dialog.ShowDialog<DialogReturnSemantic?>(owner) ?? DialogReturnSemantic.Close;
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
        var owner = ResolveOwnerWindow();
        if (owner is null)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "owner", "owner-unavailable", cancellationToken);
            await _runtime.DialogFeatureService.CompleteDialogAsync(token, DialogReturnSemantic.Close, "owner-unavailable", cancellationToken);
            return new DialogCompletion<VersionUpdateDialogPayload>(DialogReturnSemantic.Close, null, "owner-unavailable");
        }

        var dialog = new VersionUpdateDialogView();
        dialog.ApplyRequest(normalizedRequest);
        using var chromeBinding = AttachChromeLocalization(dialog, normalizedRequest.Title, normalizedRequest.Chrome);
        var semantic = await dialog.ShowDialog<DialogReturnSemantic?>(owner) ?? DialogReturnSemantic.Close;
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
        var owner = ResolveOwnerWindow();
        if (owner is null)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "owner", "owner-unavailable", cancellationToken);
            await _runtime.DialogFeatureService.CompleteDialogAsync(token, DialogReturnSemantic.Close, "owner-unavailable", cancellationToken);
            return new DialogCompletion<ProcessPickerDialogPayload>(DialogReturnSemantic.Close, null, "owner-unavailable");
        }

        var dialog = new ProcessPickerDialogView();
        dialog.ApplyRequest(normalizedRequest);
        using var chromeBinding = AttachChromeLocalization(dialog, normalizedRequest.Title, normalizedRequest.Chrome);
        var semantic = await dialog.ShowDialog<DialogReturnSemantic?>(owner) ?? DialogReturnSemantic.Close;
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
        var owner = ResolveOwnerWindow();
        if (owner is null)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "owner", "owner-unavailable", cancellationToken);
            await _runtime.DialogFeatureService.CompleteDialogAsync(token, DialogReturnSemantic.Close, "owner-unavailable", cancellationToken);
            return new DialogCompletion<EmulatorPathDialogPayload>(DialogReturnSemantic.Close, null, "owner-unavailable");
        }

        var dialog = new EmulatorPathSelectionDialogView();
        dialog.ApplyRequest(normalizedRequest);
        using var chromeBinding = AttachChromeLocalization(dialog, normalizedRequest.Title, normalizedRequest.Chrome);
        var semantic = await dialog.ShowDialog<DialogReturnSemantic?>(owner) ?? DialogReturnSemantic.Close;
        var payload = semantic == DialogReturnSemantic.Confirm ? dialog.BuildPayload() : null;
        await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "return", semantic.ToString(), cancellationToken);
        await _runtime.DialogFeatureService.CompleteDialogAsync(token, semantic, "emulator-path-dialog-complete", cancellationToken);
        return new DialogCompletion<EmulatorPathDialogPayload>(semantic, payload, "emulator-path-dialog-complete");
    }

    public async Task<DialogCompletion<ErrorDialogPayload>> ShowErrorAsync(
        ErrorDialogRequest request,
        string sourceScope,
        Func<CancellationToken, Task<UiOperationResult>>? openIssueReportAsync = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequest = request with
        {
            Title = NormalizeDialogTitle(request.Title),
        };
        var token = await _runtime.DialogFeatureService.BeginDialogAsync(DialogType.Error, sourceScope, normalizedRequest.Title, cancellationToken);
        var owner = ResolveOwnerWindow();
        if (owner is null)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "owner", "owner-unavailable", cancellationToken);
            await _runtime.DialogFeatureService.CompleteDialogAsync(token, DialogReturnSemantic.Close, "owner-unavailable", cancellationToken);
            return new DialogCompletion<ErrorDialogPayload>(DialogReturnSemantic.Close, null, "owner-unavailable");
        }

        var dialog = new ErrorDialogView();
        dialog.ApplyRequest(normalizedRequest, openIssueReportAsync ?? OpenIssueReportAsync);
        using var chromeBinding = AttachChromeLocalization(dialog, normalizedRequest.Title, normalizedRequest.Chrome);
        var semantic = await dialog.ShowDialog<DialogReturnSemantic?>(owner) ?? DialogReturnSemantic.Close;
        var payload = dialog.BuildPayload();
        if (payload.Copied)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "copy", "clipboard", cancellationToken);
        }

        if (payload.IssueReportOpened)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "open-issue", "issue-report-entry", cancellationToken);
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
        var owner = ResolveOwnerWindow();
        if (owner is null)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "owner", "owner-unavailable", cancellationToken);
            await _runtime.DialogFeatureService.CompleteDialogAsync(token, DialogReturnSemantic.Close, "owner-unavailable", cancellationToken);
            return new DialogCompletion<AchievementListDialogPayload>(DialogReturnSemantic.Close, null, "owner-unavailable");
        }

        var dialog = new AchievementListDialogView();
        dialog.ApplyRequest(normalizedRequest);
        using var chromeBinding = AttachChromeLocalization(dialog, normalizedRequest.Title, normalizedRequest.Chrome);
        var semantic = await dialog.ShowDialog<DialogReturnSemantic?>(owner) ?? DialogReturnSemantic.Close;
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
        var owner = ResolveOwnerWindow();
        if (owner is null)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "owner", "owner-unavailable", cancellationToken);
            await _runtime.DialogFeatureService.CompleteDialogAsync(token, DialogReturnSemantic.Close, "owner-unavailable", cancellationToken);
            return new DialogCompletion<TextDialogPayload>(DialogReturnSemantic.Close, null, "owner-unavailable");
        }

        var dialog = new TextDialogView();
        dialog.ApplyRequest(normalizedRequest);
        using var chromeBinding = AttachChromeLocalization(dialog, normalizedRequest.Title, normalizedRequest.Chrome);
        var semantic = await dialog.ShowDialog<DialogReturnSemantic?>(owner) ?? DialogReturnSemantic.Close;
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
        var owner = ResolveOwnerWindow();
        if (owner is null)
        {
            await _runtime.DialogFeatureService.RecordDialogActionAsync(token, "owner", "owner-unavailable", cancellationToken);
            await _runtime.DialogFeatureService.CompleteDialogAsync(token, DialogReturnSemantic.Close, "owner-unavailable", cancellationToken);
            return new DialogCompletion<WarningConfirmDialogPayload>(DialogReturnSemantic.Close, null, "owner-unavailable");
        }

        var dialog = new WarningConfirmDialogView();
        dialog.ApplyRequest(
            normalizedRequest.Title,
            normalizedRequest.Message,
            normalizedRequest.ConfirmText,
            normalizedRequest.CancelText,
            normalizedRequest.Language,
            normalizedRequest.CountdownSeconds);
        using var chromeBinding = AttachChromeLocalization(dialog, normalizedRequest.Title, normalizedRequest.Chrome);
        var confirmed = await dialog.ShowDialog<bool?>(owner);
        var semantic = confirmed switch
        {
            true => DialogReturnSemantic.Confirm,
            false => DialogReturnSemantic.Cancel,
            null => DialogReturnSemantic.Close,
        };
        var payload = semantic == DialogReturnSemantic.Confirm
            ? new WarningConfirmDialogPayload(true)
            : null;
        var summary = semantic switch
        {
            DialogReturnSemantic.Confirm => "warning-confirm-dialog-confirmed",
            DialogReturnSemantic.Cancel => "warning-confirm-dialog-cancelled",
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

        return desktop.MainWindow;
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
