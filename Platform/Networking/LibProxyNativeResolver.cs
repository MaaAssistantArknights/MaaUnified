// <copyright file="LibProxyNativeResolver.cs" company="MaaAssistantArknights">
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

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MAAUnified.Platform.Networking;

internal sealed class LibProxyNativeResolver
{
    private readonly ProxyFactoryHandle _factory;
    private readonly GetProxiesCallback _getProxies;
    private readonly FreeProxies _freeProxies;

    private LibProxyNativeResolver(ProxyFactoryHandle factory, GetProxiesCallback getProxies, FreeProxies freeProxies)
    {
        _factory = factory;
        _getProxies = getProxies;
        _freeProxies = freeProxies;
    }

    internal static LibProxyNativeResolver? TryCreate()
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libproxy.so.1", out var module))
        {
            return null;
        }

        var retained = false;
        try
        {
            if (!TryGetExport<NewFactory>(module, "px_proxy_factory_new", out var create)
                || !TryGetExport<FreeFactory>(module, "px_proxy_factory_free", out var free)
                || !TryGetExport<GetProxiesCallback>(module, "px_proxy_factory_get_proxies", out var getProxies)
                || !TryGetExport<FreeProxies>(module, "px_proxy_factory_free_proxies", out var freeProxies))
            {
                return null;
            }

            var factory = create!();
            if (factory == nint.Zero)
            {
                return null;
            }

            var handle = new ProxyFactoryHandle(factory, module, free!);
            retained = true;
            return new LibProxyNativeResolver(handle, getProxies!, freeProxies!);
        }
        finally
        {
            if (!retained)
            {
                NativeLibrary.Free(module);
            }
        }
    }

    internal string[]? GetProxies(Uri destination)
    {
        var referenced = false;
        try
        {
            _factory.DangerousAddRef(ref referenced);
            lock (_factory)
            {
                var vector = _getProxies(_factory.DangerousGetHandle(), destination.AbsoluteUri);
                return ReadAndFreeProxyList(vector, pointer => _freeProxies(pointer));
            }
        }
        finally
        {
            if (referenced)
            {
                _factory.DangerousRelease();
            }
        }
    }

    internal static string[]? ReadAndFreeProxyList(nint vector, Action<nint> release)
    {
        if (vector == nint.Zero)
        {
            return null;
        }

        try
        {
            var proxies = new List<string>();
            for (var index = 0; index < 256; index++)
            {
                var value = Marshal.ReadIntPtr(vector, index * nint.Size);
                if (value == nint.Zero)
                {
                    break;
                }

                var proxy = Marshal.PtrToStringUTF8(value);
                if (!string.IsNullOrEmpty(proxy))
                {
                    proxies.Add(proxy);
                }
            }

            return proxies.ToArray();
        }
        finally
        {
            release(vector);
        }
    }

    private static bool TryGetExport<T>(nint module, string symbol, out T? callback)
        where T : Delegate
    {
        callback = NativeLibrary.TryGetExport(module, symbol, out var address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;
        return callback is not null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint NewFactory();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeFactory(nint factory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetProxiesCallback(nint factory, [MarshalAs(UnmanagedType.LPUTF8Str)] string url);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeProxies(nint proxies);

    private sealed class ProxyFactoryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly nint _module;
        private readonly FreeFactory _free;

        internal ProxyFactoryHandle(nint factory, nint module, FreeFactory free)
            : base(ownsHandle: true)
        {
            _module = module;
            _free = free;
            SetHandle(factory);
        }

        protected override bool ReleaseHandle()
        {
            _free(handle);
            NativeLibrary.Free(_module);
            return true;
        }
    }
}
