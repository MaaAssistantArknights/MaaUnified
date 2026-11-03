using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.VersionUpdate;
using Xunit;

namespace MAAUnified.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UpdateProxyRoutingCollection
{
    public const string Name = "Update proxy process state";
}

[Collection(UpdateProxyRoutingCollection.Name)]
public sealed class UpdateProxyRoutingTests
{
    private static readonly Uri Destination = new("http://maa-proxy-test.invalid/update/manifest.json");

    [Fact]
    public async Task ManualHttpProxy_RoutesTheActualRequest()
    {
        await using var proxy = new LoopbackServer("manual");
        using var client = UpdateHttpClientFactory.Create(
            VersionUpdatePolicy.Default with { ProxyType = "http", Proxy = proxy.Address.Authority },
            TimeSpan.FromSeconds(10));

        Assert.Equal("manual", await client.GetStringAsync(Destination));
        Assert.Contains(proxy.RequestLines, line => line.StartsWith($"GET {Destination} ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1:1234")]
    [InlineData("not a valid proxy address !")]
    public async Task NoneMode_BypassesConfiguredSystemAndManualProxy(string savedAddress)
    {
        await using var origin = new LoopbackServer("direct");
        await using var proxy = new LoopbackServer("unexpected proxy");
        using var scope = new DefaultProxyScope(new MutableProxy(proxy.Address));
        using var client = UpdateHttpClientFactory.Create(
            VersionUpdatePolicy.Default with { ProxyType = "none", Proxy = savedAddress },
            TimeSpan.FromSeconds(10),
            () => throw new InvalidOperationException("None must not create a system proxy handler."));

        Assert.Equal("direct", await client.GetStringAsync(origin.Address));
        Assert.Empty(proxy.RequestLines);
        Assert.Single(origin.RequestLines);
    }

    [Fact]
    public async Task NoneMode_NeverQueriesTheDefaultProxy()
    {
        await using var origin = new LoopbackServer("direct");
        using var scope = new DefaultProxyScope(new ThrowingProxy());
        using var client = UpdateHttpClientFactory.Create(
            VersionUpdatePolicy.Default with { ProxyType = "NONE" },
            TimeSpan.FromSeconds(10));

        Assert.Equal("direct", await client.GetStringAsync(origin.Address));
    }

    [Fact]
    public async Task SoftwareUpdateCheck_NoneWithEmptyAddress_DoesNotUseSystemClient()
    {
        const string feed = """
            [{"tag_name":"v1.0.0","name":"direct-feed","prerelease":false,"assets":[]}]
            """;
        await using var origin = new LoopbackServer(feed);
        await using var proxy = new LoopbackServer(feed.Replace("direct-feed", "proxy-feed", StringComparison.Ordinal));
        using var scope = new DefaultProxyScope(new MutableProxy(proxy.Address));
        var workflow = new AppUpdateWorkflowService(new NoOpAppLifecycleService());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await workflow.CheckForUpdatesAsync(
            VersionUpdatePolicy.Default with
            {
                ProxyType = "none",
                Proxy = string.Empty,
                ResourceApi = new Uri(origin.Address, "releases.json").AbsoluteUri,
            },
            "v1.0.0",
            timeout.Token);

        Assert.Equal("direct-feed", result.ReleaseName);
        Assert.Single(origin.RequestLines);
        Assert.Empty(proxy.RequestLines);
    }

    [Fact]
    public async Task SoftwarePackageDownload_NoneWithEmptyAddress_DoesNotUseSystemClient()
    {
        await using var origin = new LoopbackServer("direct-package");
        await using var proxy = new LoopbackServer("proxy-package");
        using var scope = new DefaultProxyScope(new MutableProxy(proxy.Address));
        var workflow = new AppUpdateWorkflowService(new NoOpAppLifecycleService());
        var root = Path.Combine(Path.GetTempPath(), $"maa-proxy-workflow-{Guid.NewGuid():N}");
        var packageDirectory = Path.Combine(root, "update-packages");
        var packagePath = Path.Combine(packageDirectory, "proxy-route-test.zip");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var release = new VersionUpdateCheckResult(
            Channel: "Stable",
            CurrentVersion: "v1.0.0",
            TargetVersion: "v2.0.0",
            ReleaseName: "v2.0.0",
            Summary: string.Empty,
            Body: string.Empty,
            PackageName: "proxy-route-test.zip",
            PackageDownloadUrl: new Uri(origin.Address, "package.zip"),
            PackageSize: null,
            IsNewVersion: true,
            HasPackage: true,
            PackageResolutionStatus: PackageResolutionStatus.Available,
            PackageSourceKind: PackageSourceKind.ReleaseAsset);

        try
        {
            var result = await workflow.DownloadPackageAsync(
                release,
                root,
                forceDownload: true,
                VersionUpdatePolicy.Default with { ProxyType = "none", Proxy = string.Empty },
                progress: null,
                timeout.Token);

            Assert.True(result.Success, result.Message);
            Assert.Equal("direct-package", await File.ReadAllTextAsync(packagePath, timeout.Token));
            Assert.Single(origin.RequestLines);
            Assert.Empty(proxy.RequestLines);
        }
        finally
        {
            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }

            if (Directory.Exists(packageDirectory))
            {
                Directory.Delete(packageDirectory);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root);
            }
        }
    }

    [Fact]
    public async Task SystemMode_IgnoresRetainedInvalidManualAddress()
    {
        await using var proxy = new LoopbackServer("system");
        using var client = UpdateHttpClientFactory.Create(
            VersionUpdatePolicy.Default with { ProxyType = "system", Proxy = "previous manual input, not a URI" },
            TimeSpan.FromSeconds(10),
            () => new HttpClientHandler { Proxy = new MutableProxy(proxy.Address), UseProxy = true });

        Assert.Equal("system", await client.GetStringAsync(Destination));
    }

    [Fact]
    public async Task EmptyLegacyManualAddress_UsesTheSystemProxy()
    {
        await using var proxy = new LoopbackServer("legacy-system");
        using var client = UpdateHttpClientFactory.Create(
            VersionUpdatePolicy.Default with { ProxyType = "http", Proxy = string.Empty },
            TimeSpan.FromSeconds(10),
            () => new HttpClientHandler { Proxy = new MutableProxy(proxy.Address), UseProxy = true });

        Assert.Equal("legacy-system", await client.GetStringAsync(Destination));
    }

    [Fact]
    public async Task SystemMode_PreservesDestinationBypass()
    {
        await using var origin = new LoopbackServer("direct");
        await using var proxy = new LoopbackServer("proxy");
        using var client = UpdateHttpClientFactory.Create(
            VersionUpdatePolicy.Default,
            TimeSpan.FromSeconds(10),
            () => new HttpClientHandler { Proxy = new MutableProxy(proxy.Address) { Bypass = true }, UseProxy = true });

        Assert.Equal("direct", await client.GetStringAsync(origin.Address));
        Assert.Empty(proxy.RequestLines);
        Assert.Single(origin.RequestLines);
    }

    [Fact]
    public async Task SystemProxyChanges_AreUsedByAnExistingClient()
    {
        await using var first = new LoopbackServer("first");
        await using var second = new LoopbackServer("second");
        var mutableProxy = new MutableProxy(first.Address);
        using var client = UpdateHttpClientFactory.Create(
            VersionUpdatePolicy.Default,
            TimeSpan.FromSeconds(10),
            () => new HttpClientHandler { Proxy = mutableProxy, UseProxy = true });

        Assert.Equal("first", await client.GetStringAsync(Destination));
        mutableProxy.Address = second.Address;
        Assert.Equal("second", await client.GetStringAsync(Destination));
    }

    [Theory]
    [InlineData("file:///tmp/proxy")]
    [InlineData("ftp://127.0.0.1:8080")]
    [InlineData("http://")]
    public void InvalidManualProxy_DoesNotSilentlySendDirect(string address)
    {
        Assert.Throws<ArgumentException>(() => UpdateHttpClientFactory.Create(
            VersionUpdatePolicy.Default with { ProxyType = "http", Proxy = address },
            TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ResourceUpdateCheck_UsesItsSavedProxyPolicy()
    {
        await using var manual = new LoopbackServer("blocked locally", statusCode: 502);
        await using var system = new LoopbackServer("unexpected system route", statusCode: 502);
        using var scope = new DefaultProxyScope(new MutableProxy(system.Address));
        var service = new VersionUpdateFeatureService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await service.CheckResourceUpdateAsync(
            VersionUpdatePolicy.Default with { ProxyType = "http", Proxy = manual.Address.Authority },
            "Official",
            timeout.Token);

        Assert.False(result.Success);
        Assert.Contains(manual.RequestLines, line => line.StartsWith("CONNECT mirrorchyan.com:443 ", StringComparison.Ordinal));
        Assert.Empty(system.RequestLines);
    }

    private sealed class DefaultProxyScope : IDisposable
    {
        private readonly IWebProxy _original = HttpClient.DefaultProxy;

        public DefaultProxyScope(IWebProxy replacement) => HttpClient.DefaultProxy = replacement;

        public void Dispose() => HttpClient.DefaultProxy = _original;
    }

    private sealed class ThrowingProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => throw new InvalidOperationException("None must not resolve a proxy.");

        public bool IsBypassed(Uri host) => throw new InvalidOperationException("None must not query proxy bypass.");
    }

    private sealed class MutableProxy(Uri address) : IWebProxy
    {
        public Uri Address { get; set; } = address;

        public bool Bypass { get; init; }

        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => Address;

        public bool IsBypassed(Uri host) => Bypass;
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serverTask;
        private readonly string _body;
        private readonly int _statusCode;

        public LoopbackServer(string body, int statusCode = 200)
        {
            _body = body;
            _statusCode = statusCode;
            _listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _serverTask = RunAsync();
        }

        public Uri Address { get; }

        public ConcurrentQueue<string> RequestLines { get; } = new();

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException) when (_stop.IsCancellationRequested)
            {
            }
            finally
            {
                _stop.Dispose();
            }
        }

        private async Task RunAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(_stop.Token);
                if (requestLine is null)
                {
                    continue;
                }

                RequestLines.Enqueue(requestLine);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token)))
                {
                }

                var body = Encoding.UTF8.GetBytes(_body);
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {_statusCode} Test\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, _stop.Token);
                await stream.WriteAsync(body, _stop.Token);
            }
        }
    }
}
