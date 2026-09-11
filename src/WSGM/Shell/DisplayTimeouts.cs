using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
/// The session's one owner of the display-off timeouts, behind both the overlay's Power page and the
/// rows WSGM adds to Steam's Screensaver settings.
/// </summary>
/// <remarks>
/// Windows holds the values and nothing here caches them: every reading and every choice goes to the
/// active power scheme. What this adds is Steam's screensaver timeouts as the Screensaver settings last
/// reported them, which bound the display timeouts (<see cref="DisplayTimeoutPolicy"/>). A report that
/// finds a display timeout below its bound raises it once; a write Windows refuses is logged and not
/// retried, and the next attempt comes only from the next report or a choice the user makes.
/// </remarks>
internal sealed class DisplayTimeouts : ISteamScreensaverBackend
{
    private static readonly (string Row, PowerTimeoutKind Kind, string Label)[] Rows =
    [
        ("battery", PowerTimeoutKind.DisplayDc, "Turn display off after (on battery)"),
        ("plugged-in", PowerTimeoutKind.DisplayAc, "Turn display off after (plugged in)"),
    ];

    private readonly Func<PowerTimeoutKind, int?> _read;
    private readonly Func<PowerTimeoutKind, int, bool> _write;
    private readonly object _gate = new();
    private SteamScreensaverReport? _steam;
    private long _revision;

    /// <summary>Creates the owner over the active power scheme.</summary>
    internal DisplayTimeouts()
        : this(PowerTimeouts.Read, PowerTimeouts.Write)
    {
    }

