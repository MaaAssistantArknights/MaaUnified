using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.Infrastructure;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;

namespace MAAUnified.App.Features.Dialogs;

public partial class AnnouncementDialogView : Window, IDialogChromeAware
{
    private const string DoNotRemindKey = "Announcement.DoNotRemind";
    private const string DoNotShowKey = "Announcement.DoNotShow";
    private readonly RootLocalizationTextMap _texts = new("Root.Localization.Dialog.Announcement");

    public AnnouncementDialogView()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
    }

    public void ApplyRequest(AnnouncementDialogRequest request)
    {
        Title = request.Title;
        DialogShell.Title = request.Title;
        AnnouncementInfoBox.Text = request.AnnouncementInfo;
        AnnouncementInfoBox.CaretIndex = 0;
        DoNotRemindBox.IsChecked = request.DoNotRemindThisAnnouncementAgain;
        DoNotShowBox.IsChecked = request.DoNotShowAnnouncement;
        ConfirmButton.Content = request.ConfirmText;
        CancelButton.Content = request.CancelText;
        DoNotRemindBox.Content = Text(
            "Settings.About.Dialog.DoNotRemindThisAnnouncementAgain",
            "Do not remind this announcement again");
        DoNotShowBox.Content = Text(
            "Settings.About.Dialog.DoNotShowAnnouncement",
            "Do not show announcement");
    }

    public AnnouncementDialogPayload BuildPayload()
    {
        return new AnnouncementDialogPayload(
            AnnouncementInfo: AnnouncementInfoBox.Text ?? string.Empty,
            DoNotRemindThisAnnouncementAgain: DoNotRemindBox.IsChecked ?? false,
            DoNotShowAnnouncement: DoNotShowBox.IsChecked ?? false);
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

    private string Text(string key, string fallback = "")
    {
        _texts.Language = App.Runtime.UiLanguageCoordinator.CurrentLanguage;
        return _texts.GetOrDefault(key, fallback.Length == 0 ? key : fallback);
    }

    public void ApplyDialogChrome(DialogChromeSnapshot chrome)
    {
        Title = chrome.Title;
        DialogShell.Title = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.SectionTitle, chrome.Title);
        ConfirmButton.Content = chrome.ConfirmText ?? ConfirmButton.Content;
        CancelButton.Content = chrome.CancelText ?? CancelButton.Content;
        DoNotRemindBox.Content = chrome.GetNamedTextOrDefault(
            DoNotRemindKey,
            Text("Settings.About.Dialog.DoNotRemindThisAnnouncementAgain", "Do not remind this announcement again"));
        DoNotShowBox.Content = chrome.GetNamedTextOrDefault(
            DoNotShowKey,
            Text("Settings.About.Dialog.DoNotShowAnnouncement", "Do not show announcement"));
    }
}
