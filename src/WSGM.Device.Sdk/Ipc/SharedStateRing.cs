using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;

namespace WSGM.Device.Sdk.Ipc;

/// <summary>
/// A single-producer shared-memory ring for high-rate controller and motion samples.
/// </summary>
/// <remarks>
/// Controller and IMU samples are far too frequent to serialize as pipe messages. Measured on the
/// reference handheld at 64 bytes per sample, a framed named-pipe write and read cost 15.3 µs and
/// allocated 164 bytes per sample; this ring costs a fraction of that and allocates nothing. At a
/// 250 Hz report rate that is the difference between roughly 0.4% of a core and a rounding error, on
/// a device whose entire controller path is budgeted under 2%.
/// <para>
/// Consistency uses a per-slot sequence counter rather than a lock: the writer makes the counter odd
/// before touching a slot and even after, so a reader that sees an odd counter, or a different one
/// before and after, knows it read a slot mid-write and retries. A lock would let a descheduled or
/// dead writer stall the reader, and the reader here is the UI.
/// </para>
/// <para>
/// The producer never blocks and never waits for a reader. A reader that falls behind loses samples,
/// which is the correct trade for input state: the newest sample supersedes everything older, so a
/// stalled consumer should skip forward rather than replay a backlog of stale stick positions.
/// </para>
/// </remarks>
public sealed unsafe class SharedStateRing : IDisposable
{
    /// <summary>Bytes reserved at the start of the mapping for the header.</summary>
    public const int HeaderBytes = 64;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly int _slotCount;
    private readonly int _slotBytes;
    private readonly bool _ownsFile;
    private bool _disposed;

    private SharedStateRing(
        MemoryMappedFile file,
        MemoryMappedViewAccessor view,
        int slotCount,
        int slotBytes,
        bool ownsFile)
    {
        _file = file;
        _view = view;
        _slotCount = slotCount;
        _slotBytes = slotBytes;
        _ownsFile = ownsFile;

        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _base = pointer + _view.PointerOffset;
    }

    /// <summary>Number of slots in the ring.</summary>
    public int SlotCount => _slotCount;

    /// <summary>Usable payload bytes per slot.</summary>
    public int SlotPayloadBytes => _slotBytes - sizeof(long);

    /// <summary>
    /// Creates the ring and its backing mapping.
    /// </summary>
    /// <param name="name">Mapping name, scoped to the session like the control pipe.</param>
    /// <param name="slotCount">Number of slots. Must be a power of two.</param>
    /// <param name="slotPayloadBytes">Usable bytes per slot.</param>
    /// <returns>The created ring.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The slot count is not a positive power of two.</exception>
    /// <remarks>
    /// A power-of-two slot count so the index is a mask rather than a modulo, and so the index stays
    /// correct when the monotonic counter wraps — with any other count, wrapping would skip slots and
    /// silently corrupt the ordering.
    /// </remarks>
    public static SharedStateRing Create(string name, int slotCount, int slotPayloadBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotPayloadBytes);

        if ((slotCount & (slotCount - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slotCount), slotCount, "Slot count must be a power of two.");
        }

        int slotBytes = slotPayloadBytes + sizeof(long);
        long capacity = HeaderBytes + ((long)slotBytes * slotCount);

        MemoryMappedFile file = MemoryMappedFile.CreateNew(name, capacity);
        MemoryMappedViewAccessor view = file.CreateViewAccessor(0, capacity);

