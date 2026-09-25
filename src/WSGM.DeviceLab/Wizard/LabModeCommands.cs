using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Capture.Live;
using WSGM.DeviceLab.Knowledge;
using WSGM.Interop;

namespace WSGM.DeviceLab.Wizard;

/// <summary>The wizard stages a mode command is offered in.</summary>
[Flags]
internal enum LabModeStages
{
    /// <summary>No stage.</summary>
    None = 0,

    /// <summary>The buttons stage.</summary>
    Buttons = 1,

    /// <summary>The motion stage, before the poses.</summary>
    Motion = 2
}

/// <summary>How a mode command reaches the controller.</summary>
internal enum LabModeWrite
{
    /// <summary>Output reports through <c>WriteFile</c>, padded to the collection's output length.</summary>
    Output,

    /// <summary>Feature reports through <c>HidD_SetFeature</c>, padded to the collection's feature length.</summary>
    Feature,

    /// <summary>
    ///     A controller mode switch whose current mode is read from the product ID, sent and restored through
    ///     <see cref="LabControllerInit" />.
    /// </summary>
    ControllerMode
}

/// <summary>How the bytes of a report are laid out before they are written.</summary>
internal enum LabModeFraming
{
    /// <summary>The report as written, report ID first, padded with zeros.</summary>
    Padded,

    /// <summary>
    ///     A OneXPlayer vendor command <c>"cid: payload"</c>, framed as HC's
    ///     <c>OneXPlayerX1.WriteVendorHidCommand</c> frames it: <c>cid 3F 01 payload ... 3F cid</c>.
    /// </summary>
    OxpVendor
}

/// <summary>The collection a mode command is written to. Every set field must match exactly.</summary>
/// <param name="VendorId">USB vendor ID.</param>
/// <param name="ProductIds">USB product IDs; empty accepts every product of the vendor, as HC does.</param>
/// <param name="UsagePage">Top-level collection usage page, when HC or HHD selects by it.</param>
/// <param name="Usage">Top-level collection usage, when HC or HHD selects by it.</param>
/// <param name="Interface">USB interface number (<c>MI_xx</c>), when HHD or HC selects by it.</param>
/// <param name="InputLengths">Accepted input report lengths, report ID included; empty accepts any.</param>
/// <param name="OutputLength">Required output report length, report ID included.</param>
internal sealed record LabModeEndpoint(
    ushort VendorId,
    IReadOnlyList<ushort> ProductIds,
    ushort? UsagePage = null,
    ushort? Usage = null,
    int? Interface = null,
    IReadOnlyList<int>? InputLengths = null,
    int? OutputLength = null);

/// <summary>One controller mode or init command HC (or HHD) sends, reviewed for the wizard.</summary>
internal sealed record LabModeCommand
{
    /// <summary>Stable ID, recorded in evidence and in the crash-recovery file.</summary>
    public required string Id { get; init; }

    /// <summary>
    ///     What the command does. Class-bound commands with the same group override each other like HC's
    ///     virtual methods: the one nearest the device class wins.
    /// </summary>
    public required string Group { get; init; }

    /// <summary>The device the command is for, shown to the tester.</summary>
    public required string Device { get; init; }

    /// <summary>Stages that offer it.</summary>
    public required LabModeStages Stages { get; init; }

    /// <summary>
    ///     The HC device class that sends it; null for a controller HC drives by its USB ID on any device.
    /// </summary>
    public string? HcClass { get; init; }

    /// <summary>The collection it is written to.</summary>
    public required LabModeEndpoint Endpoint { get; init; }

    /// <summary>How it is written.</summary>
    public required LabModeWrite Write { get; init; }

    /// <summary>How each report is framed.</summary>
    public LabModeFraming Framing { get; init; } = LabModeFraming.Padded;

    /// <summary>Reports, as hex, in the order HC sends them.</summary>
    public IReadOnlyList<string> Reports { get; init; } = [];

    /// <summary>Reports that undo it, as hex; empty when HC and HHD have none.</summary>
    public IReadOnlyList<string> Restore { get; init; } = [];

    /// <summary>For <see cref="LabModeWrite.ControllerMode" />: the <see cref="LabControllerInit" /> parameters.</summary>
    public IReadOnlyDictionary<string, string> ModeParameters { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether the state before the command can be read back.</summary>
    public bool Readable { get; init; }

    /// <summary>When above zero, the reports are sent again at this interval while the stage runs, as HC does.</summary>
    public int RepeatMs { get; init; }

    /// <summary>Pause between reports, as HC pauses.</summary>
    public int DelayMs { get; init; } = 20;

    /// <summary>One plain line for the tester.</summary>
    public required string Description { get; init; }

    /// <summary>HC and HHD file:line references for the bytes, the match and the restore.</summary>
    public required IReadOnlyList<string> Provenance { get; init; }

    /// <summary>What the lab deliberately leaves out of HC's sequence, or where HC and HHD disagree.</summary>
    public string? Note { get; init; }

    /// <summary>When set, HC sends something here that the lab does not; the reason is recorded, nothing is offered.</summary>
    public string? Withheld { get; init; }

    /// <summary>Whether the lab can undo it: a readable mode switch, or restore reports.</summary>
    public bool Reversible => Write == LabModeWrite.ControllerMode || Restore.Count > 0;
}

/// <summary>One HID collection as a mode command sees it.</summary>
/// <param name="Endpoint">The collection; its path stays out of the evidence.</param>
/// <param name="InputLength">Input report length, report ID included.</param>
/// <param name="FeatureLength">Feature report length, report ID included.</param>
/// <param name="Interface">USB interface number from the path, when it has one.</param>
internal sealed record LabModeHid(LabRumbleHidEndpoint Endpoint, int InputLength, int FeatureLength, int? Interface);

/// <summary>One write and its result.</summary>
/// <param name="Phase"><c>send</c> or <c>restore</c>.</param>
/// <param name="Report">The bytes written, as hex.</param>
/// <param name="Result"><c>ok</c> or the Windows error.</param>
/// <param name="At">When it was written.</param>
internal sealed record LabModeWriteRecord(string Phase, string Report, string Result, DateTimeOffset At);

/// <summary>A command recorded before it was sent, so a killed session can undo it.</summary>
/// <param name="Id">Command ID.</param>
/// <param name="RecordedAt">When it was recorded.</param>
internal sealed record LabPendingModeCommand(string Id, DateTimeOffset RecordedAt);

/// <summary>What a stage did with one command, for the evidence.</summary>
/// <param name="Id">Command ID.</param>
/// <param name="Outcome"><c>sent</c>, <c>failed</c>, <c>skipped</c>, <c>not-present</c> or <c>withheld</c>.</param>
/// <param name="Reversible">Whether the lab undoes it.</param>
/// <param name="Readable">Whether the state before it can be read.</param>
/// <param name="Collection">The collection written to, without its path.</param>
/// <param name="Writes">Every write and its result.</param>
/// <param name="Repeats">Repeat cycles sent after the first.</param>
/// <param name="RepeatStopped">Why repeating stopped early, when it did.</param>
/// <param name="Mode">The controller mode switch, for a mode command.</param>
/// <param name="Problem">What went wrong, or why it was not offered.</param>
internal sealed record LabModeEvidence(
    string Id,
    string Outcome,
    bool Reversible,
    bool Readable,
    object? Collection,
    IReadOnlyList<LabModeWriteRecord> Writes,
    long Repeats,
    string? RepeatStopped,
    LabInitResult? Mode,
    string? Problem);

/// <summary>A command that was tried in a stage, possibly still repeating.</summary>
internal sealed class LabModeSession
{
    private readonly Lock _gate = new();
    private readonly List<LabModeWriteRecord> _writes = [];
    private SafeFileHandle? _handle;
    private Task? _loop;
    private CancellationTokenSource? _repeat;
    private string? _repeatStopped;
    private long _repeats;

