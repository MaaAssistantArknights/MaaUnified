using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using MAAUnified.App.Infrastructure;

namespace MAAUnified.App.Views;

public partial class ScreenshotPreviewWindow : Window
{
    private Bitmap? _previewBitmap;

    public ScreenshotPreviewWindow()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
        Closed += OnClosed;
    }

    public void SetPreview(Bitmap bitmap, string title, string subtitle, string? statusText = null)
    {
        UpdateChrome(title, subtitle, statusText);
        ReplacePreviewBitmap(bitmap);
    }

    public void UpdateChrome(string title, string subtitle, string? statusText = null)
    {
        Title = title;
        DialogShell.Title = title;
        PreviewSectionTitleText.Text = title;
        PreviewHeaderText.Text = subtitle;
        PreviewHeaderText.IsVisible = !string.IsNullOrWhiteSpace(subtitle);
        PreviewStatusText.Text = statusText ?? string.Empty;
        PreviewStatusText.IsVisible = false;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnShellCloseRequested(object? sender, EventArgs e)
    {
        Close();
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
