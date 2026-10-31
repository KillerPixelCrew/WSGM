using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Owns the session's one Bluetooth pairing attempt: starting it, relaying Windows' questions to
///     the UI, cancelling it and publishing how it ended.
/// </summary>
/// <remarks>
///     A part of <see cref="RadioManager" />, which keeps discovery running while an attempt is in
///     progress and owns the panel's status line. Invoke members on the Avalonia UI thread; ceremony
///     callbacks arrive on Windows threads and are posted to the dispatcher. Dispose declines the
///     open question and ends the running attempt.
/// </remarks>
public sealed class BluetoothPairing : IDisposable
{
    private readonly BluetoothDeviceCatalog _catalog;
    private readonly Action _finished;

    private readonly
        Func<string, Action<WindowsRadio.PairingRequest>, CancellationToken, Task<WindowsRadio.PairingResult>>
        _pairBluetooth;

    private readonly Action<string> _reportStatus;

    /// <summary>
    ///     Ends the running pairing attempt when the owner is disposed. A user cancel declines the
    ///     pending question instead, so it ends with Windows' own cancelled outcome and text.
    /// </summary>
    private CancellationTokenSource? _cancellation;

    private bool _cancelled;
    private volatile bool _disposed;
    private string? _endpointId;
    private BluetoothDeviceEntry? _entry;
    private uint _token;

    /// <summary>Creates the pairing part of a radio manager.</summary>
    /// <param name="pairBluetooth">Starts one bounded pairing operation and invokes ceremony callbacks from a Windows thread.</param>
    /// <param name="catalog">The owner's Bluetooth catalog, told about a confirmed pairing.</param>
    /// <param name="reportStatus">Shows a line on the radio panel's shared status; UI thread.</param>
    /// <param name="finished">Called on the UI thread once an attempt has ended, so discovery demand can follow.</param>
    internal BluetoothPairing(
        Func<string, Action<WindowsRadio.PairingRequest>, CancellationToken, Task<WindowsRadio.PairingResult>>
            pairBluetooth,
        BluetoothDeviceCatalog catalog,
        Action<string> reportStatus,
        Action finished)
    {
        _pairBluetooth = pairBluetooth;
        _catalog = catalog;
        _reportStatus = reportStatus;
        _finished = finished;
    }

    /// <summary>Gets whether a pairing attempt is running. UI thread only.</summary>
    internal bool InProgress { get; private set; }

    /// <summary>Declines the open question, ends the running attempt and detaches the UI. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (InProgress)
        {
            if (_token != 0)
            {
                Respond(_token, false, null);
            }

            // On the thread pool, like every pairing answer: completing a pairing deferral on the
            // UI thread's STA can wedge the Device Association service.
            _ = _cancellation?.CancelAsync();
            Finish();
        }

