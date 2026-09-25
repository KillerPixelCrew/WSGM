using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using WSGM.DeviceLab.Capture.Live;
using WSGM.DeviceLab.Knowledge;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One source that reacted during a step, as a candidate for what the tester pressed.</summary>
/// <param name="Source">Capture source.</param>
/// <param name="Device">Device ID, when known.</param>
/// <param name="DeviceDescription">Kind, USB IDs and collection.</param>
/// <param name="Evidence">What changed, readable.</param>
/// <param name="FirstMs">First reaction, in milliseconds after the step started.</param>
/// <param name="Events">How many events it produced.</param>
/// <param name="KnownRole">The knowledge record's role for this endpoint, when it matches one.</param>
internal sealed record LabInputCandidate(
    string Source,
    string? Device,
    string? DeviceDescription,
    IReadOnlyList<string> Evidence,
    double FirstMs,
    int Events,
    string? KnownRole);

/// <summary>Presses of one key on one source, counted by key-up.</summary>
/// <param name="Source">Capture source.</param>
/// <param name="Device">Device ID.</param>
/// <param name="Key">Key name.</param>
/// <param name="Presses">Key-ups.</param>
/// <param name="Downs">Key-downs, including auto-repeat.</param>
/// <param name="HoldsMs">How long each press was held.</param>
/// <param name="Swallowed">Whether the capture swallowed it as a shell shortcut.</param>
internal sealed record LabKeyPresses(
    string Source,
    string? Device,
    string Key,
    int Presses,
    int Downs,
    IReadOnlyList<double> HoldsMs,
    bool Swallowed);

/// <summary>One stick's travel.</summary>
/// <param name="MinX">Smallest X.</param>
/// <param name="MaxX">Largest X.</param>
/// <param name="MinY">Smallest Y.</param>
/// <param name="MaxY">Largest Y.</param>
/// <param name="StartCentre">X and Y at the start of the step.</param>
/// <param name="EndCentre">X and Y at the end, after release.</param>
/// <param name="MaxRadius">Largest distance from centre, 1 being full travel.</param>
/// <param name="Circularity">Share of sixteen direction sectors reached beyond 80 % travel.</param>
internal sealed record LabStickRange(
    int MinX,
    int MaxX,
    int MinY,
    int MaxY,
    IReadOnlyList<int> StartCentre,
    IReadOnlyList<int> EndCentre,
    double MaxRadius,
    double Circularity);

/// <summary>Trigger and stick ranges seen on one XInput device during a step.</summary>
/// <param name="Device">Device ID.</param>
/// <param name="Samples">State changes seen.</param>
/// <param name="LeftTriggerMax">Largest left trigger value (255 is full travel).</param>
/// <param name="RightTriggerMax">Largest right trigger value.</param>
/// <param name="Left">Left stick travel.</param>
/// <param name="Right">Right stick travel.</param>
internal sealed record LabAnalogRange(
    string Device,
    int Samples,
    int LeftTriggerMax,
    int RightTriggerMax,
    LabStickRange Left,
    LabStickRange Right);

/// <summary>Attributes a recorded step to candidate sources. Candidates, never proof.</summary>
internal static partial class LabInputAnalysis
{
    /// <summary>Lists the sources that reacted during a step, earliest first.</summary>
    /// <param name="step">Recorded step.</param>
    /// <param name="devices">Devices the capture knows.</param>
    /// <param name="record">The knowledge record the tester confirmed, used to name known endpoints.</param>
    /// <returns>The candidates.</returns>
    public static IReadOnlyList<LabInputCandidate> Candidates(
        LabInputStepRecord step,
        IReadOnlyList<LabInputDevice> devices,
        DeviceKnowledgeRecord? record)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(devices);
        var byId = devices.ToDictionary(device => device.Id, StringComparer.Ordinal);
        Dictionary<string, (string Source, string? Device, SortedSet<string> Evidence, double First, int Count)>
            groups = new(StringComparer.Ordinal);
        Dictionary<string, Dictionary<int, SortedSet<string>>> reportBytes = new(StringComparer.Ordinal);

