using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using MAAUnified.App.ViewModels.Settings;

namespace MAAUnified.App.Features.Settings;

public partial class GameSettingsView : UserControl
{
    private const string MaaDocsBase = "https://docs.maa.plus";
    private const string OverseasAdaptationRelativePath = "/develop/overseas-client-adaptation.html";

    public GameSettingsView()
    {
        InitializeComponent();
    }

    private SettingsPageViewModel? VM => DataContext as SettingsPageViewModel;

    private void OnOpenYoStarResolutionGuideClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenUrl($"{MaaDocsBase}/{ResolveDocLanguage()}/");
    }

    private void OnOpenOverseasAdaptationGuideClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenUrl($"{MaaDocsBase}/{ResolveDocLanguage()}{OverseasAdaptationRelativePath}");
    }

    private void OnScriptPathDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = TryGetFirstDroppedLocalFile(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnStartsWithScriptDrop(object? sender, DragEventArgs e)
    {
        if (VM is null || !TryGetFirstDroppedLocalFile(e.Data, out var filePath))
        {
            return;
        }

        VM.StartsWithScript = filePath;
        e.Handled = true;
    }

    private void OnEndsWithScriptDrop(object? sender, DragEventArgs e)
    {
        if (VM is null || !TryGetFirstDroppedLocalFile(e.Data, out var filePath))
        {
            return;
        }

        VM.EndsWithScript = filePath;
        e.Handled = true;
    }

    private static bool TryGetFirstDroppedLocalFile(IDataObject data, out string filePath)
    {
        filePath = string.Empty;
        var first = data.GetFiles()?.FirstOrDefault();
        var localPath = first?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return false;
        }

        filePath = localPath;
        return true;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Keep settings UI responsive even if shell open fails.
        }
    }

    private string ResolveDocLanguage()
    {
        var language = VM?.ConnectionGameSharedState.RootTexts.Language;
        var normalized = (language ?? "en-us").Trim().ToLowerInvariant();
        return normalized;
    }
}