    internal LabModeSession(LabModeCommand command, LabModeHid? hid)
    {
        Command = command;
        Hid = hid;
    }

    /// <summary>The command.</summary>
    public LabModeCommand Command { get; }

    /// <summary>The collection it went to.</summary>
    public LabModeHid? Hid { get; }

    /// <summary>Whether every report of the first send was written.</summary>
    public bool Sent { get; internal set; }

    /// <summary>What went wrong.</summary>
    public string? Problem { get; internal set; }

    /// <summary>The controller mode switch, for a mode command.</summary>
    public LabInitResult? Mode { get; internal set; }

    /// <summary>Whether a restore is owed.</summary>
    public bool RestoreOwed { get; internal set; }

    /// <summary>Whether it was stopped and, when owed, restored.</summary>
    public bool Stopped { get; internal set; }

    /// <summary>The restore's problem, when the restore failed.</summary>
    public string? RestoreProblem { get; internal set; }

    internal void Add(LabModeWriteRecord record)
    {
        lock (_gate)
        {
            _writes.Add(record);
        }
    }

    internal void StartRepeating(SafeFileHandle handle, IReadOnlyList<byte[]> frames, CancellationToken lifetime)
    {
        _handle = handle;
        _repeat = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var token = _repeat.Token;
        var interval = TimeSpan.FromMilliseconds(Command.RepeatMs);
        _loop = Task.Run(async () =>
        {
            using PeriodicTimer timer = new(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    foreach (var frame in frames)
                    {
                        var error = LabModeCommands.WriteFrame(handle, Command.Write, frame);
                        if (error is not null)
                        {
                            // A failed write is not retried: repeating stops and the evidence says so.
                            _repeatStopped = $"{LabModeCommands.Hex(frame)}: {error}";
                            return;
                        }
                    }

                    Interlocked.Increment(ref _repeats);
                }
            }
            catch (OperationCanceledException)
            {
                // The stage ended.
            }
        }, CancellationToken.None);
    }

    /// <summary>Stops repeating and releases the handle; restores nothing. Safe to call more than once.</summary>
    public void StopRepeating()
    {
        var repeat = Interlocked.Exchange(ref _repeat, null);
        if (repeat is null)
        {
            return;
        }

        repeat.Cancel();
        if (_loop is null || _loop.Wait(TimeSpan.FromSeconds(3)))
        {
            _handle?.Dispose();
        }

        repeat.Dispose();
    }

    /// <summary>The evidence for this command.</summary>
    /// <returns>The record.</returns>
    public LabModeEvidence Evidence()
    {
        lock (_gate)
        {
            return new LabModeEvidence(Command.Id, Sent ? "sent" : "failed", Command.Reversible, Command.Readable,
                Hid is null ? null : LabModeCommands.Describe(Hid), [.. _writes], Interlocked.Read(ref _repeats),
                _repeatStopped, Mode, RestoreProblem ?? Problem);
        }
    }
}

/// <summary>
///     The controller mode and init commands HC (and HHD, where it has them) sends for devices that have no
///     curated record, compiled and reviewed here rather than read from inventory.
/// </summary>
/// <remarks>
///     A command is offered only when the tester opts in, only when the confirmed record is not curated
///     (curated devices keep <see cref="LabControllerInit" />), and only when exactly one present collection
///     matches its endpoint. Every write and result goes into the evidence and none is retried. A reversible
///     command is recorded in the crash-recovery file before it is sent and undone at the stage end and, if
///     the session was killed, on the next start. HC's writes to the EC (the OXP turbo takeover), its factory
///     resets, the GameSir extra-button reset and the lighting commands are deliberately left out.
/// </remarks>
internal static class LabModeCommands
{
    private const string Hc = "_ref/HandheldCompanion 1.3.1.6: ";
    private const string HcDevices = Hc + "HandheldCompanion/";
    private const string Hhd = "_ref/hhd: src/hhd/device/";

    private const string OxpPage1 =
        "02 38 20 01 01 01 01 01 00 00 00 02 01 02 00 00 00 03 01 03 00 00 00 04 01 04 00 00 00 05 01 05 00 00 00 06 01 06 00 00 00 07 01 07 00 00 00 08 01 08 00 00 00 09 01 09 00 00 00";

    private const string OxpPage2M1F2 =
        "02 38 20 02 01 0A 01 0A 00 00 00 0B 01 0B 00 00 00 0C 01 0C 00 00 00 0D 01 0D 00 00 00 0E 01 0E 00 00 00 0F 01 0F 00 00 00 10 01 10 00 00 00 22 02 01 67 00 00 23 02 01 66 00 00";

    private const string OxpPage3 = "02 38 20 03 01 24 02 02 05 00 00 25 01 21 00 00 00";

    private const string OxpV2Page1 =
        "02 38 02 01 01 01 01 01 00 00 00 02 01 02 00 00 00 03 01 03 00 00 00 04 01 04 00 00 00 05 01 05 00 00 00 06 01 06 00 00 00 07 01 07 00 00 00 08 01 08 00 00 00 09 01 09 00 00 00";

