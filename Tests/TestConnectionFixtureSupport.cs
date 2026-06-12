using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Compat.Runtime;

namespace MAAUnified.Tests;

internal static class TestConnectionFixtureSupport
{
    public const string ReadyConnectAddress = "emulator-5554";
    public const string ReadyConnectConfig = "General";

    public static void SeedMacMaaAdbControlUnitLibrary(string runtimeBaseDirectory)
    {
        Directory.CreateDirectory(runtimeBaseDirectory);
        File.WriteAllText(
            RuntimeLayout.ResolveMacMaaFrameworkRuntimeLibraryPath(
                runtimeBaseDirectory,
                RuntimeLayout.MacMaaAdbControlUnitLibraryFileName),
            "test-control-unit");
    }

    public static async Task<string> CreateExecutableAdbAsync(string root, string name)
    {
        var directory = Path.Combine(root, "adb-tools", name);
        Directory.CreateDirectory(directory);
        var adbPath = Path.Combine(directory, OperatingSystem.IsWindows() ? "adb.exe" : "adb");
        await File.WriteAllTextAsync(
            adbPath,
            OperatingSystem.IsWindows()
                ? string.Empty
                : """
                  #!/bin/sh
                  if [ "$1" = "-s" ] && [ "$3" = "get-state" ]; then
                    printf 'device\n'
                    exit 0
                  fi
                  if [ "$1" = "devices" ]; then
                    printf 'List of devices attached\nemulator-5554\tdevice\nemulator-5556\tdevice\n127.0.0.1:5555\tdevice\n'
                    exit 0
                  fi
                  if [ "$1" = "devices" ] && [ "$2" = "-l" ]; then
                    printf 'List of devices attached\nemulator-5554\tdevice product:test model:test device:test\n'
                    exit 0
                  fi
                  if [ "$1" = "connect" ] && [ -n "$2" ]; then
                    printf 'already connected to %s\n' "$2"
                    exit 0
                  fi
                  exit 0
                  """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                adbPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return adbPath;
    }

    public static async Task<string> PrepareReadyRuntimeAsync(
        string root,
        UnifiedConfigurationService? config = null,
        string adbName = "adb-ready")
    {
        SeedMacMaaAdbControlUnitLibrary(root);
        var adbPath = await CreateExecutableAdbAsync(root, adbName);
        if (config is not null && config.TryGetCurrentProfile(out var profile))
        {
            profile.Values["ConnectAddress"] = JsonValue.Create(ReadyConnectAddress);
            profile.Values["ConnectConfig"] = JsonValue.Create(ReadyConnectConfig);
            profile.Values["AdbPath"] = JsonValue.Create(adbPath);
            profile.Values[MacBundledAdbPolicy.ProfileUseBundledAdbKey] = JsonValue.Create(false);
        }

        return adbPath;
    }

    public static Task<UiOperationResult> ConnectReadyAsync(
        IConnectFeatureService connectFeatureService,
        string adbPath,
        CancellationToken cancellationToken = default)
    {
        return connectFeatureService.ConnectAsync(
            ReadyConnectAddress,
            ReadyConnectConfig,
            adbPath,
            cancellationToken);
    }
}
