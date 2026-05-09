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

        var app = new MAAUnified.App.App();
        app.Initialize();
    }

    private static void RegisterCursorFactory()
    {
        // Cursor values in app XAML need a platform factory, but these tests only load and measure controls.
        var locatorType = typeof(global::Avalonia.AvaloniaLocator);
        var currentMutable = locatorType.GetProperty(
            "CurrentMutable",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var locator = currentMutable?.GetValue(null);
        if (locator is null)
        {
            return;
        }

        var registryField = locatorType.GetField(
            "_registry",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var registry = registryField?.GetValue(locator) as IDictionary<Type, Func<object>>;
        if (registry is null)
        {
            throw new InvalidOperationException("Unable to register Avalonia test cursor factory.");
        }

        registry[typeof(ICursorFactory)] = () => NoOpCursorFactoryProxy.Create();
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
