using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Capture.Live;

namespace WSGM.DeviceLab.Wizard;

/// <summary>What sending a controller init did.</summary>
/// <param name="Sent">Whether every report was written.</param>
/// <param name="Reports">Each report as hex, with its result.</param>
/// <param name="Before">Controller product IDs present before.</param>
/// <param name="After">Controller product IDs present after.</param>
/// <param name="Problem">Why it stopped, when it did.</param>
internal sealed record LabInitResult(
    bool Sent,
    IReadOnlyList<string> Reports,
    IReadOnlyList<string> Before,
    IReadOnlyList<string> After,
    string? Problem);

/// <summary>A controller init a knowledge record describes, and how to undo it.</summary>
/// <param name="Feature"><c>controller-mode</c> or <c>button-init</c>.</param>
/// <param name="Description">What it does, for the tester.</param>
/// <param name="Reversible">
///     Whether the original state can be read and put back. When it cannot, the tester must opt in
///     knowing the change stays.
/// </param>
/// <param name="Mechanism">The record's mechanism.</param>
internal sealed record LabControllerInitPlan(
    string Feature,
    string Description,
    bool Reversible,
    DeviceMechanismKnowledge Mechanism);

/// <summary>A pending controller change, recorded before it is made so a killed session can undo it.</summary>
/// <param name="VendorId">USB vendor ID.</param>
/// <param name="OriginalMode">Mode to restore.</param>
/// <param name="Parameters">The mechanism parameters needed to restore.</param>
internal sealed record LabPendingControllerMode(
    string VendorId,
    int OriginalMode,
    IReadOnlyDictionary<string, string> Parameters);

