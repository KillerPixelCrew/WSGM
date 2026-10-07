using System.Runtime.InteropServices;

namespace Avalonia.LiveBackdrop;

/// <summary>Cdecl ABI to the Windows 11 x64 compositor backend; session handles stay on their creating UI thread.</summary>
internal static class NativeMethods
{
    private const string Library = "Avalonia.LiveBackdrop.Native";

    /// <summary>Creates one native backdrop; partially acquired resources are released before a failed return.</summary>
    /// <param name="owner">Borrowed HWND owned by the calling thread and current process.</param>
    /// <param name="sigma">Finite Gaussian deviation in physical pixels, from zero through 60.</param>
    /// <param name="callback">Root until destruction; it must not throw or synchronously destroy the session.</param>
    /// <param name="session">Owned handle on success, otherwise zero; release with <see cref="BackdropDestroy" />.</param>
    /// <returns>An HRESULT; negative values indicate failure.</returns>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int BackdropCreate(nint owner, float sigma, FailureCallback callback, out nint session);

    /// <summary>Commits a blur change on the session's creating thread.</summary>
    /// <param name="session">Live handle returned by <see cref="BackdropCreate" />.</param>
    /// <param name="sigma">Finite Gaussian deviation in physical pixels, from zero through 60.</param>
    /// <returns>An HRESULT; a failed compositor update also notifies the failure callback.</returns>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int BackdropSetBlur(nint session, float sigma);

    /// <summary>Removes hooks and releases the session on its creating thread; zero is harmless.</summary>
    /// <param name="session">Owned handle to consume exactly once; it must not be used after this call.</param>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void BackdropDestroy(nint session);

    /// <summary>Reports the first asynchronous native session failure on the creating thread.</summary>
    /// <param name="result">Failed HRESULT. Post cleanup rather than destroying native state inside this callback.</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FailureCallback(int result);
}
