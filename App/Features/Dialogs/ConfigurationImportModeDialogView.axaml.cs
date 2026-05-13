using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.Infrastructure;

namespace MAAUnified.App.Features.Dialogs;

public partial class ConfigurationImportModeDialogView : Window
{
    private readonly TaskCompletionSource<ConfigurationImportMode> _selectionCompletion = new();
    private bool _selectionStarted;
    private bool _allowCloseAfterSelection;

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

    public Task<ConfigurationImportMode> WaitForSelectionAsync() => _selectionCompletion.Task;

    public void CloseIfOpen()
    {
        if (_selectionStarted && !_allowCloseAfterSelection)
        {
            return;
        }

        if (!_selectionCompletion.Task.IsCompleted)
        {
            _selectionCompletion.TrySetResult(ConfigurationImportMode.Cancel);
        }

        if (!IsVisible)
        {
            return;
        }

        Close();
    }

    public void CompleteSelectionFlowAndClose()
    {
        _allowCloseAfterSelection = true;
        CloseIfOpen();
    }

    private void OnLegacyWindowsClick(object? sender, RoutedEventArgs e)
        => TryStartSelection(ConfigurationImportMode.LegacyWindows);

    private void OnUnifiedClick(object? sender, RoutedEventArgs e)
        => TryStartSelection(ConfigurationImportMode.Unified);

    private void OnManualClick(object? sender, RoutedEventArgs e)
        => TryStartSelection(ConfigurationImportMode.Manual);

    private void OnShellCloseRequested(object? sender, EventArgs e)
        => CloseIfOpen();

    private void TryStartSelection(ConfigurationImportMode mode)
    {
        if (_selectionStarted)
        {
            return;
        }

        _selectionStarted = true;
        LegacyWindowsButton.IsEnabled = false;
        UnifiedButton.IsEnabled = false;
        ManualButton.IsEnabled = false;
        _selectionCompletion.TrySetResult(mode);
    }
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