    private const string OxpV2Page2M67 =
        "02 38 02 02 01 0A 01 0A 00 00 00 0B 01 0B 00 00 00 0C 01 0C 00 00 00 0D 01 0D 00 00 00 0E 01 0E 00 00 00 0F 01 0F 00 00 00 10 01 10 00 00 00 22 02 01 67 00 00 23 02 01 66 00 00";

    private const string OxpV2Page2M68 =
        "02 38 02 02 01 0A 01 0A 00 00 00 0B 01 0B 00 00 00 0C 01 0C 00 00 00 0D 01 0D 00 00 00 0E 01 0E 00 00 00 0F 01 0F 00 00 00 10 01 10 00 00 00 22 02 01 68 00 00 23 02 01 69 00 00";

    private const string OxpV2Page3 = "02 38 02 03 01 24 02 02 05 00 00 25 01 21 00 00 00";

    private const string OxpRemapDescription =
        "Write Handheld Companion's button layout to the controller, so the extra buttons report. This replaces the controller's own layout, which cannot be read back or undone by this tool; the OneXConsole app can set it again.";

    private const string SteamLizardOff = "00 81 00|00 87 03 08 07 00|00 87 03 07 07 00";
    private const string SteamLizardOn = "00 85 00|00 8E 00";

    private static readonly string StatePath = Path.Combine(
        Path.GetDirectoryName(LabMachineState.ForCurrentUser.Path)!, "mode-commands.json");

    private static readonly LabModeEndpoint LegionTablet = new(0x17EF,
        [0x6182, 0x6183, 0x6184, 0x6185, 0x61EB, 0x61EC, 0x61ED, 0x61EE], 0xFFA0, 0x0001);

    private static readonly LabModeEndpoint LegionGoS = new(0x1A86, [0xE310, 0xE311], 0xFFA0, 0x0001, 3, [65], 65);
    private static readonly LabModeEndpoint SteamDeck = new(0x28DE, [0x1205, 0x12F0], InputLengths: [65, 64]);
    private static readonly LabModeEndpoint SteamController = new(0x28DE, [0x1102, 0x1142], InputLengths: [65, 64]);
    private static readonly LabModeEndpoint OxpX1 = new(0x1A86, [0xFE00], 0xFF00, 0x0001);
    private static readonly LabModeEndpoint OxpX2 = new(0x1A86, [0xFE00, 0x1305], 0xFF00, 0x0001);

