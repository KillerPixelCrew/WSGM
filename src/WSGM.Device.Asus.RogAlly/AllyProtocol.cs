// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace WSGM.Device.Asus.RogAlly;

/// <summary>Aura effects HC offers on the Ally (<c>ROGAlly.cs:22-29, 224-228</c>).</summary>
internal enum AuraEffect : byte
{
    /// <summary>HC <c>SolidColor</c>, HHD <c>solid</c>.</summary>
    Solid = 0,

    /// <summary>HC <c>Breathing</c>, HHD <c>pulse</c>/<c>duality</c>: primary and secondary colours.</summary>
    Breathing = 1,

    /// <summary>HC <c>Wheel</c>, HHD <c>rainbow</c> ("color cycle").</summary>
    ColorCycle = 2,

    /// <summary>HC <c>Rainbow</c>, HHD <c>spiral</c>.</summary>
    Rainbow = 3
}

/// <summary>Aura zones: HC <c>LEDZone</c>, HHD <c>rgb_command</c> zones (identical numbering).</summary>
internal enum AuraZone : byte
{
    All = 0,
    LeftStickLeft = 1,
    LeftStickRight = 2,
    RightStickLeft = 3,
    RightStickRight = 4
}

/// <summary>Byte-level encoders and decoders. Nothing here opens a device.</summary>
internal static class AllyProtocol
{
    /// <summary>HHD <c>FEATURE_KBD_DRIVER</c>, HC <c>INPUT_HID_ID</c> (90).</summary>
    public const byte VendorReportId = 0x5A;

    /// <summary>HC <c>AURA_HID_ID</c> (93), HHD <c>FEATURE_KBD_APP</c>.</summary>
    public const byte AuraReportId = 0x5D;

    public const ushort VendorUsagePage = 0xFF31;
    public const ushort VendorUsage = 0x0080;
    public const int ConfigurationLength = 64;

    /// <summary>
    ///     HC <c>AuraSpeed</c>: Slow 0xEB, Medium 0xF5, Fast 0xE1 (<c>ROGAlly.cs:31-36</c>). HHD reads
    ///     the same bytes the other way round, 0xE1 low to 0xF5 high (<c>hid.py:92-100</c>); HC is
    ///     followed for lighting and the disagreement is in PROVENANCE.md.
    /// </summary>
    public const byte SpeedSlow = 0xEB;

    public const byte SpeedMedium = 0xF5;
    public const byte SpeedFast = 0xE1;

    /// <summary>HHD <c>REMAP_M1M2_F17F18</c>: M1 and M2 become keyboard keys the plugin can read.</summary>
    /// <remarks>
    ///     Without this block the rear buttons send ASUS's own M1/M2 codes, which no Windows API
    ///     surfaces. HC writes the identical bytes as <c>M1F18M2F17</c> (<c>ROGAlly.cs:163-168</c>).
    /// </remarks>
    public static readonly byte[] RearKeyboardMapping =
        Pad("5a d1 02 08 2c 02 00 28 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 02 00 30");

    /// <summary>HHD <c>REMAP_M1M2_DEFAULT</c>: ASUS's M2 code 0x8E on the first pair, M1 0x8F on the second.</summary>
    /// <remarks>
    ///     HHD <c>rog_ally/const.py:839-896</c>. HC's <c>M1M2Default</c> is the same block truncated
    ///     before its fourth entry (<c>ROGAlly.cs:156-161</c>), so HHD's complete block is used.
    /// </remarks>
    public static readonly byte[] RearDefaultMapping =
        Pad("5a d1 02 08 2c 02 00 8e 00 00 00 00 00 00 00 00 02 00 8e 00 00 00 00 00 00 00 00 02 00 8f 00 00 00 00"
            + " 00 00 00 00 02 00 8f");

    // HHD COMMIT_RESET (const.py:1073-1118) with HC's commitReset1of4-4of4 values (ROGAlly.cs:177-183):
    // turbo reset, vibration 100/100, stick and trigger ranges 0-100. HHD sends vibration 100 on the
    // Ally and 50 on the others, and stick and trigger outer limits of 0x40 or 0x60.
    private static readonly byte[][] Commit =
    [
        Pad("5a d1 0f 20"),
        Pad("5a d1 06 02 64 64"),
        Pad("5a d1 04 04 00 64 00 64"),
        Pad("5a d1 05 04 00 64 00 64")
    ];

