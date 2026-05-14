using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MAAUnified.App.ViewModels.Settings;

namespace MAAUnified.App.Features.Settings;

public partial class BackgroundSettingsView : UserControl
{
    private FilePickerFileType CreateImageFileType() => new(T("Settings.Background.ImagePath"))
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"],
        MimeTypes = ["image/png", "image/jpeg", "image/bmp", "image/webp"],
    };

    public BackgroundSettingsView()
    {
        InitializeComponent();
    }

    private SettingsPageViewModel? VM => DataContext as SettingsPageViewModel;
    private string T(string key) => VM?.RootTexts[key] ?? key;

    private async void OnBackgroundPathLostFocus(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.SaveGuiSettingsAsync();
        }
    }

    private async void OnBackgroundPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || VM is null)
        {
            return;
        }

        await VM.SaveGuiSettingsAsync();
    }

    private async void OnSelectBackgroundImageClick(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm is null)
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
                AllowMultiple = false,
                Title = T("Settings.Background.Dialog.SelectImage"),
                FileTypeFilter = [CreateImageFileType()],
            });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        vm.BackgroundImagePath = path;
        await vm.SaveGuiSettingsAsync();
    }

    private async void OnClearBackgroundImageClick(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm is null)
        {
            return;
        }

        vm.BackgroundImagePath = string.Empty;
        await vm.SaveGuiSettingsAsync();
    }
}