    /// <summary>Every reviewed command, in the order a stage offers them.</summary>
    public static IReadOnlyList<LabModeCommand> All { get; } =
    [
        // Legion Go (tablet, 2023 and 2025 firmware, and Go 2). The mode switch comes first: it
        // re-enumerates the controllers, and the commands after it look for their collection again.
        new()
        {
            Id = "legion-go-controller-mode",
            Group = "legion-controller-mode",
            Device = "Legion Go controllers",
            Stages = LabModeStages.Buttons,
            HcClass = "LegionGoTablet",
            Endpoint = LegionTablet,
            Write = LabModeWrite.ControllerMode,
            Readable = true,
            ModeParameters = new Dictionary<string, string>
            {
                ["vendorId"] = "17EF",
                ["report"] = "05 00 04 0E 03 <mode>",
                ["modeFromProductId"] = "6182 1, 6183 2, 61EB 1, 61EC 2",
                ["commandEndpoints"] = "6182 FFA0:0001, 61EB FFA0:0001, 6183 FFA0:0001, 61EC FFA0:0001",
                ["testMode"] = "1"
            },
            Description =
                "Switch the Legion controllers to XInput, the mode Handheld Companion uses by default. It is switched back at the end.",
            Provenance =
            [
                HcDevices +
                "HandheldCompanion.Devices.Lenovo/LegionGoTablet.cs:249 (ApplyGamepadMode: 05 00 04 0E 03 mode)",
                HcDevices + "HandheldCompanion.Devices.Lenovo/LegionGoTablet.cs:16 (XInput = 1, DInput = 2)",
                HcDevices +
                "HandheldCompanion.Devices.Lenovo/LegionGoTablet.cs:37 (collection FFA0:0001 per product ID)",
                HcDevices + "app.config:1361 (LegionControllerMode defaults to 0, XInput)",
                Hhd + "legion_go/tablet/base.py:35 (product ID per mode: 6182/61EB xinput, 6183/61EC dinput)"
            ],
            Note =
                "Dual DInput (6184/61ED) and FPS (6185/61EE) have no HC mode value, so the current mode cannot be read there and nothing is sent."
        },
        new()
        {
            Id = "legion-go-touchpad-passthrough-off",
            Group = "legion-touchpad",
            Device = "Legion Go controllers",
            Stages = LabModeStages.Buttons,
            HcClass = "LegionGoTablet",
            Endpoint = LegionTablet,
            Write = LabModeWrite.Output,
            Reports = ["05 06 6B 02 04 00 01"],
            Description =
                "Have the controller report the touchpad itself instead of passing it to Windows, as Handheld Companion does. The earlier setting cannot be read, so this tool does not change it back.",
            Provenance =
            [
                HcDevices +
                "HandheldCompanion.Devices.Lenovo/LegionGoTablet.cs:293 (SetPassthrough: 05 06 6B 02 04 enabled 01)",
                HcDevices +
                "HandheldCompanion.Devices.Lenovo/LegionGo.cs:296 (HC sends passthrough off when it closes)",
                HcDevices + "app.config:1157 (LegionControllerPassthrough defaults to False)"
            ]
        },
        new()
        {
            Id = "legion-go-gyro-on",
            Group = "legion-gyro",
            Device = "Legion Go controllers",
            Stages = LabModeStages.Motion,
            HcClass = "LegionGoTablet",
            Endpoint = LegionTablet,
            Write = LabModeWrite.Output,
            Reports = ["05 06 6A 02 03 01 01", "05 06 6A 07 03 02 01", "05 06 6A 02 04 01 01", "05 06 6A 07 04 02 01"],
            Restore = ["05 06 6A 07 03 01 01", "05 06 6A 07 04 01 01"],
            Description =
                "Turn on the gyro in both detachable controllers, as Handheld Companion does. They are turned off again at the end, as HHD does when it exits; whether they were on before cannot be read.",
            Provenance =
            [
                HcDevices +
                "HandheldCompanion.Devices.Lenovo/LegionGoTablet.cs:225 (EnableControllerGyro, sent for controllers 3 and 4 at line 209)",
                HcDevices + "HandheldCompanion.Devices.Lenovo/LegionGoTablet.cs:235 (DisableControllerGyro)",
                Hhd + "legion_go/tablet/hid.py:113 (controller_enable_gyro: same bytes)",
                Hhd + "legion_go/tablet/hid.py:324 (close disables both gyros)"
            ],
            Note = "HC first sends a controller factory reset (04 05 05 01 01 n 01); the lab leaves it out."
        },

        // Legion Go S.
        new()
        {
            Id = "legion-go-s-touchpad-passthrough-off",
            Group = "legion-touchpad",
            Device = "Legion Go S controller",
            Stages = LabModeStages.Buttons,
            HcClass = "LegionGoSZ1",
            Endpoint = LegionGoS,
            Write = LabModeWrite.Output,
            Reports = ["00 04 08 01", "00 08 03 01"],
            Description =
                "Send Handheld Companion's touchpad setup for the Legion Go S (touchpad on, touchpad vibration on). The earlier settings cannot be read, so this tool does not change them back.",
            Provenance =
            [
                HcDevices +
                "HandheldCompanion.Devices.Lenovo/LegionGoSZ1.cs:110 (SetPassthrough(false): 04 08 01 and 08 03 01 after report ID 0)",
                HcDevices +
                "HandheldCompanion.Devices.Lenovo/LegionGoSZ1.cs:67 (collection with 65-byte input and output reports)",
                Hhd + "legion_go/slim/hid.py:137 (04 08 01 enables the touchpad; 08 03 00 disables its vibration)",
                Hhd + "legion_go/slim/base.py:167 (configuration interface 3, FFA0:0001)"
            ]
        },
        new()
        {
            Id = "legion-go-s-gyro-on",
            Group = "legion-gyro",
            Device = "Legion Go S controller",
            Stages = LabModeStages.Motion,
            HcClass = "LegionGoSZ1",
            Endpoint = LegionGoS,
            Write = LabModeWrite.Output,
            Reports = ["00 04 07 01", "00 04 05 01"],
            Description =
                "Turn on the Legion Go S controller's gyro reports, the two gyro commands from Handheld Companion's controller setup. This tool cannot read them first or turn them off again.",
            Provenance =
            [
                HcDevices +
                "HandheldCompanion.Devices.Lenovo/LegionGoSZ1.cs:105 (04 07 01 and 04 05 01 in ControllerFactoryReset)",
                Hhd + "legion_go/slim/hid.py:143 (04 07 01 enables the gyro, 04 05 01 the HID IMU)"
            ],
            Note =
                "HC sends these as part of a factory reset that also resets the XInput mapping; the lab sends only the two gyro commands."
        },

        // Steam Deck and Steam Controller: HC drives these by USB ID on any device.
        new()
        {
            Id = "steam-deck-lizard-off",
            Group = "steam-lizard",
            Device = "Steam Deck controller",
            Stages = LabModeStages.Buttons | LabModeStages.Motion,
            Endpoint = SteamDeck,
            Write = LabModeWrite.Feature,
            Reports = SteamLizardOff.Split('|'),
            Restore = SteamLizardOn.Split('|'),
            RepeatMs = 1000,
            Description =
                "Turn off the Deck's built-in keyboard and mouse emulation (lizard mode) while this step runs, sent again every second as Handheld Companion does. It is turned back on at the end.",
            Provenance =
            [
                Hc +
                "steam-hidapi.net/steam_hidapi.net/SteamController.cs:89 (SetLizardMode: CLEAR_MAPPINGS, RPAD_MODE 7, LPAD_MODE 7; on: DEFAULT_MAPPINGS, DEFAULT_MOUSE)",
                Hc +
                "steam-hidapi.net/steam_hidapi.net/SteamController.cs:40 (commands and registers are feature reports: 81 00, 87 03 reg lo hi)",
                Hc +
                "steam-hidapi.net/steam_hidapi.net/NeptuneController.cs:75 (ConfigureLoop re-sends lizard off every 1000 ms)",
                Hc + "hidapi.net/hidapi/HidDevice.cs:254 (feature reports go out with report ID 0 first)",
                HcDevices +
                "HandheldCompanion.Controllers.Steam/NeptuneController.cs:107 (input report length 65, then 64)",
                HcDevices + "HandheldCompanion.Managers/ControllerManager.cs:1760 (product IDs 1205 and 12F0)"
            ],
            Note =
                "HC sends no separate IMU command for the Deck; the lizard mode loop is all it writes while it reads the gyro."
        },
        new()
        {
            Id = "steam-controller-lizard-off",
            Group = "steam-lizard",
            Device = "Steam Controller",
            Stages = LabModeStages.Buttons,
            Endpoint = SteamController,
            Write = LabModeWrite.Feature,
            Reports = SteamLizardOff.Split('|'),
            Restore = SteamLizardOn.Split('|'),
            Description =
                "Turn off the Steam Controller's built-in keyboard and mouse emulation (lizard mode), as Handheld Companion does. It is turned back on at the end.",
            Provenance =
            [
                Hc + "steam-hidapi.net/steam_hidapi.net/SteamController.cs:89 (SetLizardMode)",
                HcDevices +
                "HandheldCompanion.Controllers.Steam/GordonController.cs:223 (lizard off on open, on again at line 242)",
                HcDevices + "HandheldCompanion.Managers/ControllerManager.cs:1739 (1102 on interface 2, and 1142)"
            ],
            Note = "HC also sets IDLE_TIMEOUT to 300 while open and 0 on close; the lab leaves the idle timeout alone."
        },
        new()
        {
            Id = "steam-controller-gyro-on",
            Group = "steam-gyro",
            Device = "Steam Controller",
            Stages = LabModeStages.Motion,
            Endpoint = SteamController,
            Write = LabModeWrite.Feature,
            Reports = ["00 87 03 30 18 00"],
            Restore = ["00 87 03 30 00 00"],
            Description =
                "Turn on the Steam Controller's gyro reports (GYRO_MODE 0x18), as Handheld Companion does. It is turned off again at the end.",
            Provenance =
            [
                Hc + "steam-hidapi.net/steam_hidapi.net/GordonController.cs:66 (SetGyroscope: GYRO_MODE 24 on, 0 off)",
                Hc + "steam-hidapi.net/steam_hidapi.net.Hid/SCRegister.cs:10 (GYRO_MODE = 48)",
                HcDevices +
                "HandheldCompanion.Controllers.Steam/GordonController.cs:224 (gyro on at open, off at line 243)"
            ]
        },

        // GameSir Tarantula Pro: HC drives it by vendor ID on any device.
        new()
        {
            Id = "gamesir-test-mode",
            Group = "gamesir-mode",
            Device = "GameSir controller",
            Stages = LabModeStages.Buttons | LabModeStages.Motion,
            Endpoint = new LabModeEndpoint(0x3537, [], InputLengths: [64]),
            Write = LabModeWrite.Output,
            Reports = ["07 04 0A 02 01"],
            Description =
                "Put the GameSir controller in the test mode Handheld Companion uses, where it reports its buttons and motion sensor. The earlier mode cannot be read, so this tool cannot switch it back.",
            Provenance =
            [
                Hc +
                "controller-hidapi.net/controller_hidapi.net/TarantulaProController.cs:8 (ControllerMode 07 04 0A 02 mode)",
                Hc +
                "controller-hidapi.net/controller_hidapi.net/TarantulaProController.cs:80 (SetTestMode sends mode 1)",
                HcDevices +
                "HandheldCompanion.Controllers.GameSir/TarantulaProController.cs:103 (collection with a 64-byte input report)",
                HcDevices + "HandheldCompanion.Managers/ControllerManager.cs:2285 (every 3537 product)"
            ],
            Note =
                "After the mode HC writes nine ButtonMode reports that reset the extra buttons' mapping (TarantulaProController.cs:92); the lab leaves them out."
        },

        // OneXPlayer: the remap pages ConfigureController sends. The EC turbo takeover is left out.
        new()
        {
            Id = "oxp-x1-remap",
            Group = "oxp-remap",
            Device = "OneXPlayer controller",
            Stages = LabModeStages.Buttons,
            HcClass = "OneXPlayerX1",
            Endpoint = OxpX1,
            Write = LabModeWrite.Output,
            Framing = LabModeFraming.OxpVendor,
            Reports = ["B4: " + OxpPage1, "B4: " + OxpPage2M1F2],
            DelayMs = 50,
            Description = OxpRemapDescription,
            Provenance =
            [
                HcDevices +
                "HandheldCompanion.Devices/OneXPlayerX1.cs:497 (ConfigureController: page 1, then page 2 with M1 0x67 and M2 0x66)",
                HcDevices +
                "HandheldCompanion.Devices/OneXPlayerX1.cs:619 (BuildRemapPage1 and BuildRemapPage2, preset 1)",
                HcDevices + "HandheldCompanion.Devices/OneXPlayerX1.cs:589 (WriteVendorHidCommand framing)",
                HcDevices + "HandheldCompanion.Devices/OneXPlayerX1.cs:75 (1A86:FE00, FF00:0001)"
            ],
            Note =
                "HC's X1 pages carry 0x20 in their third byte where HHD's carry 0x02 (hhd oxp/hid_v1.py:109); the lab sends HC's bytes for HC's class."
        },
        new()
        {
            Id = "oxp-x1-mini-setup",
            Group = "oxp-remap",
            Device = "OneXPlayer X1 Mini controller",
            Stages = LabModeStages.Buttons,
            HcClass = "OneXPlayerX1Mini",
            Endpoint = OxpX1,
            Write = LabModeWrite.Output,
            Framing = LabModeFraming.OxpVendor,
            Description = "HC's controller setup for the X1 Mini.",
            Provenance =
            [
                HcDevices +
                "HandheldCompanion.Devices/OneXPlayerX1Mini.cs:25 (ConfigureController: command B3 with 01 05 05)",
                Hhd + "oxp/hid_v1.py:75 (HHD uses command B3 for vibration strength)"
            ],
            Withheld =
                "HC sends command B3 01 05 05 here, and its meaning is not documented (HHD uses B3 for vibration strength), so the lab does not send it."
        },
        new()
        {
            Id = "oxp-x2-remap",
            Group = "oxp-remap",
            Device = "OneXPlayer controller",
            Stages = LabModeStages.Buttons,
            HcClass = "OneXPlayerX2",
            Endpoint = OxpX2,
            Write = LabModeWrite.Output,
            Framing = LabModeFraming.OxpVendor,
            Reports = ["B4: " + OxpPage1, "B4: " + OxpPage2M1F2, "B4: " + OxpPage3],
            DelayMs = 50,
            Description = OxpRemapDescription,
            Provenance =
            [
                HcDevices + "HandheldCompanion.Devices/OneXPlayerX2.cs:185 (ConfigureController: pages 1, 2 and 3)",
                HcDevices + "HandheldCompanion.Devices/OneXPlayerX2.cs:195 (BuildRemapPage3)",
                HcDevices + "HandheldCompanion.Devices/OneXPlayerX1.cs:619 (pages 1 and 2)",
                HcDevices + "HandheldCompanion.Devices/OneXPlayerX2.cs:62 (1A86:FE00 and 1A86:1305, FF00:0001)"
            ],
            Note = "HHD's X2 pages (hhd oxp/hid_v1.py:120) carry 0x02 in the third byte and M1/M2 0x68/0x69."
        },
        new()
        {
            Id = "oxp-3-remap",
            Group = "oxp-remap",
            Device = "OneXPlayer 3 controller",
            Stages = LabModeStages.Buttons,
            HcClass = "OneXPlayer3",
            Endpoint = OxpX1,
            Write = LabModeWrite.Output,
            Framing = LabModeFraming.OxpVendor,
            Reports = ["B4: " + OxpPage1, "B4: " + OxpPage2M1F2, "B4: " + OxpPage3],
            DelayMs = 50,
            Description = OxpRemapDescription,
            Provenance =
            [
                HcDevices + "HandheldCompanion.Devices/OneXPlayerX2.cs:185 (inherited ConfigureController)",
                HcDevices + "HandheldCompanion.Devices/OneXPlayer3.cs:17 (1A86:FE00 only)"
            ]
        },
        new()
        {
            Id = "oxp-x2-mini-pro-remap",
            Group = "oxp-remap",
            Device = "OneXPlayer X2 Mini Pro controller",
            Stages = LabModeStages.Buttons,
            HcClass = "OneXPlayerX2MiniPro",
            Endpoint = OxpX2,
            Write = LabModeWrite.Output,
            Framing = LabModeFraming.OxpVendor,
            Reports = ["B4: " + OxpV2Page1, "B4: " + OxpV2Page2M68, "B4: " + OxpV2Page3, "B2: 00 01 02"],
            DelayMs = 50,
            Description = OxpRemapDescription,
            Provenance =
            [
                HcDevices +
                "HandheldCompanion.Devices/OneXPlayerX2MiniPro.cs:75 (ConfigureController: pages 1-3, then intercept off)",
                HcDevices + "HandheldCompanion.Devices/OneXPlayerX2MiniPro.cs:86 (pages with 0x02, M1 0x68, M2 0x69)",
                HcDevices + "HandheldCompanion.Devices/OneXPlayerX1.cs:651 (BuildIntercept(false): 00 01 02)",
                Hhd + "oxp/hid_v1.py:120 (INITIALIZE_X2: the same pages and intercept off)"
            ]
        },
        new()
        {
            Id = "oxp-apex-remap",
            Group = "oxp-remap",
            Device = "OneXPlayer Apex controller",
            Stages = LabModeStages.Buttons,
            HcClass = "OneXPlayerApex",
            Endpoint = OxpX1,
            Write = LabModeWrite.Output,
            Framing = LabModeFraming.OxpVendor,
            Reports =
                ["B4: " + OxpV2Page1, "B4: " + OxpV2Page2M67, "B2: 01 1F 40 03 02 03 00 00 00 01", "B2: 00 01 02"],
            DelayMs = 50,
            Description = OxpRemapDescription,
            Provenance =
            [
                HcDevices +
                "HandheldCompanion.Devices.OneXPlayer/OneXPlayerApex.cs:119 (ConfigureController: pages 1-2, page 3 as B2, intercept off)",
                HcDevices + "HandheldCompanion.Devices.OneXPlayer/OneXPlayerApex.cs:130 (pages and page 3 bytes)",
                HcDevices + "HandheldCompanion.Devices/OneXPlayerX1.cs:651 (BuildIntercept(false))"
            ]
        },

        // ZOTAC Gaming Zone.
        new()
        {
            Id = "zotac-m1-m2-remap",
            Group = "zotac-remap",
            Device = "ZOTAC Gaming Zone controller",
            Stages = LabModeStages.Buttons,
            HcClass = "GamingZone",
            Endpoint = new LabModeEndpoint(0x1EE9, [0x1590], InputLengths: [65], OutputLength: 65),
            Write = LabModeWrite.Output,
            Reports =
            [
                "00 E1 00 00 3C A1 01 00 00 00 00 09 00 44 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 FC 5C",
                "00 E1 00 00 3C A1 02 00 00 00 00 09 00 45 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 1E 6E"
            ],
            Description =
                "Map M1 and M2 to Ctrl+Win+F11 and Ctrl+Win+F12, as Handheld Companion does. This replaces their current mapping, which cannot be read back or undone by this tool; ZOTAC's app can set it again.",
            Provenance =
            [
                HcDevices + "HandheldCompanion.Devices.Zotac/GamingZone.cs:704 (Device_Inserted sends both)",
                HcDevices +
                "HandheldCompanion.Devices.Zotac/GamingZone.cs:765 (RemapM1_CtrlWinF11 and RemapM2_CtrlWinF12: modifiers 09, key at byte 13)",
                HcDevices + "HandheldCompanion.Devices.Zotac/GamingZone.cs:387 (F11 = 0x44, F12 = 0x45)",
                HcDevices +
                "HandheldCompanion.Devices.Zotac/GamingZone.cs:847 (CalcZotacCRC over bytes 5-62, stored big-endian in 63-64)",
                HcDevices +
                "HandheldCompanion.Devices.Zotac/GamingZone.cs:876 (1EE9:1590 collection with 65-byte input and output reports)"
            ]
        }
    ];

