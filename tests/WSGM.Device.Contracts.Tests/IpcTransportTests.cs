using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using WSGM.Device.Contracts.Ipc;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of the real control endpoint and shared state page, exercised
/// against actual Windows primitives rather than a model of them.
/// </summary>
public class IpcTransportTests
{
    [Fact]
    public void ThePipeDacl_GrantsTheCurrentUserAndNobodyElse()
    {
        // Built from scratch rather than by editing a default: a default pipe DACL typically grants
        // read access to Everyone or Authenticated Users, and starting from one leaves the endpoint
        // only as private as the entries somebody remembered to remove.
        PipeSecurity security = DeviceControlPipe.CreateCurrentUserOnlySecurity();

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        AuthorizationRuleCollection rules =
            security.GetAccessRules(true, true, typeof(SecurityIdentifier));

        Assert.All(rules.Cast<PipeAccessRule>(), rule =>
            Assert.Equal(identity.User, rule.IdentityReference));

        Assert.DoesNotContain(rules.Cast<PipeAccessRule>(), rule =>
            ((SecurityIdentifier)rule.IdentityReference).IsWellKnown(WellKnownSidType.WorldSid)
            || ((SecurityIdentifier)rule.IdentityReference)
                .IsWellKnown(WellKnownSidType.AuthenticatedUserSid));
    }

