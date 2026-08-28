using WSGM.Device.Contracts.Ipc;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of the IPC boundary: what a hostile peer can and cannot make this
/// side do.
/// </summary>
public class WireFormatTests
{
    [Fact]
    public void AHeader_RoundTrips()
    {
        FrameHeader original = new()
        {
            PayloadLength = 128,
            ProtocolVersion = 1,
            MessageType = DeviceMessageType.StateDelta,
            RequestId = 42,
            Flags = FrameFlags.IsResponse,
        };

        Span<byte> buffer = stackalloc byte[FrameHeader.Size];
        original.WriteTo(buffer);

        Assert.Equal(FrameError.None, FrameHeader.TryRead(buffer, out FrameHeader decoded));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void ANegativeLengthPrefix_IsRejected()
    {
        // Sign-extension or a hostile value. Reported separately from "too large" so diagnostics keep
        // the difference.
        Span<byte> buffer = stackalloc byte[FrameHeader.Size];
        Header(1).WriteTo(buffer);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer, -1);

        Assert.Equal(FrameError.NegativeLength, FrameHeader.TryRead(buffer, out _));
    }

    [Fact]
    public void AnOversizedLengthPrefix_IsRejectedBeforeAnythingIsAllocated()
    {
        // The prefix comes from an untrusted peer and would otherwise size a read of their choosing.
        Span<byte> buffer = stackalloc byte[FrameHeader.Size];
        Header(1).WriteTo(buffer);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            buffer, FrameHeader.MaxPayloadBytes + 1);

