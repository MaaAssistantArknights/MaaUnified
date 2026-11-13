using System.Net;
using System.Net.Http.Headers;

namespace MAAUnified.Application.Services.VersionUpdate;

/// <summary>
/// 下载链路的共用收口：429/503 的 Retry-After 重试一次，读流期间的空闲超时。
/// WPF dev-v2 HttpService 的完整传输层重构（gzip/HTTP2/SOCKS5/ETag）不照搬，只补这两条
/// 用户可感知的失败模式（P2）。
/// </summary>
public static class HttpDownloadSupport
{
    /// <summary>Gets or sets 单次读流允许的最长静默时间，超时抛 <see cref="TimeoutException"/>。</summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets or sets Retry-After 的最大等待，避免服务端给出超长延迟卡住更新。</summary>
    public static TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// GET 一个下载地址；遇到 429/503 且带 Retry-After 时等待后重试一次。
    /// </summary>
    /// <param name="httpClient">HTTP 客户端。</param>
    /// <param name="url">下载地址。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>最终响应，调用方负责释放。</returns>
    public static async Task<HttpResponseMessage> GetWithRetryAfterAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var response = await httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            || !TryGetRetryAfter(response, out var delay))
        {
            return response;
        }

        var wait = delay > MaxRetryAfter ? MaxRetryAfter : delay;
        response.Dispose();
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        return await httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 逐块复制流，任意一次读超过 <see cref="IdleTimeout"/> 即失败，避免服务器停发数据后
    /// 更新一直卡在“下载中”。
    /// </summary>
    /// <param name="source">源流。</param>
    /// <param name="destination">目标流。</param>
    /// <param name="progress">可选的已传输字节回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task CopyToAsync(
        Stream source,
        Stream destination,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read;
            try
            {
                read = await source
                    .ReadAsync(buffer, cancellationToken)
                    .AsTask()
                    .WaitAsync(IdleTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    $"Download stalled: no data received for {IdleTimeout.TotalSeconds:0} s.",
                    ex);
            }

            if (read == 0)
            {
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            progress?.Invoke(total);
        }
    }

    private static bool TryGetRetryAfter(HttpResponseMessage response, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            delay = delta;
            return true;
        }

        if (retryAfter?.Date is { } date)
        {
            delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero;
        }

        return false;
    }
}
