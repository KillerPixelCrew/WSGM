using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using WSGM.Device.Sdk.Windows;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Capture.Live;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Transports;

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
///     with one output report; the current mode is read from which product ID is present, so the wizard
///     records it (<see cref="RecordPending" />) before the switch, and it is restored at the end of the
///     stage and, through <see cref="LabRecovery" />, after a crash. A
///     <c>button-init</c> mechanism (the ROG Ally X) writes button tables that cannot be read back, so
///     it is sent only when the tester opts in and it is never described as undone. Only Curated
///     records are used, and only the record's exact bytes on the record's exact collection.
/// </remarks>
internal static class LabControllerInit
{
    private const string PendingFileName = "controller-mode.json";

    /// <summary>Whether a controller mode change is recorded and waiting to be undone.</summary>
    /// <param name="machine">The machine record, whose folder holds the controller-mode record.</param>
    /// <returns>True when the controller-mode record exists.</returns>
    public static bool HasPending(LabMachineState machine)
    {
        return File.Exists(machine.SidePath(PendingFileName));
    }

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

    /// <summary>
    ///     Sends the init. Blocking; call off the UI thread. A <c>controller-mode</c> plan must have its
    ///     original mode recorded with <see cref="RecordPending" /> first.
    /// </summary>
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

            return SwitchMode(parameters, vendor, target, before, cancellationToken);
        }

        List<string> reports = [];
        var length = int.Parse(parameters.GetValueOrDefault("reportLength") ?? "64", CultureInfo.InvariantCulture);
        var (endpoint, problem) = Endpoint(vendor, parameters, "0xFF31", "0x0080");
        if (endpoint is null)
        {
            return new LabInitResult(false, [], before, before, problem);
        }

        using var handle = LabHid.OpenForWrite(endpoint);
        foreach (var hex in new[] { parameters.GetValueOrDefault("gamepadMode") }
                     .Concat((parameters.GetValueOrDefault("commit") ?? string.Empty).Split(';'))
                     .Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            var error = SetFeature(handle, endpoint, Padded(hex!, length));
            reports.Add($"{hex!.Trim()}: {error ?? "ok"}");
            if (error is not null)
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
    ///     Records a controller-mode plan's original mode before the switch, so a killed session can put it
    ///     back. A record whose mode already equals the target is harmless: the restore finds the original.
    /// </summary>
    /// <param name="machine">The machine record, whose folder holds the controller-mode record.</param>
    /// <param name="plan">A <c>controller-mode</c> plan.</param>
    /// <param name="originalMode">The mode before the switch.</param>
    public static void RecordPending(LabMachineState machine, LabControllerInitPlan plan, int originalMode)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(plan);
        var parameters = plan.Mechanism.Parameters;
        var path = machine.SidePath(PendingFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = DurableFile.StagingPath(path);
        DurableFile.WriteNewText(staging, JsonSerializer.Serialize(
            new LabPendingControllerMode(parameters["vendorId"], originalMode, parameters), LabProject.JsonOptions));
        File.Move(staging, path, true);
    }

    /// <summary>The recorded controller-mode change; a missing record is none, an unreadable one throws.</summary>
    /// <param name="machine">The machine record.</param>
    /// <returns>The pending change, or null.</returns>
    /// <exception cref="IOException">The record exists but could not be read.</exception>
    public static LabPendingControllerMode? ReadPending(LabMachineState machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return LabMachineState.ReadJson<LabPendingControllerMode>(machine.SidePath(PendingFileName),
            "controller-mode record");
    }

    /// <summary>Forgets the controller-mode record after a verified restore.</summary>
    /// <param name="machine">The machine record.</param>
    public static void ClearPending(LabMachineState machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        File.Delete(machine.SidePath(PendingFileName));
    }

    /// <summary>
    ///     Puts back a recorded controller mode in this process and forgets the record once the mode reads
    ///     back. Blocking; call off the UI thread.
    /// </summary>
    /// <param name="machine">The machine record.</param>
    /// <param name="cancellationToken">Cancels waiting for re-enumeration.</param>
    /// <returns>Null when nothing was recorded or the restore was verified; otherwise the problem.</returns>
    public static string? RestorePending(LabMachineState machine, CancellationToken cancellationToken)
    {
        if (ReadPending(machine) is not { } pending)
        {
            return null;
        }

        var problem = RecoverControllerMode(pending, cancellationToken);
        if (problem is null)
        {
            ClearPending(machine);
        }

        return problem;
    }

    /// <summary>
    ///     Puts a recorded controller mode back. It reads and writes no record; the caller clears the record
    ///     after a null result. Blocking; call off the UI thread.
    /// </summary>
    /// <param name="pending">The recorded change.</param>
    /// <param name="cancellationToken">Cancels waiting for re-enumeration.</param>
    /// <returns>Null when the original mode reads back; otherwise the problem.</returns>
    public static string? RecoverControllerMode(LabPendingControllerMode pending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var vendor = ushort.Parse(pending.VendorId, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var before = ProductIds(vendor);
        if (CurrentMode(pending.Parameters, before) == pending.OriginalMode)
        {
            return null;
        }

        var result = SwitchMode(pending.Parameters, vendor, pending.OriginalMode, before, cancellationToken);
        return result.Sent && CurrentMode(pending.Parameters, result.After) == pending.OriginalMode
            ? null
            : result.Problem ?? "The controller did not come back in its original mode.";
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

    private static LabInitResult SwitchMode(
        IReadOnlyDictionary<string, string> parameters,
        ushort vendor,
        int mode,
        IReadOnlyList<string> before,
        CancellationToken cancellationToken)
    {
        // Several product IDs can share a mode (the Legion Go's 2023 and 2025 firmware); the one present is used.
        var current = CurrentMode(parameters, before);
        var present = LabHid.HidEndpoints(vendor);
        var (endpoint, problem) = Single(Endpoints(parameters)
            .Where(item => Mode(parameters, item.Product) == current)
            .SelectMany(spec => present.Where(item =>
                item.ProductId.ToString("X4") == spec.Product && item.UsagePage == spec.Page
                                                              && item.Usage == spec.Usage)),
            "The controller's command collection is not present.");
        if (endpoint is null)
        {
            return new LabInitResult(false, [], before, before, problem);
        }

        var template = parameters["report"].Replace("<mode>", mode.ToString("X2"), StringComparison.Ordinal);
        var bytes = Padded(template, endpoint.OutputLength);
        long result;
        using (var handle = LabHid.OpenForWrite(endpoint))
        {
            result = LabHid.WriteReport(handle, bytes);
        }

        var line = $"{template}: {(result == 0 ? "ok" : $"error {result}")}";
        if (result != 0)
        {
            return new LabInitResult(false, [line], before, ProductIds(vendor),
                "The controller refused the mode switch.");
        }

        // Switching re-enumerates the controller; wait for the product ID of the new mode.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var after = before;
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

    // "1901 FFA0:0001, 1902 FFF0:0040": the command collection in each mode. The power plan reads the
    // same list for the Claw's lighting collections.
    internal static IEnumerable<(string? Product, ushort Page, ushort Usage)> Endpoints(
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

    private static (LabHidEndpoint? Endpoint, string? Problem) Endpoint(
        ushort vendor,
        IReadOnlyDictionary<string, string> parameters,
        string defaultPage,
        string defaultUsage)
    {
        var page = LabRumbleRoutes.ParseUShortHex(parameters.GetValueOrDefault("usagePage") ?? defaultPage);
        var usage = LabRumbleRoutes.ParseUShortHex(parameters.GetValueOrDefault("usage") ?? defaultUsage);
        return Single(LabHid.HidEndpoints(vendor).Where(item => item.UsagePage == page && item.Usage == usage),
            "The controller's vendor collection is not present.");
    }

    /// <summary>The one collection a controller write may go to; several matches are refused before any write.</summary>
    /// <param name="matches">The present collections that match.</param>
    /// <param name="absent">The problem when none is present.</param>
    /// <returns>The collection, or why there is none.</returns>
    internal static (LabHidEndpoint? Endpoint, string? Problem) Single(IEnumerable<LabHidEndpoint> matches,
        string absent)
    {
        var found = matches.ToList();
        return found.Count switch
        {
            0 => (null, absent),
            1 => (found[0], null),
            _ => (null, $"{found.Count} collections match, so it is not clear which one to write to.")
        };
    }

    private static IReadOnlyList<string> ProductIds(ushort vendor)
    {
        return
        [
            .. LabHid.HidEndpoints(vendor).Select(item => item.ProductId.ToString("X4")).Distinct().Order()
        ];
    }

    // Pads a report with zeros to the collection's length; a longer report is sent whole.
    private static byte[] Padded(string hex, int length)
    {
        var report = LabModeCommands.ParseHex(hex);
        return report.Length >= length ? report : [.. report, .. new byte[length - report.Length]];
    }

    // The SDK pads to the collection's feature length and refuses a longer report; either failure is final.
    private static string? SetFeature(SafeFileHandle handle, LabHidEndpoint endpoint, byte[] report)
    {
        try
        {
            HidDevices.SetFeature(handle, endpoint.Collection, report);
            return null;
        }
        catch (Win32Exception ex)
        {
            return $"error {ex.NativeErrorCode}";
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }
}
