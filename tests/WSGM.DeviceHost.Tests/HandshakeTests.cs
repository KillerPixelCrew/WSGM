using WSGM.Device.Contracts.Ipc;

namespace WSGM.DeviceHost.Tests;

public class HandshakeTests
{
    [Fact]
    public async Task Accept_ConcurrentReplay_ConsumesTheNonceExactlyOnce()
    {
        byte[] nonce = Enumerable.Range(0, ControlEndpoint.NonceBytes)
            .Select(index => (byte)index)
            .ToArray();
        HandshakeVerifier verifier = new(nonce);
        using ManualResetEventSlim start = new(false);

        Task<bool>[] attempts = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return verifier.Accept(nonce);
            }))
            .ToArray();
        start.Set();
        bool[] results = await Task.WhenAll(attempts);

        Assert.Single(results, accepted => accepted);
        Assert.True(verifier.IsConsumed);
    }

    [Fact]
    public void ValidateAck_MatchingLaunchAndWireEnvelope_AcceptsTheNegotiatedVersion()
    {
        DeviceHostHelloAck ack = Ack("wsgm.device.test");
        FrameHeader header = AckHeader(ack.ProtocolVersion);

        bool accepted = HostHandshakeValidator.TryValidateAck(
            header,
            ack,
            expectedRequestId: 1,
            expectedPackageId: "wsgm.device.test",
            out ushort protocolVersion,
            out string detail);

        Assert.True(accepted, detail);
        Assert.Equal(DeviceProtocol.MaxSupportedVersion, protocolVersion);
    }

    [Theory]
    [InlineData("wsgm.device.other", 1, true)]
    [InlineData("wsgm.device.test", 2, true)]
    [InlineData("wsgm.device.test", 1, false)]
    public void ValidateAck_LaunchOrEnvelopeConfusion_IsRejected(
        string packageId,
        uint requestId,
        bool responseFlag)
    {
        DeviceHostHelloAck ack = Ack(packageId);
        FrameHeader header = AckHeader(ack.ProtocolVersion) with
        {
            RequestId = requestId,
            Flags = responseFlag ? FrameFlags.IsResponse : FrameFlags.None,
        };

        Assert.False(HostHandshakeValidator.TryValidateAck(
            header,
            ack,
            expectedRequestId: 1,
            expectedPackageId: "wsgm.device.test",
            out _,
            out _));
    }

    [Fact]
    public void ValidateAck_HeaderAndPayloadProtocolDisagree_IsRejected()
    {
        DeviceHostHelloAck ack = Ack("wsgm.device.test");
        FrameHeader header = AckHeader((ushort)(ack.ProtocolVersion + 1));

        Assert.False(HostHandshakeValidator.TryValidateAck(
            header,
            ack,
            expectedRequestId: 1,
            expectedPackageId: "wsgm.device.test",
            out _,
            out _));
    }

    [Fact]
    public void IsExpectedAckEnvelope_AnotherMessageType_IsRejectedBeforePayloadDecode()
    {
        FrameHeader header = AckHeader(DeviceProtocol.MaxSupportedVersion) with
        {
            MessageType = DeviceMessageType.Command,
        };

        Assert.False(HostHandshakeValidator.IsExpectedAckEnvelope(header, 1, out _));
    }

    private static DeviceHostHelloAck Ack(string packageId) => new()
    {
        Accepted = true,
        Negotiation = NegotiationResult.Agreed,
        ProtocolVersion = DeviceProtocol.MaxSupportedVersion,
        PackageId = packageId,
    };

    private static FrameHeader AckHeader(ushort protocolVersion) => new()
    {
        PayloadLength = 1,
        ProtocolVersion = protocolVersion,
        MessageType = DeviceMessageType.HelloAck,
        RequestId = 1,
        Flags = FrameFlags.IsResponse,
    };
}
