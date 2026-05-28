using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.Infrastructure;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;

namespace MAAUnified.App.Features.Dialogs;

public partial class ErrorDialogView : Window, IDialogChromeAware
{
    private const double DefaultWidth = 760d;
    private const double DefaultHeight = 540d;
    private const double DefaultMinWidth = 640d;
    private const double DefaultMinHeight = 420d;
    private const double SimpleWidth = 620d;
    private const double SimpleMinWidth = 520d;

    private Func<CancellationToken, Task<UiOperationResult>>? _openIssueReportAsync;
    private Func<CancellationToken, Task<UiOperationResult>>? _openSettingsAsync;
    private bool _copied;
    private bool _issueOpened;
    private bool _settingsOpened;
    private bool _simpleConnectFailureMode;
    private string _language = "en-us";
    private string _formattedText = string.Empty;
    private ErrorDialogRequest? _request;

    public ErrorDialogView()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
    }

    public void ApplyRequest(
        ErrorDialogRequest request,
        Func<CancellationToken, Task<UiOperationResult>>? openIssueReportAsync = null,
        Func<CancellationToken, Task<UiOperationResult>>? openSettingsAsync = null)
    {
        _copied = false;
        _issueOpened = false;
        _settingsOpened = false;
        _request = request;
        _language = request.Language;
        _simpleConnectFailureMode = request.Result.Error?.Code == UiErrorCode.ConnectFailed;
        Title = request.Title;
        _openIssueReportAsync = openIssueReportAsync;
        _openSettingsAsync = openSettingsAsync;
        DialogShell.Title = request.Title;
        ApplyWindowSizeMode();
        CopyButton.Content = _simpleConnectFailureMode
            ? DialogTextCatalog.ErrorDialogCopyErrorInfoButton(request.Language)
            : DialogTextCatalog.ErrorDialogCopyButton(request.Language);
        IssueReportButton.Content = DialogTextCatalog.ErrorDialogIssueReportButton(request.Language);
        OpenSettingsButton.Content = DialogTextCatalog.ErrorDialogOpenSettingsButton(request.Language);
        ConfirmButton.Content = _simpleConnectFailureMode
            ? DialogTextCatalog.WarningDialogConfirmButton(request.Language)
            : request.ConfirmText;
        CancelButton.Content = request.CancelText;
        var code = request.Result.Error?.Code ?? UiErrorCode.UiOperationFailed;
        ContextLine.Text = $"[{request.Context}] {code}";
        ContextLine.IsVisible = !_simpleConnectFailureMode;
        FriendlyMessageText.Text = _simpleConnectFailureMode
            ? request.Suggestion ?? request.Result.Message
            : request.Result.Message;
        FriendlyMessageText.Classes.Set("error-dialog-simple-message", _simpleConnectFailureMode);
        SuggestionText.Text = _simpleConnectFailureMode ? string.Empty : request.Suggestion ?? string.Empty;
        SuggestionPanel.IsVisible = !_simpleConnectFailureMode && !string.IsNullOrWhiteSpace(request.Suggestion);
        SummaryHero.IsVisible = !_simpleConnectFailureMode;
        Grid.SetColumn(SummaryContentPanel, _simpleConnectFailureMode ? 0 : 1);
        Grid.SetColumnSpan(SummaryContentPanel, _simpleConnectFailureMode ? 2 : 1);
        SummaryContentPanel.Margin = _simpleConnectFailureMode ? new Thickness(0, 2, 0, 0) : new Thickness(18, 2, 0, 0);
        DetailHost.IsVisible = !_simpleConnectFailureMode;
        DetailSectionTitle.Text = DialogTextCatalog.ErrorDialogSectionTitle(request.Language);
        _formattedText = BuildFormattedErrorText(request);
        ErrorDetailBox.Text = _formattedText;
        ErrorDetailBox.CaretIndex = 0;
        IssueReportButton.IsVisible = !_simpleConnectFailureMode && _openIssueReportAsync is not null;
        IssueReportButton.IsEnabled = !_simpleConnectFailureMode && _openIssueReportAsync is not null;
        OpenSettingsButton.IsVisible = _simpleConnectFailureMode && _openSettingsAsync is not null;
        OpenSettingsButton.IsEnabled = _simpleConnectFailureMode && _openSettingsAsync is not null;
        CancelButton.IsVisible = !_simpleConnectFailureMode;
    }

    private void ApplyWindowSizeMode()
    {
        if (_simpleConnectFailureMode)
        {
            Width = SimpleWidth;
            MinWidth = SimpleMinWidth;
            Height = double.NaN;
            MinHeight = 0d;
            SizeToContent = SizeToContent.Height;
            return;
        }

        Width = DefaultWidth;
        MinWidth = DefaultMinWidth;
        Height = DefaultHeight;
        MinHeight = DefaultMinHeight;
        SizeToContent = SizeToContent.Manual;
    }

    public ErrorDialogPayload BuildPayload()
    {
        return new ErrorDialogPayload(
            FormattedErrorText: _formattedText,
            Copied: _copied,
            IssueReportOpened: _issueOpened,
            SettingsOpened: _settingsOpened);
    }

    private static string BuildFormattedErrorText(ErrorDialogRequest request)
    {
        var code = request.Result.Error?.Code ?? UiErrorCode.UiOperationFailed;
        var details = request.Result.Error?.Details ?? string.Empty;
        var suggestion = request.Suggestion ?? string.Empty;
        var language = request.Language;
        return
            $"{DialogTextCatalog.ErrorDialogTimestampLabel(language)}: {DateTimeOffset.UtcNow:O}{Environment.NewLine}" +
            $"{DialogTextCatalog.ErrorDialogContextLabel(language)}: {request.Context}{Environment.NewLine}" +
            $"{DialogTextCatalog.ErrorDialogCodeLabel(language)}: {code}{Environment.NewLine}" +
            $"{DialogTextCatalog.ErrorDialogMessageLabel(language)}: {request.Result.Message}{Environment.NewLine}" +
            $"{DialogTextCatalog.ErrorDialogDetailsLabel(language)}: {details}{Environment.NewLine}" +
            $"{DialogTextCatalog.ErrorDialogSuggestionLabel(language)}: {suggestion}";
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_simpleConnectFailureMode && _request is not null)
        {
            ExpandSimpleErrorDetails(_request);
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is null)
        {
            return;
        }

        await topLevel.Clipboard.SetTextAsync(_formattedText);
        _copied = true;
    }

    private async void OnOpenIssueReportClick(object? sender, RoutedEventArgs e)
    {
        if (_openIssueReportAsync is null)
        {
            return;
        }

        IssueReportButton.IsEnabled = false;
        try
        {
            var result = await _openIssueReportAsync(CancellationToken.None);
            if (result.Success)
            {
                _issueOpened = true;
            }
        }
        finally
        {
            IssueReportButton.IsEnabled = _openIssueReportAsync is not null;
        }
    }

    private async void OnOpenSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (_openSettingsAsync is null)
        {
            return;
        }

        OpenSettingsButton.IsEnabled = false;
        try
        {
            var result = await _openSettingsAsync(CancellationToken.None);
            if (result.Success)
            {
                _settingsOpened = true;
                Close(DialogReturnSemantic.Details);
            }
        }
        finally
        {
            OpenSettingsButton.IsEnabled = _openSettingsAsync is not null;
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        Close(DialogReturnSemantic.Confirm);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(DialogReturnSemantic.Cancel);
    }

    private void OnShellCloseRequested(object? sender, EventArgs e)
    {
        Close();
    }

    private void ExpandSimpleErrorDetails(ErrorDialogRequest request)
    {
        _simpleConnectFailureMode = false;
        ApplyWindowSizeMode();

        CopyButton.Content = DialogTextCatalog.ErrorDialogCopyButton(_language);
        ConfirmButton.Content = request.ConfirmText;
        CancelButton.Content = request.CancelText;
        ContextLine.IsVisible = true;
        SummaryHero.IsVisible = true;
        DetailHost.IsVisible = true;
        IssueReportButton.IsVisible = _openIssueReportAsync is not null;
        IssueReportButton.IsEnabled = _openIssueReportAsync is not null;
        OpenSettingsButton.IsVisible = false;
        OpenSettingsButton.IsEnabled = false;
        CancelButton.IsVisible = true;

        FriendlyMessageText.Text = request.Result.Message;
        FriendlyMessageText.Classes.Set("error-dialog-simple-message", false);
        SuggestionText.Text = request.Suggestion ?? string.Empty;
        SuggestionPanel.IsVisible = !string.IsNullOrWhiteSpace(request.Suggestion);
        Grid.SetColumn(SummaryContentPanel, 1);
        Grid.SetColumnSpan(SummaryContentPanel, 1);
        SummaryContentPanel.Margin = new Thickness(18, 2, 0, 0);
    }

    public void ApplyDialogChrome(DialogChromeSnapshot chrome)
    {
        Title = chrome.Title;
        DialogShell.Title = chrome.Title;
        CopyButton.Content = _simpleConnectFailureMode
            ? chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.CopyButton, DialogTextCatalog.ErrorDialogCopyErrorInfoButton(_language))
            : DialogTextCatalog.ErrorDialogCopyButton(_language);
        if (_simpleConnectFailureMode)
        {
            FriendlyMessageText.Text = chrome.GetNamedTextOrDefault(
                DialogTextCatalog.ChromeKeys.Prompt,
                FriendlyMessageText.Text ?? string.Empty);
        }

        DetailSectionTitle.Text = chrome.GetNamedTextOrDefault(
            DialogTextCatalog.ChromeKeys.SectionTitle,
            DetailSectionTitle.Text ?? "Error details");
        IssueReportButton.Content = chrome.GetNamedTextOrDefault(
            DialogTextCatalog.ChromeKeys.IssueReportButton,
            IssueReportButton.Content?.ToString() ?? "IssueReport");
        OpenSettingsButton.Content = chrome.GetNamedTextOrDefault(
            DialogTextCatalog.ChromeKeys.OpenSettingsButton,
            OpenSettingsButton.Content?.ToString() ?? "Settings");
        ConfirmButton.Content = chrome.ConfirmText ?? ConfirmButton.Content;
        CancelButton.Content = chrome.CancelText ?? CancelButton.Content;
    }
}
