using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.Infrastructure;

namespace MAAUnified.App.Features.Dialogs;

public partial class ConfigurationSaveStatusDialogView : Window
{
    public ConfigurationSaveStatusDialogView()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
    }

    public void ApplyMessage(string title, string message, bool showConfirmButton)
    {
        Title = title;
        DialogShell.Title = title;
        MessageTextBlock.Text = message;
        ConfirmButton.IsVisible = showConfirmButton;
        DialogShell.ShowCloseButton = showConfirmButton;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void OnShellCloseRequested(object? sender, EventArgs e)
    {
        if (ConfirmButton.IsVisible)
        {
            Close(false);
        }
    }
}
