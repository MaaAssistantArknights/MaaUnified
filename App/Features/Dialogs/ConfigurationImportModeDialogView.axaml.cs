using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.Infrastructure;

namespace MAAUnified.App.Features.Dialogs;

public partial class ConfigurationImportModeDialogView : Window
{
    public ConfigurationImportModeDialogView()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
    }

    public void ApplyRequest(ConfigurationImportModeDialogRequest request)
    {
        Title = request.Title;
        DialogShell.Title = request.Title;
        LegacyWindowsTitleTextBlock.Text = request.LegacyWindowsTitle;
        LegacyWindowsDescriptionTextBlock.Text = request.LegacyWindowsDescription;
        UnifiedTitleTextBlock.Text = request.UnifiedTitle;
        UnifiedDescriptionTextBlock.Text = request.UnifiedDescription;
        ManualTitleTextBlock.Text = request.ManualTitle;
        ManualDescriptionTextBlock.Text = request.ManualDescription;
    }

    private void OnLegacyWindowsClick(object? sender, RoutedEventArgs e)
        => Close(ConfigurationImportMode.LegacyWindows);

    private void OnUnifiedClick(object? sender, RoutedEventArgs e)
        => Close(ConfigurationImportMode.Unified);

    private void OnManualClick(object? sender, RoutedEventArgs e)
        => Close(ConfigurationImportMode.Manual);

    private void OnShellCloseRequested(object? sender, EventArgs e)
        => Close(ConfigurationImportMode.Cancel);
}

public sealed record ConfigurationImportModeDialogRequest(
    string Title,
    string LegacyWindowsTitle,
    string LegacyWindowsDescription,
    string UnifiedTitle,
    string UnifiedDescription,
    string ManualTitle,
    string ManualDescription);

public enum ConfigurationImportMode
{
    Cancel = 0,
    LegacyWindows = 1,
    Unified = 2,
    Manual = 3,
}
