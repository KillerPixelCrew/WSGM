using WSGM.Core;
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

/// <summary>Covers the two anti-squatter gates in front of Steam's CEF port. The port
/// is unauthenticated by design (Steam's CEF has no auth) and loopback-only, so these
/// checks are what stops another same-user listener from being driven as if it were
/// Steam, or redirecting the CDP client off-box through a spoofed target list.</summary>
public class SteamCefGateTests
{
    private const int DebugPort = 8080;

    [Theory]
    [InlineData("ws://127.0.0.1:8080/devtools/page/A")]
    [InlineData("ws://localhost:8080/devtools/page/A")]
    [InlineData("wss://127.0.0.1:8080/devtools/page/A")]
    public void LoopbackWebSocketUrlsOnTheDebugPortAreAccepted(string url)
        => Assert.True(SteamCef.IsAllowedDebuggerUrl(url));

    [Theory]
    [InlineData("ws://10.0.0.5:8080/devtools/page/A")]     // off-box host
    [InlineData("ws://evil.example:8080/devtools/page/A")] // named foreign host
    [InlineData("ws://127.0.0.1:9222/devtools/page/A")]    // foreign port
    [InlineData("http://127.0.0.1:8080/devtools/page/A")]  // non-WebSocket scheme
    [InlineData("file:///C:/windows/system32/cmd.exe")]
    [InlineData("not a url at all")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingOtherThanALoopbackWebSocketOnTheDebugPortIsRejected(string? url)
        => Assert.False(SteamCef.IsAllowedDebuggerUrl(url));

    [Fact]
    public void AnUnreadableListenerTableIsNotTreatedAsNothingListening()
    {
        Assert.False(SteamCef.IsSteamPortOwner(null, static _ => "steam", out string reason));
        Assert.Contains("unavailable", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NoListenerOnTheDebugPortIsNotSteam()
    {
        Assert.False(SteamCef.IsSteamPortOwner(
            [new NativeTcp.Listener(NativeTcp.Loopback, 1234, 42)],
            static _ => "steam",
            out string reason));
        Assert.Contains("nothing is listening", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("steam")]
    [InlineData("steamwebhelper")]
    public void ASteamProcessOwningTheDebugPortIsAccepted(string name)
    {
        Assert.True(SteamCef.IsSteamPortOwner(
            [new NativeTcp.Listener(NativeTcp.Loopback, DebugPort, 42)],
            _ => name,
            out string reason));
        Assert.Contains(name, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AForeignProcessOwningTheDebugPortIsRejected()
    {
        Assert.False(SteamCef.IsSteamPortOwner(
            [new NativeTcp.Listener(NativeTcp.Loopback, DebugPort, 42)],
            static _ => "squatter",
            out string reason));
        Assert.Contains("squatter", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AListenerWhoseProcessHasExitedDoesNotDecideTheVerdict()
    {
        Assert.False(SteamCef.IsSteamPortOwner(
            [new NativeTcp.Listener(NativeTcp.Loopback, DebugPort, 42)],
            static _ => null,
            out string reason));
        Assert.Contains("could not be attributed", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The four ways the probe can fail must not describe each other. The caller logs this string
    /// verbatim, and when it hardcoded one cause for all of them the log reported a squatter on a
    /// port that nothing was listening on — sending the maintainer after a process that did not
    /// exist.
    /// </summary>
    [Fact]
    public void EachRefusalReasonNamesItsOwnCause()
    {
        NativeTcp.Listener[] onPort = [new NativeTcp.Listener(NativeTcp.Loopback, DebugPort, 42)];
        SteamCef.IsSteamPortOwner(null, static _ => "steam", out string unreadable);
        SteamCef.IsSteamPortOwner(
            [new NativeTcp.Listener(NativeTcp.Loopback, 1234, 42)],
            static _ => "steam",
            out string absent);
        SteamCef.IsSteamPortOwner(onPort, static _ => "squatter", out string foreign);
        SteamCef.IsSteamPortOwner(onPort, static _ => null, out string unidentified);

        string[] reasons = [unreadable, absent, foreign, unidentified];
        Assert.Equal(reasons.Length, reasons.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(reasons, r => string.IsNullOrWhiteSpace(r));
    }

    /// A squatter on 127.0.0.1 must not be able to hide behind Steam's own wildcard
    /// row: the loopback listener is examined first, so it — not Steam — decides.
    [Fact]
    public void ALoopbackSquatterIsCheckedBeforeSteamsWildcardListener()
        => Assert.False(SteamCef.IsSteamPortOwner(
            [
                new NativeTcp.Listener(NativeTcp.AnyAddress, DebugPort, 1),
                new NativeTcp.Listener(NativeTcp.Loopback, DebugPort, 2),
            ],
            static pid => pid == 1 ? "steam" : "squatter",
            out _));
}
