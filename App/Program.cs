using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Win32;
using Avalonia.X11;
using MAAUnified.Compat.Constants;
using MAAUnified.Compat.Runtime;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Application.Services.VersionUpdate;
using MAAUnified.Application.Security;

namespace MAAUnified.App;

internal static class Program
{
    private const long RecommendedMaxGpuResourceSizeBytes = 256L * 1024L * 1024L;
    private const string StartupScope = "App.Startup";
    private const string StartupNoDisplayCode = "UiStartupNoDisplay";
    private const string StartupUnhandledCode = "UiStartupUnhandled";
    private const string StartupPortablePackageMissingCode = "UiStartupPortablePackageMissing";
    private const string StartupDuplicateInstanceCode = "UiStartupDuplicateInstance";
    internal const string StartupTraceLogName = "avalonia-ui-startup.log";
    internal const string StartupErrorLogName = "avalonia-ui-errors.log";
    private const string ConfigDirectoryName = "config";
    private const string AvaloniaConfigFileName = "avalonia.json";
    private const string GuiNewConfigFileName = "gui.new.json";
    internal const string RestartWaitForInstanceArgument = ProcessAppLifecycleService.RestartWaitForInstanceArgument;
    /// <summary>
    /// 更新后「立即重启」链写入的启动参数（WPF <c>60cf643a2d</c>）：本次进程跳过启动后自动运行/自动开模拟器。
    /// </summary>
    internal const string SkipStartupAutoRunArgument = "--skip-startup-auto-run";
    private static readonly TimeSpan RestartInstanceWaitTimeout = TimeSpan.FromSeconds(15);
    private static readonly Stopwatch StartupElapsedSinceEntry = Stopwatch.StartNew();

    internal static string? RequestedConfigurationName { get; private set; }

    /// <summary>
    /// 当前进程是否因更新重启链要求跳过启动自动运行（WPF <c>Bootstrapper.ShouldSkipStartupAutoRun</c>）。
    /// </summary>
    internal static bool ShouldSkipStartupAutoRun { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
#if MAA_UNIFIED_LINUX_SECURE_STORAGE
        if (SensitiveStorageComposition.IsSqlCipherSmokeTest(args))
        {
            return SensitiveStorageComposition.RunSqlCipherSmokeTest(args[1], args[2]);
        }
#endif

        var startupArguments = ParseStartupArguments(args);
        RequestedConfigurationName = startupArguments.ConfigurationName;
        ShouldSkipStartupAutoRun = startupArguments.SkipStartupAutoRun;
        args = startupArguments.AvaloniaArguments;
        var runtimeBaseDirectory = RuntimeLayout.ResolveRuntimeBaseDirectory();
        RecordStartupStage("Main.Entry", BuildStartupEnvironmentSnapshot(args));
        if (!ValidateLinuxPortablePackageLayout(runtimeBaseDirectory))
        {
            return 1;
        }

        if (!TryAcquireSingleInstanceGuard(
                runtimeBaseDirectory,
                startupArguments.WaitForSingleInstance ? RestartInstanceWaitTimeout : TimeSpan.Zero,
                out var instanceGuard))
        {
            return 3;
        }

        using var heldInstanceGuard = instanceGuard;
        // Expose the held guard so the restart flow can release it before
        // spawning the successor process (WPF OnExit Release-then-spawn order).
        if (instanceGuard is not null)
        {
            PackageInstanceGuard.AdoptProcessWideGuard(instanceGuard);
        }

        var macSeedResult = MacAppRuntimeSeed.EnsureSeeded(AppContext.BaseDirectory, runtimeBaseDirectory);
        if (macSeedResult.Status == MacAppRuntimeSeedStatus.Ready)
        {
            RecordStartupStage("Main.MacAppRuntimeSeed.Ready", $"resourceSeeded={macSeedResult.ResourceSeeded}; resourceSeedReason={macSeedResult.ResourceSeedReason}; nativeLibraries={macSeedResult.NativeLibraryCount}; seededMaaFrameworkRuntimeLibraries={FormatStartupList(macSeedResult.SeededMaaFrameworkRuntimeLibraries)}; missingMaaFrameworkRuntimeLibraries={FormatStartupList(macSeedResult.MissingMaaFrameworkRuntimeLibraries)}");
        }

        var linuxSeedResult = LinuxPackagedRuntimeSeed.EnsureSeeded(AppContext.BaseDirectory, runtimeBaseDirectory);
        if (linuxSeedResult.Status == LinuxPackagedRuntimeSeedStatus.Ready)
        {
            RecordStartupStage("Main.LinuxPackagedRuntimeSeed.Ready", $"resourceSeeded={linuxSeedResult.ResourceSeeded}; resourceSeedReason={linuxSeedResult.ResourceSeedReason}; nativeLibraries={linuxSeedResult.NativeLibraryCount}");
        }

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

        // P0-12：失败标记跨启动保留，安装目录再对照 filelist.txt 做一次完整性检查；
        // 任一成立即提示用户用完整包修复（提示不删除标记，用户下次启动仍会看到）。
        ReportUpdateHealthIfNeeded(runtimeBaseDirectory);

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
            StartupPrompt.Show(
                LocalizeStartupText(runtimeBaseDirectory, "Common.ErrorPrefix", "Error"),
                BuildStartupFailurePromptMessage(),
                StartupPromptSeverity.Error);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var runtimeBaseDirectory = RuntimeLayout.ResolveRuntimeBaseDirectory();
        var useSoftwareRendering = ResolveSoftwareRenderingPreference(runtimeBaseDirectory);
        RecordStartupStage(
            "Main.RenderingPreference",
            $"softwareRendering={useSoftwareRendering}");

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

    private static string FormatStartupList(IReadOnlyList<string>? values)
        => values is null || values.Count == 0
            ? "<none>"
            : string.Join(',', values);

    internal static bool IsDisplayInitializationFailure(Exception exception)
    {
        return ContainsMessage(exception, "XOpenDisplay failed")
            || ContainsMessage(exception, "unable to open display");
    }

    internal static string BuildStartupEnvironmentSnapshot(string[] args)
    {
        var runtimeBaseDirectory = RuntimeLayout.ResolveRuntimeBaseDirectory();
        var commandLine = args.Length == 0
            ? "<none>"
            : string.Join(' ', args.Select(static arg =>
                Path.IsPathRooted(arg)
                    ? "<path>"
                    : arg.Contains(' ', StringComparison.Ordinal) ? $"\"{arg}\"" : arg));
        var processPath = Environment.ProcessPath;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"framework={RuntimeInformation.FrameworkDescription}; os={RuntimeInformation.OSDescription}; osArch={RuntimeInformation.OSArchitecture}; processArch={RuntimeInformation.ProcessArchitecture}; processPath={(string.IsNullOrWhiteSpace(processPath) ? "<unknown>" : "<path>")}; args={commandLine}; argsCount={args.Length}; runtimeLayoutAvailable={!string.IsNullOrWhiteSpace(runtimeBaseDirectory)}");
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
            line.Append(" | ").Append(exception.GetType().Name).Append(": ")
                .Append(DiagnosticDataSanitizer.Sanitize(exception.Message));
        }