/// <summary>
///     Sends the init a Curated knowledge record gives for the buttons stage, and restores it.
/// </summary>
/// <remarks>
///     Two shapes exist. A <c>controller-mode</c> mechanism (the Claw) switches the controller mode
///     with one output report; the current mode is read from which product ID is present, so it is
///     restored at the end of the stage and, through <see cref="RecoverPending" />, after a crash. A
///     <c>button-init</c> mechanism (the ROG Ally X) writes button tables that cannot be read back, so
///     it is sent only when the tester opts in and it is never described as undone. Only Curated
///     records are used, and only the record's exact bytes on the record's exact collection.
/// </remarks>
internal static class LabControllerInit
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WSGM Device Lab", "wizard", "controller-mode.json");

    /// <summary>The init the record offers, if any.</summary>
    /// <param name="record">Confirmed knowledge record.</param>
    /// <returns>The plan, or null.</returns>
    public static LabControllerInitPlan? For(DeviceKnowledgeRecord? record)
    {
        if (record is not { Status: DeviceKnowledgeStatus.Curated })
        {
            return null;
        }

        var mode = record.Mechanisms.FirstOrDefault(item => item.Feature == "controller-mode");
        if (mode is not null && mode.Parameters.ContainsKey("modeFromProductId")
                             && mode.Parameters.ContainsKey("testMode"))
        {
            return new LabControllerInitPlan("controller-mode",
                mode.Parameters.GetValueOrDefault("description")
                ?? "Switch the controller to the mode where every button reports. It is switched back at the end.",
                true, mode);
        }

        var init = record.Mechanisms.FirstOrDefault(item => item.Feature == "button-init");
        return init is null
            ? null
            : new LabControllerInitPlan("button-init",
                "Set the controller's buttons to the factory layout, as Handheld Companion does. This cannot be read back or undone by this tool; Armoury Crate can set your own layout again afterwards.",
                false, init);
    }

    /// <summary>Sends the init. Blocking; call off the UI thread.</summary>
    /// <param name="plan">Plan from <see cref="For" />.</param>
    /// <param name="cancellationToken">Cancels waiting for the controller to come back.</param>
    /// <returns>What happened.</returns>
    public static LabInitResult Send(LabControllerInitPlan plan, CancellationToken cancellationToken)
    {
        var parameters = plan.Mechanism.Parameters;
        var vendor = ushort.Parse(parameters["vendorId"], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var before = ProductIds(vendor);
        if (plan.Feature == "controller-mode")
        {
            var original = CurrentMode(parameters, before);
            if (original is null)
            {
                return new LabInitResult(false, [], before, before, "The current controller mode could not be read.");
            }

            var target = int.Parse(parameters["testMode"], CultureInfo.InvariantCulture);
            if (original == target)
            {
                return new LabInitResult(true, [], before, before, null);
            }

            WritePending(new LabPendingControllerMode(parameters["vendorId"], original.Value, parameters));
            return SwitchMode(parameters, vendor, target, before, cancellationToken);
        }

        List<string> reports = [];
        var length = int.Parse(parameters.GetValueOrDefault("reportLength") ?? "64", CultureInfo.InvariantCulture);
        var endpoint = Endpoint(vendor, parameters, "0xFF31", "0x0080");
        if (endpoint is null)
        {
            return new LabInitResult(false, [], before, before, "The controller's vendor collection is not present.");
        }

        using var handle = LabRumbleNative.OpenForWrite(endpoint);
        foreach (var hex in new[] { parameters.GetValueOrDefault("gamepadMode") }
                     .Concat((parameters.GetValueOrDefault("commit") ?? string.Empty).Split(';'))
                     .Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            var bytes = Bytes(hex!, length);
            var ok = HidD_SetFeature(handle, bytes, (uint)bytes.Length);
            reports.Add($"{hex!.Trim()}: {(ok ? "ok" : $"error {Marshal.GetLastWin32Error()}")}");
            if (!ok)
            {
                // An uncertain write is not retried; the tester is told and the stage continues.
                return new LabInitResult(false, reports, before, ProductIds(vendor),
                    "The controller refused a report; nothing further was sent.");
            }

            Thread.Sleep(20);
        }

        return new LabInitResult(true, reports, before, ProductIds(vendor), null);
    }

    /// <summary>
    ///     Undoes everything a controller init left recorded: a switched controller mode and any opt-in mode
    ///     command from <see cref="LabModeCommands" />. Blocking; call off the UI thread.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for re-enumeration.</param>
    /// <returns>Null when there was nothing to restore or every restore succeeded; otherwise the problems.</returns>
    public static string? RecoverPending(CancellationToken cancellationToken)
    {
        var commands = LabModeCommands.HasPending ? LabModeCommands.RecoverPending() : null;
        var mode = RecoverControllerMode(cancellationToken);
        return commands is null ? mode : mode is null ? commands : $"{mode} {commands}";
    }

    /// <summary>Puts the controller mode back. Blocking; call off the UI thread.</summary>
    /// <param name="cancellationToken">Cancels waiting for re-enumeration.</param>
    /// <returns>Null when there was nothing to restore or the restore was verified; otherwise the problem.</returns>
    public static string? RecoverControllerMode(CancellationToken cancellationToken)
    {
        LabPendingControllerMode? pending;
        try
        {
            pending = File.Exists(StatePath)
                ? JsonSerializer.Deserialize<LabPendingControllerMode>(File.ReadAllText(StatePath),
                    LabProject.JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return $"The controller mode record could not be read: {ex.Message}";
        }

        if (pending is null)
        {
            return null;
        }

        var vendor = ushort.Parse(pending.VendorId, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var before = ProductIds(vendor);
        if (CurrentMode(pending.Parameters, before) == pending.OriginalMode)
        {
            File.Delete(StatePath);
            return null;
        }

        var result = SwitchMode(pending.Parameters, vendor, pending.OriginalMode, before, cancellationToken);
        if (result.Sent && CurrentMode(pending.Parameters, result.After) == pending.OriginalMode)
        {
            File.Delete(StatePath);
            return null;
        }

        return result.Problem ?? "The controller did not come back in its original mode.";
    }

    /// <summary>The controller mode now, for a controller-mode plan; null when it cannot be read.</summary>
    /// <param name="plan">Plan from <see cref="For" />.</param>
    /// <returns>The mode.</returns>
    public static int? CurrentMode(LabControllerInitPlan plan)
    {
        var parameters = plan.Mechanism.Parameters;
        var vendor = ushort.Parse(parameters["vendorId"], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return plan.Feature == "controller-mode" ? CurrentMode(parameters, ProductIds(vendor)) : null;
    }

    /// <summary>The mode a controller-mode plan switches to.</summary>
    /// <param name="plan">Plan.</param>
    /// <returns>The test mode.</returns>
    public static int? TestMode(LabControllerInitPlan plan)
    {
        return plan.Mechanism.Parameters.TryGetValue("testMode", out var mode)
            ? int.Parse(mode, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>Whether a controller mode change or an opt-in mode command is waiting to be undone.</summary>
    public static bool HasPending => HasControllerModePending || LabModeCommands.HasPending;

    /// <summary>Whether a controller mode change is waiting to be undone.</summary>
    public static bool HasControllerModePending => File.Exists(StatePath);

    private static LabInitResult SwitchMode(
        IReadOnlyDictionary<string, string> parameters,
        ushort vendor,
        int mode,
        IReadOnlyList<string> before,
        CancellationToken cancellationToken)
    {
        // Several product IDs can share a mode (the Legion Go's 2023 and 2025 firmware); the one present is used.
        var current = CurrentMode(parameters, before);
        var present = LabRumbleNative.HidEndpoints(vendor);
        var endpoint = Endpoints(parameters)
            .Where(item => Mode(parameters, item.Product) == current)
            .Select(spec => present.FirstOrDefault(item =>
                item.ProductId.ToString("X4") == spec.Product && item.UsagePage == spec.Page
                                                              && item.Usage == spec.Usage))
            .FirstOrDefault(item => item is not null);
        if (endpoint is null)
        {
            return new LabInitResult(false, [], before, before, "The controller's command collection is not present.");
        }

        var template = parameters["report"].Replace("<mode>", mode.ToString("X2"), StringComparison.Ordinal);
        var bytes = Bytes(template, Math.Max((int)endpoint.OutputLength, template.Split(' ').Length));
        long result;
        using (var handle = LabRumbleNative.OpenForWrite(endpoint))
        {
            result = LabRumbleNative.WriteReport(handle, bytes);
        }

        var line = $"{template}: {(result == 0 ? "ok" : $"error {result}")}";
        if (result != 0)
        {
            return new LabInitResult(false, [line], before, ProductIds(vendor), "The controller refused the mode switch.");
        }

        // Switching re-enumerates the controller; wait for the product ID of the new mode.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        IReadOnlyList<string> after = before;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            Thread.Sleep(250);
            after = ProductIds(vendor);
            if (CurrentMode(parameters, after) == mode)
            {
                return new LabInitResult(true, [line], before, after, null);
            }
        }

        return new LabInitResult(false, [line], before, after,
            "The controller did not come back in the new mode within 15 seconds.");
    }

    private static int? CurrentMode(IReadOnlyDictionary<string, string> parameters, IReadOnlyList<string> products)
    {
        return products.Select(product => Mode(parameters, product)).FirstOrDefault(mode => mode is not null);
    }

    // "1901 1, 1902 2": product ID to mode.
    private static int? Mode(IReadOnlyDictionary<string, string> parameters, string? product)
    {
        foreach (var pair in parameters["modeFromProductId"].Split(',', StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && string.Equals(parts[0], product, StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(parts[1], CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    // "1901 FFA0:0001, 1902 FFF0:0040": the command collection in each mode.
    private static IEnumerable<(string? Product, ushort Page, ushort Usage)> Endpoints(
        IReadOnlyDictionary<string, string> parameters)
    {
        foreach (var pair in (parameters.GetValueOrDefault("commandEndpoints") ?? string.Empty).Split(',',
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split([' ', ':'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3)
            {
                yield return (parts[0].ToUpperInvariant(),
                    ushort.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    ushort.Parse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
        }
    }

    private static LabRumbleHidEndpoint? Endpoint(
        ushort vendor,
        IReadOnlyDictionary<string, string> parameters,
        string defaultPage,
        string defaultUsage)
    {
        var page = Hex(parameters.GetValueOrDefault("usagePage") ?? defaultPage);
        var usage = Hex(parameters.GetValueOrDefault("usage") ?? defaultUsage);
        return LabRumbleNative.HidEndpoints(vendor).FirstOrDefault(item => item.UsagePage == page && item.Usage == usage);
    }

    private static IReadOnlyList<string> ProductIds(ushort vendor)
    {
        return [.. LabRumbleNative.HidEndpoints(vendor).Select(item => item.ProductId.ToString("X4")).Distinct().Order()];
    }

    private static ushort Hex(string value)
    {
        return ushort.Parse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static byte[] Bytes(string hex, int length)
    {
        var tokens = hex.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[Math.Max(length, tokens.Length)];
        for (var i = 0; i < tokens.Length; i++)
        {
            bytes[i] = byte.Parse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    private static void WritePending(LabPendingControllerMode pending)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var staging = DurableFile.StagingPath(StatePath);
        DurableFile.WriteNewText(staging, JsonSerializer.Serialize(pending, LabProject.JsonOptions));
        File.Move(staging, StatePath, true);
    }

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle device, byte[] buffer, uint length);
}
