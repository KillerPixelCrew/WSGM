using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>
///     One overlay's hybrid core-placement workflow. Entry points and notifications belong to
///     the UI thread; native calls run on a worker. Closing prevents late publication.
/// </summary>
/// <param name="cores">Borrowed shared power-policy owner.</param>
/// <param name="readOnly">True to render current policy without permitting writes.</param>
internal sealed class HybridCoreSelection(HybridCores cores, bool readOnly = false) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    internal HybridCoreStatus Status { get; private set; } = new(false, 0, 0, [], null, null);

    internal string Detail { get; private set; } = "Read the processor core preference to choose one.";

    internal bool Busy { get; private set; }

    internal bool CanSelect => !readOnly && !Busy && !_disposed && Status.Supported;

    internal IReadOnlyList<HybridCoreOption> Options => Status.Options;

    /// <summary>Cancels this UI projection's lifetime and prevents later publication; borrowed session owners remain alive.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>Raised on the captured UI context when projected state or busy status changes.</summary>
    internal event Action? Changed;

    /// <summary>Reads shared state without overlapping this projection's active operation.</summary>
    /// <returns>Completion of the refresh; disposed/busy projections are a no-op and late results are not published.</returns>
    internal Task RefreshAsync()
    {
        return RunAsync(null);
    }

    /// <summary>Applies one explicit hybrid-core preference through the shared power-policy owner.</summary>
    /// <param name="mode">Requested AC and battery preference; unavailable/read-only state is ignored.</param>
    /// <returns>Completion of worker dispatch and UI status publication; operational failures become detail text.</returns>
    internal Task ApplyAsync(HybridCoreMode mode)
    {
        return CanSelect ? RunAsync(mode) : Task.CompletedTask;
    }

    private async Task RunAsync(HybridCoreMode? requested)
    {
        if (_disposed || Busy)
        {
            return;
        }

        Busy = true;
        Detail = requested is null
            ? "Reading the processor core preference..."
            : "Applying the processor core preference...";
        Changed?.Invoke();
        var token = _lifetime.Token;
        try
        {
            var status = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (requested is { } mode)
                {
                    cores.Apply(mode, token);
                    return Status with { OnAc = mode, OnBattery = mode };
                }

                return cores.Read();
            }, token);
            if (_disposed)
            {
                return;
            }

            Status = status;
            Detail = Describe(status);
            if (readOnly)
            {
                Detail += " Preview only; changes are disabled.";
            }
        }
        catch (OperationCanceledException)
        {
            // The overlay closed while the worker was mid-call. Nothing is published for a surface
            // that has gone away. Apply has already returned the native write outcome.
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                Detail = requested is null
                    ? $"The processor core preference could not be read: {ex.Message}"
                    : $"The processor core preference was not applied: {ex.Message}";
            }
        }
        finally
        {
            if (!_disposed)
            {
                Busy = false;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Says what is in effect, naming each power source only when the two disagree.</summary>
    /// <param name="status">Published support and AC/battery choices.</param>
    /// <returns>A user-facing support, error or policy summary.</returns>
    internal static string Describe(HybridCoreStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!status.Supported)
        {
            return "This processor has one kind of core, so there is nothing to choose.";
        }

        var cores = $"{status.PerformanceCores} performance and {status.EfficiencyCores} efficiency cores.";
        if (status.OnAc is null || status.OnBattery is null)
        {
            // Something outside WSGM wrote a placement WSGM does not offer. Saying which of its own
            // modes is active would be a claim about a value it did not set.
            return $"{cores} The current preference was not set by WSGM.";
        }

        return status.OnAc == status.OnBattery
            ? $"{cores} Applies to both battery and plugged in."
            : $"{cores} Plugged in and battery currently differ; applying sets both.";
    }
}
