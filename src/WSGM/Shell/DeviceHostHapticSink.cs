using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Input;
using WSGM.Input;

namespace WSGM.Shell;

/// <summary>
/// The physical haptic return path: output frames travel back to the plugin over the host pipe.
/// </summary>
/// <remarks>
/// Ownership is the plugin's, not this object's. The sink reports what the plugin published for the
/// controller it currently holds, and stops reporting ownership the moment that publication is
/// withdrawn — a frame written into a released controller would reach whatever owner took it next.
/// </remarks>
internal sealed class DeviceHostHapticSink : IPhysicalHapticSink
{
    private readonly Func<HapticOutputFrame, CancellationToken, Task> _applyAsync;
    private readonly object _gate = new();
    private HapticCapabilities? _capabilities;
    private long _sourceGeneration;

    internal DeviceHostHapticSink(Func<HapticOutputFrame, CancellationToken, Task> applyAsync)
    {
        ArgumentNullException.ThrowIfNull(applyAsync);
        _applyAsync = applyAsync;
    }

    /// <inheritdoc/>
    public long SourceGeneration
    {
        get
        {
            lock (_gate)
            {
                return _sourceGeneration;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsOwned
    {
        get
        {
            lock (_gate)
            {
                return _capabilities is not null;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Every channel unsupported while unowned, so a frame that races the withdrawal is clamped to
    /// silence rather than delivered at full strength.
    /// </remarks>
    public HapticCapabilities Capabilities
    {
        get
        {
            lock (_gate)
            {
                return _capabilities ?? new HapticCapabilities();
            }
        }
    }

    /// <summary>Records what the plugin published for the controller it now owns.</summary>
    /// <param name="capabilities">The published capabilities, or null when it drives no haptics.</param>
    /// <param name="sourceGeneration">Cycle generation the publication belongs to.</param>
    internal void Publish(HapticCapabilities? capabilities, long sourceGeneration)
    {
        lock (_gate)
        {
            _capabilities = capabilities;
            _sourceGeneration = sourceGeneration;
        }
    }

    /// <summary>Withdraws ownership so no further frame is delivered.</summary>
    internal void Withdraw()
    {
        lock (_gate)
        {
            _capabilities = null;
        }
    }

    /// <inheritdoc/>
    public Task ApplyAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return IsOwned ? _applyAsync(frame, cancellationToken) : Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(long targetGeneration, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!IsOwned)
        {
            return Task.CompletedTask;
        }

        // An explicit silent frame, not merely the absence of frames: the plugin latches the last
        // rumble values it was given, so stopping without one leaves the motors running.
        return _applyAsync(
            HapticOutputFrame.Stop(targetGeneration, DateTimeOffset.UtcNow),
            cancellationToken);
    }
}
