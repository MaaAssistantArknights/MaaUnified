using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MAAUnified.Application.Models;
using MAAUnified.App.Controls;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.Services;
using MAAUnified.App.Views;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Runtime;

namespace MAAUnified.App.Features.Settings;

public partial class ConnectSettingsView : UserControl
{
    private const int ScreenshotPreviewWidth = 1280;
    private const int ScreenshotPreviewHeight = 720;
    private const int ScreenshotPreviewChannels = 3;
    private static readonly TimeSpan ScreenshotTestConnectTimeout = TimeSpan.FromSeconds(20);
    private const int ScreenshotTestSampleCount = 3;
    private ScreenshotPreviewWindow? _screenshotPreviewWindow;

    public ConnectSettingsView()
    {
        InitializeComponent();
        App.Runtime.UiLanguageCoordinator.LanguageChanged += OnUiLanguageChanged;
    }

    private ConnectionGameSharedStateViewModel? VM => DataContext as ConnectionGameSharedStateViewModel;
    private string T(string key, string fallback) => VM?.RootTexts[key] ?? fallback;

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

    private void OnConnectAddressItemDeleted(object? sender, AppHistoryInputItemEventArgs e)
    {
        if (VM is not null && e.Item is string address)
        {
            VM.RemoveAddressFromHistory(address);
        }
    }

    private void OnConnectAddressSelectionCommitted(object? sender, AppHistoryInputItemEventArgs e)
    {
        if (VM is null || e.Item is not string address)
        {
            return;
        }

        VM.ConnectAddress = address;
    }

