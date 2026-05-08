using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.ViewModels.Toolbox;

namespace MAAUnified.App.Features.Advanced;

public partial class ToolboxOperBoxView : UserControl
{
    public ToolboxOperBoxView()
    {
        InitializeComponent();
    }

    private ToolboxPageViewModel? VM => DataContext as ToolboxPageViewModel;

    private void OnSelectOperBoxNotHaveClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            VM.OperBoxSelectedIndex = 0;
        }
    }

    private void OnSelectOperBoxHaveClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            VM.OperBoxSelectedIndex = 1;
        }
    }

    private async void OnOperBoxStartClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.StartOperBoxAsync();
        }
    }

    private async void OnOperBoxExportClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        await CopyTextAsync(VM.OperBoxExportText);
        VM.NotifyOperBoxExportCopied();
    }

    private async Task CopyTextAsync(string text)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await topLevel.Clipboard.SetTextAsync(text);
    }
}
