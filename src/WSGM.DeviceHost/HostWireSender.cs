using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Contracts.Ipc;

namespace WSGM.DeviceHost;

/// <summary>Serializes only source-generated, closed semantic payloads.</summary>
internal sealed class HostWireSender
{
    private readonly DeviceFrameStream _stream;

    public HostWireSender(DeviceFrameStream stream)
    {
        _stream = stream;
    }

    public ValueTask SendAsync<T>(
        DeviceMessageType messageType,
        uint requestId,
        FrameFlags flags,
        T payload,
        JsonTypeInfo<T> typeInfo,
        ushort protocolVersion,
        CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);
        return _stream.WriteAsync(new FrameHeader
        {
            PayloadLength = bytes.Length,
            ProtocolVersion = protocolVersion,
            MessageType = messageType,
            RequestId = requestId,
            Flags = flags,
        }, bytes, cancellationToken);
    }
}