    /// <summary>Whether a mode command is waiting to be undone.</summary>
    public static bool HasPending => File.Exists(StatePath);

    /// <summary>The commands a stage offers for the confirmed record. Pure; touches no hardware.</summary>
    /// <param name="record">Confirmed knowledge record, or null for an unknown device.</param>
    /// <param name="stage">The stage.</param>
    /// <returns>
    ///     Nothing for a curated record. Otherwise, per group, the class-bound command nearest the record's HC
    ///     class, and every controller command HC drives by USB ID; the endpoint decides the rest.
    /// </returns>
    public static IReadOnlyList<LabModeCommand> For(DeviceKnowledgeRecord? record, LabModeStages stage)
    {
        if (record is { Status: DeviceKnowledgeStatus.Curated })
        {
            return [];
        }

        List<string> chain = [];
        if (record?.HcClass is { } hcClass)
        {
            chain.Add(hcClass);
        }

        chain.AddRange(record?.HcBaseClasses ?? []);
        HashSet<string> chosen = new(StringComparer.Ordinal);
        foreach (var group in All.Where(command => (command.Stages & stage) != 0).GroupBy(command => command.Group))
        {
            var bound = group.Where(command => command.HcClass is not null).ToList();
            var nearest = chain.FirstOrDefault(name => bound.Any(command => command.HcClass == name));
            if (nearest is not null)
            {
                chosen.Add(bound.First(command => command.HcClass == nearest).Id);
            }

            foreach (var command in group.Where(command => command.HcClass is null))
            {
                chosen.Add(command.Id);
            }
        }

        return [.. All.Where(command => chosen.Contains(command.Id))];
    }

