using System.Runtime.InteropServices;

namespace MAAUnified.Platform;

public static partial class MacApplicationActivationPolicy
{
    private const int NSApplicationActivationPolicyRegular = 0;
    private const int NSApplicationActivationPolicyAccessory = 1;
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";

    public static bool TryShowInDock()
        => TrySetActivationPolicy(NSApplicationActivationPolicyRegular);

    public static bool TryHideFromDock()
        => TrySetActivationPolicy(NSApplicationActivationPolicyAccessory);

    private static bool TrySetActivationPolicy(int activationPolicy)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            var nsApplicationClass = NativeMethods.objc_getClass("NSApplication");
            if (nsApplicationClass == nint.Zero)
            {
                return false;
            }

            var sharedApplication = NativeMethods.IntPtr_objc_msgSend(
                nsApplicationClass,
                Selectors.SharedApplication);
            if (sharedApplication == nint.Zero)
            {
                return false;
            }

            return NativeMethods.bool_objc_msgSend_nint(
                sharedApplication,
                Selectors.SetActivationPolicy,
                (nint)activationPolicy);
        }
        catch
        {
            return false;
        }
    }

    private static class Selectors
    {
        public static readonly nint SetActivationPolicy = NativeMethods.sel_registerName("setActivationPolicy:");
        public static readonly nint SharedApplication = NativeMethods.sel_registerName("sharedApplication");
    }

    private static partial class NativeMethods
    {
        [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
        public static partial nint objc_getClass(string name);

        [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
        public static partial nint sel_registerName(string name);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        public static extern nint IntPtr_objc_msgSend(nint receiver, nint selector);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool bool_objc_msgSend_nint(nint receiver, nint selector, nint value);
    }
}
