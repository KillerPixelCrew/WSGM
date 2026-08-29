using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Coalesces per-device desired-profile edits into fresh config transactions.</summary>
internal sealed class DeviceProfileStore : IAsyncDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(350);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly Dictionary<string, DeviceDesiredProfile> _pending = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private bool _disposed;

    internal DeviceProfileStore()
    {
        _timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal void Queue(DeviceDesiredProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.DeviceIdentityKey))
        {
            throw new ArgumentException("A desired profile requires a stable device identity.",
                nameof(profile));
        }

        DeviceDesiredProfile copy = Clone(profile);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending[copy.DeviceIdentityKey] = copy;
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    internal async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, DeviceDesiredProfile> batch;
            lock (_gate)
            {
                batch = new Dictionary<string, DeviceDesiredProfile>(_pending, StringComparer.Ordinal);
                _pending.Clear();
            }

            if (batch.Count == 0)
            {
                return;
            }

            try
            {
                await Task.Run(() => ConfigStore.Mutate(config =>
                {
                    foreach ((string identity, DeviceDesiredProfile profile) in batch)
                    {
                        int index = config.DeviceIntegration.Profiles.FindIndex(existing =>
                            string.Equals(existing.DeviceIdentityKey, identity, StringComparison.Ordinal));
                        if (index >= 0)
                        {
                            config.DeviceIntegration.Profiles[index] = profile;
                        }
                        else
                        {
                            config.DeviceIntegration.Profiles.Add(profile);
                        }
                    }
                }), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (_gate)
                {
                    foreach ((string identity, DeviceDesiredProfile profile) in batch)
                    {
                        _pending.TryAdd(identity, profile);
                    }
                }

                throw;
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _timer.Dispose();
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Device desired-profile flush failed during shutdown: {ex.Message}");
        }

        _flushGate.Dispose();
    }

    private void OnTimer(object? state) => _ = ObserveFlushAsync();

    private async Task ObserveFlushAsync()
    {
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Device desired-profile save failed; edits remain queued: {ex.Message}");
        }
    }

    private static DeviceDesiredProfile Clone(DeviceDesiredProfile profile)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            profile,
            ConfigJsonContext.Default.DeviceDesiredProfile);
        return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.DeviceDesiredProfile)
            ?? throw new InvalidOperationException("Desired profile clone returned null.");
    }
}
