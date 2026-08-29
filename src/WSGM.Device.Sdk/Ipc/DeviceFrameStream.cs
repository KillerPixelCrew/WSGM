using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Device.Sdk.Ipc;

/// <summary>One validated frame read from the semantic control stream.</summary>
public sealed record DeviceFrame(FrameHeader Header, byte[] Payload);

/// <summary>Bounded asynchronous framing over the authenticated named pipe.</summary>
public sealed class DeviceFrameStream : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    /// <summary>Creates a framed view over an owned duplex stream.</summary>
    /// <param name="stream">Connected readable and writable stream.</param>
    public DeviceFrameStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite)
        {
            throw new ArgumentException("The control stream must be duplex.", nameof(stream));
        }

        _stream = stream;
    }

    /// <summary>Reads one complete frame, rejecting malformed or truncated input.</summary>
    /// <param name="cancellationToken">Cancels the bounded read.</param>
    /// <returns>The frame, or <see langword="null"/> after an orderly peer close.</returns>
    /// <exception cref="InvalidDataException">The peer sent malformed framing.</exception>
    public async ValueTask<DeviceFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(FrameHeader.Size);
        try
        {
            int headerBytes = await ReadExactlyOrEofAsync(
                headerBuffer.AsMemory(0, FrameHeader.Size), cancellationToken).ConfigureAwait(false);
            if (headerBytes == 0)
            {
                return null;
            }

            if (headerBytes != FrameHeader.Size)
            {
                throw new InvalidDataException("The peer closed during a frame header.");
            }

            FrameError error = FrameHeader.TryRead(
                headerBuffer.AsSpan(0, FrameHeader.Size), out FrameHeader header);
            if (error is not FrameError.None)
            {
                throw new InvalidDataException($"Rejected frame header: {error}.");
            }

            byte[] payload = GC.AllocateUninitializedArray<byte>(header.PayloadLength);
            if (payload.Length > 0)
            {
                int payloadBytes = await ReadExactlyOrEofAsync(payload, cancellationToken)
                    .ConfigureAwait(false);
                if (payloadBytes != payload.Length)
                {
                    throw new InvalidDataException("The peer closed during a frame payload.");
                }
            }

            return new DeviceFrame(header, payload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }

    /// <summary>Serializes one frame atomically with respect to other writers.</summary>
    /// <param name="header">Header whose payload length must match <paramref name="payload"/>.</param>
    /// <param name="payload">Bounded serialized payload.</param>
    /// <param name="cancellationToken">Cancels waiting or writing.</param>
    public async ValueTask WriteAsync(
        FrameHeader header,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (payload.Length != header.PayloadLength)
        {
            throw new ArgumentException("Frame payload length does not match its header.", nameof(payload));
        }

        if (payload.Length > FrameHeader.MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Frame payload is too large.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(FrameHeader.Size);
            try
            {
                header.WriteTo(headerBuffer.AsSpan(0, FrameHeader.Size));
                await _stream.WriteAsync(
                    headerBuffer.AsMemory(0, FrameHeader.Size), cancellationToken).ConfigureAwait(false);
                if (!payload.IsEmpty)
                {
                    await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                }

                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(headerBuffer);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Closes the framing owner and the underlying pipe.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeGate.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<int> ReadExactlyOrEofAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = await _stream.ReadAsync(destination[total..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
