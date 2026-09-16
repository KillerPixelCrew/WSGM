// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WSGM.Device.Asus.RogAllyX;

internal interface IAsusReader : IDisposable
{
    byte[] Read(AsusReadControl control, int profile, CancellationToken cancellationToken);
}

/// <summary>Serialized DSTS-only transport. Not activated by the production plugin yet.</summary>
/// <remarks>
///     Invoke from a bounded diagnostic worker, never the UI thread. Cancellation cannot undo
///     or interrupt a synchronous driver call. No DEVS, INIT, watchdog or arbitrary IOCTL is exposed.
/// </remarks>
internal sealed partial class WindowsAsusReader : IAsusReader
{
    private const uint GenericReadWrite = 0xC0000000;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;

    private readonly Lock _gate = new();
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    private WindowsAsusReader(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public byte[] Read(AsusReadControl control, int profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new byte[AsusReadProtocol.QueryLength];
        if (!AsusReadProtocol.TryWriteQuery(request, control, profile))
        {
            throw new ArgumentOutOfRangeException(nameof(control));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var response = new byte[AsusReadProtocol.ResponseLength];
            if (!DeviceIoControl(
                    _handle,
                    AsusReadProtocol.Ioctl,
                    request,
                    (uint)request.Length,
                    response,
                    (uint)response.Length,
                    out var returned,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            cancellationToken.ThrowIfCancellationRequested();
            var minimum = AsusReadProtocol.IsCurve(control) ? AsusReadProtocol.QueryLength : 4;
            if (returned < minimum || returned > response.Length)
            {
                throw new IOException("ASUS returned a truncated or oversized status response.");
            }

            return response.AsSpan(0, (int)returned).ToArray();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _handle.Dispose();
        }
    }

    internal static WindowsAsusReader Open()
    {
        // ATKACPI requires a read/write handle even though this type only issues status queries.
        var handle = CreateFile(@"\\.\ATKACPI", GenericReadWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return new WindowsAsusReader(handle);
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new Win32Exception(error, "ASUS System Control Interface is unavailable.");
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string path,
        uint access,
        uint share,
        IntPtr security,
        uint creation,
        uint flags,
        IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle file,
        uint ioctl,
        byte[] input,
        uint inputLength,
        byte[] output,
        uint outputLength,
        out uint returned,
        IntPtr overlapped);
}
