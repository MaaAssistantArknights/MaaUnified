using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MAAUnified.Application.Services.VersionUpdate;

/// <summary>
/// 更新应用能力的平台画像。
/// </summary>
/// <param name="ExternalUpdaterSupported">是否存在可委托的外部更新器可执行文件。</param>
/// <param name="ExternalUpdaterExecutableName">外部更新器文件名（仅 Windows）。</param>
/// <param name="MutexCoordinationSupported">是否使用命名互斥量与前进程协调（仅 Windows 外部更新器链路）。</param>
/// <param name="OccupiedFileRetrySupported">落盘时是否按占用重试。</param>
/// <param name="DegradationReason">不可用能力的显式降级原因（英文，进日志）；全部可用时为空字符串。</param>
public sealed record UpdateApplyCapabilities(
    bool ExternalUpdaterSupported,
    string ExternalUpdaterExecutableName,
    bool MutexCoordinationSupported,
    bool OccupiedFileRetrySupported,
    string DegradationReason);

/// <summary>
/// 更新应用能力的平台抽象。
/// WPF（<c>4ce5e9bf97</c>、<c>112b242020</c>、<c>208d28d6fa</c>）把涉及 dll 的更新交给独立的
/// <c>MAA.Updater.exe</c>，并用命名互斥量与被替换进程协调。MAAUnified 目前只随包发布托管应用本体，
/// 没有 Windows 外部更新器可执行文件，因此：
/// <list type="bullet">
/// <item>Windows：包内存在外部更新器时才启用委托，否则显式降级为进程内应用并记录原因；</item>
/// <item>macOS / Linux：外部更新器与命名互斥量都不适用（mach-O/AppImage 的替换语义不同），显式降级为进程内应用；</item>
/// <item>占用重试：三平台都启用（Windows 上用来兜住杀软/句柄占用，其他平台等价于一次成功）。</item>
/// </list>
/// </summary>
public static class UpdateApplyPlatform
{
    public const string ExternalUpdaterExecutableName = "MAA.Updater.exe";

    public static UpdateApplyCapabilities Detect(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var hasUpdater = File.Exists(Path.Combine(baseDirectory, ExternalUpdaterExecutableName));
        return Detect(CurrentPlatformName(), hasUpdater);
    }

    /// <summary>
    /// 按平台名探测能力，供测试覆盖 Windows 有/无外部更新器与 macOS/Linux 显式降级分支。
    /// </summary>
    /// <param name="platform">平台名（<c>windows</c> / <c>macos</c> / <c>linux</c>）。</param>
    /// <param name="hasExternalUpdater">安装目录内是否存在外部更新器。</param>
    internal static UpdateApplyCapabilities Detect(string platform, bool hasExternalUpdater)
    {
        if (string.Equals(platform, "windows", StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateApplyCapabilities(
                ExternalUpdaterSupported: hasExternalUpdater,
                ExternalUpdaterExecutableName: ExternalUpdaterExecutableName,
                MutexCoordinationSupported: hasExternalUpdater,
                OccupiedFileRetrySupported: true,
                DegradationReason: hasExternalUpdater
                    ? string.Empty
                    : $"External updater {ExternalUpdaterExecutableName} is not packaged; applying updates in-process.");
        }

        var platformName = string.Equals(platform, "macos", StringComparison.OrdinalIgnoreCase)
            ? "macOS"
            : "Linux";
        return new UpdateApplyCapabilities(
            ExternalUpdaterSupported: false,
            ExternalUpdaterExecutableName: ExternalUpdaterExecutableName,
            MutexCoordinationSupported: false,
            OccupiedFileRetrySupported: true,
            DegradationReason:
                $"External updater delegation and named-mutex coordination are Windows-only; {platformName} applies updates in-process.");
    }

    internal static string CurrentPlatformName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "windows";
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos" : "linux";
    }
}
