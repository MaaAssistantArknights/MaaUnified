using Avalonia;
using Avalonia.X11;
using MAAUnified.App;

namespace MAAUnified.Tests;

public sealed class StartupProgramTests
{
    [Fact]
    public void HasLinuxDesktopDisplay_WhenDisplaySet_ReturnsTrue()
    {
        var originalDisplay = Environment.GetEnvironmentVariable("DISPLAY");
        var originalWayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        try
        {
            Environment.SetEnvironmentVariable("DISPLAY", ":99");
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);

            Assert.True(Program.HasLinuxDesktopDisplay());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISPLAY", originalDisplay);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", originalWayland);
        }
    }

    [Fact]
    public void HasLinuxDesktopDisplay_WhenWaylandSet_ReturnsTrue()
    {
        var originalDisplay = Environment.GetEnvironmentVariable("DISPLAY");
        var originalWayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        try
        {
            Environment.SetEnvironmentVariable("DISPLAY", null);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "wayland-0");

            Assert.True(Program.HasLinuxDesktopDisplay());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISPLAY", originalDisplay);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", originalWayland);
        }
    }

    [Fact]
    public void HasLinuxDesktopDisplay_WhenBothUnset_ReturnsFalse()
    {
        var originalDisplay = Environment.GetEnvironmentVariable("DISPLAY");
        var originalWayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        try
        {
            Environment.SetEnvironmentVariable("DISPLAY", null);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);

            Assert.False(Program.HasLinuxDesktopDisplay());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISPLAY", originalDisplay);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", originalWayland);
        }
    }

    [Fact]
    public void IsDisplayInitializationFailure_WhenContainsXOpenDisplayError_ReturnsTrue()
    {
        var ex = new Exception("Bootstrap failed.", new InvalidOperationException("XOpenDisplay failed"));
        Assert.True(Program.IsDisplayInitializationFailure(ex));
    }

    [Fact]
    public void IsDisplayInitializationFailure_WhenContainsOpenDisplayMessage_ReturnsTrue()
    {
        var ex = new Exception("Unable to open display :1");
        Assert.True(Program.IsDisplayInitializationFailure(ex));
    }

    [Fact]
    public void IsDisplayInitializationFailure_WhenUnrelatedError_ReturnsFalse()
    {
        var ex = new Exception("Configuration load failed.");
        Assert.False(Program.IsDisplayInitializationFailure(ex));
    }

    [Fact]
    public void BuildStartupEnvironmentSnapshot_IncludesFrameworkAndArguments()
    {
        var snapshot = Program.BuildStartupEnvironmentSnapshot(["--profile", "default profile"]);

        Assert.Contains("framework=", snapshot, StringComparison.Ordinal);
        Assert.Contains("os=", snapshot, StringComparison.Ordinal);
        Assert.Contains("processPath=", snapshot, StringComparison.Ordinal);
        Assert.Contains("args=--profile \"default profile\"", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseStartupArguments_ConsumesConfigAndRestartWaitArguments()
    {
        var parsed = Program.ParseStartupArguments(["--config", "Night profile", "--wait-for-single-instance", "--trace"]);

        Assert.Equal("Night profile", parsed.ConfigurationName);
        Assert.True(parsed.WaitForSingleInstance);
        Assert.Equal(["--trace"], parsed.AvaloniaArguments);
    }

    [Fact]
    public void ParseStartupArguments_SupportsInlineConfigValue()
    {
        var parsed = Program.ParseStartupArguments(["--config=Night profile"]);

        Assert.Equal("Night profile", parsed.ConfigurationName);
        Assert.Empty(parsed.AvaloniaArguments);
    }

    [Fact]
    public void BuildStartupFailurePromptMessage_ReferencesDebugLog()
    {
        Assert.Contains("debug", Program.BuildStartupFailurePromptMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStartupTracePayload_WhenExceptionProvided_IncludesStageAndException()
    {
        var payload = Program.BuildStartupTracePayload(
            "FrameworkInit.RuntimeCreate.Fail",
            "Runtime creation failed.",
            new InvalidOperationException("boom"));

        Assert.Contains("[STARTUP]", payload, StringComparison.Ordinal);
        Assert.Contains("App.Startup.FrameworkInit.RuntimeCreate.Fail", payload, StringComparison.Ordinal);
        Assert.Contains("Runtime creation failed.", payload, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: boom", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordStartupStage_RedactsUserPathBeforeWritingStderr()
    {
        var originalError = Console.Error;
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using var stderr = new StringWriter();
        try
        {
            Console.SetError(stderr);

            Program.RecordStartupStage(
                "Main.PortablePackage.Invalid",
                $"packageLayout=invalid path={Path.Combine(userHome, "portable")}");
        }
        finally
        {
            Console.SetError(originalError);
        }

        var output = stderr.ToString();
        Assert.Contains("packageLayout=invalid", output, StringComparison.Ordinal);
        Assert.DoesNotContain(userHome, output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFileName(userHome), output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveSoftwareRenderingPreference_WhenAvaloniaConfigExists_ReturnsConfiguredValue()
    {
        var root = CreateTempConfigDirectory(
            """
            {
              "GlobalValues": {
                "GUI.IgnoreBadModulesAndUseSoftwareRendering": true
              }
            }
            """);

        try
        {
            Assert.True(Program.ResolveSoftwareRenderingPreference(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveSoftwareRenderingPreference_WhenAvaloniaConfigMissing_FallsBackToGuiNew()
    {
        var root = CreateTempConfigDirectory(
            avaloniaJson: null,
            guiNewJson:
            """
            {
              "GUI": {
                "IgnoreBadModulesAndUseSoftwareRendering": "true"
              }
            }
            """);

        try
        {
            Assert.True(Program.ResolveSoftwareRenderingPreference(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildLinuxPlatformOptions_DisablesDbusMenu()
    {
        var options = Program.BuildLinuxPlatformOptions(useSoftwareRendering: false);

        Assert.False(options.UseDBusMenu);
    }

    [Fact]
    public void BuildLinuxPlatformOptions_WhenSoftwareRenderingRequested_UsesSoftwareMode()
    {
        var options = Program.BuildLinuxPlatformOptions(useSoftwareRendering: true);

        Assert.Contains(global::Avalonia.X11RenderingMode.Software, options.RenderingMode);
    }

    private static string CreateTempConfigDirectory(string? avaloniaJson = null, string? guiNewJson = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"maa-unified-program-{Guid.NewGuid():N}");
        var configDir = Path.Combine(root, "config");
        Directory.CreateDirectory(configDir);

        if (avaloniaJson is not null)
        {
            File.WriteAllText(Path.Combine(configDir, "avalonia.json"), avaloniaJson);
        }

        if (guiNewJson is not null)
        {
            File.WriteAllText(Path.Combine(configDir, "gui.new.json"), guiNewJson);
        }

        return root;
    }
}
