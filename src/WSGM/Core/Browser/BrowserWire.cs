using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Browser;

// The only IPC contract. The helper cannot request host actions, access configuration,
// read files, launch programs or obtain a Steam/plugin bridge. All text is untrusted data.
internal sealed record BrowserPacket
{
    public string Kind { get; init; } = "";
    public string Text { get; init; } = "";
    public long Handle { get; init; }
    public int ProcessId { get; init; }
    public bool Visible { get; init; }
    public bool Loading { get; init; }
    public bool CanGoBack { get; init; }
    public bool CanGoForward { get; init; }
    public double Zoom { get; init; } = 1;
    public ulong NavigationId { get; init; }
}

[JsonSerializable(typeof(BrowserPacket))]
internal partial class BrowserWireJson : JsonSerializerContext;

internal static class BrowserWire
{
    internal const int MaximumPacketBytes = 32768;

    internal static async Task WriteAsync(Stream stream, BrowserPacket packet, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet, BrowserWireJson.Default.BrowserPacket);
        if (bytes.Length > MaximumPacketBytes)
        {
            throw new InvalidDataException("Browser message exceeds the protocol limit.");
        }

        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<BrowserPacket> ReadAsync(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size is <= 0 or > MaximumPacketBytes)
        {
            throw new InvalidDataException("Invalid browser message size.");
        }

        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, BrowserWireJson.Default.BrowserPacket)
               ?? throw new InvalidDataException("Invalid browser message.");
    }
}
