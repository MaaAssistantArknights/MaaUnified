// <copyright file="MacSystemProxy.cs" company="MaaAssistantArknights">
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
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MAAUnified.Platform.Networking;

/// <summary>
/// Supplements the runtime's macOS HTTP/PAC support with direct system SOCKS routes.
/// </summary>
internal static class MacSystemProxy
{
    private static readonly Lazy<NativeResolver?> Native = new(NativeResolver.TryCreate);

    internal static IWebProxy Create(IWebProxy runtimeProxy)
    {
        ArgumentNullException.ThrowIfNull(runtimeProxy);
        if (!OperatingSystem.IsMacOS())
        {
            return runtimeProxy;
        }

        return CreateForResolver(
            runtimeProxy,
            destination => Native.Value?.Resolve(destination) ?? ProxyRoute.Runtime,
            HasProxyEnvironment(Environment.GetEnvironmentVariable));
    }

    internal static IWebProxy CreateForResolver(
        IWebProxy runtimeProxy,
        Func<Uri, ProxyRoute> resolve,
        bool hasProxyEnvironment = false)
        => new Adapter(runtimeProxy, resolve, hasProxyEnvironment);

    internal static bool HasProxyEnvironment(Func<string, string?> read)
    {
        foreach (var name in new[] { "http", "https", "all" })
        {
            var lower = read($"{name}_proxy");
            // The Unix runtime ignores the uppercase HTTP_PROXY CGI header.
            var upper = name == "http" && read("GATEWAY_INTERFACE") is not null
                ? null
                : read($"{name.ToUpperInvariant()}_PROXY");
            if (!string.IsNullOrWhiteSpace(lower ?? upper))
            {
                return true;
            }
        }

        return false;
    }

    internal enum RouteKind
    {
        Runtime,
        Direct,
        Socks,
    }

    internal readonly record struct ProxyRoute(RouteKind Kind, Uri? Address = null)
    {
        internal static ProxyRoute Runtime => new(RouteKind.Runtime);

        internal static ProxyRoute Direct => new(RouteKind.Direct);

        internal static ProxyRoute Socks(
            string? host,
            int port,
            string? username = null,
            string? password = null)
        {
            if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
            {
                return Runtime;
            }

            try
            {
                var builder = new UriBuilder("socks5", host, port);
                if (!string.IsNullOrEmpty(username))
                {
                    builder.UserName = Uri.EscapeDataString(username);
                    builder.Password = Uri.EscapeDataString(password ?? string.Empty);
                }

                return new ProxyRoute(RouteKind.Socks, builder.Uri);
            }
            catch (UriFormatException)
            {
                return Runtime;
            }
        }
    }

    private sealed class Adapter(
        IWebProxy runtime,
        Func<Uri, ProxyRoute> resolve,
        bool hasProxyEnvironment) : IWebProxy
    {
        public ICredentials? Credentials { get; set; } = new ProxyCredentials(runtime.Credentials);

        public Uri? GetProxy(Uri destination)
        {
            var route = Resolve(destination);
            var proxy = route.Kind switch
            {
                RouteKind.Direct => null,
                RouteKind.Socks => route.Address,
                _ => runtime.GetProxy(destination),
            };
            return proxy == destination ? null : proxy;
        }

        public bool IsBypassed(Uri host)
        {
            var route = Resolve(host);
            return route.Kind switch
            {
                RouteKind.Direct => true,
                RouteKind.Socks => false,
                _ => runtime.IsBypassed(host),
            };
        }

        private ProxyRoute Resolve(Uri destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            return hasProxyEnvironment ? ProxyRoute.Runtime : resolve(destination);
        }
    }

    private sealed class ProxyCredentials(ICredentials? fallback) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            if (uri.Scheme != "socks5" || string.IsNullOrEmpty(uri.UserInfo))
            {
                return fallback?.GetCredential(uri, authType);
            }

