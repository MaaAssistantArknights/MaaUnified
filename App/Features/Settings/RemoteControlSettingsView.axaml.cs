using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.ViewModels.Settings;

namespace MAAUnified.App.Features.Settings;

public partial class RemoteControlSettingsView : UserControl
{
    public RemoteControlSettingsView()
    {
        InitializeComponent();
    }

    private SettingsPageViewModel? VM => DataContext as SettingsPageViewModel;

    private async void OnTestRemoteConnectivityClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.TestRemoteControlConnectivityAsync();
        }
    }

    private async void OnRegenerateRemoteDeviceIdentityClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        VM.RemoteDeviceIdentity = Guid.NewGuid().ToString("N");
        await VM.SaveRemoteControlAsync();
    }
}
