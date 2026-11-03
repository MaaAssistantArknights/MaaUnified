// <copyright file="SystemProxyFactory.cs" company="MaaAssistantArknights">
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
using System.Net.Http;

namespace MAAUnified.Platform.Networking;

public static class SystemProxyFactory
{
    private static readonly Lazy<LibProxyNativeResolver?> LinuxNative = new(LibProxyNativeResolver.TryCreate);
    private static readonly Lazy<IWebProxy> LinuxProxy = new(CreateLinuxProxy);

    public static HttpMessageHandler CreateHttpHandler()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = OperatingSystem.IsLinux()
                ? LinuxProxy.Value
                : OperatingSystem.IsMacOS()
                    ? MacSystemProxy.Create(HttpClient.DefaultProxy)
                    : null,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        };

        return new BackgroundProxyHttpHandler(handler);
    }

    private static IWebProxy CreateLinuxProxy()
    {
        return new LibProxyWebProxy(
            destination => LinuxNative.Value?.GetProxies(destination),
            HttpClient.DefaultProxy);
    }
}
