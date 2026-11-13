using System.Reflection;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class PostActionExecutorSupportTests
{
    [Fact]
    public void CommandPostActionExecutorService_GetCapabilityMatrix_ShouldReportWindowsOnlySupportForExitEmulator()
    {
        var service = new CommandPostActionExecutorService();
        var request = new PostActionExecutorRequest(ConnectAddress: "127.0.0.1:5555", ConnectConfig: "LDPlayer", AdbPath: "adb");

        var capability = service.GetCapabilityMatrix(request).Get(PostActionType.ExitEmulator);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(capability.Supported, capability.Message);
            return;
        }

        Assert.False(capability.Supported);
        Assert.Contains("unsupported", capability.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsEmulatorExitHelper_TryResolveMuMuIndex_ShouldPreferBridgeIndexAndPortRules()
    {
        var helperType = typeof(CommandPostActionExecutorService).Assembly.GetType("MAAUnified.Platform.WindowsEmulatorExitHelper");
        Assert.NotNull(helperType);

        var method = helperType!.GetMethod(
            "TryResolveMuMuIndex",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);

        var byBridge = new PostActionExecutorRequest(
            ConnectAddress: "127.0.0.1:16416",
            ConnectConfig: "MuMuEmulator12",
            MuMuBridgeConnection: true,
            MuMu12Index: "3");
        var byPort = new PostActionExecutorRequest(
            ConnectAddress: "127.0.0.1:16448",
            ConnectConfig: "MuMuEmulator12");

        object?[] bridgeArgs = [byBridge, 0];
        object?[] portArgs = [byPort, 0];
        Assert.True((bool)method!.Invoke(null, bridgeArgs)!);
        Assert.Equal(3, Assert.IsType<int>(bridgeArgs[1]));
        Assert.True((bool)method.Invoke(null, portArgs)!);
        Assert.Equal(2, Assert.IsType<int>(portArgs[1]));
    }

    [Fact]
    public void WindowsEmulatorExitHelper_TryResolveLdPlayerIndex_ShouldSupportManualAndSerialFormats()
    {
        var helperType = typeof(CommandPostActionExecutorService).Assembly.GetType("MAAUnified.Platform.WindowsEmulatorExitHelper");
        Assert.NotNull(helperType);

        var method = helperType!.GetMethod(
            "TryResolveLdPlayerIndex",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);

        var manual = new PostActionExecutorRequest(
            ConnectAddress: "127.0.0.1:5555",
            ConnectConfig: "LDPlayer",
            LdPlayerManualSetIndex: true,
            LdPlayerIndex: "4");
        var serial = new PostActionExecutorRequest(
            ConnectAddress: "emulator-5558",
            ConnectConfig: "LDPlayer");

        object?[] manualArgs = [manual, 0];
        object?[] serialArgs = [serial, 0];
        Assert.True((bool)method!.Invoke(null, manualArgs)!);
        Assert.Equal(4, Assert.IsType<int>(manualArgs[1]));
        Assert.True((bool)method.Invoke(null, serialArgs)!);
        Assert.Equal(2, Assert.IsType<int>(serialArgs[1]));
    }

    [Fact]
    public void CommandPostActionExecutorService_GetCapabilityMatrix_ShouldNotUseLegacyCommandLineFallback_ForCoreManagedActions()
    {
        var service = new CommandPostActionExecutorService();
        var withLegacyCommand = new PostActionExecutorRequest(CommandLine: "echo close-maa");
        var capability = service.GetCapabilityMatrix(withLegacyCommand);

        Assert.False(capability.ExitArknights.Supported);
        Assert.False(capability.BackToAndroidHome.Supported);
        Assert.False(capability.ExitSelf.Supported);
        // WPF dev-v2 3b4f06dd86: the PC client exit reuses MaaCore CloseDown, so it is core-managed
        // exactly like ｢exit Arknights｣ and never falls back to a legacy command line.
        Assert.False(capability.ExitPCClient!.Supported);
        Assert.Contains("requires native provider", capability.ExitArknights.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requires native provider", capability.BackToAndroidHome.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requires native provider", capability.ExitSelf.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requires native provider", capability.ExitPCClient!.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WindowsEmulatorExitHelper_ExecuteConsoleWithFallback_UsesConsoleResultWhenSuccessful()
    {
        var fallbackCalled = false;

        var result = await WindowsEmulatorExitHelper.ExecuteConsoleWithFallbackAsync(
            (_, _) => Task.FromResult(PlatformOperation.NativeSuccess("test", "console ok", "test-op")),
            () =>
            {
                fallbackCalled = true;
                return PlatformOperation.NativeSuccess("test", "fallback ok", "test-op");
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("console ok", result.Message);
        Assert.False(fallbackCalled);
    }

    [Fact]
    public async Task WindowsEmulatorExitHelper_ExecuteConsoleWithFallback_FallsBackWhenConsoleFails()
    {
        var fallbackCalled = false;

        var result = await WindowsEmulatorExitHelper.ExecuteConsoleWithFallbackAsync(
            (_, _) => Task.FromResult(PlatformOperation.Failed(
                "test",
                "console timed out",
                PlatformErrorCodes.PostActionExecutionFailed,
                "test-op")),
            () =>
            {
                fallbackCalled = true;
                return PlatformOperation.NativeSuccess("test", "fallback ok", "test-op");
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("fallback ok", result.Message);
        Assert.True(fallbackCalled);
    }

    [Fact]
    public async Task PostActionExecutorSupport_ExecuteDirectCommand_TimesOutAndReportsFailure()
    {
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c ping -n 10 127.0.0.1")
            : ("/bin/sleep", "5");

        var result = await PostActionExecutorSupport.ExecuteDirectCommandAsync(
            "test",
            "test-op",
            "console command finished.",
            fileName,
            arguments,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(300));

        Assert.False(result.Success);
        Assert.Contains("Timed out", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\"/opt/tool/run.sh\" --fast -noWindow -minimized", "/opt/tool/run.sh", "--fast", true, true)]
    [InlineData("\"/opt/tool/run.sh\"", "/opt/tool/run.sh", "", false, false)]
    [InlineData("/opt/tool/run.sh", "/opt/tool/run.sh", "", false, false)]
    public void PostActionExecutorSupport_TryParseUserScript_ParsesQuotedPathAndFlags(
        string raw,
        string expectedFile,
        string expectedArguments,
        bool expectedNoWindow,
        bool expectedMinimized)
    {
        var parsed = PostActionExecutorSupport.TryParseUserScript(
            raw,
            out var fileName,
            out var arguments,
            out var createNoWindow,
            out var minimized);

        Assert.True(parsed);
        Assert.Equal(expectedFile, fileName);
        Assert.Equal(expectedArguments, arguments);
        Assert.Equal(expectedNoWindow, createNoWindow);
        Assert.Equal(expectedMinimized, minimized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void PostActionExecutorSupport_TryParseUserScript_RejectsEmptyInput(string raw)
    {
        var parsed = PostActionExecutorSupport.TryParseUserScript(
            raw,
            out _,
            out _,
            out _,
            out _);

        Assert.False(parsed);
    }

    [Fact]
    public async Task PostActionExecutorSupport_ExecuteUserScript_RunsProcessToCompletion()
    {
        var script = OperatingSystem.IsWindows()
            ? "\"cmd.exe\" /c exit 0 -noWindow"
            : "\"/bin/echo\" hello -noWindow";

        var result = await PostActionExecutorSupport.ExecuteUserScriptAsync(script, CancellationToken.None);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task PostActionExecutorSupport_ExecuteUserScript_FailsForMissingFile()
    {
        var result = await PostActionExecutorSupport.ExecuteUserScriptAsync(
            "\"definitely-not-a-real-script-9f8d7c6b5a\" -noWindow",
            CancellationToken.None);

        Assert.False(result.Success);
    }
}
