using System.Net;
using MAAUnified.Platform.Networking;
using Xunit;

namespace MAAUnified.Tests;

public sealed class MacSystemProxyTests
{
    [Fact]
    public void RuntimeFallbackChangingToDirect_IsNotUsedAsAnOriginProxy()
    {
        var proxy = MacSystemProxy.CreateForResolver(
            new RuntimeDirectProxy(),
            _ => MacSystemProxy.ProxyRoute.Runtime);

        Assert.False(proxy.IsBypassed(Destination));
        Assert.Null(proxy.GetProxy(Destination));
    }

    private sealed class RuntimeDirectProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        // Simulates the runtime changing to bypass after IsBypassed was read.
        public bool IsBypassed(Uri destination) => false;

        public Uri GetProxy(Uri destination) => destination;
    }

    [Fact]
    public void SocksDisabledBetweenTransportLookups_DoesNotUseTheOriginAsAProxy()
    {
        var calls = 0;
        var proxy = MacSystemProxy.CreateForResolver(
            new StubRuntimeProxy(),
            _ => ++calls == 1
                ? MacSystemProxy.ProxyRoute.Socks("localhost", 1080)
                : MacSystemProxy.ProxyRoute.Direct);

        Assert.False(proxy.IsBypassed(Destination));
        Assert.Null(proxy.GetProxy(Destination));
        Assert.Equal(2, calls);
    }

    private static readonly Uri Destination = new("https://maa.example/update/manifest.json");

    [Fact]
    public void DirectSystemSocksRoute_UsesSocks5AndCredentials()
    {
        var proxy = MacSystemProxy.CreateForResolver(
            new StubRuntimeProxy(),
            _ => MacSystemProxy.ProxyRoute.Socks("127.0.0.1", 1080, "a:user", "p@ss:word"));

        Assert.False(proxy.IsBypassed(Destination));
        var address = proxy.GetProxy(Destination)!;
        Assert.Equal("socks5", address.Scheme);
        Assert.Equal(1080, address.Port);
        var credential = proxy.Credentials!.GetCredential(address, "Basic")!;
        Assert.Equal("a:user", credential.UserName);
        Assert.Equal("p@ss:word", credential.Password);
    }

    [Fact]
    public void HttpOrPacRoute_DelegatesToRuntime()
    {
        var runtime = new StubRuntimeProxy();
        var proxy = MacSystemProxy.CreateForResolver(runtime, _ => MacSystemProxy.ProxyRoute.Runtime);

        Assert.False(proxy.IsBypassed(Destination));
        Assert.Equal(runtime.Address, proxy.GetProxy(Destination));
    }

    [Fact]
    public void ExplicitNativeDirectRoute_PreservesBypass()
    {
        var runtime = new StubRuntimeProxy();
        var proxy = MacSystemProxy.CreateForResolver(runtime, _ => MacSystemProxy.ProxyRoute.Direct);

        Assert.True(proxy.IsBypassed(Destination));
        Assert.Null(proxy.GetProxy(Destination));
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public void ProxyEnvironment_LeavesRuntimeProxyAndNoProxyRulesInControl()
    {
        var runtime = new StubRuntimeProxy { Bypass = true };
        var resolverCalls = 0;
        var proxy = MacSystemProxy.CreateForResolver(
            runtime,
            _ =>
            {
                resolverCalls++;
                return MacSystemProxy.ProxyRoute.Socks("localhost", 1080);
            },
            hasProxyEnvironment: true);

        Assert.True(proxy.IsBypassed(Destination));
        Assert.Equal(runtime.Address, proxy.GetProxy(Destination));
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public void SystemChanges_AreResolvedByFullUrlWithoutRecreatingProxy()
    {
        var port = 1080;
        var observed = new List<Uri>();
        var proxy = MacSystemProxy.CreateForResolver(
            new StubRuntimeProxy(),
            destination =>
            {
                observed.Add(destination);
                return MacSystemProxy.ProxyRoute.Socks("localhost", port);
            });

        Assert.Equal(1080, proxy.GetProxy(Destination)!.Port);
        port = 1081;
        var otherPath = new Uri("https://maa.example/other/manifest.json?version=2");
        Assert.Equal(1081, proxy.GetProxy(otherPath)!.Port);
        Assert.Equal(new[] { Destination, otherPath }, observed);
    }

    [Theory]
    [InlineData(null, 1080)]
    [InlineData("", 1080)]
    [InlineData("localhost", 0)]
    [InlineData("localhost", -1)]
    [InlineData("localhost", 65536)]
    public void InvalidSocksEndpoint_RetainsRuntimeFallback(string? host, int port)
    {
        var runtime = new StubRuntimeProxy();
        var proxy = MacSystemProxy.CreateForResolver(
            runtime,
            _ => MacSystemProxy.ProxyRoute.Socks(host, port));

        Assert.Equal(runtime.Address, proxy.GetProxy(Destination));
    }

    [Fact]
    public void RuntimeCredentials_ArePreservedForDelegatedProxy()
    {
        var credential = new NetworkCredential("runtime", "secret");
        var runtime = new StubRuntimeProxy { Credentials = credential };
        var proxy = MacSystemProxy.CreateForResolver(runtime, _ => MacSystemProxy.ProxyRoute.Runtime);

        Assert.Same(credential, proxy.Credentials!.GetCredential(runtime.Address, "Basic"));
    }

    [Theory]
    [InlineData("http_proxy")]
    [InlineData("HTTP_PROXY")]
    [InlineData("https_proxy")]
    [InlineData("HTTPS_PROXY")]
    [InlineData("all_proxy")]
    [InlineData("ALL_PROXY")]
    public void ProxyEnvironmentVariables_AreRecognized(string name)
    {
        Assert.True(MacSystemProxy.HasProxyEnvironment(
            key => key == name ? "http://localhost:8080" : null));
    }

    [Fact]
    public void NoProxyAlone_DoesNotReplaceSystemProxyBackend()
    {
        Assert.False(MacSystemProxy.HasProxyEnvironment(
            key => key == "NO_PROXY" ? "localhost" : null));
    }

    [Fact]
    public void LowercaseEmptyValue_TakesPrecedenceOverUppercaseValue()
    {
        Assert.False(MacSystemProxy.HasProxyEnvironment(key => key switch
        {
            "http_proxy" => string.Empty,
            "HTTP_PROXY" => "http://localhost:8080",
            _ => null,
        }));
    }

    [Fact]
    public void CgiHttpHeader_DoesNotCountAsProxyConfiguration()
    {
        Assert.False(MacSystemProxy.HasProxyEnvironment(key => key switch
        {
            "GATEWAY_INTERFACE" => "CGI/1.1",
            "HTTP_PROXY" => "http://localhost:8080",
            _ => null,
        }));
    }

    [Fact]
    public void NonMacPlatform_DoesNotLoadNativeFrameworks()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        var runtime = new StubRuntimeProxy();
        Assert.Same(runtime, MacSystemProxy.Create(runtime));
    }

    private sealed class StubRuntimeProxy : IWebProxy
    {
        public Uri Address { get; } = new("http://runtime.example:8080/");

        public bool Bypass { get; init; }

        public int Calls { get; private set; }

        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination)
        {
            Calls++;
            return Address;
        }

        public bool IsBypassed(Uri host)
        {
            Calls++;
            return Bypass;
        }
    }
}