    /// <summary>Creates the owner over supplied reads and writes, for tests.</summary>
    /// <param name="read">Reads one timeout in seconds, or null when Windows gives no answer.</param>
    /// <param name="write">Writes one timeout and reports whether Windows accepted it.</param>
    internal DisplayTimeouts(Func<PowerTimeoutKind, int?> read, Func<PowerTimeoutKind, int, bool> write)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
    }

    /// <summary>Raised after any timeout was written or Steam's screensaver timeouts were reported.</summary>
    internal event Action? Changed;

    /// <summary>Steam's screensaver timeouts as last reported, or null before any report.</summary>
    internal SteamScreensaverReport? Steam
    {
        get
        {
            lock (_gate)
            {
                return _steam;
            }
        }
    }

    /// <summary>Drops Steam's reported screensaver timeouts, so nothing bounds the display until Steam reports again.</summary>
    /// <remarks>
    /// Called whenever the Screensaver settings surface does not hold: a client without the
    /// screensaver, a Steam restart, or the rows switched off. A bound kept from a client that is gone
    /// goes on refusing display timeouts that nothing needs.
    /// </remarks>
    internal void ForgetSteam()
    {
        lock (_gate)
        {
            if (_steam is null)
            {
                return;
            }
            _steam = null;
        }

        Log.Change("steam.screensaver", "Steam's screensaver timeouts no longer apply: the Screensaver settings are not active.");
        OnChanged();
    }

    /// <summary>The bound on one display timeout, or null when nothing bounds it.</summary>
    /// <param name="kind">The display timeout.</param>
    /// <returns>The minimum in seconds, or null.</returns>
    internal int? Minimum(PowerTimeoutKind kind) => DisplayTimeoutPolicy.Minimum(kind, Steam);

    /// <summary>Cycles one idle timeout to its next preset, skipping presets the screensaver forbids.</summary>
    /// <param name="kind">The timeout the overlay's row controls.</param>
    /// <returns>Whether a value was written.</returns>
    internal bool Cycle(PowerTimeoutKind kind)
    {
        bool written;
        lock (PowerSchemes.MutationGate)
        {
            // A failed read refuses to write blind.
            int? current = _read(kind);
            if (current is null)
            {
                return false;
            }

            int next = DisplayTimeoutPolicy.DisplayKinds.Contains(kind)
                ? DisplayTimeoutPolicy.NextAllowed(current.Value, Minimum(kind))
                : PowerTimeouts.NextPreset(current.Value);
            written = _write(kind, next);
        }

        OnChanged();
        return written;
    }

    /// <summary>The rows Steam's Screensaver settings show, read from Windows now.</summary>
    /// <returns>One row per display timeout, with only the choices the screensaver allows.</returns>
    internal SteamScreensaverState ReadState()
    {
        List<SteamTimeoutRow> rows = new(Rows.Length);
        foreach ((string row, PowerTimeoutKind kind, string label) in Rows)
        {
            int? current = _read(kind);
            int? minimum = Minimum(kind);
            SteamTimeoutOption[] options = current is null
                ? []
                : DisplayTimeoutPolicy.Choices(current.Value, minimum)
                    .Select(static seconds => new SteamTimeoutOption(seconds, PowerTimeouts.Describe(seconds)))
                    .ToArray();
            rows.Add(new SteamTimeoutRow(
                row,
                label,
                minimum is null ? string.Empty : $"Not before the screensaver starts ({PowerTimeouts.Describe(minimum.Value)})",
                current ?? 0,
                options,
                current is not null));
        }

        return new SteamScreensaverState(rows, Interlocked.Read(ref _revision));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ReportAsync(
        SteamScreensaverReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            _steam = report;
        }

        Log.Change(
            "steam.screensaver",
            $"Steam screensaver starts after {DescribeScreensaver(report.PluggedInSeconds)} plugged in, "
                + $"{(report.BatterySeconds is int battery ? DescribeScreensaver(battery) : "unset")} on battery; "
                + $"Steam {(report.Battery ? "keeps them apart" : "applies the plugged-in timeout everywhere")}.");
        foreach (PowerTimeoutKind kind in DisplayTimeoutPolicy.DisplayKinds)
        {
            RaiseBelowBound(kind);
        }

        OnChanged();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetTimeoutAsync(string row, int seconds, CancellationToken cancellationToken)
    {
        (string Row, PowerTimeoutKind Kind, string Label) target = Rows.FirstOrDefault(entry => entry.Row == row);
        if (target.Row is null)
        {
            return Refuse("That timeout row is not one of WSGM's.");
        }

        bool written;
        lock (PowerSchemes.MutationGate)
        {
            int? minimum = Minimum(target.Kind);
            if (!DisplayTimeoutPolicy.Allows(seconds, minimum))
            {
                return Refuse(
                    $"The display cannot turn off before Steam's screensaver starts ({PowerTimeouts.Describe(minimum!.Value)}).");
            }

            written = _write(target.Kind, seconds);
        }

        OnChanged();
        return written
            ? Task.FromResult(SteamUiCommandResult.Applied)
            : Refuse("Windows did not accept the display timeout.");
    }

    private void RaiseBelowBound(PowerTimeoutKind kind)
    {
        lock (PowerSchemes.MutationGate)
        {
            int? minimum = Minimum(kind);
            int? current = _read(kind);
            if (minimum is null || current is null || DisplayTimeoutPolicy.Allows(current.Value, minimum))
            {
                return;
            }

            int raised = DisplayTimeoutPolicy.Raised(minimum.Value);
            if (_write(kind, raised))
            {
                Log.Info($"Display timeout {kind} raised from {PowerTimeouts.Describe(current.Value)} to "
                    + $"{PowerTimeouts.Describe(raised)} so the display stays on until Steam's screensaver starts.");
            }
            else
            {
                Log.Warn($"Display timeout {kind} is {PowerTimeouts.Describe(current.Value)}, below Steam's screensaver "
                    + $"({PowerTimeouts.Describe(minimum.Value)}), and Windows refused raising it; left as it is.");
            }
        }
    }

    private void OnChanged()
    {
        Interlocked.Increment(ref _revision);
        Changed?.Invoke();
    }

    private static string DescribeScreensaver(int seconds) =>
        seconds == 0 ? "never (disabled)" : PowerTimeouts.Describe(seconds);

    private static Task<SteamUiCommandResult> Refuse(string reason) =>
        Task.FromResult(new SteamUiCommandResult(false, reason));
}
