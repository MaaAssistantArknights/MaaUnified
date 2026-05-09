using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Win32;
using Avalonia.X11;
using MAAUnified.Compat.Constants;
using MAAUnified.Compat.Runtime;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.VersionUpdate;

namespace MAAUnified.App;

internal static class Program
{
    private const long RecommendedMaxGpuResourceSizeBytes = 256L * 1024L * 1024L;
    private const string StartupScope = "App.Startup";
    private const string StartupNoDisplayCode = "UiStartupNoDisplay";
    private const string StartupUnhandledCode = "UiStartupUnhandled";
    internal const string StartupTraceLogName = "avalonia-ui-startup.log";
    internal const string StartupErrorLogName = "avalonia-ui-errors.log";
    private const string ConfigDirectoryName = "config";
    private const string AvaloniaConfigFileName = "avalonia.json";
    private const string GuiNewConfigFileName = "gui.new.json";
    private static readonly Stopwatch StartupElapsedSinceEntry = Stopwatch.StartNew();

    [STAThread]
    public static int Main(string[] args)
    {
        var runtimeBaseDirectory = RuntimeLayout.ResolveRuntimeBaseDirectory();
        RecordStartupStage("Main.Entry", BuildStartupEnvironmentSnapshot(args));
        var pendingUpdateResult = PendingAppUpdateService.TryApplyPendingUpdatePackage(runtimeBaseDirectory);
        if (pendingUpdateResult.Status == PendingAppUpdateStatus.Applied)
        {
            RecordStartupStage("Main.PendingUpdate.Applied", pendingUpdateResult.Message);
            var restartResult = new ProcessAppLifecycleService().RestartAsync().GetAwaiter().GetResult();
            if (!restartResult.Success)
            {
                ReportStartupFailure(
                    StartupUnhandledCode,
                    $"Pending software update was applied, but restart failed. {restartResult.Message}");
                return 1;
            }

            RecordStartupStage("Main.PendingUpdate.Restart", restartResult.Message);
            return 0;
        }

        if (pendingUpdateResult.Status == PendingAppUpdateStatus.Failed)
        {
            RecordStartupStage("Main.PendingUpdate.Failed", pendingUpdateResult.Message);
        }

        if (OperatingSystem.IsLinux() && !HasLinuxDesktopDisplay())
        {
            ReportStartupFailure(
                StartupNoDisplayCode,
                $"No Linux graphical display detected. Set DISPLAY or WAYLAND_DISPLAY before launching MAAUnified. {BuildDisplayEnvironmentSnapshot()}");
            return 2;
        }

        try
        {
            RecordStartupStage("Main.BuildApp", "Configuring Avalonia application builder.");
            var builder = BuildAvaloniaApp();
            RecordStartupStage("Main.StartLifetime", "Starting classic desktop lifetime.");
            var exitCode = builder.StartWithClassicDesktopLifetime(args);
            RecordStartupStage("Main.Exit", $"Classic desktop lifetime returned exitCode={exitCode}.");
            return exitCode;
        }
        catch (Exception ex) when (IsDisplayInitializationFailure(ex))
        {
            ReportStartupFailure(
                StartupNoDisplayCode,
                $"Failed to open Linux display server. Verify DISPLAY/WAYLAND_DISPLAY and desktop session permissions. {BuildDisplayEnvironmentSnapshot()}",
                ex);
            return 2;
        }
        catch (Exception ex)
        {
            ReportStartupFailure(StartupUnhandledCode, "Unhandled startup failure.", ex);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var runtimeBaseDirectory = RuntimeLayout.ResolveRuntimeBaseDirectory();
        var useSoftwareRendering = ResolveSoftwareRenderingPreference(runtimeBaseDirectory);
        RecordStartupStage(
            "Main.RenderingPreference",
            $"softwareRendering={useSoftwareRendering}; executableBaseDir={AppContext.BaseDirectory}; runtimeBaseDir={runtimeBaseDirectory}");

        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();

        builder = builder.With(new CompositionOptions
        {
            UseRegionDirtyRectClipping = true,
        });

        builder = builder.With(new SkiaOptions
        {
            MaxGpuResourceSizeBytes = RecommendedMaxGpuResourceSizeBytes,
        });

        if (global::MAAUnified.Platform.MaaUnifiedBuildFlavor.CapturesVerboseDiagnostics)
        {
            builder = builder.LogToTrace();
        }

        ApplySoftwareRenderingPreference(builder, useSoftwareRendering);
        return builder;
    }

    internal static bool HasLinuxDesktopDisplay()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }

    internal static bool IsDisplayInitializationFailure(Exception exception)
    {
        return ContainsMessage(exception, "XOpenDisplay failed")
            || ContainsMessage(exception, "unable to open display");
    }

    internal static string BuildStartupEnvironmentSnapshot(string[] args)
    {
        var commandLine = args.Length == 0
            ? "<none>"
            : string.Join(' ', args.Select(static arg => arg.Contains(' ', StringComparison.Ordinal) ? $"\"{arg}\"" : arg));
        var processPath = Environment.ProcessPath ?? "<unknown>";
        var runtimeBaseDirectory = RuntimeLayout.ResolveRuntimeBaseDirectory();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"framework={RuntimeInformation.FrameworkDescription}; os={RuntimeInformation.OSDescription}; osArch={RuntimeInformation.OSArchitecture}; processArch={RuntimeInformation.ProcessArchitecture}; executableBaseDir={AppContext.BaseDirectory}; runtimeBaseDir={runtimeBaseDirectory}; currentDir={Environment.CurrentDirectory}; processPath={processPath}; args={commandLine}");
    }

    internal static string BuildStartupTracePayload(string stage, string message, Exception? exception = null)
    {
        var line = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("O"))
            .Append(" [STARTUP] [")
            .Append(StartupScope)
            .Append('.')
            .Append(stage)
            .Append("] elapsedMsSinceEntry=")
            .Append(StartupElapsedSinceEntry.ElapsedMilliseconds)
            .Append(" ")
            .Append(message);

        if (exception is not null)
        {
            line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
        }

        return line.ToString();
    }

    internal static void RecordStartupStage(string stage, string message, Exception? exception = null)
    {
        var payload = BuildStartupTracePayload(stage, message, exception);

        try
        {
            Console.Error.WriteLine(payload);
        }
        catch
        {
            // Ignore stderr failures during startup tracing.
        }

        if (exception is not null)
        {
            payload += Environment.NewLine + exception;
        }

        if (exception is not null || global::MAAUnified.Platform.MaaUnifiedBuildFlavor.CapturesVerboseDiagnostics)
        {
            TryAppendDebugLog(StartupTraceLogName, payload);
        }
    }

    internal static bool ResolveSoftwareRenderingPreference(string baseDirectory)
    {
        var configDirectory = Path.Combine(baseDirectory, ConfigDirectoryName);

        if (TryReadSoftwareRenderingFromAvaloniaConfig(
            Path.Combine(configDirectory, AvaloniaConfigFileName),
            out var avaloniaValue))
        {
            return avaloniaValue;
        }

        if (TryReadSoftwareRenderingFromGuiNewConfig(
            Path.Combine(configDirectory, GuiNewConfigFileName),
            out var legacyValue))
        {
            return legacyValue;
        }

        return false;
    }

    internal static void ApplySoftwareRenderingPreference(AppBuilder builder, bool useSoftwareRendering)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (OperatingSystem.IsLinux())
        {
            var options = BuildLinuxPlatformOptions(useSoftwareRendering);
            builder.With(options);
            RecordStartupStage(
                "Main.LinuxPlatformOptions",
                $"useDBusMenu={options.UseDBusMenu}; softwareRendering={useSoftwareRendering}");
            return;
        }

        if (!useSoftwareRendering)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software],
                CompositionMode = [Win32CompositionMode.RedirectionSurface],
            });
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            builder.With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = [AvaloniaNativeRenderingMode.Software],
            });
        }
    }

    internal static X11PlatformOptions BuildLinuxPlatformOptions(bool useSoftwareRendering)
    {
        var options = new X11PlatformOptions
        {
            // MAAUnified does not expose a global app menu, and enabling the DBus menu
            // exporter can trigger background probes for com.canonical.AppMenu.Registrar
            // on desktops where that optional service is absent.
            UseDBusMenu = false,
        };

        if (useSoftwareRendering)
        {
            options.RenderingMode = [X11RenderingMode.Software];
        }

        return options;
    }

    private static bool TryReadSoftwareRenderingFromAvaloniaConfig(string path, out bool value)
    {
        value = false;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return TryReadBooleanNode(
                root?["GlobalValues"]?[ConfigurationKeys.IgnoreBadModulesAndUseSoftwareRendering],
                out value);
        }
        catch (Exception ex)
        {
            RecordStartupStage("Main.RenderingPreference.AvaloniaConfigError", $"path={path}", ex);
            return false;
        }
    }

    private static bool TryReadSoftwareRenderingFromGuiNewConfig(string path, out bool value)
    {
        value = false;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return TryReadBooleanNode(
                root?["GUI"]?["IgnoreBadModulesAndUseSoftwareRendering"],
                out value);
        }
        catch (Exception ex)
        {
            RecordStartupStage("Main.RenderingPreference.GuiNewConfigError", $"path={path}", ex);
            return false;
        }
    }

    private static bool TryReadBooleanNode(JsonNode? node, out bool value)
    {
        value = false;
        if (node is null)
        {
            return false;
        }

        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out bool boolValue))
            {
                value = boolValue;
                return true;
            }

            if (jsonValue.TryGetValue(out int intValue))
            {
                value = intValue != 0;
                return true;
            }

            if (jsonValue.TryGetValue(out string? stringValue))
            {
                if (bool.TryParse(stringValue, out var parsedBool))
                {
                    value = parsedBool;
                    return true;
                }

                if (int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
                {
                    value = parsedInt != 0;
                    return true;
                }
            }
        }

        var raw = node.ToString();
        if (bool.TryParse(raw, out var fallbackBool))
        {
            value = fallbackBool;
            return true;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fallbackInt))
        {
            value = fallbackInt != 0;
            return true;
        }

        return false;
    }

    private static bool ContainsMessage(Exception? exception, string fragment)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildDisplayEnvironmentSnapshot()
    {
        static string Snapshot(string key)
        {
            var value = Environment.GetEnvironmentVariable(key);
            return string.IsNullOrWhiteSpace(value) ? $"{key}=<unset>" : $"{key}={value}";
        }

        return $"{Snapshot("DISPLAY")} {Snapshot("WAYLAND_DISPLAY")}";
    }

    private static void ReportStartupFailure(string code, string message, Exception? exception = null)
    {
        var line = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("O"))
            .Append(" [FAILED] [")
            .Append(StartupScope)
            .Append("] code=")
            .Append(code)
            .Append(" elapsedMsSinceEntry=")
            .Append(StartupElapsedSinceEntry.ElapsedMilliseconds)
            .Append(" message=")
            .Append(message);

        if (exception is not null)
        {
            line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
        }

        Console.Error.WriteLine(line.ToString());

        var payload = line.ToString();
        if (exception is not null)
        {
            payload += Environment.NewLine + exception;
        }

        TryAppendDebugLog(StartupErrorLogName, payload);
    }

    private static void TryAppendDebugLog(string fileName, string payload)
    {
        try
        {
            var debugDirectory = Path.Combine(RuntimeLayout.ResolveRuntimeBaseDirectory(), "debug");
            Directory.CreateDirectory(debugDirectory);
            var path = Path.Combine(debugDirectory, fileName);
            File.AppendAllText(path, payload + Environment.NewLine);
        }
        catch
        {
            // Never throw from startup error reporting.
        }
    }
}