        return line.ToString();
    }

    internal static void RecordStartupStage(string stage, string message, Exception? exception = null)
    {
        var payload = DiagnosticDataSanitizer.Sanitize(
            BuildStartupTracePayload(stage, message, exception),
            privateRoots: [RuntimeLayout.ResolveRuntimeBaseDirectory()]);

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
            payload += Environment.NewLine + $"errorType={exception.GetType().Name}";
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
            var globalValues = root?["GlobalValues"] as JsonObject;
            // dev-v2 f464016813 moved the switch into the global Performance section; keep reading
            // the flat v6.3.7 key so an un-migrated config still starts in the expected mode.
            if (TryReadBooleanNode(
                    globalValues?[ConfigurationKeys.PerformanceIgnoreBadModulesAndUseSoftwareRendering],
                    out value))
            {
                return true;
            }

            return TryReadBooleanNode(
                globalValues?[ConfigurationKeys.IgnoreBadModulesAndUseSoftwareRendering],
                out value);
        }
        catch (Exception ex)
        {
            RecordStartupStage("Main.RenderingPreference.AvaloniaConfigError", "file=avalonia.json", ex);
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
            // dev-v2 (f464016813) nests the switch under Gui.Performance; v6.3.7 kept it flat in GUI.
            if (TryReadBooleanNode(
                    root?["Gui"]?["Performance"]?[ConfigurationKeys.PerformanceIgnoreBadModulesAndUseSoftwareRendering],
                    out value))
            {
                return true;
            }

            return TryReadBooleanNode(
                root?["GUI"]?["IgnoreBadModulesAndUseSoftwareRendering"],
                out value);
        }
        catch (Exception ex)
        {
            RecordStartupStage("Main.RenderingPreference.GuiNewConfigError", "file=gui.new.json", ex);
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

    internal static bool ValidateLinuxPortablePackageLayout(string runtimeBaseDirectory)
    {
        if (!OperatingSystem.IsLinux()
            || !RuntimeLayout.IsLinuxPortablePackageLaunchInvalid(AppContext.BaseDirectory, linuxAppImagePath: null, out _))
        {
            return true;
        }

        var message = LocalizeStartupText(
            runtimeBaseDirectory,
            "Startup.LinuxPortablePackageMissing",
            "Linux portable package is incomplete. Please extract the full package and launch MAAUnified.AppImage from that directory.");
        var title = LocalizeStartupText(
            runtimeBaseDirectory,
            "Common.ErrorPrefix",
            "Error");
        RecordStartupStage(
            "Main.PortablePackage.Invalid",
            "packageLayout=invalid");
        ReportStartupFailure(
            StartupPortablePackageMissingCode,
            $"{message} packageLayout=invalid");
        StartupPrompt.Show(title, message, StartupPromptSeverity.Error);
        return false;
    }

    internal static bool TryAcquireSingleInstanceGuard(string runtimeBaseDirectory, out PackageInstanceGuard? guard)
        => TryAcquireSingleInstanceGuard(runtimeBaseDirectory, TimeSpan.Zero, out guard);

    internal static bool TryAcquireSingleInstanceGuard(
        string runtimeBaseDirectory,
        TimeSpan timeout,
        out PackageInstanceGuard? guard)
    {
        guard = null;
        var identityPath = RuntimeLayout.ResolveSingleInstanceIdentityPath();
        if (PackageInstanceGuard.TryAcquire(identityPath, timeout, out guard))
        {
            RecordStartupStage("Main.SingleInstance.Acquired", "identityFile=instance.lock");
            return true;
        }

        var message = LocalizeStartupText(
            runtimeBaseDirectory,
            "MultiInstanceUnderSamePath",
            "Only one instance can be launched under the same path.");
        var title = LocalizeStartupText(
            runtimeBaseDirectory,
            "Common.WarningPrefix",
            "Warning");
        RecordStartupStage("Main.SingleInstance.Duplicate", "identityFile=instance.lock");
        ReportStartupFailure(StartupDuplicateInstanceCode, message);
        StartupPrompt.Show(title, message, StartupPromptSeverity.Warning);
        return false;
    }

    internal static StartupArguments ParseStartupArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? configurationName = null;
        var waitForSingleInstance = false;
        var skipStartupAutoRun = false;
        var avaloniaArguments = new List<string>();

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, RestartWaitForInstanceArgument, StringComparison.Ordinal))
            {
                waitForSingleInstance = true;
                continue;
            }

            if (string.Equals(argument, SkipStartupAutoRunArgument, StringComparison.OrdinalIgnoreCase))
            {
                skipStartupAutoRun = true;
                continue;
            }

            if (string.Equals(argument, "--config", StringComparison.Ordinal))
            {
                if (index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    configurationName = args[++index].Trim();
                }

                continue;
            }

            const string configPrefix = "--config=";
            if (argument.StartsWith(configPrefix, StringComparison.Ordinal))
            {
                var value = argument[configPrefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    configurationName = value;
                }

                continue;
            }

            avaloniaArguments.Add(argument);
        }

        return new StartupArguments(configurationName, waitForSingleInstance, skipStartupAutoRun, avaloniaArguments.ToArray());
    }

    internal static string BuildStartupFailurePromptMessage()
        => "MAAUnified could not start. Review the startup error log in the debug directory for details.";

    /// <summary>
    /// 启动期更新健康度提示（P0-12 / WPF <c>217db38484</c>、<c>da79ff88ad</c>）：
    /// 上次更新失败标记仍在或安装目录对照 <c>filelist.txt</c> 有缺失文件时给出修复提示。
    /// 只读探测，失败标记不会被删除，用户忽略后下次启动仍会看到。
    /// </summary>
    internal static void ReportUpdateHealthIfNeeded(string runtimeBaseDirectory)
    {
        var caps = UpdateApplyPlatform.Detect(runtimeBaseDirectory);
        if (!string.IsNullOrEmpty(caps.DegradationReason))
        {
            RecordStartupStage("Main.UpdateApply.Degraded", caps.DegradationReason);
        }

        var report = UpdateStartupHealthInspector.Inspect(runtimeBaseDirectory);
        if (!report.RequiresRepair)
        {
            return;
        }

        var summary = BuildUpdateRepairSummary(report);
        RecordStartupStage(
            "Main.UpdateHealth.RepairRequired",
            $"updateFailure={report.HasPersistedUpdateFailure}; missingFiles={report.MissingFiles.Count}; reason={DiagnosticDataSanitizer.Sanitize(report.UpdateFailureReason ?? "<none>")}; {summary}");
        StartupPrompt.Show(
            LocalizeStartupText(runtimeBaseDirectory, "Startup.UpdateRepair.Title", "Update repair required"),
            LocalizeStartupText(runtimeBaseDirectory, "Startup.UpdateRepair.Message", summary),
            StartupPromptSeverity.Warning);
    }

    /// <summary>
    /// 组装修复提示正文：缺失文件数与失败原因；失败原因按 WPF <c>GetDelegatedUpdateFailureDescription</c>
    /// 的三档归类（文件占用 / 更新包异常 / 未知），只展示归类结果，技术细节留在启动日志。
    /// </summary>
    internal static string BuildUpdateRepairSummary(UpdateStartupHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>();
        if (report.HasPersistedUpdateFailure)
        {
            var reason = DescribeUpdateFailureReason(report.UpdateFailureReason);
            lines.Add($"The previous update did not finish cleanly. {reason}");
        }

        if (report.HasMissingInstallationFiles)
        {
            var preview = string.Join(", ", report.MissingFiles.Take(5));
            if (report.MissingFiles.Count > 5)
            {
                preview += ", ...";
            }

            lines.Add($"Installation files are missing ({report.MissingFiles.Count}): {preview}");
        }

        lines.Add("Download or drop a full package to repair the installation, then restart MAAUnified.");
        return string.Join(Environment.NewLine, lines);
    }

    internal static string DescribeUpdateFailureReason(string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(failureReason))
        {
            return "The update failed for an unknown reason.";
        }

        if (failureReason.Contains("Failed to move", StringComparison.Ordinal)
            || failureReason.Contains("Failed to back up", StringComparison.Ordinal))
        {
            return "A file was locked by another process (antivirus scan or another program).";
        }

        if (failureReason.Contains("Illegal path", StringComparison.Ordinal)
            || failureReason.Contains("Plan file", StringComparison.Ordinal)
            || failureReason.Contains("Failed to open file", StringComparison.Ordinal)
            || failureReason.Contains("Failed to read file", StringComparison.Ordinal))
        {
            return "The update package itself is invalid.";
        }

        // 更新器写入的面向用户文案可能含非 ASCII 字符（多实例互斥等），原样展示。
        return failureReason.Any(c => c > 0x7f)
            ? failureReason
            : "The update failed for an unknown reason.";
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
            line.Append(" | ").Append(exception.GetType().Name).Append(": ")
                .Append(DiagnosticDataSanitizer.Sanitize(exception.Message));
        }

        var safeLine = DiagnosticDataSanitizer.Sanitize(
            line.ToString(),
            privateRoots: [RuntimeLayout.ResolveRuntimeBaseDirectory()]);
        Console.Error.WriteLine(safeLine);

        var payload = safeLine;
        if (exception is not null)
        {
            payload += Environment.NewLine + $"errorType={exception.GetType().Name}";
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
            var sanitized = DiagnosticDataSanitizer.Sanitize(payload, privateRoots: [RuntimeLayout.ResolveRuntimeBaseDirectory()]);
            File.AppendAllText(path, sanitized + Environment.NewLine);
        }
        catch
        {
            // Never throw from startup error reporting.
        }
    }

    private static string LocalizeStartupText(string runtimeBaseDirectory, string key, string fallback)
    {
        var language = ResolveStartupLanguage(runtimeBaseDirectory);
        return UiLocalizer.Create(language).GetOrDefault(key, fallback, StartupScope);
    }

    private static string ResolveStartupLanguage(string runtimeBaseDirectory)
    {
        try
        {
            var configPath = Path.Combine(runtimeBaseDirectory, ConfigDirectoryName, AvaloniaConfigFileName);
            if (!File.Exists(configPath))
            {
                return UiLanguageCatalog.DefaultLanguage;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!document.RootElement.TryGetProperty("GlobalValues", out var globalValues)
                || !globalValues.TryGetProperty(ConfigurationKeys.Localization, out var languageNode)
                || languageNode.ValueKind != JsonValueKind.String)
            {
                return UiLanguageCatalog.DefaultLanguage;
            }

            return UiLanguageCatalog.Normalize(languageNode.GetString());
        }
        catch
        {
            return UiLanguageCatalog.DefaultLanguage;
        }
    }
}

internal sealed record StartupArguments(
    string? ConfigurationName,
    bool WaitForSingleInstance,
    bool SkipStartupAutoRun,
    string[] AvaloniaArguments);
