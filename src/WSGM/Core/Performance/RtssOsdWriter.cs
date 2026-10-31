using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace WSGM.Core;

/// <summary>
///     Owns WSGM's RTSS OSD slot: read-write mapping, reopen after an RTSS restart, and a
///     zeroed release on dispose so the slot returns to the pool.
/// </summary>
internal sealed class RtssOsdWriter : IDisposable
{
    private const string Owner = "WSGM";
    private bool _disposed;

    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_view is not null)
            {
                var region = new RtssMappedRegion(_view);
                var released = RtssOsdSlots.Release(region, Owner);
                var deadline = Environment.TickCount64 + 200;
                while (!released && Environment.TickCount64 < deadline)
                {
                    // A refused lock changed nothing; a bounded cleanup retry cannot duplicate a write.
                    Thread.Sleep(5);
                    released = RtssOsdSlots.Release(region, Owner);
                }

                if (!released)
                {
                    Log.Warn("RTSS OSD slot could not be released within its cleanup budget.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or ObjectDisposedException)
        {
            // RTSS already gone; there is no slot left to hand back.
        }

        Close();
    }

    /// <summary>Writes the OSD text, claiming a slot when needed.</summary>
    /// <param name="text">OSD text; empty clears the display while keeping the slot.</param>
    /// <returns>False while RTSS is not running or offers no free slot.</returns>
    internal bool TryUpdate(string text)
    {
        if (_disposed || !TryOpen())
        {
            return false;
        }

        try
        {
            var written = RtssOsdSlots.TryWrite(new RtssMappedRegion(_view!), Owner, text);
            Log.Change(
                "rtss-osd-slot",
                written
                    ? "RTSS OSD slot claimed and updating."
                    : "RTSS OSD write deferred: mapping layout, busy writer or no free slot.");
            return written;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or ObjectDisposedException)
        {
            // RTSS exited or replaced its mapping mid-write; reopen on the next tick.
            Log.Warn($"RTSS OSD write failed; reopening next tick: {ex.Message}");
            Close();
            return false;
        }
    }

    private bool TryOpen()
    {
        if (_view is not null)
        {
            return true;
        }

        try
        {
            _map = MemoryMappedFile.OpenExisting(RtssMappedRegion.MapName, MemoryMappedFileRights.ReadWrite);
            _view = _map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            Log.Change("rtss-osd-map", "RTSS OSD mapping open for writing.");
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException
                                       or IOException)
        {
            // Not running, or running elevated while WSGM is not — the OSD is simply
            // unavailable until RTSS is reachable, exactly like the frametime source.
            Log.Change(
                "rtss-osd-map",
                $"RTSS OSD mapping unavailable: {ex.GetType().Name}: {ex.Message}");
            Close();
            return false;
        }
    }

    private void Close()
    {
        _view?.Dispose();
        _view = null;
        _map?.Dispose();
        _map = null;
    }
}
