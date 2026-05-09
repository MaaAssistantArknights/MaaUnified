using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.ViewModels.Settings;

namespace MAAUnified.App.Features.Settings;

public partial class AboutSettingsView : UserControl
{
    public AboutSettingsView()
    {
        InitializeComponent();
    }

    private SettingsPageViewModel? VM => DataContext as SettingsPageViewModel;

    private async void OnOpenOfficialWebsiteClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.OpenAboutOfficialWebsiteAsync();
        }
    }

    private async void OnOpenCommunityClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.OpenAboutCommunityAsync();
        }
    }

    private async void OnOpenBilibiliClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.OpenAboutBilibiliAsync();
        }
    }

    private async void OnOpenGithubClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.OpenAboutGithubAsync();
        }
    }

    private async void OnOpenDownloadClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.OpenAboutDownloadAsync();
        }
    }

    private async void OnOpenQqGroupClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.OpenAboutQqGroupAsync();
        }
    }

    private async void OnOpenQqChannelClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.OpenAboutQqChannelAsync();
        }
    }

    private async void OnOpenTelegramClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.OpenAboutTelegramAsync();
        }
    }

    private async void OnOpenDiscordClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.OpenAboutDiscordAsync();
        }
    }

    private async void OnCheckAnnouncementClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.CheckAndDownloadAboutAnnouncementWithDialogAsync();
        }
    }
}
