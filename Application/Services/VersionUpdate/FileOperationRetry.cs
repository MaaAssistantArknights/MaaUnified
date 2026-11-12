using System;
using System.IO;
using System.Threading;

namespace MAAUnified.Application.Services.VersionUpdate;

/// <summary>
/// 更新落盘时的文件占用重试。
/// 对应 WPF <c>MAA.Updater</c> 的「文件被占用时重试」（<c>208d28d6fa</c>）语义：
/// Windows 上杀软扫描新解压文件、其他进程短暂持有句柄都会让 <see cref="File.Move(string, string)"/> 抛
/// <see cref="IOException"/>，短暂重试即可恢复；macOS/Linux 上同一处不会因独占句柄失败，重试退化为一次成功。
/// </summary>
public static class FileOperationRetry
{
    public const int DefaultMaxAttempts = 5;

    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(120);

    public static void Execute(Action operation, int maxAttempts = DefaultMaxAttempts, TimeSpan? delay = null)
        => Execute<object?>(() =>
        {
            operation();
            return null;
        }, maxAttempts, delay);

    public static T Execute<T>(Func<T> operation, int maxAttempts = DefaultMaxAttempts, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Max attempts must be positive.");
        }

        var wait = delay ?? DefaultDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                if (wait > TimeSpan.Zero)
                {
                    Thread.Sleep(wait);
                }
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                if (wait > TimeSpan.Zero)
                {
                    Thread.Sleep(wait);
                }
            }
        }
    }
}
