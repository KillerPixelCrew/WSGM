using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>Flat iphlpapi interop for "which process owns this listening port".
///
/// This exists because parsing <c>netstat</c> output cannot answer that question
/// reliably: netstat's STATE column is localized (German Windows prints
/// <c>ABHÖREN</c>, not <c>LISTENING</c>), so any literal match fails closed on a
/// non-English machine. GetExtendedTcpTable returns the same data as a fixed
/// binary layout with no text, no locale and no child process.
///
/// The row layout is decoded from documented offsets by a pure span reader, so it
/// is unit-testable from a synthetic buffer without a live socket.</summary>
internal static partial class NativeTcp
{
    /// <summary>AF_INET.</summary>
    private const uint AfInet = 2;

    /// <summary>TCP_TABLE_OWNER_PID_LISTENER — listeners only, with owning PID.</summary>
    private const uint TcpTableOwnerPidListener = 3;

    private const uint ErrorInsufficientBuffer = 122;

    /// <summary>sizeof(MIB_TCPROW_OWNER_PID): state, localAddr, localPort,
    /// remoteAddr, remotePort, owningPid — six DWORDs.</summary>
    internal const int RowSize = 24;

    private const int OffsetLocalAddr = 4;
    private const int OffsetLocalPort = 8;
    private const int OffsetOwningPid = 20;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref uint pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        uint ulAf, uint tableClass, uint reserved);

    /// <summary>One IPv4 TCP listener and the process that owns it.</summary>
    /// <param name="LocalAddress">Local bind address in host byte order.</param>
    /// <param name="Port">Local port in host byte order.</param>
    /// <param name="ProcessId">Owning process id.</param>
    internal readonly record struct Listener(uint LocalAddress, int Port, int ProcessId);

    /// <summary>127.0.0.1 in host byte order.</summary>
    internal const uint Loopback = 0x7F000001;

    /// <summary>0.0.0.0 in host byte order.</summary>
    internal const uint AnyAddress = 0;

    /// <summary>Decodes an MIB_TCPTABLE_OWNER_PID buffer: a DWORD count followed
    /// by that many fixed-size rows. Exposed for tests.</summary>
    /// <param name="buffer">The raw table bytes.</param>
    internal static List<Listener> DecodeTable(ReadOnlySpan<byte> buffer)
    {
        var listeners = new List<Listener>();
        if (buffer.Length < 4)
        {
            return listeners;
        }
        var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        for (var i = 0; i < count; i++)
        {
            var start = 4 + (i * RowSize);
            if (start + RowSize > buffer.Length)
            {
                break;
            }
            var row = buffer.Slice(start, RowSize);
            // dwLocalAddr and dwLocalPort are both network byte order; the port
            // occupies the low two bytes of its DWORD.
            var address = BinaryPrimitives.ReadUInt32BigEndian(row.Slice(OffsetLocalAddr, 4));
            var port = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(OffsetLocalPort, 2));
            var pid = BinaryPrimitives.ReadInt32LittleEndian(row.Slice(OffsetOwningPid, 4));
            listeners.Add(new Listener(address, port, pid));
        }
        return listeners;
    }

    /// <summary>How often the size+read pair is retried when a concurrent socket
    /// change invalidates the size measured by the first call.</summary>
    private const int ReadAttempts = 3;

    /// <summary>Enumerates IPv4 TCP listeners with their owning process ids. An empty
    /// list means the table was read and holds no listeners; <c>null</c> means the
    /// table could not be read at all, which callers must not report as "nothing is
    /// listening".</summary>
    internal static List<Listener>? ListListeners()
    {
        // Classic two-call pattern: a socket opened or closed between the sizing call
        // and the data call makes the second one fail with ERROR_INSUFFICIENT_BUFFER
        // and hand back the new requirement, so retry the pair a bounded number of
        // times before declaring the table unreadable.
        for (var attempt = 0; attempt < ReadAttempts; attempt++)
        {
            uint size = 0;
            var status = GetExtendedTcpTable(
                IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
            if (status == 0)
            {
                return [];
            }
            if (status != ErrorInsufficientBuffer || size == 0)
            {
                return null;
            }
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = GetExtendedTcpTable(
                    buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                if (status == 0)
                {
                    unsafe
                    {
                        return DecodeTable(new ReadOnlySpan<byte>((void*)buffer, (int)size));
                    }
                }
                if (status != ErrorInsufficientBuffer)
                {
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }
}
