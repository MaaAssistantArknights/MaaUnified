using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MAAUnified.Application.Services.VersionUpdate;

/// <summary>
/// 一次外部更新器启动请求。字段与 WPF <c>MAA.Updater.exe</c> 的参数顺序一一对应
/// （<c>4ce5e9bf97</c>、<c>208d28d6fa</c>）：父进程、安装根、解压目录、备份目录、更新包、
/// 成功/失败状态文件、重启可执行文件、安装计划、命名互斥量与输出开关。
/// </summary>
/// <param name="ParentProcessId">当前 MAAUnified 进程 id，更新器会等待其退出。</param>
/// <param name="ExecutablePath">外部更新器可执行文件路径。</param>
/// <param name="BaseDirectory">安装根目录。</param>
/// <param name="ExtractDirectory">已解压的更新包目录。</param>
/// <param name="BackupDirectory">旧文件备份目录。</param>
/// <param name="PackagePath">待应用更新包路径（保留到更新器启动成功）。</param>
/// <param name="SuccessStatusFilePath">更新器成功状态文件路径。</param>
/// <param name="FailureStatusFilePath">更新器失败状态文件路径。</param>
/// <param name="RelaunchExecutablePath">成功更新后由更新器重启的可执行文件。</param>
/// <param name="PlanPath">安装计划 JSON 路径。</param>
/// <param name="MutexName">命名互斥量；空字符串表示不启用。</param>
/// <param name="ShowConsole">是否显示更新器控制台。</param>
/// <param name="ShowProgress">是否显示更新器进度窗口。</param>
public sealed record ExternalUpdaterLaunchSpec(
    int ParentProcessId,
    string ExecutablePath,
    string BaseDirectory,
    string ExtractDirectory,
    string BackupDirectory,
    string PackagePath,
    string SuccessStatusFilePath,
    string FailureStatusFilePath,
    string RelaunchExecutablePath,
    string PlanPath,
    string MutexName,
    bool ShowConsole,
    bool ShowProgress);

/// <summary>
/// 外部更新器进程启动抽象。生产实现走 <see cref="Process.Start(ProcessStartInfo)"/>，
/// 测试注入假实现，确保不会真的拉起进程。
/// </summary>
public interface IExternalUpdaterProcessLauncher
{
    /// <summary>
    /// 启动外部更新器。
    /// </summary>
    /// <param name="spec">启动请求。</param>
    /// <param name="message">失败原因（英文，进日志）。</param>
    /// <returns>进程已成功创建时为 <c>true</c>。</returns>
    bool TryStart(ExternalUpdaterLaunchSpec spec, out string message);
}

/// <summary>
/// 默认的外部更新器启动器。
/// </summary>
public sealed class ProcessExternalUpdaterProcessLauncher : IExternalUpdaterProcessLauncher
{
    public bool TryStart(ExternalUpdaterLaunchSpec spec, out string message)
    {
        ArgumentNullException.ThrowIfNull(spec);
        try
        {
            using var process = Process.Start(ExternalUpdaterDelegation.BuildStartInfo(spec));
            if (process is null)
            {
                message = $"Failed to start external updater `{spec.ExecutablePath}`.";
                return false;
            }

            message = $"External updater started with pid {process.Id}.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Failed to start external updater `{spec.ExecutablePath}`: {ex.Message}";
            return false;
        }
    }
}

/// <summary>
/// 外部更新器委托：参数构造、安装计划写入与进程启动。
/// </summary>
public static class ExternalUpdaterDelegation
{
    public const string SuccessStatusFileName = "pending-update-success.txt";

    /// <summary>
    /// 按 <c>MAA.Updater.exe</c> 的参数顺序构造命令行。
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(ExternalUpdaterLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var arguments = new List<string>
        {
            spec.ParentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            spec.BaseDirectory,
            spec.ExtractDirectory,
            spec.BackupDirectory,
            spec.PackagePath,
            spec.SuccessStatusFilePath,
            spec.FailureStatusFilePath,
            spec.RelaunchExecutablePath,
            spec.PlanPath,
        };

        if (!string.IsNullOrWhiteSpace(spec.MutexName))
        {
            arguments.Add("--mutex-name");
            arguments.Add(spec.MutexName);
        }

        if (spec.ShowConsole)
        {
            arguments.Add("--show-console");
        }

        if (!spec.ShowProgress)
        {
            arguments.Add("--no-progress-ui");
        }

        return arguments;
    }

    /// <summary>
    /// 构造进程启动信息；参数走 <see cref="ProcessStartInfo.ArgumentList"/>，避免手工拼接引号。
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(ExternalUpdaterLaunchSpec spec)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = !spec.ShowConsole,
            WorkingDirectory = spec.BaseDirectory,
        };

        foreach (var argument in BuildArguments(spec))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    /// <summary>
    /// 写入 <c>MAA.Updater.exe</c> 使用的安装计划 JSON。
    /// </summary>
    /// <param name="planPath">计划文件路径。</param>
    /// <param name="packageType">完整包传 <c>full</c>，OTA 传 <c>ota</c>。</param>
    /// <param name="removeEntries">需移入备份的旧条目（相对安装根）。</param>
    /// <param name="moveEntries">需从解压目录搬入安装目录的条目（相对安装根）。</param>
    /// <param name="relaunchArguments">更新成功后透传给重启进程的参数；可为空。</param>
    public static void WritePlan(
        string planPath,
        string packageType,
        IReadOnlyCollection<string> removeEntries,
        IReadOnlyCollection<string> moveEntries,
        IReadOnlyCollection<string>? relaunchArguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageType);

        var plan = new JsonObject
        {
            ["packageType"] = packageType,
            ["removeList"] = new JsonArray(removeEntries.Select(static entry => (JsonNode?)JsonValue.Create(entry)).ToArray()),
            ["moveList"] = new JsonArray(moveEntries.Select(static entry => (JsonNode?)JsonValue.Create(entry)).ToArray()),
        };

        if (relaunchArguments is { Count: > 0 })
        {
            plan["relaunchArgs"] = new JsonArray(relaunchArguments.Select(static entry => (JsonNode?)JsonValue.Create(entry)).ToArray());
        }

        var parent = Path.GetDirectoryName(planPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(planPath, plan.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// 把包内相对路径规范化为更新器使用的正斜杠形式；拒绝绝对路径与路径穿越。
    /// </summary>
    public static bool TryNormalizePlanEntry(string? relativePath, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var trimmed = relativePath.Trim().Replace('\\', '/');
        if (trimmed.EndsWith('/'))
        {
            trimmed = trimmed.TrimEnd('/');
        }

        if (trimmed.Length == 0
            || Path.IsPathRooted(trimmed)
            || trimmed.Split('/').Contains("..", StringComparer.Ordinal))
        {
            return false;
        }

        normalized = trimmed;
        return true;
    }
}