        Assert.Equal(FrameError.PayloadTooLarge, FrameHeader.TryRead(buffer, out _));
    }

    [Fact]
    public void ATruncatedHeader_IsRejected()
    {
        Assert.Equal(FrameError.Truncated,
            FrameHeader.TryRead(new byte[FrameHeader.Size - 1], out _));
    }

    [Fact]
    public void TheReservedZeroMessageType_IsMalformed()
    {
        Span<byte> buffer = stackalloc byte[FrameHeader.Size];
        Header(1).WriteTo(buffer);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..], 0);

        Assert.Equal(FrameError.MalformedMessageType, FrameHeader.TryRead(buffer, out _));
    }

    [Fact]
    public void DecodingNeverThrowsOnHostileInput()
    {
        // A malformed frame from a peer is an expected condition on an untrusted boundary, not an
        // exceptional one. Property-style sweep over adversarial byte patterns.
        byte[] buffer = new byte[FrameHeader.Size];
        int seed = 0;

        for (int iteration = 0; iteration < 5000; iteration++)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                seed = (seed * 1103515245) + 12345;
                buffer[i] = (byte)(seed >> 16);
            }

            FrameError error = FrameHeader.TryRead(buffer, out FrameHeader header);

            if (error is FrameError.None)
            {
                Assert.InRange(header.PayloadLength, 0, FrameHeader.MaxPayloadBytes);
                Assert.NotEqual(DeviceMessageType.None, header.MessageType);
            }
        }
    }

    [Theory]
    [InlineData(FrameError.Truncated)]
    [InlineData(FrameError.NegativeLength)]
    [InlineData(FrameError.PayloadTooLarge)]
    [InlineData(FrameError.MalformedMessageType)]
    public void EveryFramingError_Disconnects(FrameError error)
    {
        // A length prefix that cannot be trusted means the next frame's position is unknown, so
        // continuing would resynchronize on attacker-chosen boundaries.
        Assert.Equal(UnknownMessageResponse.Disconnect, FrameHandling.ForFrameError(error));
    }

    [Fact]
    public void AnUnknownMessageType_IsSurvivable()
    {
        // Unlike a framing error: the length is trustworthy, so the frame can be skipped cleanly.
        // This is what lets a newer peer send messages an older one has never heard of.
        Assert.Equal(UnknownMessageResponse.ReplyWithError,
            FrameHandling.ForUnknownMessage(isRequest: true));
        Assert.Equal(UnknownMessageResponse.Ignore,
            FrameHandling.ForUnknownMessage(isRequest: false));
    }

    [Fact]
    public void TheMessageVocabularyIsExactlyTheReviewedSet()
    {
        // The closed enum is the security boundary of the IPC surface: there is no message type for
        // executing a command, running a shell, opening a file, invoking WMI, sending a HID report,
        // reading an EC register, issuing an IOCTL, or passing a raw buffer, so a peer cannot ask for
        // one.
        //
        // Asserted as an exact set rather than by searching for suspicious words. A substring scan
        // both misses a passthrough named something innocuous and trips over innocent names -
        // "LifecycleState" contains "ec", "DescriptorSet" contains "script". Any addition to the enum
        // fails here and has to be justified in review.
        string[] expected =
        [
            "None",
            "Hello", "HelloAck",
            "LifecycleState", "Activate", "Deactivate", "Suspend", "Resume", "ResourceState",
            "DescriptorSet", "StateDelta", "Command", "CommandResult", "CancelCommand",
            "PhysicalIdentities", "OemEvent", "HapticOutput", "ControllerHandoff",
            "DiagnosticsRequest", "DiagnosticsSnapshot",
            "Error",
        ];

        Assert.Equal(
            expected.OrderBy(n => n, StringComparer.Ordinal),
            Enum.GetNames<DeviceMessageType>().OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Negotiate_TwoCurrentBuilds_AgreeOnTheHighestSharedVersion()
    {
        NegotiationResult result = DeviceProtocol.Negotiate(
            DeviceProtocol.MinSupportedVersion,
            DeviceProtocol.MaxSupportedVersion,
            DeviceProtocol.SchemaFingerprint,
            out ushort negotiated);

        Assert.Equal(NegotiationResult.Agreed, result);
        Assert.Equal(DeviceProtocol.MaxSupportedVersion, negotiated);
    }

    [Fact]
    public void Negotiate_APeerOnlySupportingOlderVersions_IsRefused()
    {
        Assert.Equal(NegotiationResult.PeerTooOld, DeviceProtocol.Negotiate(
            0, 0, DeviceProtocol.SchemaFingerprint, out _));
    }

    [Fact]
    public void Negotiate_APeerOnlySupportingNewerVersions_IsRefused()
    {
        Assert.Equal(NegotiationResult.PeerTooNew, DeviceProtocol.Negotiate(
            (ushort)(DeviceProtocol.MaxSupportedVersion + 1),
            (ushort)(DeviceProtocol.MaxSupportedVersion + 5),
            DeviceProtocol.SchemaFingerprint,
            out _));
    }

    [Fact]
    public void Negotiate_AnInvertedRange_IsMalformed()
    {
        Assert.Equal(NegotiationResult.MalformedRange,
            DeviceProtocol.Negotiate(5, 1, DeviceProtocol.SchemaFingerprint, out _));
    }

    [Fact]
    public void Negotiate_MatchingVersionsWithChangedContracts_AreRefused()
    {
        // Two builds can agree on a version number and still disagree about what a message contains.
        // Without the fingerprint they would misread each other's payloads.
        Assert.Equal(NegotiationResult.SchemaMismatch, DeviceProtocol.Negotiate(
            DeviceProtocol.MinSupportedVersion,
            DeviceProtocol.MaxSupportedVersion,
            "wsgm-device-v1-modified",
            out _));
    }

    [Fact]
    public void PipeName_IsScopedPerSessionAndPerLaunch()
    {
        // Per session so two interactive sessions never share an endpoint; per launch so a stale host
        // cannot occupy the name a new one expects.
        Assert.NotEqual(
            ControlEndpoint.PipeName(1, "token-a"),
            ControlEndpoint.PipeName(2, "token-a"));

        Assert.NotEqual(
            ControlEndpoint.PipeName(1, "token-a"),
            ControlEndpoint.PipeName(1, "token-b"));
    }

    [Fact]
    public void NonceMatches_AcceptsTheExpectedNonce()
    {
        byte[] nonce = new byte[ControlEndpoint.NonceBytes];
        Random.Shared.NextBytes(nonce);

        Assert.True(ControlEndpoint.NonceMatches(nonce, nonce.ToArray()));
    }

    [Fact]
    public void NonceMatches_RejectsAWrongNonceEvenWithACorrectPrefix()
    {
        // The comparison must not short-circuit: leaking how many leading bytes were right turns
        // guessing the nonce into a per-byte search.
        byte[] expected = new byte[ControlEndpoint.NonceBytes];
        Random.Shared.NextBytes(expected);

        byte[] almost = expected.ToArray();
        almost[^1] ^= 0xFF;

        Assert.False(ControlEndpoint.NonceMatches(almost, expected));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ControlEndpoint.NonceBytes - 1)]
    [InlineData(ControlEndpoint.NonceBytes + 1)]
    public void NonceMatches_RejectsAnyWrongLength(int length)
    {
        byte[] expected = new byte[ControlEndpoint.NonceBytes];

        Assert.False(ControlEndpoint.NonceMatches(new byte[length], expected));
    }

    private static FrameHeader Header(int payloadLength) => new()
    {
        PayloadLength = payloadLength,
        ProtocolVersion = 1,
        MessageType = DeviceMessageType.Hello,
        RequestId = 1,
    };
}