    /// <summary>Whether a collection matches an endpoint. Pure.</summary>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="vendorId">Collection vendor ID.</param>
    /// <param name="productId">Collection product ID.</param>
    /// <param name="usagePage">Collection usage page.</param>
    /// <param name="usage">Collection usage.</param>
    /// <param name="usbInterface">USB interface number, when known.</param>
    /// <param name="inputLength">Input report length.</param>
    /// <param name="outputLength">Output report length.</param>
    /// <returns>True when every set field matches.</returns>
    public static bool Matches(
        LabModeEndpoint endpoint,
        ushort vendorId,
        ushort productId,
        ushort usagePage,
        ushort usage,
        int? usbInterface,
        int inputLength,
        int outputLength)
    {
        return endpoint.VendorId == vendorId
               && (endpoint.ProductIds.Count == 0 || endpoint.ProductIds.Contains(productId))
               && (endpoint.UsagePage is null || endpoint.UsagePage == usagePage)
               && (endpoint.Usage is null || endpoint.Usage == usage)
               && (endpoint.Interface is null || endpoint.Interface == usbInterface)
               && (endpoint.InputLengths is not { Count: > 0 } lengths || lengths.Contains(inputLength))
               && (endpoint.OutputLength is null || endpoint.OutputLength == outputLength);
    }