        foreach (var item in step.Events)
        {
            if (item.Noise || item.Source is "device" or "error")
            {
                continue;
            }

            string key;
            string? evidence;
            if (item.Source == "raw-input" && item.Data is { } data)
            {
                if (item.Changed is not { Count: > 0 } changed)
                {
                    continue;
                }

                var reportId = data.Length >= 2 ? data[..2] : "00";
                key = $"raw-input|{item.Device}|report {reportId}";
                var bytes = reportBytes.TryGetValue(key, out var existing) ? existing : reportBytes[key] = [];
                foreach (var offset in changed)
                {
                    if (offset * 2 + 2 <= data.Length)
                    {
                        var values = bytes.TryGetValue(offset, out var set) ? set : bytes[offset] = [];
                        if (values.Count < 6)
                        {
                            values.Add(data.Substring(offset * 2, 2));
                        }
                    }
                }

                evidence = null;
            }
            else if (KeyPattern().Match(item.Detail) is { Success: true } keyMatch)
            {
                key = $"{item.Source}|{item.Device}|key {keyMatch.Groups["name"].Value}";
                evidence = item.Detail;
            }
            else if (item.Source == "xinput" && XInputButtons().Match(item.Detail) is { Success: true } xinput)
            {
                key = $"xinput|{item.Device}";
                evidence = xinput.Groups["names"].Value.Length > 0 ? xinput.Groups["names"].Value : "axes";
                foreach (var name in evidence.Split(','))
                {
                    Add(groups, key, item, name, step.StartedMs);
                }

                continue;
            }
            else if (item.Source == "wgi" && WgiButtons().Match(item.Detail) is { Success: true } wgi)
            {
                key = $"wgi|{item.Device}";
                evidence = wgi.Groups["buttons"].Value.Length > 0
                    ? "buttons " + wgi.Groups["buttons"].Value
                    : "switch or axis";
            }
            else
            {
                key = $"{item.Source}|{item.Device}|{item.Detail}";
                evidence = item.Detail;
            }

            Add(groups, key, item, evidence, step.StartedMs);
        }

        foreach (var (key, bytes) in reportBytes)
        {
            if (groups.TryGetValue(key, out var group))
            {
                foreach (var (offset, values) in bytes.OrderBy(pair => pair.Key))
                {
                    group.Evidence.Add(
                        $"{key[(key.LastIndexOf('|') + 1)..]} byte {offset}: {string.Join(" ", values)}");
                }
            }
        }

