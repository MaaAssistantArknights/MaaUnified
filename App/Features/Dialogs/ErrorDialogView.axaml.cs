using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.Infrastructure;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;

namespace MAAUnified.App.Features.Dialogs;

public partial class ErrorDialogView : Window, IDialogChromeAware
{
    private Func<CancellationToken, Task<UiOperationResult>>? _openIssueReportAsync;
    private bool _copied;
    private bool _issueOpened;
    private string _formattedText = string.Empty;

    public ErrorDialogView()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
    }

    public void ApplyRequest(
        ErrorDialogRequest request,
        Func<CancellationToken, Task<UiOperationResult>>? openIssueReportAsync = null)
    {
        _copied = false;
        _issueOpened = false;
        Title = request.Title;
        _openIssueReportAsync = openIssueReportAsync;
        DialogShell.Title = request.Title;
        CopyButton.Content = DialogTextCatalog.ErrorDialogCopyButton(request.Language);
        IssueReportButton.Content = DialogTextCatalog.ErrorDialogIssueReportButton(request.Language);
        ConfirmButton.Content = request.ConfirmText;
        CancelButton.Content = request.CancelText;
        ContextLine.Text = $"[{request.Context}] {request.Result.Message}";
        _formattedText = BuildFormattedErrorText(request);
        ErrorDetailBox.Text = _formattedText;
        ErrorDetailBox.CaretIndex = 0;
        IssueReportButton.IsVisible = _openIssueReportAsync is not null;
        IssueReportButton.IsEnabled = _openIssueReportAsync is not null;
    }

    public ErrorDialogPayload BuildPayload()
    {
        return new ErrorDialogPayload(
            FormattedErrorText: _formattedText,
            Copied: _copied,
            IssueReportOpened: _issueOpened);
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

    public void ApplyDialogChrome(DialogChromeSnapshot chrome)
    {
        Title = chrome.Title;
        DialogShell.Title = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.SectionTitle, chrome.Title);
        CopyButton.Content = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.CopyButton, CopyButton.Content?.ToString() ?? "Copy");
        IssueReportButton.Content = chrome.GetNamedTextOrDefault(
            DialogTextCatalog.ChromeKeys.IssueReportButton,
            IssueReportButton.Content?.ToString() ?? "IssueReport");
        ConfirmButton.Content = chrome.ConfirmText ?? ConfirmButton.Content;
        CancelButton.Content = chrome.CancelText ?? CancelButton.Content;
    }
}
