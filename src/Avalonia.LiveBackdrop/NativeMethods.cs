using System.Runtime.InteropServices;

namespace Avalonia.LiveBackdrop;

internal static class NativeMethods
{
    private const string Library = "Avalonia.LiveBackdrop.Native";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int BackdropCreate(nint owner, float sigma, FailureCallback callback, out nint session);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int BackdropSetBlur(nint session, float sigma);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void BackdropDestroy(nint session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FailureCallback(int result);
}