            var parts = uri.UserInfo.Split(':', 2);
            return new NetworkCredential(
                Uri.UnescapeDataString(parts[0]),
                parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
        }
    }

    private sealed class NativeResolver
    {
        private const string CFNetwork = "/System/Library/Frameworks/CFNetwork.framework/CFNetwork";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const uint Utf8 = 0x08000100;
        private readonly nint _module;
        private readonly nint _typeKey;
        private readonly nint _socksType;
        private readonly nint _directType;
        private readonly nint _hostKey;
        private readonly nint _portKey;
        private readonly nint _usernameKey;
        private readonly nint _passwordKey;

        private NativeResolver(nint module)
        {
            _module = module;
            _typeKey = Symbol("kCFProxyTypeKey");
            _socksType = Symbol("kCFProxyTypeSOCKS");
            _directType = Symbol("kCFProxyTypeNone");
            _hostKey = Symbol("kCFProxyHostNameKey");
            _portKey = Symbol("kCFProxyPortNumberKey");
            _usernameKey = Symbol("kCFProxyUsernameKey");
            _passwordKey = Symbol("kCFProxyPasswordKey");
        }

        internal static NativeResolver? TryCreate()
        {
            if (!OperatingSystem.IsMacOS() || !NativeLibrary.TryLoad(CFNetwork, out var module))
            {
                return null;
            }

            try
            {
                // Retain the framework module for the lifetime of the shared resolver,
                // because its exported CFString constants are borrowed pointers.
                return new NativeResolver(module);
            }
            catch (EntryPointNotFoundException)
            {
                NativeLibrary.Free(module);
                return null;
            }
        }

        internal ProxyRoute Resolve(Uri destination)
        {
            using var settings = new OwnedCFHandle(CFNetworkCopySystemProxySettings());
            using var urlText = new OwnedCFHandle(CFStringCreateWithCString(nint.Zero, destination.AbsoluteUri, Utf8));
            if (settings.IsInvalid || urlText.IsInvalid)
            {
                return ProxyRoute.Runtime;
            }

            using var url = new OwnedCFHandle(CFURLCreateWithString(nint.Zero, urlText.DangerousGetHandle(), nint.Zero));
            if (url.IsInvalid)
            {
                return ProxyRoute.Runtime;
            }

            using var proxies = new OwnedCFHandle(
                CFNetworkCopyProxiesForURL(url.DangerousGetHandle(), settings.DangerousGetHandle()));
            if (proxies.IsInvalid || CFArrayGetCount(proxies.DangerousGetHandle()) == 0)
            {
                return ProxyRoute.Runtime;
            }

            // Respect CFNetwork's ordering. HTTP and PAC entries are handled by
            // the runtime; do not replace them with a later SOCKS candidate.
            var dictionary = CFArrayGetValueAtIndex(proxies.DangerousGetHandle(), 0);
            var type = CFDictionaryGetValue(dictionary, _typeKey);
            if (type == nint.Zero)
            {
                return ProxyRoute.Runtime;
            }

            if (CFEqual(type, _directType) != 0)
            {
                return ProxyRoute.Direct;
            }

            if (CFEqual(type, _socksType) == 0)
            {
                return ProxyRoute.Runtime;
            }

            var number = CFDictionaryGetValue(dictionary, _portKey);
            if (number == nint.Zero
                || CFGetTypeID(number) != CFNumberGetTypeID()
                || CFNumberGetValue(number, 9, out var port) == 0)
            {
                return ProxyRoute.Runtime;
            }

            return ProxyRoute.Socks(
                GetString(dictionary, _hostKey),
                port,
                GetString(dictionary, _usernameKey),
                GetString(dictionary, _passwordKey));
        }

        private nint Symbol(string name)
        {
            var address = NativeLibrary.GetExport(_module, name);
            var value = Marshal.ReadIntPtr(address);
            return value == nint.Zero ? throw new EntryPointNotFoundException(name) : value;
        }

        private static string? GetString(nint dictionary, nint key)
        {
            var value = CFDictionaryGetValue(dictionary, key);
            if (value == nint.Zero || CFGetTypeID(value) != CFStringGetTypeID())
            {
                return null;
            }

            var pointer = CFStringGetCStringPtr(value, Utf8);
            if (pointer != nint.Zero)
            {
                return Marshal.PtrToStringUTF8(pointer);
            }

            var length = CFStringGetLength(value);
            if (length is < 0 or > 65536)
            {
                return null;
            }

            var buffer = new byte[checked((int)length * 4 + 1)];
            if (CFStringGetCString(value, buffer, buffer.Length, Utf8) == 0)
            {
                return null;
            }

            var terminator = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, terminator < 0 ? buffer.Length : terminator);
        }

        private sealed class OwnedCFHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal OwnedCFHandle(nint value)
                : base(ownsHandle: true) => SetHandle(value);

            protected override bool ReleaseHandle()
            {
                CFRelease(handle);
                return true;
            }
        }

        [DllImport(CFNetwork, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint CFNetworkCopySystemProxySettings();

        [DllImport(CFNetwork, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint CFNetworkCopyProxiesForURL(nint url, nint settings);

        [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint CFStringCreateWithCString(
            nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

        [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint CFURLCreateWithString(nint allocator, nint text, nint baseUrl);

        [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint CFArrayGetCount(nint array);

        [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint CFArrayGetValueAtIndex(nint array, nint index);

        [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint CFDictionaryGetValue(nint dictionary, nint key);

        [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
        private static extern byte CFEqual(nint first, nint second);

        [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint CFGetTypeID(nint value);

        [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint CFNumberGetTypeID();

        [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
        private static extern byte CFNumberGetValue(nint number, nint numberType, out int result);

        [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint CFStringGetTypeID();

        [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint CFStringGetCStringPtr(nint text, uint encoding);

        [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint CFStringGetLength(nint text);

        [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
        private static extern byte CFStringGetCString(nint text, [Out] byte[] buffer, nint size, uint encoding);

        [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
        private static extern void CFRelease(nint value);
    }
}