        return new SharedStateRing(file, view, slotCount, slotBytes, ownsFile: true);
    }

    /// <summary>
    /// Opens a ring another process created.
    /// </summary>
    /// <param name="name">The mapping name.</param>
    /// <param name="slotCount">Slot count agreed with the producer.</param>
    /// <param name="slotPayloadBytes">Payload bytes per slot agreed with the producer.</param>
    /// <returns>The opened ring.</returns>
    public static SharedStateRing Open(string name, int slotCount, int slotPayloadBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        int slotBytes = slotPayloadBytes + sizeof(long);
        long capacity = HeaderBytes + ((long)slotBytes * slotCount);

        MemoryMappedFile file = MemoryMappedFile.OpenExisting(name);
        MemoryMappedViewAccessor view = file.CreateViewAccessor(0, capacity);

        return new SharedStateRing(file, view, slotCount, slotBytes, ownsFile: false);
    }

    /// <summary>
    /// Publishes one sample. Never blocks.
    /// </summary>
    /// <param name="payload">The sample bytes. Must not exceed <see cref="SlotPayloadBytes"/>.</param>
    /// <returns>The sequence number assigned to this sample.</returns>
    /// <exception cref="ArgumentException">The payload is larger than a slot.</exception>
    public long Write(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > SlotPayloadBytes)
        {
            throw new ArgumentException(
                $"Payload of {payload.Length} exceeds the {SlotPayloadBytes}-byte slot.",
                nameof(payload));
        }

        long* writeCounter = (long*)_base;
        long sequence = *writeCounter + 1;
        long* stamp = (long*)(_base + SlotOffset(sequence));

        // Odd stamp means "being written". A reader that sees it retries rather than returning a slot
        // that is half old sample and half new.
        Volatile.Write(ref *stamp, (sequence * 2) - 1);

        payload.CopyTo(new Span<byte>(stamp + 1, SlotPayloadBytes));

        // Even again: the slot is complete. The write barrier inside Volatile.Write orders the copy
        // above before this store, which is what makes the stamp meaningful to a reader.
        Volatile.Write(ref *stamp, sequence * 2);

        // Published last, so a reader that sees this counter is guaranteed the slot behind it is
        // already complete.
        Volatile.Write(ref *writeCounter, sequence);

        return sequence;
    }

    /// <summary>
    /// Reads the newest published sample.
    /// </summary>
    /// <param name="destination">Buffer receiving the payload.</param>
    /// <param name="sequence">Sequence number of the sample that was read.</param>
    /// <returns><see langword="true"/> when a consistent sample was read.</returns>
    /// <remarks>
    /// Returns the newest rather than the oldest unread sample. For input state the newest supersedes
    /// everything older, so a consumer that fell behind should skip forward — replaying a backlog
    /// would move the stick through positions the user left several frames ago.
    /// </remarks>
    public bool TryReadLatest(Span<byte> destination, out long sequence)
    {
        sequence = Volatile.Read(ref *(long*)_base);
        return sequence > 0 && TryReadSlot(sequence, destination);
    }

    /// <summary>
    /// Reads a specific sample, if it has not yet been overwritten.
    /// </summary>
    /// <param name="sequence">The sequence number to read.</param>
    /// <param name="destination">Buffer receiving the payload.</param>
    /// <returns><see langword="true"/> when that sample was still present and read consistently.</returns>
    public bool TryReadSlot(long sequence, Span<byte> destination)
    {
        if (sequence <= 0)
        {
            return false;
        }

        long* stamp = (long*)(_base + SlotOffset(sequence));
        long expected = sequence * 2;
        int length = Math.Min(destination.Length, SlotPayloadBytes);

        // Two attempts. One retry absorbs a reader that landed mid-write; a second failure means the
        // writer has lapped this reader, and looping would burn CPU chasing a producer that is faster
        // than the consumer by construction.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (Volatile.Read(ref *stamp) != expected)
            {
                continue;
            }

            new ReadOnlySpan<byte>(stamp + 1, length).CopyTo(destination[..length]);

            // Re-read after the copy: if the stamp still matches, no write touched this slot while
            // the bytes were being read.
            if (Volatile.Read(ref *stamp) == expected)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How many samples a reader missed.
    /// </summary>
    /// <param name="lastReadSequence">The last sequence the reader consumed.</param>
    /// <returns>Samples published since then, which may exceed <see cref="SlotCount"/>.</returns>
    /// <remarks>
    /// Reported rather than hidden. A consumer that missed more than <see cref="SlotCount"/> samples
    /// has a genuine discontinuity and must mark its next sample accordingly, because it can no
    /// longer derive button edges by comparing against what it last saw.
    /// </remarks>
    public long MissedSince(long lastReadSequence) =>
        Math.Max(0, Volatile.Read(ref *(long*)_base) - lastReadSequence);

    /// <summary>Releases the pointer and the mapping.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Released before the view: the handle keeps a reference count that must go back to zero
        // while the view is still alive to release it.
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();

        if (_ownsFile)
        {
            _file.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long SlotOffset(long sequence) =>
        HeaderBytes + (((sequence - 1) & (_slotCount - 1)) * _slotBytes);
}
