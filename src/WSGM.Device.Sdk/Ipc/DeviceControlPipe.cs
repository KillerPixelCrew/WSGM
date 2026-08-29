using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace WSGM.Device.Sdk.Ipc;

/// <summary>
/// Creates the control endpoint with an ACL the operating system enforces.
/// </summary>
/// <remarks>
/// A named pipe rather than localhost TCP, and the ACL is the reason. A TCP listener is reachable by
/// every process on the machine and would need an authentication scheme invented for it; a pipe
/// carries a DACL the kernel checks before a single byte is read, so an unauthorized process fails at
/// <c>Connect</c> rather than somewhere inside a handshake we wrote.
/// <para>
/// The nonce is not a substitute for the ACL. It binds the connection to the host WSGM actually
/// spawned — a different process running as the same user would pass the ACL but cannot know a nonce
/// that was passed as a spawn argument.
/// </para>
/// </remarks>
public static class DeviceControlPipe
{
    /// <summary>Maximum concurrent server instances for one endpoint.</summary>
    /// <remarks>
    /// One. A second instance would let a racing process claim the name and receive the connection
    /// intended for the host.
    /// </remarks>
    public const int MaxServerInstances = 1;

    /// <summary>
    /// Builds the pipe DACL: the creating user, and nobody else.
    /// </summary>
    /// <returns>A security descriptor granting full control to the current user only.</returns>
    /// <remarks>
    /// Built from scratch rather than by editing a default. A default DACL on a pipe typically grants
    /// read access to <c>Everyone</c> or <c>Authenticated Users</c>, and starting from one means the
    /// endpoint is only as private as the entries somebody remembered to remove.
    /// <para>
    /// No administrators entry either: WSGM runs elevated and its own SID already matches, so adding
    /// one would widen the endpoint to every administrator on the machine for no gain.
    /// </para>
    /// </remarks>
    public static PipeSecurity CreateCurrentUserOnlySecurity()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User
            ?? throw new InvalidOperationException("The current identity has no user SID.");

        PipeSecurity security = new();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(
            user,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return security;
    }

    /// <summary>
    /// Creates the server end of the control pipe.
    /// </summary>
    /// <param name="pipeName">The name from <see cref="ControlEndpoint.PipeName"/>.</param>
    /// <returns>A server stream awaiting a connection.</returns>
    /// <remarks>
    /// Message transmission mode, so a read returns one frame rather than an arbitrary slice of the
    /// byte stream. That does not remove the need to validate the frame header — a peer still chooses
    /// what is inside the message — but it removes an entire class of resynchronization bug from the
    /// reader.
    /// </remarks>
    public static NamedPipeServerStream CreateServer(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            MaxServerInstances,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: FrameHeader.MaxPayloadBytes,
            outBufferSize: FrameHeader.MaxPayloadBytes,
            CreateCurrentUserOnlySecurity());
    }

    /// <summary>
    /// Creates the client end of the control pipe.
    /// </summary>
    /// <param name="pipeName">The name supplied to the host at spawn.</param>
    /// <returns>A client stream that has not yet connected.</returns>
    /// <remarks>
    /// <see cref="TokenImpersonationLevel.Anonymous"/> so the server cannot impersonate the client.
    /// The server has no reason to act as the host, and a pipe server that can impersonate its client
    /// is a privilege boundary nobody asked for.
    /// </remarks>
    public static NamedPipeClientStream CreateClient(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        return new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            TokenImpersonationLevel.Anonymous);
    }
}

/// <summary>
/// Verifies the one-time handshake nonce that binds a connection to the spawned host.
/// </summary>
/// <remarks>
/// Single use. A nonce that stayed valid would let a process that observed it once reconnect later,
/// which is the replay this exists to stop — so acceptance consumes it, and a second presentation of
/// the same value fails exactly like a wrong one.
/// </remarks>
public sealed class HandshakeVerifier
{
    private readonly byte[] _expected;
    private int _consumed;

    /// <summary>Creates a verifier for one host launch.</summary>
    /// <param name="expected">The nonce WSGM generated and passed to the host.</param>
    /// <exception cref="ArgumentException">The nonce is the wrong length.</exception>
    public HandshakeVerifier(ReadOnlySpan<byte> expected)
    {
        if (expected.Length != ControlEndpoint.NonceBytes)
        {
            throw new ArgumentException(
                $"Nonce must be {ControlEndpoint.NonceBytes} bytes.", nameof(expected));
        }

        _expected = expected.ToArray();
    }

    /// <summary>Whether the nonce has already been accepted.</summary>
    public bool IsConsumed => Volatile.Read(ref _consumed) != 0;

    /// <summary>
    /// Verifies a presented nonce and consumes it on success.
    /// </summary>
    /// <param name="presented">The nonce the peer sent.</param>
    /// <returns><see langword="true"/> when it matched and had not been used.</returns>
    public bool Accept(ReadOnlySpan<byte> presented)
    {
        if (!ControlEndpoint.NonceMatches(presented, _expected))
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
    }
}
