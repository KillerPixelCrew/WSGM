using System;
using Avalonia.Controls;
using Avalonia.Platform;
using WSGM.Interop;

namespace WSGM.Overlay;

/// <summary>Shared clipped native anchor for media and the isolated arbitrary-web viewport.</summary>
internal abstract class OverlayNativeWebViewHost : NativeControlHost
{
    protected nint NativeHandle { get; private set; }
    protected virtual bool AcceptsInput => false;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (parent.HandleDescriptor != "HWND")
        {
            return base.CreateNativeControlCore(parent);
        }

        var style = AcceptsInput ? 0x46000000u : 0x4e000000u;
        var handle = NativeMethods.CreateWindowExW(0, "STATIC", "WSGM WebView viewport", style,
            0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (handle == 0)
        {
            throw new InvalidOperationException("The WebView viewport could not be created.");
        }

        NativeHandle = handle;
        OnNativeCreated(handle);
        return new PlatformHandle(handle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try
        {
            OnNativeDestroying(control.Handle);
        }
        finally
        {
            NativeHandle = 0;
            if (control.HandleDescriptor == "HWND")
            {
                NativeMethods.DestroyWindow(control.Handle);
            }
            else
            {
                base.DestroyNativeControlCore(control);
            }
        }
    }

    protected abstract void OnNativeCreated(nint handle);
    protected abstract void OnNativeDestroying(nint handle);
}
