using System.Diagnostics;
using System.Runtime.InteropServices;
using MAAUnified.Application.Models;

namespace MAAUnified.Application.Services;

public interface IMacScreenRecordingPermissionService
{
    bool IsSupported { get; }

    bool HasScreenRecordingPermission();

    bool RequestScreenRecordingPermission();

    UiOperationResult OpenScreenRecordingSettings();
}

public sealed class MacScreenRecordingPermissionService : IMacScreenRecordingPermissionService
{
    private const string ScreenRecordingSettingsUri =
        "x-apple.systempreferences:com.apple.PreferencePanes.Security?Privacy_ScreenCapture";

    public bool IsSupported => OperatingSystem.IsMacOS();

    public bool HasScreenRecordingPermission()
    {
        return IsSupported && MacCoreGraphics.CGPreflightScreenCaptureAccess();
    }

    public bool RequestScreenRecordingPermission()
    {
        return IsSupported && MacCoreGraphics.CGRequestScreenCaptureAccess();
    }

    public UiOperationResult OpenScreenRecordingSettings()
    {
        if (!IsSupported)
        {
            return UiOperationResult.Fail(
                UiErrorCode.PlatformOperationFailed,
                "Screen Recording settings are only available on macOS.");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(ScreenRecordingSettingsUri);

            var process = Process.Start(startInfo);
            return process is null
                ? UiOperationResult.Fail(
                    UiErrorCode.ExternalTargetOpenFailed,
                    "Failed to open Screen Recording settings.")
                : UiOperationResult.Ok("Screen Recording settings opened.");
        }
        catch (Exception ex)
        {
            return UiOperationResult.Fail(
                UiErrorCode.ExternalTargetOpenFailed,
                $"Failed to open Screen Recording settings: {ex.Message}",
                ex.Message);
        }
    }

    private static class MacCoreGraphics
    {
        private const string CoreGraphicsLibrary = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        [DllImport(CoreGraphicsLibrary)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool CGPreflightScreenCaptureAccess();

        [DllImport(CoreGraphicsLibrary)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool CGRequestScreenCaptureAccess();
    }
}
