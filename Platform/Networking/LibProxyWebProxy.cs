// <copyright file="LibProxyWebProxy.cs" company="MaaAssistantArknights">
// Part of the MAAUnified project, maintained by the MaaAssistantArknights team (Maa Team)
// Copyright (C) 2021-2026 MaaAssistantArknights Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License v3.0 only as published by
// the Free Software Foundation, either version 3 of the License, or
// any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY
// </copyright>

using System.Net;

namespace MAAUnified.Platform.Networking;

internal sealed class LibProxyWebProxy : IWebProxy
{
    private readonly Func<Uri, string[]?> _resolve;
    private readonly IWebProxy _fallback;

    internal LibProxyWebProxy(Func<Uri, string[]?> resolve, IWebProxy fallback)
    {
        _resolve = resolve;
        _fallback = fallback;
        Credentials = new ProxyUriCredentials(fallback.Credentials);
    }

    public ICredentials? Credentials { get; set; }

    // A null result is essential if the system changes to direct routing
    // between the transport's IsBypassed and GetProxy calls.
    public Uri? GetProxy(Uri destination)
    {
        var proxy = Resolve(destination);
        return proxy == destination ? null : proxy;
    }

    public bool IsBypassed(Uri host) => Resolve(host) is null;

    private Uri? Resolve(Uri destination)
    {
        var candidates = _resolve(destination);
        if (candidates is not null)
        {
            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate, "direct://", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                // IWebProxy cannot express libproxy's ordered connection
                // failover. Prefer SOCKS5 for the generic SOCKS route.
                var normalized = candidate.StartsWith("socks://", StringComparison.OrdinalIgnoreCase)
                    ? "socks5://" + candidate["socks://".Length..]
                    : candidate;
                if (Uri.TryCreate(normalized, UriKind.Absolute, out var proxy)
                    && !string.IsNullOrEmpty(proxy.Host)
                    && proxy.Port is > 0 and <= 65535
                    && proxy.Scheme is "http" or "https" or "socks4" or "socks4a" or "socks5")
                {
                    return proxy;
                }
            }
        }

        return _fallback.IsBypassed(destination) ? null : _fallback.GetProxy(destination);
    }

    private sealed class ProxyUriCredentials(ICredentials? fallback) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            if (string.IsNullOrEmpty(uri.UserInfo))
            {
                return fallback?.GetCredential(uri, authType);
            }

            var separator = uri.UserInfo.IndexOf(':');
            var user = separator < 0 ? uri.UserInfo : uri.UserInfo[..separator];
            var password = separator < 0 ? string.Empty : uri.UserInfo[(separator + 1)..];
            return new NetworkCredential(Uri.UnescapeDataString(user), Uri.UnescapeDataString(password));
        }
    }
}
