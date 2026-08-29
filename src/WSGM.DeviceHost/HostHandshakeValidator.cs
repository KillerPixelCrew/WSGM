using System;
using WSGM.Device.Sdk.Ipc;

namespace WSGM.DeviceHost;

/// <summary>Validates that a handshake acknowledgment answers this exact host launch.</summary>
internal static class HostHandshakeValidator
{
    public static bool IsExpectedAckEnvelope(
        FrameHeader header,
        uint expectedRequestId,
        out string detail)
    {
        if (header.MessageType is not DeviceMessageType.HelloAck
            || header.RequestId != expectedRequestId
            || header.Flags != FrameFlags.IsResponse)
        {
            detail = "The first coordinator frame was not the exact HelloAck envelope.";
            return false;
        }

        detail = string.Empty;
        return true;
    }

    public static bool TryValidateAck(
        FrameHeader header,
        DeviceHostHelloAck acknowledgment,
        uint expectedRequestId,
        string expectedPackageId,
        out string detail)
    {
        ArgumentNullException.ThrowIfNull(acknowledgment);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPackageId);
        if (!IsExpectedAckEnvelope(header, expectedRequestId, out detail))
        {
            return false;
        }

        if (!acknowledgment.Accepted
            || header.ProtocolVersion != DeviceProtocol.Version
            || !string.Equals(
                acknowledgment.PackageId,
                expectedPackageId,
                StringComparison.Ordinal))
        {
            detail = "The coordinator refused or confused the DeviceHost handshake.";
            return false;
        }

        detail = string.Empty;
        return true;
    }
}
