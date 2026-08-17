using System.Reflection;
using Avalonia.Platform;

namespace MAAUnified.Tests;

internal static class AvaloniaTestApplication
{
    public static void Ensure()
    {
        if (global::Avalonia.Application.Current is not null)
        {
            return;
        }

        RegisterCursorFactory();
        RegisterAssetLoader();

        var app = new MAAUnified.App.App();
        app.Initialize();
    }

    private static void RegisterCursorFactory()
    {
        // Cursor values in app XAML need a platform factory, but these tests only load and measure controls.
        var registry = GetLocatorRegistry();
        registry[typeof(ICursorFactory)] = () => NoOpCursorFactoryProxy.Create();
    }

    private static void RegisterAssetLoader()
    {
        // Without an AppBuilder setup the locator has no runtime IAssetLoader,
        // and avares:// icon loads (e.g. the tray brand icon) fail even though
        // the App assembly is loaded.
        var registry = GetLocatorRegistry();
        registry[typeof(IAssetLoader)] = () => new StandardAssetLoader();
    }

    private static IDictionary<Type, Func<object>> GetLocatorRegistry()
    {
        // Avalonia 11.3 exposes no public mutation entry point for the locator,
        // so both registrations share this reflection path; if an Avalonia
        // upgrade renames these members, fail here with one clear message
        // instead of breaking cursor/asset loading in obscure ways.
        var locatorType = typeof(global::Avalonia.AvaloniaLocator);
        if (locatorType.GetProperty(
                "CurrentMutable",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?
                .GetValue(null) is not { } locator
            || locatorType.GetField(
                "_registry",
                BindingFlags.NonPublic | BindingFlags.Instance)?
                .GetValue(locator) is not IDictionary<Type, Func<object>> registry)
        {
            throw new InvalidOperationException(
                "Unable to access the Avalonia locator registry; the Avalonia version in use likely changed its internals. Update the test locator registration helpers accordingly.");
        }

        return registry;
    }

    private class NoOpCursorFactoryProxy : DispatchProxy
    {
        private static readonly ICursorImpl Cursor = NoOpCursorImplProxy.Create();

        public static ICursorFactory Create()
        {
            return Create<ICursorFactory, NoOpCursorFactoryProxy>();
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return Cursor;
        }
    }

    private class NoOpCursorImplProxy : DispatchProxy
    {
        public static ICursorImpl Create()
        {
            return Create<ICursorImpl, NoOpCursorImplProxy>();
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return null;
        }
    }
}
