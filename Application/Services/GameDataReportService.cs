using System.Net;
using System.Reflection;
using System.Text;

namespace MAAUnified.Application.Services;

/// <summary>
/// Uploads game data reports (Penguin Stats / Yituliu) requested by the core through the
/// ReportRequest callback. Mirrors the frozen WPF GameDataReportService.PostWithRetryAsync
/// behavior: 3 retries per domain, 3000ms initial backoff with x1.5 growth on 5xx,
/// Penguin Stats backup domains, silent failure for non-penguin reports, and the
/// x-penguin-set-penguinid response-header callback. The default transport mirrors the
/// frozen WPF HttpService used by WPF uploads (HttpService.BuildHttpClient / PostAsync):
/// 15s timeout, a MAA product User-Agent, Accept: application/json, and an HTTP/2 request
/// version. Proxy configuration is intentionally not replicated here: with no configured
/// proxy WPF falls back to the system proxy, which is also the default HttpClientHandler
/// behavior; MAAUnified's user-configured proxy is owned by the version-update feature
/// (VersionUpdatePolicy) and plumbing it into this fire-and-forget path is out of scope.
/// </summary>
public sealed class GameDataReportService
{
    public const int MaxRetryPerDomain = 3;
    public const int InitialBackoffMs = 3000;
    public const string PenguinIoDomain = "https://penguin-stats.io";
    public const string PenguinStatsSubtask = "ReportToPenguinStats";
    public const string PenguinIdResponseHeader = "x-penguin-set-penguinid";

    public static readonly string[] PenguinBackupDomains =
    [
        "https://penguin-stats.cn",
    ];

    private static readonly HttpClient DefaultHttpClient = BuildDefaultHttpClient();

    private readonly HttpClient _httpClient;
    private readonly Func<int, CancellationToken, Task> _delayAsync;
    private readonly Action<string>? _logger;

    public GameDataReportService(
        HttpClient? httpClient = null,
        Func<int, CancellationToken, Task>? delayAsync = null,
        Action<string>? logger = null)
    {
        _httpClient = httpClient ?? DefaultHttpClient;
        _delayAsync = delayAsync ?? ((milliseconds, cancellationToken) => Task.Delay(milliseconds, cancellationToken));
        _logger = logger;
    }

    public async Task<bool> PostWithRetryAsync(
        string url,
        string body,
        IReadOnlyDictionary<string, string>? headers,
        string subtask,
        Action<string>? penguinIdCallback = null,
        CancellationToken cancellationToken = default)
    {
        _logger?.Invoke($"Start PostWithRetryAsync, url: {url}");

        var originalResponse = await TryPostAsync(url, body, headers, cancellationToken).ConfigureAwait(false);
        if (originalResponse is not null && (int)originalResponse.StatusCode == 200)
        {
            ProcessResponseId(originalResponse, penguinIdCallback);
            return true;
        }

        _logger?.Invoke($"Initial request failed, status code: {originalResponse?.StatusCode.ToString() ?? "null"}");

        if (!string.Equals(subtask, PenguinStatsSubtask, StringComparison.Ordinal) || !url.Contains(PenguinIoDomain, StringComparison.Ordinal))
        {
            // 一图流的上报失败不提示
            return true;
        }

        foreach (var backupDomain in PenguinBackupDomains)
        {
            var newUrl = url.Replace(PenguinIoDomain, backupDomain, StringComparison.Ordinal);
            _logger?.Invoke($"Trying backup domain {backupDomain}, url: {newUrl}");
            var response = await TryPostAsync(newUrl, body, headers, cancellationToken).ConfigureAwait(false);
            if (response is not null && (int)response.StatusCode == 200)
            {
                ProcessResponseId(response, penguinIdCallback);
                return true;
            }

            _logger?.Invoke($"Request failed on backup domain {backupDomain}, status code: {response?.StatusCode.ToString() ?? "null"}");
        }

        _logger?.Invoke($"All retries failed for URL: {url}");
        return false;
    }

    internal static HttpClient BuildDefaultHttpClient()
    {
        // Frozen WPF reference: HttpService.BuildHttpClient sets User-Agent
        // MaaWpfGui/{informationalVersion} and Timeout = 15s. MAAUnified uses its own
        // product name with the same assembly-informational-version source
        // (Directory.Build.props VersionPrefix/VersionSuffix), dropping any '+commit' suffix
        // exactly like WPF.
        var uiVersion = typeof(GameDataReportService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+')[0] ?? "0.0.1";
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MAAUnified/{uiVersion}");
        return client;
    }

    private async Task<HttpResponseMessage?> TryPostAsync(
        string targetUrl,
        string body,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        try
        {
            var currentBackoff = InitialBackoffMs;
            for (var attempt = 1; attempt <= MaxRetryPerDomain; attempt++)
            {
                // Frozen WPF reference: HttpService.PostAsync sends an HTTP/2 request with
                // Accept: application/json appended after the caller-provided headers.
                using var request = new HttpRequestMessage(HttpMethod.Post, targetUrl)
                {
                    Version = HttpVersion.Version20,
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                if (headers is not null)
                {
                    foreach (var (name, value) in headers)
                    {
                        request.Headers.TryAddWithoutValidation(name, value);
                    }
                }

                request.Headers.TryAddWithoutValidation("Accept", "application/json");

                var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                switch ((int)response.StatusCode)
                {
                    case 200:
                        return response;
                    case >= 500 and < 600:
                        await _delayAsync(currentBackoff, cancellationToken).ConfigureAwait(false);
                        currentBackoff = (int)(currentBackoff * 1.5);
                        continue;
                    default:
                        return response;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Error during HTTP POST request to {targetUrl}: {ex.Message}");
        }

        return null;
    }

    private static void ProcessResponseId(HttpResponseMessage response, Action<string>? callback)
    {
        if (callback is null || !response.Headers.TryGetValues(PenguinIdResponseHeader, out var values))
        {
            return;
        }

        var penguinId = values.FirstOrDefault();
        if (!string.IsNullOrEmpty(penguinId))
        {
            callback.Invoke(penguinId);
        }
    }
}
