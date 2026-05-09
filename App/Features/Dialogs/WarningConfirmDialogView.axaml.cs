using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.Infrastructure;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;

namespace MAAUnified.App.Features.Dialogs;

public partial class WarningConfirmDialogView : Window, IDialogChromeAware
{
    private CancellationTokenSource? _countdownCts;
    private string _confirmSnapshot = string.Empty;
    private string _titleSnapshot = string.Empty;
    private string _messageSnapshot = string.Empty;
    private string _cancelSnapshot = string.Empty;
    private string _leadSnapshot = string.Empty;
    private string _emphasisSnapshot = string.Empty;
    private string _detailSnapshot = string.Empty;
    private string _detailsButtonSnapshot = string.Empty;
    private int _countdownSeconds;
    private int _remainingCountdownSeconds;

    public WarningConfirmDialogView()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
        Opened += OnOpened;
        Closed += OnClosed;
    }

    public void ApplyRequest(
        string title,
        string message,
        string confirmText = "",
        string cancelText = "",
        string? language = null,
        int countdownSeconds = 0)
    {
        StopCountdown();
        var effectiveLanguage = language ?? "en-us";
        _titleSnapshot = string.IsNullOrWhiteSpace(title) ? DialogTextCatalog.WarningDialogTitle(effectiveLanguage) : title.Trim();
        _messageSnapshot = string.IsNullOrWhiteSpace(message) ? DialogTextCatalog.WarningDialogPrompt(effectiveLanguage) : message.Trim();
        _confirmSnapshot = string.IsNullOrWhiteSpace(confirmText)
            ? DialogTextCatalog.WarningDialogConfirmButton(effectiveLanguage)
            : confirmText;
        _cancelSnapshot = string.IsNullOrWhiteSpace(cancelText)
            ? DialogTextCatalog.WarningDialogCancelButton(effectiveLanguage)
            : cancelText;
        _leadSnapshot = string.Empty;
        _emphasisSnapshot = string.Empty;
        _detailSnapshot = _messageSnapshot;
        _detailsButtonSnapshot = string.Empty;
        _countdownSeconds = Math.Max(0, countdownSeconds);
        _remainingCountdownSeconds = _countdownSeconds;
        Title = _titleSnapshot;
        DialogShell.Title = _titleSnapshot;
        ApplyContent(_leadSnapshot, _emphasisSnapshot, _detailSnapshot);
        ApplyDetailsButton(_detailsButtonSnapshot);
        CancelButton.Content = _cancelSnapshot;
        UpdateConfirmButtonText(_remainingCountdownSeconds);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        StopCountdown();
        Close(DialogReturnSemantic.Confirm);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        StopCountdown();
        Close(DialogReturnSemantic.Cancel);
    }

    private void OnDetailsClick(object? sender, RoutedEventArgs e)
    {
        StopCountdown();
        Close(DialogReturnSemantic.Details);
    }

    private void OnShellCloseRequested(object? sender, EventArgs e)
    {
        StopCountdown();
        Close();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_countdownSeconds > 0)
        {
            StartCountdown();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopCountdown();
        Closed -= OnClosed;
    }

    private async void StartCountdown()
    {
        StopCountdown();
        _countdownCts = new CancellationTokenSource();

        try
        {
            for (var remaining = _countdownSeconds; remaining > 0; remaining--)
            {
                _remainingCountdownSeconds = remaining;
                UpdateConfirmButtonText(remaining);
                await Task.Delay(TimeSpan.FromSeconds(1), _countdownCts.Token);
            }

            if (_countdownCts.IsCancellationRequested)
            {
                return;
            }

            _remainingCountdownSeconds = 0;
            UpdateConfirmButtonText(0);
            Close(DialogReturnSemantic.Confirm);
        }
        catch (OperationCanceledException)
        {
            // Countdown cancelled by user interaction or dialog close.
        }
    }

    private void StopCountdown()
    {
        _remainingCountdownSeconds = 0;
        _countdownCts?.Cancel();
        _countdownCts?.Dispose();
        _countdownCts = null;
    }

    private void UpdateConfirmButtonText(int remainingSeconds)
    {
        var confirmText = _confirmSnapshot;
        ConfirmButton.Content = remainingSeconds > 0
            ? $"{confirmText} ({remainingSeconds}s)"
            : confirmText;
    }

    public void ApplyDialogChrome(DialogChromeSnapshot chrome)
    {
        var chromeTitle = string.IsNullOrWhiteSpace(chrome.Title) ? _titleSnapshot : chrome.Title;
        var prompt = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.Prompt, _messageSnapshot);
        var lead = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.LeadText, _leadSnapshot);
        var emphasis = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.EmphasisText, _emphasisSnapshot);
        var detail = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.DetailText, _detailSnapshot);

        if (string.IsNullOrWhiteSpace(lead)
            && string.IsNullOrWhiteSpace(emphasis)
            && string.IsNullOrWhiteSpace(detail))
        {
            detail = prompt;
        }
        else if (string.IsNullOrWhiteSpace(detail))
        {
            detail = prompt;
        }

        _titleSnapshot = chromeTitle;
        _messageSnapshot = prompt;
        _leadSnapshot = lead;
        _emphasisSnapshot = emphasis;
        _detailSnapshot = detail;
        _detailsButtonSnapshot = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.DetailsButton, _detailsButtonSnapshot);

        Title = chromeTitle;
        DialogShell.Title = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.SectionTitle, chromeTitle);
        ApplyContent(_leadSnapshot, _emphasisSnapshot, _detailSnapshot);
        ApplyDetailsButton(_detailsButtonSnapshot);
        _confirmSnapshot = chrome.ConfirmText ?? _confirmSnapshot;
        _cancelSnapshot = chrome.CancelText ?? _cancelSnapshot;
        CancelButton.Content = _cancelSnapshot;
        UpdateConfirmButtonText(_remainingCountdownSeconds);
    }

    private void ApplyContent(string lead, string emphasis, string detail)
    {
        LeadTextBlock.Text = lead;
        LeadTextBlock.IsVisible = !string.IsNullOrWhiteSpace(lead);

        EmphasisTextBlock.Text = emphasis;
        EmphasisPanel.IsVisible = !string.IsNullOrWhiteSpace(emphasis);

        DetailTextBlock.Text = detail;
        DetailTextBlock.IsVisible = !string.IsNullOrWhiteSpace(detail);
    }

    private void ApplyDetailsButton(string text)
    {
        DetailsButton.Content = text;
        DetailsButton.IsVisible = !string.IsNullOrWhiteSpace(text);
    }
}
