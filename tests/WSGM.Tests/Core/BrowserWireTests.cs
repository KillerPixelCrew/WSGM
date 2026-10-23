using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using WSGM.Browser;

namespace WSGM.Tests.Core;

public sealed class BrowserWireTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(BrowserWire.MaximumPacketBytes + 1)]
    [InlineData(int.MaxValue)]
    public async Task UntrustedLengthIsRejectedBeforeAnyBodyRead(int size)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, size);
        using var input = new CountedStream(prefix);

        await Assert.ThrowsAsync<InvalidDataException>(() => BrowserWire.ReadAsync(input, CancellationToken.None));
        Assert.Equal(4, input.ReadBytes);
    }

    [Fact]
    public async Task InterruptedFrameCannotBecomeAPartialCommand()
    {
        using var input = new MemoryStream(Frame("{\"Kind\":\"navigate\"}"));
        input.SetLength(input.Length - 1);

        await Assert.ThrowsAsync<EndOfStreamException>(() => BrowserWire.ReadAsync(input, CancellationToken.None));
    }

    [Fact]
    public async Task MalformedJsonCannotReachCommandRouting()
    {
        using var input = new MemoryStream(Frame("{invalid}"));
        await Assert.ThrowsAsync<JsonException>(() => BrowserWire.ReadAsync(input, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedOutboundTextWritesNoPartialHeader()
    {
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => BrowserWire.WriteAsync(output,
            new BrowserPacket { Kind = "text", Text = new string('x', BrowserWire.MaximumPacketBytes) }, CancellationToken.None));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task NavigationIdentityAndUnicodeSurviveTheBoundary()
    {
        var packet = new BrowserPacket { Kind = "address", Text = "https://example.com/攻略", NavigationId = ulong.MaxValue };
        using var stream = new MemoryStream();
        await BrowserWire.WriteAsync(stream, packet, CancellationToken.None);
        stream.Position = 0;

        Assert.Equal(packet, await BrowserWire.ReadAsync(stream, CancellationToken.None));
    }

    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    private sealed class CountedStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int ReadBytes { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            ReadBytes += count;
            return count;
        }
    }
}