    // HHD COMMANDS_GAME before the rear block (const.py:1120-1128): game mode, then the default
    // D-pad, stick, shoulder, face and view/menu tables. Each table is a 0x2C (44) byte body of four
    // 11-byte button blocks. Six match HC's defaults (ROGAlly.cs:93-154) byte for byte; HC's
    // dPadLeftRightDefault and faceButtonsABDefault drop one padding byte from their third block, so
    // their fourth block starts one byte early while the length byte still says 44. HHD's layout,
    // which agrees with that length and with HC's other tables, is sent (PROVENANCE.md).
    private static readonly byte[][] FrontTables =
    [
        Pad("5a d1 01 01 01"),
        Pad("5a d1 02 02 2c 01 0b 00 00 00 00 00 00 00 00 00 04 00 00 00 00 02 82 23 00 00 00 01 0c 00 00 00 00"
            + " 00 00 00 00 00 04 00 00 00 00 02 82 0d"),
        Pad("5a d1 02 01 2c 01 09 00 00 00 00 00 00 00 00 00 05 00 00 19 00 00 00 00 00 00 00 01 0a 00 00 00 00"
            + " 00 00 00 00 00 04 00 00 00 00 03 8c 88 76"),
        Pad("5a d1 02 03 2c 01 07 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 01 08"),
        Pad("5a d1 02 04 2c 01 05 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 01 06"),
        Pad("5a d1 02 05 2c 01 01 00 00 00 00 00 00 00 00 00 05 00 00 16 00 00 00 00 00 00 00 01 02 00 00 00 00"
            + " 00 00 00 00 00 04 00 00 00 00 02 82 31"),
        Pad("5a d1 02 06 2c 01 03 00 00 00 00 00 00 00 00 00 04 00 00 00 00 02 82 4d 00 00 00 01 04 00 00 00 00"
            + " 00 00 00 00 00 05 00 00 1e"),
        Pad("5a d1 02 07 2c 01 11 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 01 12")
    ];

    // HHD REMAP_TRIGGERS (const.py:955-1012), HC triggersDefault (ROGAlly.cs:170-175).
    private static readonly byte[] Triggers =
        Pad("5a d1 02 09 2c 01 0d 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 01 0e");

    /// <summary>
    ///     The controller configuration written when the plugin takes the gamepad: HHD's
    ///     <c>COMMANDS_GAME</c> (<c>rog_ally/const.py:1120-1132</c>) in HHD's order, which sends the D-pad
    ///     left/right table before up/down where HC's <c>ConfigureController</c> sends up/down first
    ///     (<c>ROGAlly.cs:653-654</c>), through feature reports as HC does (<c>ROGAlly.cs:646-668</c>).
    /// </summary>
    public static IReadOnlyList<byte[]> GameModeConfiguration { get; } =
        [.. FrontTables, RearKeyboardMapping, Triggers, .. Commit];

    /// <summary>The same tables with the factory M1/M2 block, written when the plugin lets go.</summary>
    /// <remarks>HC's <c>Close</c> calls <c>ConfigureController(Remap: false)</c> (<c>ROGAlly.cs:396-400</c>).</remarks>
    public static IReadOnlyList<byte[]> DefaultConfiguration { get; } =
        [.. FrontTables, RearDefaultMapping, Triggers, .. Commit];

    /// <summary>Reads one vendor input report and returns its event code.</summary>
    /// <remarks>HHD reads <c>rep[1]</c> after checking <c>rep[0] == 0x5A</c> (<c>base.py:176-179</c>).</remarks>
    public static bool TryReadVendorEvent(ReadOnlySpan<byte> report, out byte code)
    {
        code = 0;
        if (report.Length < 2 || report[0] != VendorReportId || report[1] == 0)
        {
            return false;
        }

        code = report[1];
        return true;
    }

    /// <summary>HC's brightness feature report: <c>5D BA C5 C4 level</c>, level 0-3.</summary>
    /// <remarks>HC scales 0-100 by 33.33 (<c>ROGAlly.cs:507-522</c>); HHD sends the same bytes on 0x5A.</remarks>
    public static byte[] Brightness(int percent)
    {
        var level = (byte)Math.Clamp((int)Math.Round(percent / 33.33), 0, 3);
        return [AuraReportId, 0xBA, 0xC5, 0xC4, level];
    }

    /// <summary>HC's <c>AuraMessage</c> (<c>ROGAlly.cs:595-617</c>), seventeen bytes.</summary>
    public static byte[] Color(AuraEffect effect, AuraZone zone, int primary, int secondary, byte speed)
    {
        return
        [
            AuraReportId,
            0xB3,
            (byte)zone,
            (byte)effect,
            Red(primary),
            Green(primary),
            Blue(primary),
            speed,
            0,
            effect is AuraEffect.Breathing ? (byte)1 : (byte)0,
            Red(secondary),
            Green(secondary),
            Blue(secondary),
            0,
            0,
            0,
            0
        ];
    }

    /// <summary>HC <c>MESSAGE_APPLY</c>.</summary>
    public static byte[] Apply()
    {
        return [AuraReportId, 0xB4];
    }

    /// <summary>HC <c>MESSAGE_SET</c>.</summary>
    public static byte[] Set()
    {
        return [AuraReportId, 0xB5, 0, 0, 0];
    }

    /// <summary>HC's speed bands: at most 33 slow, at most 66 medium, else fast (<c>ROGAlly.cs:552</c>).</summary>
    public static byte Speed(int percent)
    {
        return percent <= 33 ? SpeedSlow : percent <= 66 ? SpeedMedium : SpeedFast;
    }

    internal static byte[] Pad(string hex)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));
        if (bytes.Length > ConfigurationLength)
        {
            throw new ArgumentException("A configuration report is at most 64 bytes.", nameof(hex));
        }

        var report = new byte[ConfigurationLength];
        bytes.CopyTo(report, 0);
        return report;
    }

    private static byte Red(int color)
    {
        return (byte)((color >> 16) & 0xFF);
    }

    private static byte Green(int color)
    {
        return (byte)((color >> 8) & 0xFF);
    }

    private static byte Blue(int color)
    {
        return (byte)(color & 0xFF);
    }
}
