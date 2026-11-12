using System;
using System.IO;
using System.Text;

namespace MAAUnified.Application.Services.VersionUpdate;

/// <summary>
/// 更新失败状态文件（<c>pending-update-failure.txt</c>）读写。
/// 与 WPF <c>PendingUpdateApplier.MarkDelegatedUpdateFailure</c> / <c>TryReadDelegatedUpdateFailure</c>
/// （<c>217db38484</c>）语义一致：失败原因以 UTF-8 纯文本落盘，读取只读不删，
/// 跨启动保留到下次启动提示用户修复，避免用户忽略提示后重启进入半更新状态而无人提醒。
/// 清空时机：成功应用更新包，或注册新的更新包（注册即代表用户已着手修复）。
/// </summary>
public static class PendingUpdateFailureState
{
    public const string FileName = "pending-update-failure.txt";

    public static string ResolvePath(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        return Path.Combine(baseDirectory, FileName);
    }

    public static bool Exists(string baseDirectory)
        => File.Exists(ResolvePath(baseDirectory));

    /// <summary>
    /// 读取失败原因。文件不存在时返回 <c>null</c>；读取失败时返回空字符串（仍代表失败状态存在）。
    /// 本方法不删除状态文件。
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
            // 失败状态写盘失败不能反过来阻断更新流程，调用方照常返回失败结果。
        }
    }

    public static void Clear(string baseDirectory)
    {
        var path = ResolvePath(baseDirectory);
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
