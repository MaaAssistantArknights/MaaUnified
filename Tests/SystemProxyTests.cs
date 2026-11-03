// <copyright file="SystemProxyTests.cs" company="MaaAssistantArknights">
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
using System.Runtime.InteropServices;
using MAAUnified.Platform.Networking;
using Xunit;

namespace MAAUnified.Tests;

public sealed class SystemProxyTests
{
    [Fact]
    public void RuntimeFallbackChangingToDirect_IsNotUsedAsAnOriginProxy()
    {
        var destination = new Uri("https://origin.example.test/update");
        var proxy = new LibProxyWebProxy(_ => null, new RuntimeDirectProxy());

        Assert.False(proxy.IsBypassed(destination));
        Assert.Null(proxy.GetProxy(destination));
    }

    private sealed class RuntimeDirectProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        // Simulates the runtime changing to bypass after IsBypassed was read.
        public bool IsBypassed(Uri destination) => false;

        public Uri GetProxy(Uri destination) => destination;
    }

    [Fact]
    public void ProxyDisabledBetweenTransportLookups_DoesNotUseTheOriginAsAProxy()
    {
        var calls = 0;
        var destination = new Uri("https://origin.example.test/update");
        var proxy = new LibProxyWebProxy(
            _ => ++calls == 1 ? ["http://proxy.test:8080"] : ["direct://"],
            new WebProxy());

        Assert.False(proxy.IsBypassed(destination));
        Assert.Null(proxy.GetProxy(destination));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void NativeRoutes_AreResolvedForFullUrl_AndFollowChanges()
    {
        var destination = new Uri("https://example.test/private/item?kind=one");
        var address = "http://first.test:8080";
        var observed = new List<Uri>();
        var proxy = new LibProxyWebProxy(
            uri =>
            {
                observed.Add(uri);
                return [uri.AbsolutePath.StartsWith("/private", StringComparison.Ordinal) ? address : "direct://"];
            },
            new WebProxy("http://fallback.test:8080"));

        Assert.Equal(new Uri(address), proxy.GetProxy(destination));
        address = "http://second.test:8081";
        Assert.Equal(new Uri(address), proxy.GetProxy(destination));
        Assert.True(proxy.IsBypassed(new Uri("https://example.test/public")));
        Assert.Equal(destination, observed[0]);
        Assert.Equal(destination, observed[1]);
    }

    [Fact]
    public void ExplicitDirect_OverridesFallbackProxy()
    {
        var destination = new Uri("http://example.test/no-proxy");
        var proxy = new LibProxyWebProxy(
            _ => ["direct://", "http://ignored.test:8080"],
            new WebProxy("http://fallback.test:8080"));

        Assert.True(proxy.IsBypassed(destination));
        Assert.Null(proxy.GetProxy(destination));
    }

    [Theory]
    [InlineData("http://proxy.test:8080", "http://proxy.test:8080")]
    [InlineData("https://proxy.test:8443", "https://proxy.test:8443")]
    [InlineData("socks4://proxy.test:1080", "socks4://proxy.test:1080")]
    [InlineData("socks4a://proxy.test:1080", "socks4a://proxy.test:1080")]
    [InlineData("socks5://proxy.test:1080", "socks5://proxy.test:1080")]
    [InlineData("socks://proxy.test:1080", "socks5://proxy.test:1080")]
    public void NativeRoute_PreservesSupportedProtocol(string candidate, string expected)
    {
        var proxy = new LibProxyWebProxy(_ => [candidate], new WebProxy());

        Assert.Equal(new Uri(expected), proxy.GetProxy(new Uri("https://example.test")));
    }

    [Fact]
    public void OrderedRoutes_SelectFirstSupportedRoute()
    {
        var proxy = new LibProxyWebProxy(
            _ => ["not-a-proxy", "ftp://unsupported.test:21", "http://first.test:8080", "direct://"],
            new WebProxy());

        Assert.Equal(
            new Uri("http://first.test:8080"),
            proxy.GetProxy(new Uri("https://example.test")));
    }

    [Fact]
    public void UnavailableNativeResolver_UsesEnvironmentFallback()
    {
        var fallback = new WebProxy("http://environment.test:8080")
        {
            BypassList = ["bypass\\.test"],
        };
        var proxy = new LibProxyWebProxy(_ => null, fallback);

        Assert.Equal(
            new Uri("http://environment.test:8080"),
            proxy.GetProxy(new Uri("https://example.test")));
        Assert.True(proxy.IsBypassed(new Uri("https://bypass.test/path")));
    }

    [Fact]
    public void EmptyOrInvalidNativeRoutes_UseFallback()
    {
        var destination = new Uri("https://example.test");
        var proxy = new LibProxyWebProxy(
            _ => ["http://", "socks5://proxy.test:0", "file:///not-a-proxy"],
            new WebProxy("http://fallback.test:8080"));

        Assert.Equal(new Uri("http://fallback.test:8080"), proxy.GetProxy(destination));
    }

    [Fact]
    public void ProxyCredentials_AreAvailableWithoutLoggingOrRewritingRequest()
    {
        var proxy = new LibProxyWebProxy(
            _ => ["http://user%40example:p%3Ass@proxy.test:8080"],
            new WebProxy());
        var route = Assert.IsType<Uri>(proxy.GetProxy(new Uri("https://example.test")));
        var credentials = proxy.Credentials!.GetCredential(route, "Basic");

        Assert.NotNull(credentials);
        Assert.Equal("user@example", credentials.UserName);
        Assert.Equal("p:ss", credentials.Password);
    }

    [Fact]
    public void NativeVector_IsDecodedAndReleasedExactlyOnce()
    {
        var strings = new[]
        {
            Marshal.StringToCoTaskMemUTF8("http://proxy.test:8080"),
            Marshal.StringToCoTaskMemUTF8("direct://"),
        };
        var vector = Marshal.AllocHGlobal(nint.Size * 3);
        Marshal.WriteIntPtr(vector, 0, strings[0]);
        Marshal.WriteIntPtr(vector, nint.Size, strings[1]);
        Marshal.WriteIntPtr(vector, nint.Size * 2, nint.Zero);
        var released = 0;

        var result = LibProxyNativeResolver.ReadAndFreeProxyList(
            vector,
            pointer =>
            {
                released++;
                foreach (var value in strings)
                {
                    Marshal.FreeCoTaskMem(value);
                }

                Marshal.FreeHGlobal(pointer);
            });

        Assert.NotNull(result);
        Assert.Equal(["http://proxy.test:8080", "direct://"], result);
        Assert.Equal(1, released);
    }

    [Fact]
    public void NullNativeVector_IsUnavailable_AndRequiresNoFree()
    {
        var result = LibProxyNativeResolver.ReadAndFreeProxyList(
            nint.Zero,
            _ => throw new InvalidOperationException("NULL must not be freed."));

        Assert.Null(result);
    }

    [Fact]
    public async Task BlockingProxyLookup_DoesNotBlockInitiatingThread()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource<(int Thread, Task<HttpResponseMessage> Response)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new BackgroundProxyHttpHandler(new BlockingHandler(started, release)));
        var initiatingCall = Task.Run(() =>
        {
            var thread = Environment.CurrentManagedThreadId;
            var response = client.GetAsync("http://example.test");
            returned.TrySetResult((thread, response));
        });

        try
        {
            var call = await returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var workerThread = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(call.Thread, workerThread);
            Assert.False(call.Response.IsCompleted);
            release.Set();
            using var response = await call.Response;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await initiatingCall;
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task QueuedCancellation_DoesNotStartAnotherNativeWorker()
    {
        using var release = new ManualResetEventSlim();
        using var slots = new SemaphoreSlim(1);
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new BlockingHandler(started, release);
        using var client = new HttpClient(new BackgroundProxyHttpHandler(transport, slots));
        var first = client.GetAsync("http://first.test");

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource();
            var queued = client.GetAsync("http://second.test", cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Equal(1, transport.CallCount);
        }
        finally
        {
            release.Set();
            using var response = await first;
        }
    }

    private sealed class BlockingHandler(
        TaskCompletionSource<int> started,
        ManualResetEventSlim release) : HttpMessageHandler
    {
        private int _calls;

        internal int CallCount => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            started.TrySetResult(Environment.CurrentManagedThreadId);
            release.Wait();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
