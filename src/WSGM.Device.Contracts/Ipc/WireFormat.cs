using System;
using System.Buffers.Binary;

namespace WSGM.Device.Contracts.Ipc;

/// <summary>
/// The fixed frame header carried by every device-protocol message.
/// </summary>
/// <remarks>
/// Fixed width and little-endian so decoding costs a bounds check and four reads, with no allocation
/// and no reflection — this runs inside the NativeAOT WSGM process on every message.
/// <para>
/// Layout, 16 bytes: payload length (4), protocol version (2), message type (2), request id (4),
/// flags (4).
/// </para>
/// </remarks>
public readonly record struct FrameHeader
{
    /// <summary>Size of the header in bytes.</summary>
    public const int Size = 16;

    /// <summary>
    /// Largest payload a single frame may carry.
    /// </summary>
    /// <remarks>
    /// The length prefix is read from an untrusted peer and used to size a read, so it is bounded
    /// before it is believed. Without this an attacker-controlled length is an allocation of their
    /// choosing.
    /// </remarks>
    public const int MaxPayloadBytes = 1024 * 1024;

    /// <summary>Payload length in bytes, excluding the header.</summary>
    public required int PayloadLength { get; init; }

    /// <summary>Protocol version this frame is encoded under.</summary>
    public required ushort ProtocolVersion { get; init; }

    /// <summary>What the frame carries.</summary>
    public required DeviceMessageType MessageType { get; init; }

    /// <summary>
    /// Correlates a response with its request. Zero for a notification.
    /// </summary>
    public required uint RequestId { get; init; }

    /// <summary>Frame flags.</summary>
    public FrameFlags Flags { get; init; }

    /// <summary>
    /// Writes the header into a buffer.
    /// </summary>
    /// <param name="destination">Buffer of at least <see cref="Size"/> bytes.</param>
    /// <exception cref="ArgumentException">The buffer is too small.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"Header needs {Size} bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteInt32LittleEndian(destination, PayloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], (ushort)MessageType);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], RequestId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], (uint)Flags);
    }

    /// <summary>
    /// Reads a header from a buffer, validating it before it is believed.
    /// </summary>
    /// <param name="source">Buffer holding at least <see cref="Size"/> bytes.</param>
    /// <param name="header">The decoded header, when the result is <see cref="FrameError.None"/>.</param>
    /// <returns>Why the header was rejected, or <see cref="FrameError.None"/>.</returns>
    /// <remarks>
    /// Returns an error rather than throwing: a malformed frame from a peer is an expected condition
    /// on an untrusted boundary, not an exceptional one.
    /// </remarks>
    public static FrameError TryRead(ReadOnlySpan<byte> source, out FrameHeader header)
    {
        header = default;

        if (source.Length < Size)
        {
            return FrameError.Truncated;
        }

        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(source);

        // Negative is checked separately from oversized: a negative length is a sign-extension or
        // hostile value rather than a peer that simply sent too much, and conflating them would hide
        // that difference in diagnostics.
        if (payloadLength < 0)
        {
            return FrameError.NegativeLength;
        }

        if (payloadLength > MaxPayloadBytes)
        {
            return FrameError.PayloadTooLarge;
        }

        ushort messageType = BinaryPrimitives.ReadUInt16LittleEndian(source[6..]);
        if (messageType == (ushort)DeviceMessageType.None)
        {
            return FrameError.MalformedMessageType;
        }

        header = new FrameHeader
        {
            PayloadLength = payloadLength,
            ProtocolVersion = BinaryPrimitives.ReadUInt16LittleEndian(source[4..]),
            MessageType = (DeviceMessageType)messageType,
            RequestId = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]),
            Flags = (FrameFlags)BinaryPrimitives.ReadUInt32LittleEndian(source[12..]),
        };

        return FrameError.None;
    }
}

/// <summary>Per-frame flags.</summary>
[Flags]
public enum FrameFlags : uint
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>This frame answers a request.</summary>
    IsResponse = 1 << 0,

    /// <summary>More frames follow for the same request.</summary>
    Continuation = 1 << 1,

    /// <summary>The sender is applying backpressure and cannot accept more work.</summary>
    Backpressure = 1 << 2,
}

/// <summary>Why a frame was rejected.</summary>
public enum FrameError
{
    /// <summary>The frame is well-formed.</summary>
    None,

    /// <summary>Fewer bytes than a header.</summary>
    Truncated,

    /// <summary>The length prefix was negative.</summary>
    NegativeLength,

    /// <summary>The length prefix exceeded <see cref="FrameHeader.MaxPayloadBytes"/>.</summary>
    PayloadTooLarge,

    /// <summary>The message type was the reserved zero value.</summary>
    MalformedMessageType,
}

/// <summary>
/// How a malformed or unrecognised frame is handled.
/// </summary>
public static class FrameHandling
{
    /// <summary>
    /// What to do about a frame error.
    /// </summary>
    /// <param name="error">The error found while decoding.</param>
    /// <returns>How the connection should respond.</returns>
    /// <remarks>
    /// Every framing error disconnects, without exception. A length prefix that cannot be trusted
    /// means the position of the next frame is unknown, so continuing would resynchronize on
    /// attacker-chosen boundaries — reading payload bytes as headers.
    /// </remarks>
    public static UnknownMessageResponse ForFrameError(FrameError error) => error switch
    {
        FrameError.None => UnknownMessageResponse.Ignore,
        _ => UnknownMessageResponse.Disconnect,
    };

    /// <summary>
    /// What to do about a message type this build does not know.
    /// </summary>
    /// <param name="isRequest">Whether the peer expects an answer.</param>
    /// <returns>How the connection should respond.</returns>
    /// <remarks>
    /// Survivable, unlike a framing error: the frame's length is trustworthy, so it can be skipped
    /// cleanly. An unknown request is answered with an error so the peer is not left waiting, and an
    /// unknown notification is ignored — which is exactly what lets a newer peer send messages an
    /// older one has never heard of.
    /// </remarks>
    public static UnknownMessageResponse ForUnknownMessage(bool isRequest) => isRequest
        ? UnknownMessageResponse.ReplyWithError
        : UnknownMessageResponse.Ignore;
}