    /// <summary>The USB interface number in a device path (<c>MI_xx</c>), or null.</summary>
    /// <param name="path">Device interface path.</param>
    /// <returns>The interface number.</returns>
    public static int? InterfaceOf(string path)
    {
        var index = path.IndexOf("mi_", StringComparison.OrdinalIgnoreCase);
        return index >= 0 && index + 5 <= path.Length
                          && int.TryParse(path.AsSpan(index + 3, 2), NumberStyles.HexNumber,
                              CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>Builds the bytes of one report for a collection. Pure.</summary>
    /// <param name="command">The command.</param>
    /// <param name="report">One of its reports.</param>
    /// <param name="length">The collection's report length for the command's write kind.</param>
    /// <returns>The bytes, or null when they do not fit the collection.</returns>
    public static byte[]? Frame(LabModeCommand command, string report, int length)
    {
        if (command.Framing == LabModeFraming.Padded)
        {
            var bytes = ParseHex(report);
            if (bytes.Length > length)
            {
                return null;
            }

            var padded = new byte[length];
            bytes.CopyTo(padded, 0);
            return padded;
        }

        // OneXPlayerX1.WriteVendorHidCommand: a 64-byte collection takes the frame as is; any other takes
        // report ID 0 and a frame one byte shorter than the report.
        var colon = report.IndexOf(':');
        var commandId = ParseHex(report[..colon])[0];
        var payload = ParseHex(report[(colon + 1)..]);
        var frameLength = length == 64 ? length : length - 1;
        if (frameLength < 6 || payload.Length > frameLength - 5)
        {
            return null;
        }

        var frame = new byte[frameLength];
        frame[0] = commandId;
        frame[1] = 0x3F;
        frame[2] = 0x01;
        payload.CopyTo(frame, 3);
        frame[^2] = 0x3F;
        frame[^1] = commandId;
        return length == 64 ? frame : [0x00, .. frame];
    }

    /// <summary>Parses space-separated hex bytes.</summary>
    /// <param name="hex">For example <c>05 06 6A</c>.</param>
    /// <returns>The bytes.</returns>
    public static byte[] ParseHex(string hex)
    {
        return
        [
            .. hex.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(token => byte.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
        ];
    }

    /// <summary>Finds the one collection a command writes to. Blocking; call off the UI thread.</summary>
    /// <param name="command">The command.</param>
    /// <returns>The collection, or why there is none.</returns>
    public static (LabModeHid? Hid, string? Problem) Locate(LabModeCommand command)
    {
        List<LabModeHid> matches = [];
        foreach (var endpoint in LabRumbleNative.HidEndpoints(command.Endpoint.VendorId))
        {
            if (Inspect(endpoint) is { } hid && Matches(command.Endpoint, endpoint.VendorId, endpoint.ProductId,
                    endpoint.UsagePage, endpoint.Usage, hid.Interface, hid.InputLength, endpoint.OutputLength))
            {
                matches.Add(hid);
            }
        }

        return matches.Count switch
        {
            0 => (null, "The controller's collection is not present."),
            1 => (matches[0], null),
            _ => (null, $"{matches.Count} collections match, so it is not clear which one to write to.")
        };
    }

    /// <summary>
    ///     Sends a command once and, when it repeats, keeps sending it until <see cref="Stop" />. A reversible
    ///     command is recorded before anything is written. Blocking; call off the UI thread.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="hid">The collection from <see cref="Locate" />.</param>
    /// <param name="lifetime">Ends repeating when the wizard closes.</param>
    /// <returns>The session; its evidence records every write.</returns>
    public static LabModeSession Start(LabModeCommand command, LabModeHid hid, CancellationToken lifetime)
    {
        LabModeSession session = new(command, hid);
        if (command.Withheld is not null)
        {
            session.Problem = command.Withheld;
            return session;
        }

        if (command.Write == LabModeWrite.ControllerMode)
        {
            var plan = new LabControllerInitPlan("controller-mode", command.Description, true,
                new DeviceMechanismKnowledge
                {
                    Feature = "controller-mode",
                    Transport = "hid-output",
                    Parameters = command.ModeParameters
                });
            var result = LabControllerInit.Send(plan, lifetime);
            session.Mode = result;
            session.Sent = result.Sent;
            session.Problem = result.Problem;
            session.RestoreOwed = LabControllerInit.HasControllerModePending;
            return session;
        }

        var length = command.Write == LabModeWrite.Feature ? hid.FeatureLength : hid.Endpoint.OutputLength;
        List<byte[]> frames = [];
        foreach (var report in command.Reports)
        {
            if (Frame(command, report, length) is not { } frame)
            {
                session.Problem = $"A report does not fit the collection's {length}-byte report; nothing was sent.";
                return session;
            }

            frames.Add(frame);
        }

        SafeFileHandle handle;
        try
        {
            handle = LabRumbleNative.OpenForWrite(hid.Endpoint);
        }
        catch (InvalidOperationException ex)
        {
            session.Problem = ex.Message;
            return session;
        }

        if (command.Reversible)
        {
            AddPending(command.Id);
            session.RestoreOwed = true;
        }

        var keep = false;
        try
        {
            if (!SendFrames(session, handle, frames, "send"))
            {
                // An uncertain write is not retried; the tester is told and the stage continues.
                session.Problem = "The controller refused a report; nothing further was sent.";
                return session;
            }

            session.Sent = true;
            if (command.RepeatMs > 0)
            {
                session.StartRepeating(handle, frames, lifetime);
                keep = true;
            }

            return session;
        }
        finally
        {
            if (!keep)
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>Stops a session and undoes it when it is reversible. Blocking; call off the UI thread.</summary>
    /// <param name="session">The session.</param>
    /// <returns>Null when there was nothing to undo or the undo was sent; otherwise the problem.</returns>
    public static string? Stop(LabModeSession session)
    {
        if (session.Stopped)
        {
            return session.RestoreProblem;
        }

        session.StopRepeating();
        session.Stopped = true;
        if (!session.RestoreOwed)
        {
            return null;
        }

        session.RestoreProblem = session.Command.Write == LabModeWrite.ControllerMode
            ? LabControllerInit.RecoverControllerMode(CancellationToken.None)
            : Undo(session.Command, session);
        return session.RestoreProblem;
    }

    /// <summary>Stops every session's repeating without restoring; for exits that cannot wait.</summary>
    /// <param name="sessions">The sessions.</param>
    public static void StopRepeating(IEnumerable<LabModeSession> sessions)
    {
        foreach (var session in sessions)
        {
            session.StopRepeating();
        }
    }

    /// <summary>Undoes what a killed session left recorded. Blocking; call off the UI thread.</summary>
    /// <returns>Null when nothing was left or everything was undone; otherwise the problem.</returns>
    public static string? RecoverPending()
    {
        List<LabPendingModeCommand> pending;
        try
        {
            pending = ReadPending();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return $"The mode command record could not be read: {ex.Message}";
        }

        List<string> problems = [];
        foreach (var item in pending)
        {
            var command = All.FirstOrDefault(entry => entry.Id == item.Id && entry.Reversible);
            if (command is null)
            {
                // Not a command this build knows how to undo; the record cannot be acted on.
                RemovePending(item.Id);
                problems.Add($"'{item.Id}' is not a command this version can undo.");
                continue;
            }

            if (Undo(command, null) is { } problem)
            {
                problems.Add($"{command.Device}: {problem}");
            }
        }

        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    /// <summary>A collection without its path, for the evidence.</summary>
    /// <param name="hid">The collection.</param>
    /// <returns>An object to serialize.</returns>
    public static object Describe(LabModeHid hid)
    {
        return new
        {
            VendorId = hid.Endpoint.VendorId.ToString("X4"),
            ProductId = hid.Endpoint.ProductId.ToString("X4"),
            UsagePage = hid.Endpoint.UsagePage.ToString("X4"),
            Usage = hid.Endpoint.Usage.ToString("X4"),
            hid.Interface,
            hid.InputLength,
            hid.Endpoint.OutputLength,
            hid.FeatureLength
        };
    }

    /// <summary>Bytes as space-separated hex.</summary>
    /// <param name="bytes">Bytes.</param>
    /// <returns>Hex text.</returns>
    public static string Hex(byte[] bytes)
    {
        return string.Join(' ', bytes.Select(item => item.ToString("X2")));
    }

    /// <summary>Writes one frame.</summary>
    /// <param name="handle">Open handle.</param>
    /// <param name="write">Output or feature.</param>
    /// <param name="frame">The bytes.</param>
    /// <returns>Null on success, otherwise the error.</returns>
    internal static string? WriteFrame(SafeFileHandle handle, LabModeWrite write, byte[] frame)
    {
        if (write == LabModeWrite.Feature)
        {
            return HidD_SetFeature(handle, frame, (uint)frame.Length)
                ? null
                : $"error {Marshal.GetLastWin32Error()}";
        }

        var result = LabRumbleNative.WriteReport(handle, frame);
        return result == 0 ? null : $"error {result}";
    }

    private static bool SendFrames(LabModeSession session, SafeFileHandle handle, IReadOnlyList<byte[]> frames,
        string phase)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            if (i > 0)
            {
                Thread.Sleep(session.Command.DelayMs);
            }

            var error = WriteFrame(handle, session.Command.Write, frames[i]);
            session.Add(new LabModeWriteRecord(phase, Hex(frames[i]), error ?? "ok", DateTimeOffset.UtcNow));
            if (error is not null)
            {
                return false;
            }
        }

        return true;
    }

    // Sends the restore reports to the collection found now. The record is cleared once they were written;
    // none of these controllers can report the state back, so the evidence says the undo was sent.
    private static string? Undo(LabModeCommand command, LabModeSession? session)
    {
        var (hid, problem) = Locate(command);
        if (hid is null)
        {
            return $"{problem} It is tried again the next time Device Lab starts.";
        }

        var length = command.Write == LabModeWrite.Feature ? hid.FeatureLength : hid.Endpoint.OutputLength;
        var frames = command.Restore.Select(report => Frame(command, report, length)).ToList();
        if (frames.Any(frame => frame is null))
        {
            return "A restore report does not fit the collection.";
        }

        var record = session ?? new LabModeSession(command, hid);
        try
        {
            using var handle = LabRumbleNative.OpenForWrite(hid.Endpoint);
            if (!SendFrames(record, handle, frames!, "restore"))
            {
                return "The controller refused the restore. It is tried again the next time Device Lab starts.";
            }
        }
        catch (InvalidOperationException ex)
        {
            return $"{ex.Message} It is tried again the next time Device Lab starts.";
        }

        RemovePending(command.Id);
        return null;
    }

    private static LabModeHid? Inspect(LabRumbleHidEndpoint endpoint)
    {
        using var handle = Kernel32.CreateFileW(endpoint.Path, 0, Kernel32.FileShareRead | Kernel32.FileShareWrite,
            0, Kernel32.OpenExisting, 0, 0);
        if (handle.IsInvalid || !LabSensorInterop.HidD_GetPreparsedData(handle, out var preparsed))
        {
            return null;
        }

        try
        {
            return LabSensorInterop.HidP_GetCaps(preparsed, out var caps) != LabSensorInterop.HidpStatusSuccess
                ? null
                : new LabModeHid(endpoint, caps.InputReportByteLength, caps.FeatureReportByteLength,
                    InterfaceOf(endpoint.Path));
        }
        finally
        {
            LabSensorInterop.HidD_FreePreparsedData(preparsed);
        }
    }

    private static List<LabPendingModeCommand> ReadPending()
    {
        return File.Exists(StatePath)
            ? JsonSerializer.Deserialize<List<LabPendingModeCommand>>(File.ReadAllText(StatePath),
                LabProject.JsonOptions) ?? []
            : [];
    }

    private static void AddPending(string id)
    {
        var pending = ReadPending();
        if (pending.All(item => item.Id != id))
        {
            pending.Add(new LabPendingModeCommand(id, DateTimeOffset.UtcNow));
        }

        WritePending(pending);
    }

    private static void RemovePending(string id)
    {
        var pending = ReadPending();
        pending.RemoveAll(item => item.Id == id);
        if (pending.Count == 0)
        {
            File.Delete(StatePath);
            return;
        }

        WritePending(pending);
    }

    private static void WritePending(List<LabPendingModeCommand> pending)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var staging = DurableFile.StagingPath(StatePath);
        DurableFile.WriteNewText(staging, JsonSerializer.Serialize(pending, LabProject.JsonOptions));
        File.Move(staging, StatePath, true);
    }

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle device, byte[] buffer, uint length);
}
