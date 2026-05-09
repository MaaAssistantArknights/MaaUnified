using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.Infrastructure;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;

namespace MAAUnified.App.Features.Dialogs;

public partial class VersionUpdateDialogView : Window, IDialogChromeAware
{
    private string _currentVersion = string.Empty;
    private string _targetVersion = string.Empty;
    private string _summary = string.Empty;

    public VersionUpdateDialogView()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
    }

    public void ApplyRequest(VersionUpdateDialogRequest request)
    {
        _currentVersion = request.CurrentVersion;
        _targetVersion = request.TargetVersion;
        _summary = request.Summary;
        Title = request.Title;
        DialogShell.Title = request.Title;
        VersionLine.Text = $"{_currentVersion} -> {_targetVersion}";
        SummaryLine.Text = _summary;
        BodyBox.Text = request.Body;
        BodyBox.CaretIndex = 0;
        ConfirmButton.Content = request.ConfirmText;
        CancelButton.Content = request.CancelText;
    }

    public VersionUpdateDialogPayload BuildPayload()
    {
        return new VersionUpdateDialogPayload(
            Action: "confirm",
            CurrentVersion: _currentVersion,
            TargetVersion: _targetVersion,
            Summary: _summary);
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
        ConfirmButton.Content = chrome.ConfirmText ?? ConfirmButton.Content;
        CancelButton.Content = chrome.CancelText ?? CancelButton.Content;
    }
}
