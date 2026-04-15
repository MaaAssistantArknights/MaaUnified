using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MAAUnified.Application.Models;
using MAAUnified.App.Controls;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.App.Features.Settings;

public partial class ConnectSettingsView : UserControl
{
    private Window? _screenshotPreviewWindow;
    private Image? _screenshotPreviewImage;
    private Bitmap? _screenshotPreviewBitmap;

    public ConnectSettingsView()
    {
        InitializeComponent();
        App.Runtime.UiLanguageCoordinator.LanguageChanged += OnUiLanguageChanged;
    }

    private ConnectionGameSharedStateViewModel? VM => DataContext as ConnectionGameSharedStateViewModel;

    private async void OnSelectAdbPathClick(object? sender, RoutedEventArgs e)
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
                Title = T("Settings.Connect.Dialog.SelectAdbPath"),
            });
        var selected = files.FirstOrDefault();
        if (selected is null)
        {
            return;
        }

        var path = selected.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            vm.AdbPath = path;
            _ = vm.ResolveEffectiveAdbPath(updateStateWhenResolved: true);
        }
    }

    private void OnRemoveAddressHistoryClick(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm is null || sender is not Button { Tag: string address })
        {
            return;
        }

        vm.RemoveAddressFromHistory(address);
    }

    private void OnConnectAddressSelectionCommitted(object? sender, CheckComboBoxSelectionCommittedEventArgs e)
    {
        if (VM is null || e.SelectedItem is not string address)
        {
            return;
        }

        VM.ConnectAddress = address;
    }

    private void OnMuMuExtrasChecked(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm is null)
        {
            return;
        }

        if (vm.AutoDetectMuMu12EmulatorPathIfNeeded())
        {
            vm.TestLinkInfo = $"MuMu path auto-detected: {vm.MuMu12EmulatorPath}";
            return;
        }

        if (!vm.ValidateMuMu12EmulatorPath(out var error) && !string.IsNullOrWhiteSpace(error))
        {
            vm.TestLinkInfo = error;
        }
    }

    private void OnMuMuEmulatorPathLostFocus(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm is null)
        {
            return;
        }

        if (!vm.ValidateMuMu12EmulatorPath(out var error) && !string.IsNullOrWhiteSpace(error))
        {
            vm.TestLinkInfo = error;
        }
    }

    private void OnLdPlayerExtrasChecked(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm is null)
        {
            return;
        }

        if (vm.AutoDetectLdPlayerEmulatorPathIfNeeded())
        {
            vm.TestLinkInfo = $"LDPlayer path auto-detected: {vm.LdPlayerEmulatorPath}";
            return;
        }

        if (!vm.ValidateLdPlayerEmulatorPath(out var error) && !string.IsNullOrWhiteSpace(error))
        {
            vm.TestLinkInfo = error;
        }
    }

    private void OnLdPlayerEmulatorPathLostFocus(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm is null)
        {
            return;
        }

        if (!vm.ValidateLdPlayerEmulatorPath(out var error) && !string.IsNullOrWhiteSpace(error))
        {
            vm.TestLinkInfo = error;
        }
    }

    private async void OnScreenshotTestClick(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm is null)
        {
            return;
        }

        try
        {
            vm.TestLinkInfo = T("Settings.Connect.Status.ConnectingEmulator");
            LogScreenshotTestEvent("start", vm, "begin", message: "trying connection with current settings");
            var connectResult = await ConnectWithCurrentSettingsAsync(vm);
            if (!connectResult.Success)
            {
                LogScreenshotTestEvent(
                    "connect",
                    vm,
                    "failed",
                    message: connectResult.Message,
                    errorCode: connectResult.Error?.Code);
                vm.TestLinkInfo = BuildConnectFailureMessage(vm, connectResult);
                return;
            }

            LogScreenshotTestEvent("connect", vm, "succeeded", message: "starting 3x GetImage probes");

            var elapsedSamples = new List<long>(3);
            byte[]? latestImage = null;
            for (var i = 0; i < 3; i++)
            {
                var watch = Stopwatch.StartNew();
                var imageResult = await App.Runtime.CoreBridge.GetImageAsync();
                watch.Stop();
                if (!imageResult.Success || imageResult.Value is null || imageResult.Value.Length == 0)
                {
                    var errorMessage = imageResult.Error?.Message ?? T("Settings.Connect.Error.GetImageFailed");
                    LogScreenshotTestEvent(
                        "capture",
                        vm,
                        "failed",
                        message: errorMessage,
                        errorCode: imageResult.Error is null ? null : imageResult.Error.Code.ToString(),
                        sampleIndex: i + 1,
                        samplesMs: elapsedSamples);
                    vm.TestLinkInfo = Tf("Settings.Connect.Error.ScreenshotTestFailed", errorMessage);
                    return;
                }

                latestImage = imageResult.Value;
                elapsedSamples.Add(watch.ElapsedMilliseconds);
                LogScreenshotTestEvent(
                    "capture",
                    vm,
                    "sample_succeeded",
                    message: $"{watch.ElapsedMilliseconds} ms",
                    sampleIndex: i + 1,
                    samplesMs: elapsedSamples);
            }

            var min = elapsedSamples.Min();
            var max = elapsedSamples.Max();
            var avg = (long)Math.Round(elapsedSamples.Average(), MidpointRounding.AwayFromZero);
            vm.UpdateScreencapCost(min, avg, max, DateTimeOffset.Now);
            vm.TestLinkInfo = vm.ScreencapCost;
            LogScreenshotTestEvent(
                "summary",
                vm,
                "succeeded",
                message: vm.ScreencapCost,
                samplesMs: elapsedSamples);

            if (latestImage is { Length: > 0 })
            {
                ShowOrUpdateScreenshotPreview(latestImage);
            }
        }
        catch (Exception ex)
        {
            LogScreenshotTestEvent(
                "exception",
                vm,
                "failed",
                message: ex.Message,
                errorCode: ex.GetType().Name);
            vm.TestLinkInfo = Tf("Settings.Connect.Error.ScreenshotTestException", ex.Message);
        }
    }

    private async void OnReplaceAdbClick(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm is null)
        {
            return;
        }

        try
        {
            var package = ResolveAdbPackageInfo();
            if (package is null)
            {
                vm.TestLinkInfo = T("Settings.Connect.Error.AutoReplaceUnsupported");
                return;
            }

            vm.TestLinkInfo = T("Settings.Connect.Status.DownloadingAdb");

            var baseDirectory = AppContext.BaseDirectory;
            var cacheDirectory = Path.Combine(baseDirectory, "cache", "adb");
            Directory.CreateDirectory(cacheDirectory);

            var packagePath = Path.Combine(cacheDirectory, package.Value.FileName);
            await DownloadFileAsync(package.Value.Url, packagePath);

            var extractDirectory = Path.Combine(cacheDirectory, "platform-tools");
            if (Directory.Exists(extractDirectory))
            {
                Directory.Delete(extractDirectory, recursive: true);
            }

            ZipFile.ExtractToDirectory(packagePath, extractDirectory);

            var adbPath = package.Value.ResolveExtractedAdbPath(extractDirectory);
            if (!File.Exists(adbPath))
            {
                vm.TestLinkInfo = T("Settings.Connect.Error.ReplaceAdbExtractedNotFound");
                return;
            }

            vm.AdbPath = adbPath;
            vm.AdbReplaced = true;
            vm.TestLinkInfo = Tf("Settings.Connect.Status.ReplacedAdb", adbPath);
        }
        catch (Exception ex)
        {
            vm.TestLinkInfo = Tf("Settings.Connect.Error.ReplaceAdbFailed", ex.Message);
        }
    }

    private async Task<UiOperationResult> ConnectWithCurrentSettingsAsync(ConnectionGameSharedStateViewModel vm)
    {
        var effectiveAdbPath = vm.ResolveEffectiveAdbPath(updateStateWhenResolved: true);
        var adbPath = string.IsNullOrWhiteSpace(effectiveAdbPath) ? null : effectiveAdbPath;
        var instanceOptions = vm.BuildCoreInstanceOptions();
        var candidates = vm.BuildConnectAddressCandidates(includeConfiguredAddress: true);
        LogScreenshotTestEvent(
            "connect_candidates",
            vm,
            "prepared",
            message: $"count={candidates.Count}, adb={adbPath ?? "<null>"}");
        UiOperationResult? lastFailure = null;

        foreach (var candidate in candidates)
        {
            LogScreenshotTestEvent("connect_attempt", vm, "trying", candidate: candidate);
            var result = await App.Runtime.ShellFeatureService.ConnectAsync(candidate, vm.ConnectConfig, adbPath, instanceOptions);
            if (result.Success)
            {
                LogScreenshotTestEvent("connect_attempt", vm, "succeeded", candidate: candidate);
                vm.ConnectAddress = candidate;
                return result;
            }

            LogScreenshotTestEvent(
                "connect_attempt",
                vm,
                "failed",
                message: result.Message,
                errorCode: result.Error?.Code,
                candidate: candidate);
            lastFailure = result;
        }

        return lastFailure ?? UiOperationResult.Fail(UiErrorCode.UiOperationFailed, T("Settings.Connect.Error.ConnectionFailedShort"));
    }

    private static async Task DownloadFileAsync(string url, string targetPath)
    {
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3),
        };
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var file = File.Create(targetPath);
        await stream.CopyToAsync(file);
    }

    private static AdbPackageInfo? ResolveAdbPackageInfo()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new AdbPackageInfo(
                Url: "https://dl.google.com/android/repository/platform-tools-latest-windows.zip",
                FileName: "platform-tools-latest-windows.zip",
                AdbRelativePath: Path.Combine("platform-tools", "adb.exe"));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new AdbPackageInfo(
                Url: "https://dl.google.com/android/repository/platform-tools-latest-linux.zip",
                FileName: "platform-tools-latest-linux.zip",
                AdbRelativePath: Path.Combine("platform-tools", "adb"));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new AdbPackageInfo(
                Url: "https://dl.google.com/android/repository/platform-tools-latest-darwin.zip",
                FileName: "platform-tools-latest-darwin.zip",
                AdbRelativePath: Path.Combine("platform-tools", "adb"));
        }

        return null;
    }

    private string T(string key)
    {
        return VM?.RootTexts[key] ?? key;
    }

    private string Tf(string key, params object[] args)
    {
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, T(key), args);
    }

    private string BuildConnectFailureMessage(
        ConnectionGameSharedStateViewModel vm,
        UiOperationResult connectResult)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Tf("Settings.Connect.Error.ConnectFailed", connectResult.Message));

        if (!string.IsNullOrWhiteSpace(connectResult.Error?.Details))
        {
            builder.AppendLine(Tf("Settings.Connect.Error.Details", connectResult.Error.Details));
        }

        var settingsHint = vm.BuildConnectionSettingsHintMessage();
        if (!string.IsNullOrWhiteSpace(settingsHint))
        {
            builder.AppendLine(settingsHint);
        }

        return builder.ToString().Trim();
    }

    private static void LogScreenshotTestEvent(
        string stage,
        ConnectionGameSharedStateViewModel vm,
        string outcome,
        string? message = null,
        string? errorCode = null,
        int? sampleIndex = null,
        IReadOnlyList<long>? samplesMs = null,
        string? candidate = null)
    {
        var payload = new
        {
            Event = "settings.connect.screenshot-test",
            Stage = stage,
            Outcome = outcome,
            Timestamp = DateTimeOffset.UtcNow,
            vm.ConnectConfig,
            vm.ConnectAddress,
            Candidate = candidate,
            SampleIndex = sampleIndex,
            SamplesMs = samplesMs,
            ErrorCode = errorCode,
            Message = message,
        };
        App.Runtime.LogService.Debug(JsonSerializer.Serialize(payload));
    }

    private void ShowOrUpdateScreenshotPreview(byte[] imageBytes)
    {
        Bitmap bitmap;
        using (var stream = new MemoryStream(imageBytes, writable: false))
        {
            bitmap = new Bitmap(stream);
        }

        EnsureScreenshotPreviewWindow();

        var previous = _screenshotPreviewBitmap;
        _screenshotPreviewBitmap = bitmap;
        if (_screenshotPreviewImage is not null)
        {
            _screenshotPreviewImage.Source = bitmap;
        }

        previous?.Dispose();

        if (_screenshotPreviewWindow is null)
        {
            return;
        }

        if (_screenshotPreviewWindow.IsVisible)
        {
            _screenshotPreviewWindow.Activate();
            return;
        }

        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            _screenshotPreviewWindow.Show(owner);
            return;
        }

        _screenshotPreviewWindow.Show();
    }

    private void EnsureScreenshotPreviewWindow()
    {
        if (_screenshotPreviewWindow is not null && _screenshotPreviewImage is not null)
        {
            return;
        }

        var image = new Image
        {
            Stretch = Stretch.Uniform,
        };

        var host = new Border
        {
            Background = Brushes.Black,
            Padding = new Thickness(4),
            Child = image,
        };

        var window = new Window
        {
            Title = T("Settings.Connect.Dialog.ScreenshotPreview"),
            Width = 800,
            Height = 480,
            MinWidth = 480,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = host,
        };

        window.Closed += (_, _) =>
        {
            _screenshotPreviewBitmap?.Dispose();
            _screenshotPreviewBitmap = null;
            _screenshotPreviewImage = null;
            _screenshotPreviewWindow = null;
        };

        _screenshotPreviewImage = image;
        _screenshotPreviewWindow = window;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        App.Runtime.UiLanguageCoordinator.LanguageChanged -= OnUiLanguageChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnUiLanguageChanged(object? sender, UiLanguageChangedEventArgs e)
    {
        if (_screenshotPreviewWindow is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(UpdateScreenshotPreviewWindowTitle);
    }

    private void UpdateScreenshotPreviewWindowTitle()
    {
        if (_screenshotPreviewWindow is not null)
        {
            _screenshotPreviewWindow.Title = T("Settings.Connect.Dialog.ScreenshotPreview");
        }
    }

    private readonly record struct AdbPackageInfo(string Url, string FileName, string AdbRelativePath)
    {
        public string ResolveExtractedAdbPath(string extractRoot)
        {
            return Path.Combine(extractRoot, AdbRelativePath);
        }
    }
}
