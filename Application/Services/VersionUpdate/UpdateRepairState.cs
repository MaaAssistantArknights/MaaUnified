using System;
using System.IO;
using System.Text;

namespace MAAUnified.Application.Services.VersionUpdate;

/// <summary>
/// 安装修复状态（<c>pending-update-repair.txt</c>）与用户重试请求（<c>pending-update-retry.txt</c>）。
/// WPF <c>da79ff88ad</c> 在资源损坏期间强制完整包更新：OTA 增量只含版本间变化的文件，
/// 无法补齐被安全软件删除或手动删除的安装文件。MAAUnified 在启动期对照 <c>filelist.txt</c>
/// 发现缺失文件后落盘本标记，跨启动保留，直到完整包应用成功（或注册完整包修复）才清除；
/// 期间所有下载入口只接受完整包。
/// 失败标记 <see cref="PendingUpdateFailureState"/> 同样会强制完整包，两处判定统一走
/// <see cref="RequiresFullPackage"/>。
/// </summary>
public static class UpdateRepairState
{
    public const string FileName = "pending-update-repair.txt";

    public const string RetryRequestFileName = "pending-update-retry.txt";

    public static string ResolvePath(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        return Path.Combine(baseDirectory, FileName);
    }

    public static string ResolveRetryRequestPath(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        return Path.Combine(baseDirectory, RetryRequestFileName);
    }

    public static bool Exists(string baseDirectory)
        => File.Exists(ResolvePath(baseDirectory));

    /// <summary>
    /// 读取修复原因。文件不存在时返回 <c>null</c>；读取失败时返回空字符串（仍代表修复状态存在）。
    /// 本方法不删除标记文件。
    /// </summary>
    public static string? TryRead(string baseDirectory)
    {
        var path = ResolvePath(baseDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public static void Write(string baseDirectory, string reason)
    {
        var path = ResolvePath(baseDirectory);
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(path, reason ?? string.Empty, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 修复标记写盘失败不能反过来阻断启动，调用方仍会按本次检查结果提示。
        }
    }

    public static void Clear(string baseDirectory)
        => TryDeleteFile(ResolvePath(baseDirectory));

    /// <summary>
    /// 当前安装是否只允许完整包更新：安装文件缺失修复标记或上次更新失败标记任一存在。
    /// </summary>
    public static bool RequiresFullPackage(string baseDirectory)
        => Exists(baseDirectory) || PendingUpdateFailureState.Exists(baseDirectory);

    /// <summary>
    /// 记录用户选择「重试更新」。当前进程的启动提示在 Avalonia 初始化前执行，
    /// 无法直接运行带进度的下载；由启动后的更新检查消费该请求，即使关闭了启动自动检查也会执行一次。
    /// </summary>
    public static void RequestRetry(string baseDirectory)
    {
        var path = ResolveRetryRequestPath(baseDirectory);
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"), Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 尽力而为：写不进去时用户仍可在设置页手动检查更新。
        }
    }

    /// <summary>
    /// 消费一次「重试更新」请求。文件不存在时返回 <c>false</c>；读取后删除，避免每次启动重复执行。
    /// </summary>
    public static bool ConsumeRetryRequest(string baseDirectory)
    {
        var path = ResolveRetryRequestPath(baseDirectory);
        if (!File.Exists(path))
        {
            return false;
        }

        TryDeleteFile(path);
        return true;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 尽力而为。
        }
    }
}
