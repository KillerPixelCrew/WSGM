using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WSGM.Interop;

/// <summary>
///     The native half of Notes capture: a Direct3D device for Windows Graphics Capture, a capture item for
///     one window or monitor, and Windows 11's borderless-capture switch. No product decisions live here.
/// </summary>
internal static unsafe partial class NotesCaptureInterop
{
    private const int DriverTypeHardware = 1;
    private const uint CreateDeviceBgraSupport = 0x20;
    private const uint SdkVersion = 7;
    private const uint CoinitMultithreaded = 0;

    private static readonly Guid DxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid GraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid GraphicsCaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    // IGraphicsCaptureSession3 (Windows 10 2104 and later). Its IsBorderRequired is not in the SDK projection
    // WSGM targets, so it is reached through its interface directly.
    private static readonly Guid GraphicsCaptureSession3 = new("f2cdd966-22ae-5ea1-9596-3a289344c3be");

    /// <summary>Joins the multithreaded apartment; WinRT activation needs one on the calling thread.</summary>
    internal static void EnsureMultithreadedApartment()
    {
        // S_OK, S_FALSE (already joined) and RPC_E_CHANGED_MODE (a UI thread) are all usable outcomes here:
        // the caller runs on the thread pool, which is never an STA.
        _ = CoInitializeEx(0, CoinitMultithreaded);
    }

    /// <summary>Creates a hardware Direct3D 11 device wrapped for Windows Graphics Capture.</summary>
    /// <returns>The WinRT device; dispose it after capture.</returns>
    /// <exception cref="COMException">No hardware device could be created.</exception>
    internal static IDirect3DDevice CreateDevice()
    {
        nint device = 0, context = 0, dxgi = 0, inspectable = 0;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, DriverTypeHardware, 0, CreateDeviceBgraSupport, 0, 0,
                SdkVersion, out device, out _, out context));
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in DxgiDevice, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Release(inspectable);
            Release(dxgi);
            Release(context);
            Release(device);
        }
    }

    /// <summary>Creates a capture item for one top-level window or desktop display.</summary>
    /// <param name="handle">The window or monitor handle.</param>
    /// <param name="monitor">Whether the handle identifies a monitor.</param>
    /// <returns>The item for exactly that window or display.</returns>
    /// <exception cref="COMException">The target cannot be captured.</exception>
    internal static GraphicsCaptureItem CreateItem(nint handle, bool monitor = false)
    {
        nint name = 0, factory = 0, item = 0;
        try
        {
            const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Marshal.ThrowExceptionForHR(WindowsCreateString(className, (uint)className.Length, out name));
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, in GraphicsCaptureItemInterop, out factory));
            var vtable = *(nint**)factory;
            // IGraphicsCaptureItemInterop follows IUnknown with CreateForWindow, then CreateForMonitor.
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtable[monitor ? 4 : 3];
            var iid = GraphicsCaptureItemId;
            Marshal.ThrowExceptionForHR(create(factory, handle, &iid, &item));
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally
        {
            Release(item);
            Release(factory);
            if (name != 0)
            {
                _ = WindowsDeleteString(name);
            }
        }
    }

    /// <summary>Asks Windows not to draw its capture border, where the system supports it.</summary>
    /// <param name="session">The capture session.</param>
    /// <returns>Whether the request was accepted; older systems draw the border while capture runs.</returns>
    internal static bool TryHideBorder(GraphicsCaptureSession session)
    {
        nint session3 = 0;
        try
        {
            var pointer = ((IWinRTObject)session).NativeObject.ThisPtr;
            if (Marshal.QueryInterface(pointer, in GraphicsCaptureSession3, out session3) < 0)
            {
                return false;
            }

            // IInspectable occupies six slots; put_IsBorderRequired follows get_IsBorderRequired.
            var vtable = *(nint**)session3;
            var put = (delegate* unmanaged[Stdcall]<nint, byte, int>)vtable[7];
            return put(session3, 0) >= 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            Release(session3);
        }
    }

    private static void Release(nint pointer)
    {
        if (pointer != 0)
        {
            Marshal.Release(pointer);
        }
    }

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel,
        out nint immediateContext);

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string source, uint length, out nint value);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint value);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);
}
