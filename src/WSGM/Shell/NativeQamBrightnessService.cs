using System;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Serializes panel reads and writes and publishes only confirmed brightness.</summary>
internal sealed class NativeQamBrightnessService : ISteamBrightnessBackend, IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly Timer _poll;
    private readonly Func<bool> _active;
    private readonly Action _publish;
    private readonly Func<int?> _read;
    private readonly Func<int, bool> _write;
    private long _revision;
    private int _lastPolled = -1;
    private bool _disposed;
    private SteamBrightnessState? _current;
    internal event Action? Changed;
    internal SteamBrightnessState? Current { get { lock (_gate) { return _current; } } }

    internal NativeQamBrightnessService(Func<bool> active, Action publish)
        : this(active, publish,
            () => Backlight.TryReadBrightness(out int percent) ? percent : null,
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

    internal async ValueTask<SteamBrightnessState?> ReadAsync() =>
        await Task.Run(ReadCurrent).ConfigureAwait(false);

    private SteamBrightnessState? ReadCurrent()
    {
        lock (_gate)
        {
            return _disposed ? null : ReadUnderGate();
        }
    }

    private SteamBrightnessState? ReadUnderGate()
    {
        SteamBrightnessState? next = _read() is int percent and >= 0 and <= 100
            ? new SteamBrightnessState(percent, ++_revision) : null;
        bool changed = next?.Percent != _current?.Percent;
        _current = next;
        if (changed) { Changed?.Invoke(); }
        return next;
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetBrightnessAsync(int percent, CancellationToken cancellationToken)
    {
        if (percent is < 0 or > 100)
        {
            return new(false, "The brightness must be between 0 and 100 percent.");
        }

        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SteamUiCommandResult result = await Task.Run(() =>
            {
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_disposed || !_active()) { return SteamUiCommandResult.Refused; }
                    if (!_write(percent)) { return new SteamUiCommandResult(false, "The panel backlight refused the write."); }
                    SteamBrightnessState? readback = ReadUnderGate();
                    return readback is null
                        ? new SteamUiCommandResult(false, "Brightness was written but readback is unavailable.")
                        : readback.Percent != percent
                            ? new SteamUiCommandResult(false, $"Brightness readback is {readback.Percent}%, requested {percent}%.")
                            : new SteamUiCommandResult(true, null, SteamBrightnessSurface.Serialize(readback));
                }
            }, cancellationToken).ConfigureAwait(false);
            _publish();
            return result;
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate) { _disposed = true; }
        _poll.Dispose();
    }

    private void OnPoll(object? state)
    {
        try
        {
            if (!_active()) { return; }
            SteamBrightnessState? current = ReadCurrent();
            int percent = current?.Percent ?? -1;
            if (percent == Interlocked.Exchange(ref _lastPolled, percent))
            {
                return;
            }

            Log.Change("display.backlight", current is null ? "Panel backlight unavailable." : $"Panel backlight at {current.Percent}%.");
            _publish();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Panel backlight read failed: {ex.Message}");
        }
    }
}
