using System;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Serializes panel reads and writes and publishes only confirmed brightness.</summary>
internal sealed class NativeQamBrightnessService : ISteamBrightnessBackend, IDisposable
{
    private readonly Func<bool> _active;
    private readonly Lock _gate = new();
    private readonly Timer _poll;
    private readonly Action _publish;
    private readonly Func<int?> _read;
    private readonly Func<int, bool> _write;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private SteamBrightnessState? _current;
    private bool _disposed;
    private int _lastPolled = -1;
    private long _revision;

    internal NativeQamBrightnessService(Func<bool> active, Action publish)
        : this(active, publish,
            () => Backlight.TryReadBrightness(out var percent) ? percent : null,
            Backlight.TrySetBrightness, TimeSpan.FromSeconds(2))
    {
    }

    internal NativeQamBrightnessService(
        Func<bool> active, Action publish, Func<int?> read, Func<int, bool> write, TimeSpan pollInterval)
    {
        _active = active;
        _publish = publish;
        _read = read;
        _write = write;
        _poll = new Timer(OnPoll, null, pollInterval, pollInterval);
    }

    internal SteamBrightnessState? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _poll.Dispose();
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetBrightnessAsync(int percent, CancellationToken cancellationToken)
    {
        if (percent is < 0 or > 100)
        {
            return new SteamUiCommandResult(false, "The brightness must be between 0 and 100 percent.");
        }

        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await Task.Run(async () =>
            {
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_disposed || !_active())
                    {
                        return SteamUiCommandResult.Refused;
                    }

                    if (!_write(percent))
                    {
                        return new SteamUiCommandResult(false, "The panel backlight refused the write.");
                    }
                }

                SteamBrightnessState? readback = null;
                // The panel may accept the write before its brightness observation catches up. Only read
                // again while settling; an uncertain hardware write must never be repeated.
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    if (attempt > 0)
                    {
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    }

                    lock (_gate)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (_disposed || !_active())
                        {
                            return SteamUiCommandResult.Refused;
                        }

                        readback = ReadUnderGate();
                    }

                    if (readback?.Percent == percent)
                    {
                        return new SteamUiCommandResult(true, null, SteamBrightnessSurface.Serialize(readback));
                    }
                }

                return new SteamUiCommandResult(false, readback is null
                    ? "Brightness was written but readback is unavailable."
                    : $"Brightness readback is {readback.Percent}%, requested {percent}%.");
            }, cancellationToken).ConfigureAwait(false);
            _publish();
            return result;
        }
        finally
        {
            _writes.Release();
        }
    }

    internal event Action? Changed;

    internal async ValueTask<SteamBrightnessState?> ReadAsync()
    {
        return await Task.Run(ReadCurrent).ConfigureAwait(false);
    }

    private SteamBrightnessState? ReadCurrent()
    {
        lock (_gate)
        {
            return _disposed ? null : ReadUnderGate();
        }
    }

    private SteamBrightnessState? ReadUnderGate()
    {
        var percent = _read() is { } value and >= 0 and <= 100 ? value : (int?)null;
        var changed = percent != _current?.Percent;
        var next = percent is { } validPercent
            ? new SteamBrightnessState(validPercent, changed ? ++_revision : _current!.Revision)
            : null;
        _current = next;
        if (changed)
        {
            Changed?.Invoke();
        }

        return next;
    }

    private void OnPoll(object? state)
    {
        try
        {
            if (!_active())
            {
                return;
            }

            var current = ReadCurrent();
            var percent = current?.Percent ?? -1;
            if (percent == Interlocked.Exchange(ref _lastPolled, percent))
            {
                return;
            }

            Log.Change("display.backlight",
                current is null ? "Panel backlight unavailable." : $"Panel backlight at {current.Percent}%.");
            _publish();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Panel backlight read failed: {ex.Message}");
        }
    }
}
