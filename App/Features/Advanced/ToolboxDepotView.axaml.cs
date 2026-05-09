using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.ViewModels.Toolbox;

namespace MAAUnified.App.Features.Advanced;

public partial class ToolboxDepotView : UserControl
{
    public ToolboxDepotView()
    {
        InitializeComponent();
    }

    private ToolboxPageViewModel? VM => DataContext as ToolboxPageViewModel;

    private async void OnDepotStartClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.StartDepotAsync();
        }
    }

    private async void OnDepotExportArkPlannerClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        await CopyTextAsync(VM.ArkPlannerResult);
        VM.NotifyDepotExportCopied("ArkPlanner 数据");
    }

    private async void OnDepotExportLoliconClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        await CopyTextAsync(VM.LoliconResult);
        VM.NotifyDepotExportCopied("一图流数据");
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
