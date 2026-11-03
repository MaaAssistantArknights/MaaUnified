// <copyright file="BackgroundProxyHttpHandler.cs" company="MaaAssistantArknights">
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

using System.Net.Http;

namespace MAAUnified.Platform.Networking;

internal sealed class BackgroundProxyHttpHandler : DelegatingHandler
{
    private static readonly SemaphoreSlim SharedSlots = new(4);
    private readonly SemaphoreSlim _slots;

    internal BackgroundProxyHttpHandler(HttpMessageHandler inner, SemaphoreSlim? slots = null)
        : base(inner)
    {
        _slots = slots ?? SharedSlots;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // IWebProxy and PAC lookup are synchronous, including before the
            // transport's first await. Bound worker starts until headers arrive.
            return await Task.Run(
                () => base.SendAsync(request, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }
}
