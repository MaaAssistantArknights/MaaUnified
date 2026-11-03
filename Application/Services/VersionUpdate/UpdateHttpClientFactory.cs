using System.Net;
using MAAUnified.Application.Models;
using MAAUnified.Platform.Networking;

namespace MAAUnified.Application.Services.VersionUpdate;

/// <summary>
/// Applies the selected proxy policy to software and resource update requests.
/// </summary>
internal static class UpdateHttpClientFactory
{
    internal static HttpClient Create(
        VersionUpdatePolicy? policy,
        TimeSpan timeout,
        Func<HttpMessageHandler>? systemHandlerFactory = null)
    {
        HttpMessageHandler handler;
        if (policy is not null && string.Equals(policy.ProxyType, "none", StringComparison.OrdinalIgnoreCase))
        {
            handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseProxy = false,
            };
        }
        else if (policy is null
            || string.Equals(policy.ProxyType, "system", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(policy.Proxy))
        {
            // An empty legacy manual setting also retains the platform default.
            handler = (systemHandlerFactory ?? SystemProxyFactory.CreateHttpHandler)();
        }
        else
        {
            if (!TryBuildManualProxy(policy, out var proxy))
            {
                throw new ArgumentException("Invalid manual proxy address or protocol.", nameof(policy));
            }

            handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseProxy = true,
                Proxy = proxy,
            };
        }

        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    internal static bool TryBuildManualProxy(VersionUpdatePolicy policy, out WebProxy? proxy)
    {
        proxy = null;
        var raw = policy.Proxy?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var candidate = raw.Contains("://", StringComparison.Ordinal)
            ? raw
            : $"{policy.ProxyType}://{raw}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Port is < 1 or > 65535
            || uri.Scheme is not ("http" or "https" or "socks5"))
        {
            return false;
        }

        proxy = new WebProxy(uri);
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(
                Uri.UnescapeDataString(parts[0]),
                parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
        }

        return true;
    }
}