    [Fact]
    public void ThePipeGrantsNoAdministratorsEntry()
    {
        // WSGM runs elevated and its own SID already matches. An administrators entry would widen the
        // endpoint to every administrator on the machine for nothing.
        PipeSecurity security = DeviceControlPipe.CreateCurrentUserOnlySecurity();

        Assert.DoesNotContain(
            security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>(),
            rule => ((SecurityIdentifier)rule.IdentityReference)
                .IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
    }

    [Fact]
    public async Task AFrameRoundTripsOverARealPipe()
    {
        string name = ControlEndpoint.PipeName(0, $"test-{Guid.NewGuid():N}");

        using NamedPipeServerStream server = DeviceControlPipe.CreateServer(name);
        using NamedPipeClientStream client = DeviceControlPipe.CreateClient(name);

        Task waitForConnection = server.WaitForConnectionAsync(TestTimeout);
        await client.ConnectAsync(5_000, TestTimeout);
        await waitForConnection;

        byte[] payload = "hello"u8.ToArray();
        FrameHeader header = new()
        {
            PayloadLength = payload.Length,
            ProtocolVersion = DeviceProtocol.MaxSupportedVersion,
            MessageType = DeviceMessageType.Hello,
            RequestId = 7,
        };

        byte[] frame = new byte[FrameHeader.Size + payload.Length];
        header.WriteTo(frame);
        payload.CopyTo(frame.AsSpan(FrameHeader.Size));

        await client.WriteAsync(frame, TestTimeout);
        await client.FlushAsync(TestTimeout);

        byte[] received = new byte[frame.Length];
        int read = await server.ReadAsync(received, TestTimeout);

        Assert.Equal(frame.Length, read);
        Assert.Equal(FrameError.None, FrameHeader.TryRead(received, out FrameHeader decoded));
        Assert.Equal(header, decoded);
        Assert.Equal(payload, received[FrameHeader.Size..]);
    }

    [Fact]
    public async Task OnlyOneServerInstanceMayOwnTheEndpoint()
    {
        // A second instance would let a racing process claim the name and receive the connection
        // intended for the host.
        string name = ControlEndpoint.PipeName(0, $"test-{Guid.NewGuid():N}");

        using NamedPipeServerStream first = DeviceControlPipe.CreateServer(name);

        Assert.Throws<IOException>(() => DeviceControlPipe.CreateServer(name));
        await Task.CompletedTask;
    }

    [Fact]
    public void AHandshakeNonceIsSingleUse()
    {
        // A nonce that stayed valid would let a process that observed it once reconnect later, which
        // is exactly the replay it exists to stop.
        byte[] nonce = new byte[ControlEndpoint.NonceBytes];
        Random.Shared.NextBytes(nonce);

        HandshakeVerifier verifier = new(nonce);

        Assert.True(verifier.Accept(nonce));
        Assert.True(verifier.IsConsumed);
        Assert.False(verifier.Accept(nonce));
    }

    [Fact]
    public void AWrongNonceNeverConsumesTheRealOne()
    {
        byte[] nonce = new byte[ControlEndpoint.NonceBytes];
        Random.Shared.NextBytes(nonce);

        HandshakeVerifier verifier = new(nonce);
        byte[] wrong = nonce.ToArray();
        wrong[0] ^= 0xFF;

        Assert.False(verifier.Accept(wrong));
        Assert.False(verifier.IsConsumed);
        Assert.True(verifier.Accept(nonce));
    }

    [Fact]
    public void AVerifierRejectsAWrongLengthNonceAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new HandshakeVerifier(new byte[8]));
    }

    [Fact]
    public void TheRing_RoundTripsASample()
    {
        using SharedStateRing ring = NewRing(out _);

        byte[] sample = [1, 2, 3, 4, 5, 6, 7, 8];
        long written = ring.Write(sample);

        byte[] destination = new byte[ring.SlotPayloadBytes];
        Assert.True(ring.TryReadLatest(destination, out long sequence));
        Assert.Equal(written, sequence);
        Assert.Equal(sample, destination[..sample.Length]);
    }

    [Fact]
    public void TheRing_ReturnsTheNewestSampleNotTheOldest()
    {
        // For input state the newest supersedes everything older. Replaying a backlog would move the
        // stick through positions the user left several frames ago.
        using SharedStateRing ring = NewRing(out _);

        for (byte i = 1; i <= 5; i++)
        {
            ring.Write([i]);
        }

        byte[] destination = new byte[ring.SlotPayloadBytes];
        Assert.True(ring.TryReadLatest(destination, out long sequence));

        Assert.Equal(5, sequence);
        Assert.Equal(5, destination[0]);
    }

    [Fact]
    public void TheRing_WrapsCorrectlyPastItsSlotCount()
    {
        // The power-of-two slot count is what keeps the index correct when the counter wraps.
        using SharedStateRing ring = NewRing(out _, slotCount: 4);

        for (byte i = 1; i <= 20; i++)
        {
            ring.Write([i]);
        }

        byte[] destination = new byte[ring.SlotPayloadBytes];
        Assert.True(ring.TryReadLatest(destination, out long sequence));

        Assert.Equal(20, sequence);
        Assert.Equal(20, destination[0]);
    }

    [Fact]
    public void TheRing_ReportsHowManySamplesAReaderMissed()
    {
        // Reported rather than hidden: a consumer that missed more than a full lap has a genuine
        // discontinuity and can no longer derive button edges from what it last saw.
        using SharedStateRing ring = NewRing(out _, slotCount: 4);

        ring.Write([1]);
        long lastRead = 1;

        for (byte i = 2; i <= 10; i++)
        {
            ring.Write([i]);
        }

        Assert.Equal(9, ring.MissedSince(lastRead));
    }

    [Fact]
    public void TheRing_RefusesAnOverlongPayload()
    {
        using SharedStateRing ring = NewRing(out _);

        Assert.Throws<ArgumentException>(() =>
            ring.Write(new byte[ring.SlotPayloadBytes + 1]));
    }

    [Fact]
    public void TheRing_RequiresAPowerOfTwoSlotCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SharedStateRing.Create($"wsgm-test-{Guid.NewGuid():N}", 6, 64));
    }

    [Fact]
    public void TheRing_IsVisibleAcrossMappings()
    {
        // Two accessors over one mapping is the same code path the host and WSGM use across the
        // process boundary; this is what proves the layout agreement rather than a shared object.
        using SharedStateRing producer = NewRing(out string name, slotCount: 8, payloadBytes: 32);
        using SharedStateRing consumer = SharedStateRing.Open(name, 8, 32);

        producer.Write([42, 43]);

        byte[] destination = new byte[32];
        Assert.True(consumer.TryReadLatest(destination, out long sequence));
        Assert.Equal(1, sequence);
        Assert.Equal(42, destination[0]);
        Assert.Equal(43, destination[1]);
    }

    [Fact]
    public async Task TheRing_SurvivesAConcurrentWriterAndReader()
    {
        // The sequence-counter scheme exists so a reader landing mid-write retries instead of
        // returning a slot that is half old sample and half new. This exercises that directly: every
        // sample this reader accepts must be internally consistent.
        using SharedStateRing ring = NewRing(out _, slotCount: 16, payloadBytes: 64);

        const int Samples = 20_000;
        int torn = 0;

        Task writer = Task.Run(() =>
        {
            byte[] payload = new byte[64];
            for (int i = 1; i <= Samples; i++)
            {
                // Every byte carries the same marker, so any mixture of two samples is detectable.
                payload.AsSpan().Fill((byte)(i & 0xFF));
                ring.Write(payload);
            }
        });

        Task reader = Task.Run(() =>
        {
            byte[] destination = new byte[64];
            while (!writer.IsCompleted)
            {
                if (!ring.TryReadLatest(destination, out _))
                {
                    continue;
                }

                byte marker = destination[0];
                for (int i = 1; i < destination.Length; i++)
                {
                    if (destination[i] != marker)
                    {
                        Interlocked.Increment(ref torn);
                        break;
                    }
                }
            }
        });

        await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, torn);
    }

    private static SharedStateRing NewRing(
        out string name,
        int slotCount = 8,
        int payloadBytes = 64)
    {
        name = $"wsgm-test-{Guid.NewGuid():N}";
        return SharedStateRing.Create(name, slotCount, payloadBytes);
    }

    private static CancellationToken TestTimeout =>
        new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;
}
