using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using MAAUnified.App.Infrastructure;

namespace MAAUnified.App.Views;

public partial class RuntimeLogWindow : Window
{
    private Bitmap? _previewBitmap;
    private bool _isScreenshotPreviewMode;

    public RuntimeLogWindow()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
        Closed += OnClosed;
        ConfigureForRuntimeLogs();
    }

    public void ConfigureForRuntimeLogs()
    {
        _isScreenshotPreviewMode = false;
        Title = "Runtime Logs";
        WindowShell.Title = Title;
        LogHeaderText.IsVisible = true;
        PreviewHeaderText.IsVisible = false;
        LogModePanel.IsVisible = true;
        PreviewModePanel.IsVisible = false;
        CloseActionButton.Content = "Close";
        DisposePreviewBitmap();
    }

    public void ConfigureForScreenshotPreview(Bitmap bitmap, string title, string subtitle, string? statusText = null)
    {
        _isScreenshotPreviewMode = true;
        Title = title;
        WindowShell.Title = title;
        LogHeaderText.IsVisible = false;
        PreviewHeaderText.IsVisible = !string.IsNullOrWhiteSpace(subtitle);
        PreviewHeaderText.Text = subtitle;
        PreviewSectionTitleText.Text = title;
        PreviewStatusText.Text = statusText ?? string.Empty;
        PreviewStatusText.IsVisible = !string.IsNullOrWhiteSpace(statusText);
        LogModePanel.IsVisible = false;
        PreviewModePanel.IsVisible = true;
        CloseActionButton.Content = "Close";
        ReplacePreviewBitmap(bitmap);
    }

    public void UpdateScreenshotPreviewChrome(string title, string subtitle, string? statusText = null)
    {
        if (!_isScreenshotPreviewMode)
        {
            return;
        }

        Title = title;
        WindowShell.Title = title;
        PreviewSectionTitleText.Text = title;
        PreviewHeaderText.Text = subtitle;
        PreviewHeaderText.IsVisible = !string.IsNullOrWhiteSpace(subtitle);
        PreviewStatusText.Text = statusText ?? string.Empty;
        PreviewStatusText.IsVisible = !string.IsNullOrWhiteSpace(statusText);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnShellCloseRequested(object? sender, EventArgs e)
    {
        Close();
    }

    private void OnResizeGripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || sender is not Control { Tag: string tag }
            || !Enum.TryParse<WindowEdge>(tag, out var edge))
        {
            return;
        }

        BeginResizeDrag(edge, e);
        e.Handled = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DisposePreviewBitmap();
        Closed -= OnClosed;
    }

    private void ReplacePreviewBitmap(Bitmap bitmap)
    {
        var previous = _previewBitmap;
        _previewBitmap = bitmap;
        PreviewImage.Source = bitmap;
        previous?.Dispose();
    }

    private void DisposePreviewBitmap()
    {
        PreviewImage.Source = null;
        _previewBitmap?.Dispose();
        _previewBitmap = null;
    }
}
