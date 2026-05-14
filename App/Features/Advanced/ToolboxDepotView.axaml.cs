using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Models;

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
            if (VM.IsDepotExecuting)
            {
                await VM.StopActiveToolAsync();
                return;
            }

            await VM.StartDepotAsync();
        }
    }

    private void OnDepotStartPointerEntered(object? sender, PointerEventArgs e)
    {
        VM?.SetToolActionHover(ToolboxToolKind.Depot, hovering: true);
    }

    private void OnDepotStartPointerExited(object? sender, PointerEventArgs e)
    {
        VM?.SetToolActionHover(ToolboxToolKind.Depot, hovering: false);
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