    private void OnConnectAddressEditorCommitted(object? sender, AppHistoryInputEditorCommittedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        VM.ConnectAddress = e.Text;
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
            LogScreenshotTestEvent("start", vm, "begin", message: "trying connection and screenshot with current settings");
            var testResult = await RunScreenshotTestWithCurrentSettingsAsync(vm);
            if (!testResult.Success)
            {
                LogScreenshotTestEvent(
                    "test",
                    vm,
                    "failed",
                    message: testResult.Result.Message,
                    errorCode: testResult.Result.Error?.Code,
                    candidate: testResult.SuccessfulAddress);
                vm.TestLinkInfo = BuildScreenshotTestFailureStatus(testResult.Result);
                vm.TestLinkInfoSeverity = BuildScreenshotTestFailureSeverity(testResult.Result);
                await App.Runtime.DialogFeatureService.ReportErrorAsync(
                    "Settings.Connect.ScreenshotTest",
                    UiOperationResult.Fail(
                        UiErrorCode.ConnectFailed,
                        testResult.Result.Message,
                        testResult.Result.Error?.Details));
                return;
            }

            if (!string.IsNullOrWhiteSpace(testResult.SuccessfulAddress))
            {
                vm.ConnectAddress = testResult.SuccessfulAddress;
            }

            var elapsedSamples = testResult.Screenshot?.SampleMilliseconds ?? [];
            for (var i = 0; i < elapsedSamples.Count; i++)
            {
                LogScreenshotTestEvent(
                    "capture",
                    vm,
                    "sample_succeeded",
                    message: $"{elapsedSamples[i]} ms",
                    sampleIndex: i + 1,
                    samplesMs: elapsedSamples);
            }

            var min = elapsedSamples.Min();
            var max = elapsedSamples.Max();
            var avg = (long)Math.Round(elapsedSamples.Average(), MidpointRounding.AwayFromZero);
            vm.UpdateScreencapCost(min, avg, max, DateTimeOffset.Now);
            vm.TestLinkInfo = string.Empty;
            LogScreenshotTestEvent(
                "summary",
                vm,
                "succeeded",
                message: vm.ScreencapCost,
                samplesMs: elapsedSamples);

            if (testResult.Screenshot?.LatestImageBgr is { Length: > 0 } latestImage)
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
            vm.TestLinkInfo = T("Settings.Connect.Error.ConnectionFailedShort", "Connection failed.");
            vm.TestLinkInfoSeverity = TestLinkInfoSeverity.Error;
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

            var baseDirectory = RuntimeLayout.ResolveRuntimeBaseDirectory();
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

    private async Task<ConnectionScreenshotTestOperationResult> RunScreenshotTestWithCurrentSettingsAsync(ConnectionGameSharedStateViewModel vm)
    {
        IAppDialogService dialogService = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
            ? new AvaloniaDialogService(App.Runtime)
            : NoOpAppDialogService.Instance;
        var consent = await MacBundledAdbConsentService.EnsureAcceptedAsync(
            App.Runtime,
            dialogService,
            vm.UseMacBundledAdbEffective,
            "Settings.Connect.Test.MacBundledAdbConsent",
            vm.RootTexts.Language,
            CancellationToken.None);
        if (!consent.Success)
        {
            return new ConnectionScreenshotTestOperationResult(consent, null, null, []);
        }

        var effectiveAdbPath = vm.ResolveEffectiveAdbPath(updateStateWhenResolved: true);
        var candidatesResult = App.Runtime.ConnectFeatureService.BuildConnectionCandidates(
            vm.ConnectAddress,
            vm.EffectiveConnectConfig,
            effectiveAdbPath,
            vm.BuildCoreConnectionExtras(),
            vm.AutoDetect,
            vm.AlwaysAutoDetect,
            includeConfiguredAddress: true,
            timeout: ScreenshotTestConnectTimeout);
        if (!candidatesResult.Success || candidatesResult.Value is null)
        {
            var failure = UiOperationResult.Fail(
                candidatesResult.Error?.Code ?? UiErrorCode.ConnectFailed,
                candidatesResult.Message,
                candidatesResult.Error?.Details);
            var diagnostic = BuildDiagnosticConnectFailureResult(vm, failure);
            return new ConnectionScreenshotTestOperationResult(
                diagnostic,
                null,
                null,
                []);
        }

        var candidates = candidatesResult.Value;
        LogScreenshotTestEvent(
            "connect_candidates",
            vm,
            "prepared",
            message: $"count={candidates.Count}, adb={effectiveAdbPath ?? "<null>"}");

        var result = await App.Runtime.ConnectFeatureService.RunScreenshotTestAsync(
            candidates,
            sampleCount: ScreenshotTestSampleCount,
            cancellationToken: CancellationToken.None);
        foreach (var failure in result.CandidateFailures)
        {
            LogScreenshotTestEvent(
                "connect_attempt",
                vm,
                "failed",
                message: failure.Result.Message,
                errorCode: failure.Result.Error?.Code,
                candidate: failure.Candidate);
        }

        if (!result.Success)
        {
            if (string.Equals(result.Result.Error?.Code, UiErrorCode.OperationAlreadyRunning, StringComparison.Ordinal))
            {
                return result;
            }

            var candidateFailures = result.CandidateFailures
                .Select(static failure => new ConnectionAttemptFailure(failure.Candidate, failure.Result))
                .ToList();
            var diagnostic = BuildDiagnosticConnectFailureResult(vm, result.Result, candidateFailures);
            return new ConnectionScreenshotTestOperationResult(
                diagnostic,
                result.Screenshot,
                result.SuccessfulAddress,
                result.CandidateFailures);
        }

        LogScreenshotTestEvent("connect", vm, "succeeded", candidate: result.SuccessfulAddress);
        return result;
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

    private UiOperationResult BuildDiagnosticConnectFailureResult(
        ConnectionGameSharedStateViewModel vm,
        UiOperationResult connectResult,
        IReadOnlyList<ConnectionAttemptFailure>? candidateFailures = null)
    {
        var diagnostic = ConnectionFailureDiagnosticBuilder.Build(
            connectResult,
            vm,
            candidateFailures,
            language: vm.RootTexts.Language);
        return UiOperationResult.Fail(
            UiErrorCode.ConnectFailed,
            diagnostic.BuildDialogMessage(),
            diagnostic.Details);
    }

    private string BuildScreenshotTestFailureStatus(UiOperationResult result)
        => string.Equals(result.Error?.Code, UiErrorCode.OperationAlreadyRunning, StringComparison.Ordinal)
            ? result.Message
            : T("Settings.Connect.Error.ConnectionFailedShort", "Connection failed.");

    private static TestLinkInfoSeverity BuildScreenshotTestFailureSeverity(UiOperationResult result)
        => string.Equals(result.Error?.Code, UiErrorCode.OperationAlreadyRunning, StringComparison.Ordinal)
            ? TestLinkInfoSeverity.Warning
            : TestLinkInfoSeverity.Error;

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
        var bitmap = CreateBgrPreviewBitmap(imageBytes);
        if (bitmap is null)
        {
            return;
        }

        EnsureScreenshotPreviewWindow();
        if (_screenshotPreviewWindow is null)
        {
            bitmap.Dispose();
            return;
        }

        _screenshotPreviewWindow.SetPreview(
            bitmap,
            BuildScreenshotPreviewTitle(),
            BuildScreenshotPreviewSubtitle(),
            BuildScreenshotPreviewStatusText());

        if (_screenshotPreviewWindow.IsVisible)
        {
            _screenshotPreviewWindow.Activate();
            return;
        }

        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            MAAUnified.App.Features.Dialogs.DialogWindowScaling.ApplyOwnerUiScale(_screenshotPreviewWindow, owner);
            _screenshotPreviewWindow.Show(owner);
            return;
        }

        _screenshotPreviewWindow.Show();
    }

