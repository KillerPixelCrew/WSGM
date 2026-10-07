using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The session's one owner of the display-off timeouts, behind both the overlay's Power page and the
///     rows WSGM adds to Steam's Screensaver settings.
/// </summary>
/// <remarks>
///     Windows holds the values. The first publication after a write uses that written value; later
///     readings observe the active power scheme. This also holds Steam's screensaver timeouts as last
///     reported them, which bound the display timeouts (<see cref="DisplayTimeoutPolicy" />). A report that
///     finds a display timeout below its bound raises it once; a write Windows refuses is logged and not
///     retried, and the next attempt comes only from the next report or a choice the user makes.
/// </remarks>
internal sealed class DisplayTimeouts : ISteamScreensaverBackend
{
    private static readonly (string Row, PowerTimeoutKind Kind, string Label)[] Rows =
    [
        ("battery", PowerTimeoutKind.DisplayDc, "Turn display off after (on battery)"),
        ("plugged-in", PowerTimeoutKind.DisplayAc, "Turn display off after (plugged in)")
    ];

    private readonly Lock _gate = new();
    private readonly Dictionary<PowerTimeoutKind, int?> _observed = [];

    private readonly Func<PowerTimeoutKind, int?> _read;
    private readonly PowerSchemes _schemes;
    private readonly Func<PowerTimeoutKind, int, bool> _write;
    private bool _publishWritten;
    private long _revision;
    private SteamScreensaverReport? _steam;

    /// <summary>Creates the owner over the active power scheme.</summary>
    /// <param name="timeouts">The session's timeout owner, whose scheme lock every read-then-write takes.</param>
    internal DisplayTimeouts(PowerTimeouts timeouts)
        : this(timeouts.Schemes, timeouts.Read, timeouts.Write)
    {
    }

    /// <summary>Creates the owner over supplied reads and writes, for tests.</summary>
    /// <param name="schemes">The scheme owner whose mutation lock a read-then-write holds.</param>
    /// <param name="read">Reads one timeout in seconds, or null when Windows gives no answer.</param>
    /// <param name="write">Writes one timeout and reports whether Windows accepted it.</param>
    internal DisplayTimeouts(PowerSchemes schemes, Func<PowerTimeoutKind, int?> read,
        Func<PowerTimeoutKind, int, bool> write)
    {
        _schemes = schemes ?? throw new ArgumentNullException(nameof(schemes));
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
    }

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

