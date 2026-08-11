using WSGM.Interop;

namespace WSGM.Tests;

/// <summary>Covers the MIB_TCPTABLE_OWNER_PID decode from a synthetic buffer, so
/// the listener-ownership check stays correct without a live socket. The decoder
/// replaced a netstat text parse that matched the literal "LISTENING" and
/// therefore failed closed on every localized Windows.</summary>
public class NativeTcpTests
{
    private static void WriteRow(
        byte[] buffer, int index, uint addressBigEndian, int port, int pid)
    {
        var start = 4 + (index * NativeTcp.RowSize);
        BitConverter.GetBytes(2).CopyTo(buffer, start);                    // dwState
        BitConverter.GetBytes(addressBigEndian).CopyTo(buffer, start + 4); // dwLocalAddr
        // dwLocalPort holds the port in network byte order in its low two bytes.
        buffer[start + 8] = (byte)((port >> 8) & 0xFF);
        buffer[start + 9] = (byte)(port & 0xFF);
        BitConverter.GetBytes(pid).CopyTo(buffer, start + 20);             // dwOwningPid
    }

    [Fact]
    public void ListenerRowsDecodeTheFixedLayout()
    {
        var buffer = new byte[4 + NativeTcp.RowSize];
        BitConverter.GetBytes(1).CopyTo(buffer, 0);
        // 127.0.0.1 as stored by Windows: network byte order, so 0x7F,0x00,0x00,0x01.
        WriteRow(buffer, 0, 0x0100007F, 8080, 4321);

        var listeners = NativeTcp.DecodeTable(buffer);

        var listener = Assert.Single(listeners);
        Assert.Equal(NativeTcp.Loopback, listener.LocalAddress);
        Assert.Equal(8080, listener.Port);
        Assert.Equal(4321, listener.ProcessId);
    }

    [Fact]
    public void WildcardAndLoopbackRowsAreBothDecoded()
    {
        var buffer = new byte[4 + (2 * NativeTcp.RowSize)];
        BitConverter.GetBytes(2).CopyTo(buffer, 0);
        WriteRow(buffer, 0, 0x00000000, 8080, 10);
        WriteRow(buffer, 1, 0x0100007F, 8080, 20);

        var listeners = NativeTcp.DecodeTable(buffer);

        Assert.Equal(2, listeners.Count);
        Assert.Equal(NativeTcp.AnyAddress, listeners[0].LocalAddress);
        Assert.Equal(NativeTcp.Loopback, listeners[1].LocalAddress);
    }

    [Fact]
    public void TruncatedTableStopsAtTheLastCompleteRow()
    {
        // Claims three rows but only carries one — a short read must not throw.
        var buffer = new byte[4 + NativeTcp.RowSize];
        BitConverter.GetBytes(3).CopyTo(buffer, 0);
        WriteRow(buffer, 0, 0x0100007F, 8080, 7);

        Assert.Single(NativeTcp.DecodeTable(buffer));
    }

    [Fact]
    public void EmptyBufferDecodesToNoListeners()
        => Assert.Empty(NativeTcp.DecodeTable(Array.Empty<byte>()));
}
