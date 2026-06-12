using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Models;
using MAAUnified.CoreBridge;

namespace MAAUnified.Tests;

public sealed class ConnectionFailureDiagnosticBuilderTests
{
    [Fact]
    public void Build_WhenAdbPathDoesNotExist_ShouldExplainAdbPath()
    {
        var state = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = "127.0.0.1:5555",
            MacUseBundledAdb = false,
            AdbPath = OperatingSystem.IsWindows()
                ? @"Z:\maaunified-missing-adb\adb.exe"
                : "/tmp/maaunified-missing-adb/adb",
        };

        var diagnostic = ConnectionFailureDiagnosticBuilder.Build(
            UiOperationResult.Fail(UiErrorCode.ConnectFailed, "ConnectFailed"),
            state,
            language: "zh-cn");

        Assert.True(diagnostic.IsSpecific);
        Assert.Equal(ConnectionFailureCategory.AdbPathInvalid, diagnostic.Category);
        Assert.Contains("ADB 路径不存在", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(state.AdbPath, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WhenWindowsAdbPathOnNonWindows_ShouldExplainPlatformPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var state = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = "127.0.0.1:5555",
            MacUseBundledAdb = false,
            AdbPath = @"D:\Program Files\Netease\MuMuPlayer-12.0\shell\adb.exe",
        };

        var diagnostic = ConnectionFailureDiagnosticBuilder.Build(
            UiOperationResult.Fail(UiErrorCode.ConnectFailed, "ConnectFailed"),
            state,
            language: "zh-cn");

        Assert.True(diagnostic.IsSpecific);
        Assert.Equal(ConnectionFailureCategory.AdbPathInvalid, diagnostic.Category);
        Assert.Contains("Windows 路径", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WhenConnectTimeout_ShouldExplainTimeout()
    {
        var diagnostic = ConnectionFailureDiagnosticBuilder.Build(
            UiOperationResult.Fail(nameof(CoreErrorCode.ConnectTimeout), "Connect timeout after 30 seconds."),
            CreateManualAdbState(),
            language: "zh-cn");

        Assert.True(diagnostic.IsSpecific);
        Assert.Equal(ConnectionFailureCategory.ConnectTimeout, diagnostic.Category);
        Assert.Contains("超时", diagnostic.BuildDialogMessage(), StringComparison.Ordinal);
        Assert.Contains("模拟器已启动", diagnostic.BuildDialogMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WhenQuickAddressPrecheckFails_ShouldIncludeEffectiveConfigFallbackAndClientType()
    {
        var state = new ConnectionGameSharedStateViewModel
        {
            ConnectAddress = "192.168.0.10:5555",
            ConnectConfig = "General",
            MacUseBundledAdb = false,
            AdbPath = Environment.ProcessPath ?? AppContext.BaseDirectory,
            TouchMode = "MaaFwAdb",
            AdbLiteEnabled = true,
            ClientType = "YoStarEN",
        };

        var result = UiOperationResult.Fail(
            nameof(CoreErrorCode.ConnectFailed),
            "Connection address `192.168.0.10:5555` failed a quick TCP probe.",
            """
            Quick connect precheck failed.
            probe=tcp host=192.168.0.10 port=5555 timeoutMs=750
            adb=/tmp/platform-tools/adb
            address=192.168.0.10:5555
            config=General
            fallback=configured:MaaFwAdb/adbLite=True
            extras=macBundledAdb=False,touch=MaaFwAdb,adbLite=True,killAdbOnExit=False,mumu=False:<empty>:False:<empty>,ld=False:<empty>:False:<empty>,attach=<empty>:<empty>:<empty>
            clientType=YoStarEN
            adb devices:
            adb devices exit=0
            stdout:
            List of devices attached
            """);

        var diagnostic = ConnectionFailureDiagnosticBuilder.Build(
            result,
            state,
            language: "en-us");

        Assert.Contains("Fallback: configured:MaaFwAdb/adbLite=True", diagnostic.Details, StringComparison.Ordinal);
        Assert.Contains("ADB: " + state.AdbPath, diagnostic.Details, StringComparison.Ordinal);
        Assert.Contains("Address: 192.168.0.10:5555", diagnostic.Details, StringComparison.Ordinal);
        Assert.Contains("Connect config: General", diagnostic.Details, StringComparison.Ordinal);
        Assert.Contains("clientType=YoStarEN", diagnostic.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WhenTcpProbeFails_ShouldExplainAddressOrPort()
    {
        var result = UiOperationResult.Fail(
            nameof(CoreErrorCode.ConnectFailed),
            "Connection address `127.0.0.1:5554` failed a quick TCP probe. Candidate causes: emulator is not running, port is wrong, ADB debugging is disabled, or the address belongs to another emulator.",
            """
            Quick connect precheck failed.
            stage=tcp-probe-failed
            probe=tcp host=127.0.0.1 port=5554 timeoutMs=750
            """);

        var diagnostic = ConnectionFailureDiagnosticBuilder.Build(
            result,
            CreateManualAdbState(),
            language: "zh-cn");

        Assert.True(diagnostic.IsSpecific);
        Assert.Equal(ConnectionFailureCategory.AddressOrPortUnavailable, diagnostic.Category);
        Assert.Contains("地址或端口", diagnostic.BuildDialogMessage(), StringComparison.Ordinal);
        Assert.Contains("模拟器已启动", diagnostic.BuildDialogMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain("probe=tcp", diagnostic.BuildDialogMessage(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("probe=tcp host=127.0.0.1 port=5554 timeoutMs=750", diagnostic.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WhenAdbDevicesReportsUnauthorized_ShouldExplainDeviceState()
    {
        var diagnostic = ConnectionFailureDiagnosticBuilder.Build(
            UiOperationResult.Fail(
                nameof(CoreErrorCode.ConnectFailed),
                "adb devices reported device unauthorized.",
                """
                adb devices:
                List of devices attached
                emulator-5554 unauthorized
                """),
            CreateManualAdbState(),
            language: "zh-cn");

        Assert.True(diagnostic.IsSpecific);
        Assert.Equal(ConnectionFailureCategory.AdbDeviceUnavailable, diagnostic.Category);
        Assert.Contains("ADB 未连接到可用设备", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("授权", diagnostic.Suggestion, StringComparison.Ordinal);
        Assert.DoesNotContain("List of devices attached", diagnostic.BuildDialogMessage(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("emulator-5554 unauthorized", diagnostic.BuildDialogMessage(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unauthorized", diagnostic.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_WhenCoreReportsInvalidConnection_ShouldExplainConnectionConfiguration()
    {
        var diagnostic = ConnectionFailureDiagnosticBuilder.Build(
            UiOperationResult.Fail(
                "InvalidConnection",
                "InvalidConnection: invalid address or port."),
            CreateManualAdbState(),
            language: "zh-cn");

        Assert.True(diagnostic.IsSpecific);
        Assert.Equal(ConnectionFailureCategory.AddressOrPortUnavailable, diagnostic.Category);
        Assert.Contains("地址", diagnostic.BuildDialogMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidConnection", diagnostic.BuildDialogMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WhenTouchModeUnavailable_ShouldExplainTouchMode()
    {
        const string details = """
        {
          "what": "TouchModeNotAvailable",
          "details": {
            "raw_output": "Touch mode is not available."
          }
        }
        """;

        var diagnostic = ConnectionFailureDiagnosticBuilder.Build(
            UiOperationResult.Fail(nameof(CoreErrorCode.ConnectFailed), "Touch mode is not available.", details),
            CreateManualAdbState(),
            language: "zh-cn");

        Assert.True(diagnostic.IsSpecific);
        Assert.Equal(ConnectionFailureCategory.TouchModeUnavailable, diagnostic.Category);
        Assert.Contains("触控模式不可用", diagnostic.BuildDialogMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WhenAdbStartServerFails_ShouldPreferAdbCommandFailure()
    {
        var adbFailure = new AdbCommandFailureInfo(
            "adb start-server",
            "adb",
            "start-server",
            1,
            "cannot bind tcp:5037",
            string.Empty);

        var diagnostic = ConnectionFailureDiagnosticBuilder.Build(
            UiOperationResult.Fail(nameof(CoreErrorCode.ConnectFailed), "ConnectFailed"),
            CreateManualAdbState(),
            adbCommandFailure: adbFailure,
            language: "zh-cn");

        Assert.True(diagnostic.IsSpecific);
        Assert.Equal(ConnectionFailureCategory.AdbCommandFailed, diagnostic.Category);
        Assert.Contains("ADB 命令执行失败", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("ADB 输出", diagnostic.Suggestion, StringComparison.Ordinal);
        Assert.Contains("ExitCode: 1", diagnostic.Details, StringComparison.Ordinal);
        Assert.Contains("cannot bind tcp:5037", diagnostic.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WhenAdbPathIsDmgAndPermissionDenied_ShouldShowFriendlyAdbStartFailure()
    {
        var adbFailure = new AdbCommandFailureInfo(
            "adb kill-server",
            "/Users/halo/Downloads/MAAUnified-v0.1.0-beta.2-macos-arm64.dmg",
            "kill-server",
            null,
            null,
            null,
            "An error occurred trying to start process '/Users/halo/Downloads/MAAUnified-v0.1.0-beta.2-macos-arm64.dmg'. Permission denied");

        var diagnostic = ConnectionFailureDiagnosticBuilder.Build(
            UiOperationResult.Fail(nameof(CoreErrorCode.ConnectFailed), "Connection command failed to exec"),
            CreateManualAdbState(),
            adbCommandFailure: adbFailure,
            language: "zh-cn");

        Assert.True(diagnostic.IsSpecific);
        Assert.Equal(ConnectionFailureCategory.AdbCommandFailed, diagnostic.Category);
        Assert.Equal("ADB 启动失败。", diagnostic.Message);
        Assert.Contains("adb 可执行文件", diagnostic.Suggestion, StringComparison.Ordinal);
        Assert.DoesNotContain(".dmg", diagnostic.BuildDialogMessage(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Permission denied", diagnostic.BuildDialogMessage(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".dmg", diagnostic.Details, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Permission denied", diagnostic.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_WhenAdbReportsNoSuchDevice_ShouldExplainTargetDevice()
    {
        var adbFailure = new AdbCommandFailureInfo(
            "adb disconnect",
            "/tmp/platform-tools/adb",
            "disconnect 192.168.0.252:555",
            1,
            "error: no such device '192.168.0.252:555'",
            string.Empty);

        var diagnostic = ConnectionFailureDiagnosticBuilder.Build(
            UiOperationResult.Fail(nameof(CoreErrorCode.ConnectFailed), "ConnectFailed"),
            CreateManualAdbState(),
            adbCommandFailure: adbFailure,
            language: "zh-cn");

        Assert.True(diagnostic.IsSpecific);
        Assert.Equal(ConnectionFailureCategory.AdbDeviceUnavailable, diagnostic.Category);
        Assert.Equal("ADB 未连接到目标设备。", diagnostic.Message);
        Assert.Contains("地址和端口", diagnostic.Suggestion, StringComparison.Ordinal);
        Assert.Contains("5555", diagnostic.Suggestion, StringComparison.Ordinal);
        Assert.Contains("no such device", diagnostic.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_WhenCoreSaysConnectCommandDidNotReportConnected_ShouldExplainTargetDevice()
    {
        const string details = """
        {
          "details": {
            "adb": "/tmp/platform-tools/adb",
            "address": "192.168.0.252:555",
            "config": "General"
          },
          "what": "ConnectFailed",
          "why": "Connection command did not report \"connected\""
        }
        """;

        var diagnostic = ConnectionFailureDiagnosticBuilder.Build(
            UiOperationResult.Fail(nameof(CoreErrorCode.ConnectFailed), "Connection command did not report \"connected\"", details),
            CreateManualAdbState(),
            language: "zh-cn");

        Assert.True(diagnostic.IsSpecific);
        Assert.Equal(ConnectionFailureCategory.AdbDeviceUnavailable, diagnostic.Category);
        Assert.Equal("ADB 未连接到目标设备。", diagnostic.Message);
        Assert.Contains("5555", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WhenUnknownConnectFailed_ShouldKeepGenericFallback()
    {
        var diagnostic = ConnectionFailureDiagnosticBuilder.Build(
            UiOperationResult.Fail(UiErrorCode.ConnectFailed, "ConnectFailed"),
            CreateManualAdbState(),
            language: "zh-cn");

        Assert.False(diagnostic.IsSpecific);
        Assert.Equal(ConnectionFailureCategory.Unknown, diagnostic.Category);
        Assert.Contains("连接失败。请", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("连接回调", diagnostic.Message, StringComparison.Ordinal);
    }

    private static ConnectionGameSharedStateViewModel CreateManualAdbState()
        => new()
        {
            ConnectAddress = "127.0.0.1:5555",
            MacUseBundledAdb = false,
            AdbPath = Environment.ProcessPath ?? AppContext.BaseDirectory,
        };
}