    /// <summary>A detached copy of the last observations, including accepted writes; null values mean unreadable.</summary>
    internal IReadOnlyDictionary<PowerTimeoutKind, int?> ObservedValues
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<PowerTimeoutKind, int?>(_observed);
            }
        }
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
            + $"{(report.BatterySeconds is { } battery ? DescribeScreensaver(battery) : "unset")} on battery; "
            + $"Steam {(report.Battery ? "keeps them apart" : "applies the plugged-in timeout everywhere")}.");
        using (_schemes.EnterMutation())
        {
            var readings = DisplayTimeoutPolicy.DisplayKinds.Select(kind => (Kind: kind, Current: _read(kind)))
                .ToArray();
            lock (_gate)
            {
                foreach (var (kind, current) in readings)
                {
                    _observed[kind] = current;
                }
            }

            foreach (var (kind, current) in readings)
            {
                RaiseBelowBound(kind, current);
            }
        }

        OnChanged();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetTimeoutAsync(string row, int seconds, CancellationToken cancellationToken)
    {
        var target = Rows.FirstOrDefault(entry => entry.Row == row);
        if (target.Row is null)
        {
            return Refuse("That timeout row is not one of WSGM's.");
        }

        bool written;
        using (_schemes.EnterMutation())
        {
            var minimum = Minimum(target.Kind);
            if (!DisplayTimeoutPolicy.Allows(seconds, minimum))
            {
                return Refuse(
                    $"The display cannot turn off before Steam's screensaver starts ({PowerTimeouts.Describe(minimum!.Value)}).");
            }

            written = Write(target.Kind, seconds);
        }

        OnChanged();
        return written
            ? Task.FromResult(SteamUiCommandResult.Applied)
            : Refuse("Windows did not accept the display timeout.");
    }

    /// <summary>Raised synchronously after a report or an attempted selection; subscribers must not assume a write succeeded.</summary>
    internal event Action? Changed;

    /// <summary>Updates cached observations without writing Windows values or publishing a change event.</summary>
    /// <param name="values">Timeout kinds and their observed seconds, or null for unavailable readings.</param>
    internal void Observe(IReadOnlyDictionary<PowerTimeoutKind, int?> values)
    {
        lock (_gate)
        {
            foreach (var (kind, value) in values)
            {
                _observed[kind] = value;
            }
        }
    }

    /// <summary>Drops Steam's reported screensaver timeouts, so nothing bounds the display until Steam reports again.</summary>
    /// <remarks>
    ///     Clears stale bounds after a Steam restart, unavailable screensaver surface or disabled rows.
    ///     Does not write Windows timeout values.
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

        Log.Change("steam.screensaver",
            "Steam's screensaver timeouts no longer apply: the Screensaver settings are not active.");
        OnChanged();
    }

    /// <summary>The bound on one display timeout, or null when nothing bounds it.</summary>
    /// <param name="kind">The display timeout.</param>
    /// <returns>The minimum in seconds, or null.</returns>
    internal int? Minimum(PowerTimeoutKind kind)
    {
        return DisplayTimeoutPolicy.Minimum(kind, Steam);
    }

    /// <summary>Cycles one idle timeout to its next preset, skipping presets the screensaver forbids.</summary>
    /// <param name="kind">The timeout the overlay's row controls.</param>
    /// <returns>Whether a value was written.</returns>
    internal bool Cycle(PowerTimeoutKind kind)
    {
        bool written;
        using (_schemes.EnterMutation())
        {
            var current = _read(kind);
            if (current is null)
            {
                return false;
            }

            var next = DisplayTimeoutPolicy.DisplayKinds.Contains(kind)
                ? DisplayTimeoutPolicy.NextAllowed(current.Value, Minimum(kind))
                : PowerTimeouts.NextPreset(current.Value);
            written = Write(kind, next);
        }

        OnChanged();
        return written;
    }

    /// <summary>Applies an explicitly selected timeout while respecting Steam's screensaver boundary.</summary>
    /// <param name="kind">Timeout setting to write through the shared active-scheme lock.</param>
    /// <param name="seconds">Requested seconds; zero means never. Steam bounds can refuse the value.</param>
    /// <returns>Whether Windows accepted the write; false for a bounds refusal or native write failure.</returns>
    internal bool Select(PowerTimeoutKind kind, int seconds)
    {
        bool written;
        using (_schemes.EnterMutation())
        {
            if (!DisplayTimeoutPolicy.Allows(seconds, Minimum(kind)))
            {
                return false;
            }

            written = Write(kind, seconds);
        }

        OnChanged();
        return written;
    }

    /// <summary>Publishes accepted writes once, then resumes independent Windows readings.</summary>
    /// <returns>One row per display timeout, with only the choices the screensaver allows.</returns>
    internal SteamScreensaverState ReadState()
    {
        using (_schemes.EnterMutation())
        {
            return ReadStateUnderGate();
        }
    }

    private SteamScreensaverState ReadStateUnderGate()
    {
        List<SteamTimeoutRow> rows = [];
        bool publishWritten;
        lock (_gate)
        {
            publishWritten = _publishWritten;
            _publishWritten = false;
        }

        foreach (var (row, kind, label) in Rows)
        {
            int? current;
            if (publishWritten)
            {
                lock (_gate)
                {
                    current = _observed.GetValueOrDefault(kind);
                }
            }
            else
            {
                current = _read(kind);
                lock (_gate)
                {
                    _observed[kind] = current;
                }
            }

            var minimum = Minimum(kind);
            var options = current is null
                ? []
                : DisplayTimeoutPolicy.Choices(current.Value, minimum)
                    .Select(static seconds => new SteamTimeoutOption(seconds, PowerTimeouts.Describe(seconds)))
                    .ToArray();
            rows.Add(new SteamTimeoutRow(
                row,
                label,
                minimum is null
                    ? string.Empty
                    : $"Not before the screensaver starts ({PowerTimeouts.Describe(minimum.Value)})",
                current ?? 0,
                options,
                current is not null));
        }

        return new SteamScreensaverState(rows, Interlocked.Read(ref _revision));
    }

    private void RaiseBelowBound(PowerTimeoutKind kind, int? current)
    {
        using (_schemes.EnterMutation())
        {
            var minimum = Minimum(kind);
            if (minimum is null || current is null || DisplayTimeoutPolicy.Allows(current.Value, minimum))
            {
                return;
            }

            var raised = DisplayTimeoutPolicy.Raised(minimum.Value);
            if (Write(kind, raised))
            {
                Log.Info($"Display timeout {kind} raised from {PowerTimeouts.Describe(current.Value)} to "
                         + $"{PowerTimeouts.Describe(raised)} so the display stays on until Steam's screensaver starts.");
            }
            else
            {
                Log.Warn(
                    $"Display timeout {kind} is {PowerTimeouts.Describe(current.Value)}, below Steam's screensaver "
                    + $"({PowerTimeouts.Describe(minimum.Value)}), and Windows refused raising it; left as it is.");
            }
        }
    }

    private void OnChanged()
    {
        Interlocked.Increment(ref _revision);
        Changed?.Invoke();
    }

    private bool Write(PowerTimeoutKind kind, int seconds)
    {
        if (!_write(kind, seconds))
        {
            return false;
        }

        lock (_gate)
        {
            _observed[kind] = seconds;
            _publishWritten = true;
        }

        return true;
    }

    private static string DescribeScreensaver(int seconds)
    {
        return seconds == 0 ? "never (disabled)" : PowerTimeouts.Describe(seconds);
    }

    private static Task<SteamUiCommandResult> Refuse(string reason)
    {
        return Task.FromResult(new SteamUiCommandResult(false, reason));
    }
}