        Requested = null;
        Finished = null;
    }

    /// <summary>
    ///     Raised when Windows asks a pairing question and the UI must
    ///     answer with <see cref="Respond" />. Always on the UI thread.
    /// </summary>
    public event Action<PairingPrompt>? Requested;

    /// <summary>
    ///     Raised when a pairing attempt finishes, with a message to show.
    ///     Always on the UI thread.
    /// </summary>
    public event Action<string>? Finished;

    /// <summary>
    ///     Starts pairing a device. Questions arrive on
    ///     <see cref="Requested" /> and must be answered with
    ///     <see cref="Respond" />.
    /// </summary>
    /// <param name="entry">The device to pair.</param>
    /// <returns>Whether pairing was started or the device was already paired.</returns>
    /// <exception cref="ObjectDisposedException">The owner has been disposed.</exception>
    public bool Begin(BluetoothDeviceEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (entry.Paired)
        {
            return true;
        }

        if (InProgress || entry.Busy)
        {
            _reportStatus("Another pairing is already in progress.");
            return false;
        }

        if (!entry.CanPair)
        {
            _reportStatus("Put the device into pairing mode and scan again.");
            return false;
        }

        InProgress = true;
        entry.Busy = true;
        _entry = entry;
        _endpointId = entry.PairingEndpointId;
        _cancelled = false;
        _reportStatus($"Pairing with {entry.Name}...");
        // Discovery must stay active to keep an advertising association endpoint pair-ready.
        Log.Info($"Bluetooth pairing: started for {entry.Name}.");

        var cancellation = new CancellationTokenSource();
        Task<WindowsRadio.PairingResult> pairing;
        try
        {
            pairing = _pairBluetooth(_endpointId, OnRequested, cancellation.Token);
        }
        catch (Exception ex)
        {
            Finish();
            _reportStatus(RadioManager.DescribeCommandFailure($"pair with {entry.Name}", ex));
            return false;
        }

        _cancellation = cancellation;
        _ = ObserveAsync(pairing, cancellation);
        return true;
    }

    /// <summary>Waits for the attempt Begin started and reports how it ended.</summary>
    private async Task ObserveAsync(
        Task<WindowsRadio.PairingResult> pairing,
        CancellationTokenSource cancellation)
    {
        WindowsRadio.PairingResult? result = null;
        Exception? failure = null;
        try
        {
            result = await pairing;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // The source has no timer or linked parent, so it is dropped rather than disposed: a cancel
        // Dispose requested may still be running its callbacks on the thread pool.
        if (ReferenceEquals(_cancellation, cancellation))
        {
            _cancellation = null;
        }

        OnDone(result, failure);
    }

    /// <summary>Marks this row's running pairing as cancelled and declines its current or next prompt.</summary>
    /// <param name="entry">The logical Bluetooth row whose attempt should end.</param>
    /// <returns>False when another row or no row is pairing; true after recording cancellation intent.</returns>
    /// <remarks>UI thread only. Does not cancel the backend token or wait for pairing completion.</remarks>
    internal bool Cancel(BluetoothDeviceEntry entry)
    {
        if (!InProgress || _entry?.Id != entry.Id)
        {
            return false;
        }

        _cancelled = true;
        _reportStatus($"Cancelling pairing with {entry.Name}...");
        if (_token != 0)
        {
            Respond(_token, false, null);
        }

        return true;
    }

    /// <summary>Answers a pairing question raised on <see cref="Requested" />.</summary>
    /// <param name="token">The token from the prompt.</param>
    /// <param name="accept">Whether the user accepted.</param>
    /// <param name="pin">The PIN typed by the user, for the provide-pin ceremony.</param>
    /// <remarks>Queues the answer on the MTA thread pool and returns immediately; native failures are logged.</remarks>
    public static void Respond(uint token, bool accept, string? pin)
    {
        Log.Info($"Bluetooth pairing: answering token {token} with "
                 + $"{(accept ? "accept" : "decline")}{(pin is { Length: > 0 } ? " and a PIN" : "")}.");
        // A pairing deferral completed on Avalonia's STA thread can wedge the
        // Device Association service; always answer from the MTA thread pool.
        _ = Task.Run(() =>
        {
            try
            {
                WindowsRadio.RespondToPairing(token, accept, pin);
            }
            catch (Exception ex)
            {
                // Unanswered, Windows' deferral sits until it times out and the
                // row stays on "Working..." — so the reason must reach the log
                // rather than an unobserved task.
                Log.Warn($"Bluetooth pairing: reply to token {token} threw: {ex.Message}");
            }
        });
    }

    private void Finish()
    {
        if (_entry is not null)
        {
            _entry.Busy = false;
            _entry = null;
        }

        _token = 0;
        _endpointId = null;
        _cancelled = false;
        InProgress = false;
        _finished();
    }

    private void OnRequested(WindowsRadio.PairingRequest request)
    {
        if (_disposed)
        {
            Respond(request.Token, false, null);
            return;
        }

        Log.Info($"Bluetooth pairing: question received (token {request.Token}, "
                 + $"kind {request.Kind}) for {request.DeviceName}.");
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _cancelled)
            {
                _token = 0;
                Respond(request.Token, false, null);
                return;
            }

            // Recorded here, on the UI thread that reads it: a cancel that runs before this post
            // sees no token, and the cancelled flag above declines the question instead.
            _token = request.Token;
            var handled = Requested is not null;
            Log.Info($"Bluetooth pairing: prompting the user (token {request.Token}, "
                     + $"handler attached: {handled}).");
            Requested?.Invoke(new PairingPrompt(
                request.Token, request.Kind, request.Pin, request.DeviceName));
            if (handled)
            {
                return;
            }

            Log.Warn($"Bluetooth pairing: no UI attached, declining token {request.Token}.");
            Respond(request.Token, false, null);
        });
    }

    private void OnDone(WindowsRadio.PairingResult? result, Exception? failure)
    {
        if (_disposed)
        {
            return;
        }

        var outcome = result?.Outcome;
        // The library's deadline is a typed exception; the line the user sees stays today's.
        var text = failure is TimeoutException
            ? "Bluetooth pairing timed out."
            : failure?.Message
              ?? (result is { } completed ? $"Windows status {completed.RawStatus}" : string.Empty);
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            var entry = _entry;
            var name = entry?.Name ?? "device";
            // Same reasoning as unpair: apply the outcome we already know rather
            // than leaving the row stale until the next inquiry finishes.
            var paired = outcome is WindowsRadio.PairingOutcome.Paired
                or WindowsRadio.PairingOutcome.AlreadyPaired;
            if (entry is not null && paired)
            {
                if (_endpointId is { } endpointId)
                {
                    _catalog.ConfirmPairing(endpointId, true);
                }

                entry.Paired = true;
            }

            Finish();
            var summary = DescribeOutcome(outcome, name, text);
            // The raw status rides along: the grouped outcome deliberately
            // lumps rare statuses, and remote diagnosis needs the exact one.
            Log.Info($"Bluetooth pairing: finished for {name} (outcome {outcome}"
                     + $"{(text.Length > 0 ? $", {text}" : "")}). {summary}");
            _reportStatus(paired ? "" : summary);
            Finished?.Invoke(summary);
        });
    }

    /// <summary>The message for a finished pairing attempt.</summary>
    /// <param name="outcome">
    ///     How it ended, or <see langword="null" /> when the attempt threw
    ///     before Windows produced a result.
    /// </param>
    /// <param name="device">The device's display name.</param>
    /// <param name="message">
    ///     The exception message or raw Windows status, used only when there
    ///     is nothing better to say.
    /// </param>
    /// <returns>A line to show the user.</returns>
    internal static string DescribeOutcome(
        WindowsRadio.PairingOutcome? outcome,
        string device,
        string message)
    {
        return outcome switch
        {
            WindowsRadio.PairingOutcome.Paired => $"{device} is paired.",
            WindowsRadio.PairingOutcome.AlreadyPaired => $"{device} was already paired.",
            WindowsRadio.PairingOutcome.Cancelled => $"Pairing with {device} was cancelled.",
            WindowsRadio.PairingOutcome.Failed =>
                $"Could not pair with {device}. Make sure it is in pairing mode.",
            // The broker runs unelevated and may be unable to inspect an
            // elevated caller; that is a different problem from a sulky device.
            WindowsRadio.PairingOutcome.AccessDenied => $"Windows denied pairing with {device}.",
            // A hung earlier ceremony inside the Device Association service —
            // it survives WSGM, so only the radio (or a reboot) can clear it.
            WindowsRadio.PairingOutcome.AlreadyInProgress =>
                $"Windows is still busy with an earlier pairing attempt for {device}. "
                + "Turn Bluetooth off and on, then try again.",
            null => message.Length > 0 ? message : $"Pairing with {device} failed.",
            _ => $"Pairing with {device} did not complete."
        };
    }
}
