using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WSGM.DeviceLab.Windows;
using WSGM.DeviceLab.Wizard;
using WSGM.DeviceLab.Worker;

namespace WSGM.DeviceLab.Transports;

/// <summary>The HID endpoint an Aura test writes to, without its device path.</summary>
/// <param name="VendorId">USB vendor ID.</param>
/// <param name="ProductId">USB product ID.</param>
/// <param name="Release">USB release number.</param>
/// <param name="UsagePage">Top-level usage page.</param>
/// <param name="Usage">Top-level usage.</param>
/// <param name="OutputBytes">Output report length.</param>
internal sealed record LabAuraEndpoint(
    string VendorId,
    string ProductId,
    string Release,
    string UsagePage,
    string Usage,
    int OutputBytes);

/// <summary>
///     The Aura checkpoint marker. The lights are write-only, so there is no colour to capture; the
///     checkpoint only records that the lab is about to write them, and the tester sets their colour again.
/// </summary>
/// <param name="Endpoint">The endpoint about to be written.</param>
/// <param name="Note">What restoring takes.</param>
internal sealed record LabAuraOriginal(LabAuraEndpoint Endpoint, string Note);

/// <summary>
///     The Aura service the wizard calls through the hardware worker (<c>aura</c>, opened with a
///     <see cref="LabAuraLayout" />). Writes are refused until the worker's checkpoint is acknowledged.
/// </summary>
internal interface ILabAura : IDisposable
{
    /// <summary>The checkpoint marker; nothing is read from the lights.</summary>
    /// <returns>The endpoint and the restore note.</returns>
    [LabWorkerSnapshot]
    LabAuraOriginal Original();

    /// <summary>Shows one colour on one zone.</summary>
    /// <param name="zone">0 both rings, 1 left outer, 2 left inner, 3 right inner, 4 right outer.</param>
    /// <param name="channel">0 red, 1 green, 2 blue.</param>
    [LabWorkerWrite]
    void Colour(int zone, int channel);

    /// <summary>Turns the brightness to off.</summary>
    [LabWorkerWrite]
    void Off();
}

/// <summary>
///     ROG Ally Aura lighting over the vendor HID interface, ported from AllyXLab's Worker. The lights
///     are write-only: the tester's colour cannot be read, so it cannot be put back. It runs only inside
///     the hardware worker, behind <see cref="ILabAura" />.
/// </summary>
internal sealed class LabAuraLighting : ILabAura
{
    /// <summary>The zones this test steps through, by zone number: both rings, then the four half-rings.</summary>
    public static readonly IReadOnlyList<string> ZoneNames =
    [
        "both rings", "left ring outer half", "left ring inner half", "right ring inner half", "right ring outer half"
    ];

    private readonly SafeFileHandle _handle;
    private readonly LabPowerLog _log;

    private LabAuraLighting(SafeFileHandle handle, LabAuraEndpoint endpoint, LabPowerLog log)
    {
        _handle = handle;
        Endpoint = endpoint;
        _log = log;
    }

    /// <summary>The worker service registration. Opening fails when there is not exactly one endpoint.</summary>
    public static LabWorkerService Service { get; } = new("aura", typeof(ILabAura),
        (args, log) => Open(LabWorkerService.Arg<LabAuraLayout>(args, 0), log)
                       ?? throw new InvalidOperationException("The device's own lighting interface was not found."));

    /// <summary>The endpoint in use.</summary>
    public LabAuraEndpoint Endpoint { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        _handle.Dispose();
    }

    /// <inheritdoc />
    public LabAuraOriginal Original()
    {
        return new LabAuraOriginal(Endpoint,
            "Write-only lighting: the colour cannot be read, so the tester sets it again in Armoury Crate.");
    }

    /// <summary>Shows one colour on one zone: AllyXLab's init, brightness, static colour and apply.</summary>
    /// <param name="zone">0 both rings, 1 left outer, 2 left inner, 3 right inner, 4 right outer.</param>
    /// <param name="channel">0 red, 1 green, 2 blue.</param>
    public void Colour(int zone, int channel)
    {
        if (zone < 0 || zone >= ZoneNames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(zone));
        }

        if (channel is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        Output([0x5A, .. Encoding.ASCII.GetBytes("ASUS Tech.Inc.")]);
        Output([0x5A, 0xBA, 0xC5, 0xC4, 1]);
        var colour = new byte[64];
        colour[0] = 0x5A;
        colour[1] = 0xB3;
        colour[2] = (byte)zone;
        colour[4 + channel] = 80;
        Output(colour);
        Output([0x5A, 0xB5]);
        Output([0x5A, 0xB4]);
    }

    /// <summary>Turns the brightness to off, as AllyXLab does after every flash.</summary>
    public void Off()
    {
        Output([0x5A, 0xBA, 0xC5, 0xC4, 0]);
    }

    /// <summary>Opens the exact endpoint the record names; refuses when there is not exactly one.</summary>
    /// <param name="layout">The endpoint identity.</param>
    /// <param name="log">Where every write is logged.</param>
    /// <returns>The lighting, or null with the reason logged.</returns>
    public static LabAuraLighting? Open(LabAuraLayout layout, LabPowerLog log)
    {
        var matches = LabHid.HidEndpoints(layout.VendorId)
            .Where(item => item.ProductId == layout.ProductId && item.UsagePage == layout.UsagePage
                                                              && item.Usage == layout.Usage)
            .ToList();
        if (matches.Count != 1)
        {
            log.Add("aura-endpoint", new { Found = matches.Count, Reason = "exactly one endpoint is required" });
            return null;
        }

        var found = matches[0];
        LabAuraEndpoint endpoint = new($"{found.VendorId:X4}", $"{found.ProductId:X4}", $"{found.Release:X4}",
            $"{found.UsagePage:X4}", $"{found.Usage:X4}", found.OutputLength);
        if (found.OutputLength is < 64 or > 1024)
        {
            log.Add("aura-endpoint", new { Endpoint = endpoint, Reason = "the output report is not 64 bytes or more" });
            return null;
        }

        SafeFileHandle handle;
        try
        {
            handle = HidDevices.Open(found.Collection, false);
        }
        catch (Win32Exception ex)
        {
            log.Add("aura-endpoint",
                new { Endpoint = endpoint, Reason = new Win32Exception(ex.NativeErrorCode).Message });
            return null;
        }

        log.Add("aura-endpoint", new { Endpoint = endpoint, Opened = true });
        return new LabAuraLighting(handle, endpoint, log);
    }

    private void Output(byte[] bytes)
    {
        var padded = new byte[Endpoint.OutputBytes];
        bytes.CopyTo(padded, 0);
        _log.Add("hid-output", new { Bytes = Convert.ToHexString(bytes) });
        var result = LabHid.WriteReport(_handle, padded);
        if (result != 0)
        {
            // -1 is a short write; anything else is the Windows error.
            _log.Add("hid-output-failed", new { Error = result });
            throw new IOException(
                "The lighting write failed or was short; its effect is unknown and it was not tried again.");
        }

        _log.Add("hid-output-returned", new { Written = padded.Length, Verified = false });
    }
}
