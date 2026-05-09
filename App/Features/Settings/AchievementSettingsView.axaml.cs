using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using MAAUnified.App.ViewModels.Settings;

namespace MAAUnified.App.Features.Settings;

public partial class AchievementSettingsView : UserControl
{
    private static readonly FilePickerFileType JsonFileType = new("JSON")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
    };

    public AchievementSettingsView()
    {
        InitializeComponent();
    }

    private SettingsPageViewModel? VM => DataContext as SettingsPageViewModel;
    private string T(string key) => VM?.RootTexts[key] ?? key;

    private async void OnRefreshAchievementClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.RefreshAchievementPolicyAsync();
        }
    }

    private async void OnOpenAchievementGuideClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.OpenAchievementGuideAsync();
        }
    }

    private async void OnShowAchievementListClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.ShowAchievementListDialogAsync();
        }
    }

    private async void OnBackupAchievementClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { CanSave: true } storageProvider)
        {
            return;
        }

        var file = await storageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = T("Settings.Achievement.Backup"),
                SuggestedFileName = $"Achievement_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json",
                DefaultExtension = "json",
                ShowOverwritePrompt = true,
                FileTypeChoices = [JsonFileType],
            });
        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await VM.ExportAchievementsAsync(path);
        }
    }

    private async void OnRestoreAchievementClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { CanOpen: true } storageProvider)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = T("Settings.Achievement.Restore"),
                AllowMultiple = false,
                FileTypeFilter = [JsonFileType],
            });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await VM.ImportAchievementsAsync(path);
        }
    }

    private async void OnUnlockAllAchievementClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.UnlockAllAchievementsAsync();
        }
    }

    private async void OnLockAllAchievementClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.LockAllAchievementsAsync();
        }
    }

    private void OnAchievementDebugPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        VM?.HandleAchievementDebugClick();
    }
}