        return
        [
            .. groups.Values
                .OrderBy(group => group.First)
                .Select(group =>
                {
                    var device = group.Device is not null && byId.TryGetValue(group.Device, out var found)
                        ? found
                        : null;
                    return new LabInputCandidate(
                        group.Source,
                        group.Device,
                        device is null ? null : Describe(device),
                        [.. group.Evidence.Take(12)],
                        Math.Round(group.First, 1),
                        group.Count,
                        device is null ? null : KnownRole(device, record));
                })
        ];
    }

    /// <summary>
    ///     Key presses per source, device and key, counted by key-up so firmware auto-repeat does not
    ///     inflate them, with how long each was held.
    /// </summary>
    /// <param name="step">Recorded step.</param>
    /// <returns>One entry per source, device and key.</returns>
    public static IReadOnlyList<LabKeyPresses> Keys(LabInputStepRecord step)
    {
        ArgumentNullException.ThrowIfNull(step);
        Dictionary<string, KeyTally> keys = new(StringComparer.Ordinal);
        foreach (var item in step.Events)
        {
            var match = KeyEdge().Match(item.Detail);
            if (!match.Success)
            {
                continue;
            }

            var id = $"{item.Source}|{item.Device}|{match.Groups["name"].Value}";
            if (!keys.TryGetValue(id, out var entry))
            {
                entry = keys[id] = new KeyTally(item.Source, item.Device, match.Groups["name"].Value);
            }

            if (match.Groups["edge"].Value == "down")
            {
                entry.Downs++;
                entry.Down ??= item.Ms;
            }
            else
            {
                entry.Ups++;
                if (entry.Down is { } down)
                {
                    entry.Holds.Add(Math.Round(item.Ms - down, 1));
                }

                entry.Down = null;
            }

            entry.Swallowed |= item.Detail.EndsWith("swallowed", StringComparison.Ordinal);
        }

        return
        [
            .. keys.Values.Select(entry => new LabKeyPresses(entry.Source, entry.Device, entry.Key, entry.Ups,
                entry.Downs, entry.Holds, entry.Swallowed))
        ];
    }

    /// <summary>Stick and trigger ranges per XInput slot: centre, extremes and how round the stick travel is.</summary>
    /// <param name="step">Recorded step.</param>
    /// <returns>One entry per XInput device.</returns>
    public static IReadOnlyList<LabAnalogRange> Analog(LabInputStepRecord step)
    {
        ArgumentNullException.ThrowIfNull(step);
        Dictionary<string, List<int[]>> samples = new(StringComparer.Ordinal);
        foreach (var item in step.Events)
        {
            if (item.Source != "xinput" || item.Device is null || XInputState().Match(item.Detail) is not
                    { Success: true } match)
            {
                continue;
            }

            var values = new[] { "lt", "rt", "lx", "ly", "rx", "ry" }
                .Select(name => int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture)).ToArray();
            (samples.TryGetValue(item.Device, out var list) ? list : samples[item.Device] = []).Add(values);
        }

        return
        [
            .. samples.Select(pair => new LabAnalogRange(
                pair.Key,
                pair.Value.Count,
                pair.Value.Max(values => values[0]),
                pair.Value.Max(values => values[1]),
                Stick(pair.Value, 2),
                Stick(pair.Value, 4)))
        ];
    }

    private static LabStickRange Stick(List<int[]> samples, int x)
    {
        // Circularity: the share of sixteen direction sectors the stick reached beyond 80 % travel.
        HashSet<int> sectors = [];
        double maxRadius = 0;
        foreach (var values in samples)
        {
            var radius = Math.Sqrt((double)values[x] * values[x] + (double)values[x + 1] * values[x + 1]) / 32767.0;
            maxRadius = Math.Max(maxRadius, radius);
            if (radius > 0.8)
            {
                var angle = Math.Atan2(values[x + 1], values[x]);
                sectors.Add((int)Math.Floor((angle + Math.PI) / (2 * Math.PI) * 16) % 16);
            }
        }

        var first = samples[0];
        var last = samples[^1];
        return new LabStickRange(
            samples.Min(values => values[x]), samples.Max(values => values[x]),
            samples.Min(values => values[x + 1]), samples.Max(values => values[x + 1]),
            [first[x], first[x + 1]], [last[x], last[x + 1]],
            Math.Round(maxRadius, 3), sectors.Count / 16.0);
    }

    /// <summary>Describes a device in one line.</summary>
    /// <param name="device">Device.</param>
    /// <returns>The description.</returns>
    public static string Describe(LabInputDevice device)
    {
        var ids = device.VendorId is null ? string.Empty : $" {device.VendorId}:{device.ProductId}";
        var collection = device.UsagePage is { } page ? $" {page:X4}:{device.Usage:X4}" : string.Empty;
        var name = device.Name is null ? string.Empty : $" {device.Name}";
        return $"{device.Kind}{ids}{collection}{name}{(device.Virtual ? " (virtual)" : string.Empty)}";
    }

    /// <summary>
    ///     Leaves out what the tester's own touches on the screen produce (mouse, touch and pen input), for
    ///     display only. The recorded step keeps everything.
    /// </summary>
    /// <param name="candidates">Candidates.</param>
    /// <returns>The candidates worth showing.</returns>
    public static IReadOnlyList<LabInputCandidate> WithoutPointer(IReadOnlyList<LabInputCandidate> candidates)
    {
        return
        [
            .. candidates.Where(candidate =>
                candidate.DeviceDescription?.StartsWith("mouse", StringComparison.Ordinal) != true
                && candidate.DeviceDescription?.Contains(" 000D:", StringComparison.Ordinal) != true
                && !(candidate.Source == "hook" &&
                     candidate.Evidence.All(item => item.StartsWith("mouse", StringComparison.Ordinal))))
        ];
    }

    /// <summary>A short line for the tester: which sources reacted.</summary>
    /// <param name="candidates">Candidates.</param>
    /// <returns>The line.</returns>
    public static string Summary(IReadOnlyList<LabInputCandidate> candidates)
    {
        return candidates.Count == 0
            ? "Nothing reacted."
            : string.Join("; ", candidates.Take(4).Select(candidate =>
                $"{candidate.Source} {candidate.DeviceDescription ?? string.Empty} {candidate.Evidence.FirstOrDefault()}"
                    .Trim()));
    }

    private static void Add(
        Dictionary<string, (string Source, string? Device, SortedSet<string> Evidence, double First, int Count)> groups,
        string key,
        LabInputEvent item,
        string? evidence,
        double stepStarted)
    {
        if (!groups.TryGetValue(key, out var group))
        {
            group = (item.Source, item.Device, new SortedSet<string>(StringComparer.Ordinal), item.Ms - stepStarted, 0);
        }

        if (evidence is not null && group.Evidence.Count < 24)
        {
            group.Evidence.Add(evidence);
        }

        groups[key] = group with { Count = group.Count + 1 };
    }

    private static string? KnownRole(LabInputDevice device, DeviceKnowledgeRecord? record)
    {
        if (record is null || device.VendorId is null)
        {
            return null;
        }

        return record.HidEndpoints.FirstOrDefault(endpoint =>
                string.Equals(endpoint.VendorId, device.VendorId, StringComparison.OrdinalIgnoreCase)
                && (endpoint.ProductIds.Count == 0
                    || endpoint.ProductIds.Contains(device.ProductId ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                && (endpoint.UsagePage is null || endpoint.UsagePage == device.UsagePage)
                && (endpoint.Usage is null || endpoint.Usage == device.Usage))
            ?.Role;
    }

    /// <summary>Parses a hex byte, for tests and analysis.</summary>
    /// <param name="hex">Two hex digits.</param>
    /// <returns>The value.</returns>
    public static int HexByte(string hex)
    {
        return int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^key (?<name>\S+) \(.*\) (?<edge>down|up)")]
    private static partial Regex KeyEdge();

    [GeneratedRegex(@"LT (?<lt>\d+) RT (?<rt>\d+) L (?<lx>-?\d+),(?<ly>-?\d+) R (?<rx>-?\d+),(?<ry>-?\d+)")]
    private static partial Regex XInputState();

    [GeneratedRegex(@"^key (?<name>\S+) .*(?:down|up)")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^buttons [0-9A-F]{4} \[(?<names>[^\]]*)\]")]
    private static partial Regex XInputButtons();

    [GeneratedRegex(@"^buttons \[(?<buttons>[^\]]*)\]")]
    private static partial Regex WgiButtons();

    private sealed class KeyTally(string source, string? device, string key)
    {
        public string Source { get; } = source;

        public string? Device { get; } = device;

        public string Key { get; } = key;

        public int Downs { get; set; }

        public int Ups { get; set; }

        public double? Down { get; set; }

        public List<double> Holds { get; } = [];

        public bool Swallowed { get; set; }
    }
}
