using System.Reflection;
using System.Text.Json;
using MAAUnified.CoreBridge;

namespace MAAUnified.Tests;

public sealed class MaaCoreBridgeNativeClientTypeTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(" official ", "Official")]
    [InlineData("BILIBILI", "Bilibili")]
    [InlineData("txwy", "txwy")]
    [InlineData("Txwy", "txwy")]
    [InlineData("yostaren", "YoStarEN")]
    [InlineData("YOSTARJP", "YoStarJP")]
    [InlineData("YoStarKR", "YoStarKR")]
    [InlineData("CustomClient", "CustomClient")]
    public void NormalizeClientType_ShouldCanonicalizeKnownValues(string? rawClientType, string expected)
    {
        var method = typeof(MaaCoreBridgeNative).GetMethod(
            "NormalizeClientType",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var normalized = method!.Invoke(null, [rawClientType]) as string;
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("WSA", "YoStarEN", "YoStarEN")]
    [InlineData("Androws", "Txwy", "txwy")]
    [InlineData("General", "YoStarEN", "")]
    [InlineData("MuMuEmulator12", "YoStarJP", "")]
    [InlineData("LDPlayer", "YoStarKR", "")]
    public void ResolveConnectionClientType_ShouldFollowWpfParity(
        string connectConfig,
        string clientType,
        string expected)
    {
        var connectionInfo = new CoreConnectionInfo(
            "127.0.0.1:5555",
            connectConfig,
            "adb",
            new CoreConnectionExtras(ClientType: clientType));

        Assert.Equal(expected, MaaCoreBridgeNative.ResolveConnectionClientTypeForTest(connectionInfo));
    }

    [Fact]
    public void BuildMuMu12ExtrasJson_ShouldMatchWpfPayload_AndClearWhenDisabledOrOtherConfig()
    {
        var enabled = new CoreConnectionInfo(
            "127.0.0.1:16384",
            "MuMuEmulator12",
            "adb",
            new CoreConnectionExtras(
                MuMu12ExtrasEnabled: true,
                MuMu12EmulatorPath: @"C:\MuMu",
                MuMuBridgeConnection: true,
                MuMu12Index: "3"));

        using var payload = JsonDocument.Parse(MaaCoreBridgeNative.BuildMuMu12ExtrasJsonForTest(enabled));
        Assert.Equal(@"C:\MuMu", payload.RootElement.GetProperty("path").GetString());
        Assert.Equal(3, payload.RootElement.GetProperty("index").GetInt32());
        Assert.False(payload.RootElement.TryGetProperty("enable", out _));
        Assert.False(payload.RootElement.TryGetProperty("mumu_bridge_connection", out _));

        var disabled = enabled with { Extras = enabled.Extras! with { MuMu12ExtrasEnabled = false } };
        Assert.Equal("{}", MaaCoreBridgeNative.BuildMuMu12ExtrasJsonForTest(disabled));

        var otherConfig = enabled with { ConnectConfig = "General" };
        Assert.Equal("{}", MaaCoreBridgeNative.BuildMuMu12ExtrasJsonForTest(otherConfig));
    }

    [Fact]
    public void BuildLdPlayerExtrasJson_ShouldMatchWpfPayload_AndAutoDeriveIndex()
    {
        var autoIndex = new CoreConnectionInfo(
            "emulator-5558",
            "LDPlayer",
            "adb",
            new CoreConnectionExtras(
                LdPlayerExtrasEnabled: true,
                LdPlayerEmulatorPath: @"C:\LDPlayer",
                LdPlayerManualSetIndex: false,
                LdPlayerIndex: "9"));

        using var payload = JsonDocument.Parse(MaaCoreBridgeNative.BuildLdPlayerExtrasJsonForTest(autoIndex));
        Assert.Equal(@"C:\LDPlayer", payload.RootElement.GetProperty("path").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("index").GetInt32());
        Assert.Equal(0, payload.RootElement.GetProperty("pid").GetInt32());
        Assert.False(payload.RootElement.TryGetProperty("enable", out _));
        Assert.False(payload.RootElement.TryGetProperty("manual_set_index", out _));

        var manualIndex = autoIndex with
        {
            Address = "127.0.0.1:5555",
            Extras = autoIndex.Extras! with
            {
                LdPlayerManualSetIndex = true,
                LdPlayerIndex = "4",
            },
        };
        using var manualPayload = JsonDocument.Parse(MaaCoreBridgeNative.BuildLdPlayerExtrasJsonForTest(manualIndex));
        Assert.Equal(4, manualPayload.RootElement.GetProperty("index").GetInt32());

        var disabled = autoIndex with { Extras = autoIndex.Extras! with { LdPlayerExtrasEnabled = false } };
        Assert.Equal("{}", MaaCoreBridgeNative.BuildLdPlayerExtrasJsonForTest(disabled));
    }

    [Fact]
    public void TryResolveClientResourcePath_ShouldMatchDirectoryName_CaseInsensitive()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-core-bridge-client-type", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "resource", "global", "YoStarEN", "resource"));
            var method = typeof(MaaCoreBridgeNative).GetMethod(
                "TryResolveClientResourcePath",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            object?[] args =
            [
                root,
                "yostaren",
                string.Empty,
                string.Empty,
            ];

            var found = method!.Invoke(null, args);
            Assert.IsType<bool>(found);
            Assert.True((bool)found!);
            Assert.Equal("YoStarEN", Assert.IsType<string>(args[2]));
            Assert.EndsWith(
                Path.Combine("resource", "global", "YoStarEN", "resource"),
                Assert.IsType<string>(args[3]),
                StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temporary test directories
            }
        }
    }

    [Fact]
    public void TryResolveClientResourcePath_WhenClientResourceMissing_ShouldReturnFalse()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-core-bridge-client-type", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "resource", "global", "YoStarEN"));
            var method = typeof(MaaCoreBridgeNative).GetMethod(
                "TryResolveClientResourcePath",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            object?[] args =
            [
                root,
                "YoStarEN",
                string.Empty,
                string.Empty,
            ];

            var found = method!.Invoke(null, args);
            Assert.IsType<bool>(found);
            Assert.False((bool)found!);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temporary test directories
            }
        }
    }

    [Fact]
    public void BuildConnectionFailureMessage_ShouldExposeRawCommandOutput()
    {
        var method = typeof(MaaCoreBridgeNative).GetMethod(
            "BuildConnectionFailureMessage",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var doc = JsonDocument.Parse(
            """
            {
              "what": "ConnectFailed",
              "why": "Connection command failed to exec",
              "details": {
                "raw_output": "由于找不到 AdbWinApi.dll，无法继续执行代码。\r\n重新安装程序可能会解决此问题。"
              }
            }
            """);

        var message = method!.Invoke(null, [doc.RootElement.Clone(), "ConnectFailed"]) as string;

        Assert.Equal(
            "由于找不到 AdbWinApi.dll，无法继续执行代码。\n重新安装程序可能会解决此问题。",
            message);
    }

    [Fact]
    public void BuildConnectionFailureMessage_ShouldReturnWhy_WhenRawOutputMissing()
    {
        var method = typeof(MaaCoreBridgeNative).GetMethod(
            "BuildConnectionFailureMessage",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var doc = JsonDocument.Parse(
            """
            {
              "what": "ConnectFailed",
              "why": "ConfigNotFound",
              "details": {}
            }
            """);

        var message = method!.Invoke(null, [doc.RootElement.Clone(), "ConnectFailed"]) as string;

        Assert.Equal("ConfigNotFound", message);
    }

    [Fact]
    public void BuildConnectionFailureMessage_ShouldExplainTouchModeNotAvailable()
    {
        var method = typeof(MaaCoreBridgeNative).GetMethod(
            "BuildConnectionFailureMessage",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var doc = JsonDocument.Parse(
            """
            {
              "what": "TouchModeNotAvailable",
              "why": "",
              "details": {}
            }
            """);

        var message = method!.Invoke(null, [doc.RootElement.Clone(), "TouchModeNotAvailable"]) as string;

        Assert.Equal(
            "Touch mode is not available. Switch to a different touch mode in Settings > Connect.",
            message);
    }
}
