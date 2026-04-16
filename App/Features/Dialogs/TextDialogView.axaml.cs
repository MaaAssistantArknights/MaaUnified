using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.Infrastructure;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;

namespace MAAUnified.App.Features.Dialogs;

public partial class TextDialogView : Window, IDialogChromeAware
{
    private string _promptSnapshot = string.Empty;
    private string _payloadText = string.Empty;
    private bool _readOnlyContentMode;

    public TextDialogView()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
        Opened += (_, _) =>
        {
            if (_readOnlyContentMode)
            {
                ConfirmButton.Focus();
                return;
            }

            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public void ApplyRequest(TextDialogRequest request)
    {
        Title = request.Title;
        _promptSnapshot = request.Prompt ?? string.Empty;
        _payloadText = request.DefaultText ?? string.Empty;
        _readOnlyContentMode = request.ReadOnlyContent;
        var readOnlyBodyText = _payloadText;
        if (_readOnlyContentMode && string.IsNullOrWhiteSpace(readOnlyBodyText))
        {
            readOnlyBodyText = _promptSnapshot;
            _promptSnapshot = string.Empty;
        }

        PromptText.Text = _promptSnapshot;
        PromptText.IsVisible = !string.IsNullOrWhiteSpace(_promptSnapshot);
        EditableInputPanel.IsVisible = !_readOnlyContentMode;
        ReadOnlyContentPanel.IsVisible = _readOnlyContentMode;
        InputBox.Text = _payloadText;
        InputBox.AcceptsReturn = request.MultiLine;
        ReadOnlyContentBox.Text = readOnlyBodyText;
        ReadOnlyContentBox.CaretIndex = 0;
        DialogShell.Title = Title;
        ConfirmButton.Content = request.ConfirmText;
        CancelButton.Content = request.CancelText;
    }

    public TextDialogPayload BuildPayload()
    {
        return new TextDialogPayload(_readOnlyContentMode ? _payloadText : InputBox.Text ?? string.Empty);
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
        PromptText.Text = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.Prompt, _promptSnapshot);
        PromptText.IsVisible = !string.IsNullOrWhiteSpace(PromptText.Text);
        ConfirmButton.Content = chrome.ConfirmText ?? ConfirmButton.Content;
        CancelButton.Content = chrome.CancelText ?? CancelButton.Content;
    }
}