    private static WriteableBitmap? CreateBgrPreviewBitmap(byte[] bgrData)
    {
        var stride = ScreenshotPreviewWidth * ScreenshotPreviewChannels;
        var frameBytes = ScreenshotPreviewHeight * stride;
        if (bgrData.Length < frameBytes)
        {
            return null;
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(ScreenshotPreviewWidth, ScreenshotPreviewHeight),
            new Vector(96, 96),
            PixelFormats.Bgr24,
            AlphaFormat.Opaque);
        using var framebuffer = bitmap.Lock();
        if (framebuffer.RowBytes == stride)
        {
            Marshal.Copy(bgrData, 0, framebuffer.Address, frameBytes);
            return bitmap;
        }

        for (var row = 0; row < ScreenshotPreviewHeight; row++)
        {
            var sourceOffset = row * stride;
            var destination = IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes);
            Marshal.Copy(bgrData, sourceOffset, destination, stride);
        }

        return bitmap;
    }

    private void EnsureScreenshotPreviewWindow()
    {
        if (_screenshotPreviewWindow is not null)
        {
            return;
        }

        var window = new ScreenshotPreviewWindow();

        window.Closed += (_, _) =>
        {
            _screenshotPreviewWindow = null;
        };

        _screenshotPreviewWindow = window;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        App.Runtime.UiLanguageCoordinator.LanguageChanged -= OnUiLanguageChanged;
        CloseScreenshotPreviewWindow();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnUiLanguageChanged(object? sender, UiLanguageChangedEventArgs e)
    {
        if (_screenshotPreviewWindow is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(UpdateScreenshotPreviewWindowChrome, DispatcherPriority.Background);
    }

    private void UpdateScreenshotPreviewWindowChrome()
    {
        if (_screenshotPreviewWindow is not null)
        {
            _screenshotPreviewWindow.UpdateChrome(
                BuildScreenshotPreviewTitle(),
                BuildScreenshotPreviewSubtitle(),
                BuildScreenshotPreviewStatusText());
        }
    }

    private string BuildScreenshotPreviewTitle()
    {
        return T("Settings.Connect.Dialog.ScreenshotPreview");
    }

    private string BuildScreenshotPreviewSubtitle()
    {
        var vm = VM;
        if (vm is null)
        {
            return string.Empty;
        }

        var address = string.IsNullOrWhiteSpace(vm.ConnectAddress) ? "ADB / attach window" : vm.ConnectAddress.Trim();
        return string.IsNullOrWhiteSpace(vm.ScreencapCost)
            ? address
            : $"{address} · {vm.ScreencapCost}";
    }

    private string? BuildScreenshotPreviewStatusText()
    {
        var info = VM?.TestLinkInfo;
        return string.IsNullOrWhiteSpace(info) ? null : info.Trim();
    }

    private void CloseScreenshotPreviewWindow()
    {
        if (_screenshotPreviewWindow is null)
        {
            return;
        }

        _screenshotPreviewWindow.Close();
        _screenshotPreviewWindow = null;
    }

    private readonly record struct AdbPackageInfo(string Url, string FileName, string AdbRelativePath)
    {
        public string ResolveExtractedAdbPath(string extractRoot)
        {
            return Path.Combine(extractRoot, AdbRelativePath);
        }
    }
}
