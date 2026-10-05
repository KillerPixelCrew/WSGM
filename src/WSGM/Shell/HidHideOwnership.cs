using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Shell;

/// <summary>One entry WSGM added to HidHide, remembered so exit and uninstall take exactly that back out.</summary>
internal sealed class HidHideOwnedDelta
{
    public HidHideEntryKind EntryKind { get; init; }

    public string Value { get; init; } = string.Empty;
}

/// <summary>The entries WSGM added, persisted so a crash still leaves them for the next exit to remove.</summary>
internal sealed class HidHideOwnershipLedger
{
    public List<HidHideOwnedDelta> Deltas { get; init; } = [];
}

internal interface IHidHideOwnershipStore
{
    Task<HidHideOwnershipLedger?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(HidHideOwnershipLedger ledger, CancellationToken cancellationToken);

    Task DeleteAsync(CancellationToken cancellationToken);
}

internal sealed class FileHidHideOwnershipStore : IHidHideOwnershipStore
{
    private readonly string _path;

    internal FileHidHideOwnershipStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public async Task<HidHideOwnershipLedger?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using FileStream stream = new(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync(
            stream,
            HidHideOwnershipJsonContext.Default.HidHideOwnershipLedger,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(
        HidHideOwnershipLedger ledger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The HidHide ledger path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        await AtomicFile.WriteAsync(
            _path,
            (stream, token) => JsonSerializer.SerializeAsync(
                stream,
                ledger,
                HidHideOwnershipJsonContext.Default.HidHideOwnershipLedger,
                token),
            true,
            cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(_path);
        return Task.CompletedTask;
    }
}

/// <summary>How a HidHide change went, for the log and the controller state.</summary>
internal sealed record HidHideResult(bool Succeeded, string Detail);

/// <summary>Hides the physical pad while WSGM drives a virtual one, and shows it again on leaving.</summary>
/// <remarks>
///     HC's model: the cloak is on while WSGM runs controller management, the pad's paths are hidden
///     once, and they stay hidden across sleep and restarts. Only leaving (exit, disabling controller
///     management, uninstall, or a device that could not be brought back) removes WSGM's entries and
///     turns the cloak off, so no user is left with a hidden controller (see "Never strand users on
///     exit"). Writes are trusted when the driver accepts them; nothing is compared or retried.
/// </remarks>
internal sealed class HidHideOwnership
{
    internal const string FileName = "hidhide-ownership.json";

    internal static HidHideOwnership ForUser(string root)
    {
        return new HidHideOwnership(new NativeHidHideControl(),
            new FileHidHideOwnershipStore(Path.Combine(root, FileName)));
    }

    private readonly IHidHideControl _control;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IHidHideOwnershipStore _store;

    internal HidHideOwnership(IHidHideControl control, IHidHideOwnershipStore store)
    {
        _control = control;
        _store = store;
    }

    /// <summary>Whether WSGM holds the cloak or entries from a run that did not leave cleanly.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>True when the ledger exists, even empty, or cannot be read.</returns>
    /// <remarks>
    ///     The file's presence, not its entry count, means WSGM holds the cloak: a hide that found every
    ///     entry already listed still records an empty ledger before it turns the cloak on.
    /// </remarks>
    internal async Task<bool> HasOwnershipRecordAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _store.LoadAsync(cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            // A corrupt recovery record still warrants cloak-off; preserve its bytes for diagnosis.
            return true;
        }
    }

    /// <summary>Makes WSGM able to read devices HidHide is hiding, before it needs to.</summary>
    /// <param name="controllerManagementEnabled">Whether controller management may run at all.</param>
    /// <param name="controllerReaderApplication">The WSGM image path to allow.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A description of what was found, for the log.</returns>
    /// <remarks>
    ///     When something else hid the controller first, the plugin cannot see the device it is asked to
    ///     discover, and hiding it later does not help that discovery (device evidence in
    ///     <c>docs\device-integration.md</c>, "HidHide findings"). Allowing WSGM's own process is harmless
    ///     to every other owner and is recorded like any other entry WSGM added.
    /// </remarks>
    internal async Task<string> EnsureReadableAsync(
        bool controllerManagementEnabled,
        string controllerReaderApplication,
        CancellationToken cancellationToken)
    {
        if (!controllerManagementEnabled || string.IsNullOrWhiteSpace(controllerReaderApplication))
        {
            return "Controller management is off; HidHide was not consulted.";
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _control.Read();
            if (!state.Succeeded || state.Inverse)
            {
                return "HidHide is not available; nothing to allow.";
            }

            if (!state.Active || state.Devices.Count == 0)
            {
                return "HidHide is hiding nothing; no allowance needed.";
            }

            if (Contains(state.Applications, controllerReaderApplication))
            {
                return $"HidHide hides {state.Devices.Count} device(s); WSGM is already allowed.";
            }

            var added = await AddAsync(state, HidHideEntryKind.Application, [controllerReaderApplication],
                cancellationToken).ConfigureAwait(false);
            return added.Succeeded
                ? $"HidHide hides {state.Devices.Count} device(s) that WSGM does not own; "
                  + "added WSGM to its allowlist so the plugin can read them."
                : "HidHide is hiding devices and WSGM could not add itself to its allowlist: " + added.Detail;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Allows WSGM, hides the pad and turns the cloak on. Entries already there are left alone.</summary>
    /// <param name="controllerReaderApplication">The WSGM image path that must still read the pad.</param>
    /// <param name="physicalDevices">The devices the plugin reads.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>Succeeded when the driver took every write.</returns>
    internal async Task<HidHideResult> HideAsync(
        string controllerReaderApplication,
        IReadOnlyList<PhysicalDeviceIdentity> physicalDevices,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerReaderApplication);
        ArgumentNullException.ThrowIfNull(physicalDevices);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _control.Read();
            if (!state.Succeeded)
            {
                return new HidHideResult(false, $"HidHide is not available (Win32 error {state.Error}).");
            }

            if (state.Inverse)
            {
                return new HidHideResult(false, "HidHide inverse mode is incompatible with WSGM's allowlist.");
            }

            var allowed = await AddAsync(state, HidHideEntryKind.Application, [controllerReaderApplication],
                cancellationToken).ConfigureAwait(false);
            if (!allowed.Succeeded)
            {
                return allowed;
            }

            string[] devices =
            [
                .. physicalDevices
                    .Where(device => device.RequiresHiding && !string.IsNullOrWhiteSpace(device.InstancePath))
                    .Select(device => device.InstancePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
            ];
            var hidden = await AddAsync(state, HidHideEntryKind.Device, devices, cancellationToken)
                .ConfigureAwait(false);
            if (!hidden.Succeeded)
            {
                return hidden;
            }

            if (!state.Active)
            {
                // WSGM owns the cloak. Handheld Companion's uninstaller turns it off (its Inno script runs
                // HidHideCLI --cloak-off), and a WSGM that only checked the switch then ran without a
                // virtual pad while Steam read the physical one (Xbox Ally X, 2026-09-28). The ledger is
                // recorded first, even empty when every entry was already listed, so a crash still leaves
                // the next start a record that WSGM turned the cloak on.
                HidHideOwnershipLedger? recorded;
                try
                {
                    recorded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsUnreadable(ex))
                {
                    return UnreadableLedgerForHide();
                }

                if (recorded is null)
                {
                    try
                    {
                        await _store.SaveAsync(new HidHideOwnershipLedger(), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return UnsavedLedgerForHide(ex);
                    }
                }

                var error = _control.WriteActive(true);
                if (error != 0)
                {
                    return new HidHideResult(false, $"HidHide cloak could not be turned on (Win32 error {error}).");
                }

                Log.Info("HidHide cloak was off; turned it on.");
            }

            return new HidHideResult(true, $"HidHide hides {devices.Length} device(s) for WSGM.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes every entry WSGM added and turns the cloak off, so the physical pad comes back.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>Succeeded when the driver took every write.</returns>
    /// <remarks>
    ///     Runs on every leave: shutdown, session end, update, disabling controller management, and a
    ///     device that could not be brought back. The cloak goes off whether or not this run turned it on,
    ///     because WSGM owns it and a user whose WSGM has stopped must have their controller back.
    /// </remarks>
    internal async Task<HidHideResult> ShowAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ShowUnderGateAsync([], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Uninstall: shows the pad again and takes every WSGM executable off the allowlist.</summary>
    /// <param name="ownApplications">WSGM's own executables, in any path notation.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>Succeeded when the driver took every write, or HidHide is not installed.</returns>
    internal async Task<HidHideResult> ShowForUninstallAsync(
        IReadOnlyList<string> ownApplications,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownApplications);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ShowUnderGateAsync(ownApplications, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HidHideResult> ShowUnderGateAsync(
        IReadOnlyList<string> extraApplications,
        CancellationToken cancellationToken)
    {
        List<string> problems = [];
        var cloakError = _control.WriteActive(false);
        if (HidHideControlState.IsNotInstalled(cloakError))
        {
            await _store.DeleteAsync(cancellationToken).ConfigureAwait(false);
            return new HidHideResult(true, "HidHide is not installed, so it hides nothing.");
        }

        if (cloakError != 0)
        {
            problems.Add($"cloak off (Win32 error {cloakError})");
        }
        else
        {
            Log.Info("HidHide cloak turned off; the physical controller is visible again.");
        }

        var state = _control.Read();
        if (!state.Succeeded)
        {
            if (!HidHideControlState.IsNotInstalled(state.Error))
            {
                return new HidHideResult(false, $"HidHide could not be read (Win32 error {state.Error}).");
            }

            await _store.DeleteAsync(cancellationToken).ConfigureAwait(false);
            return new HidHideResult(true, "HidHide is not installed, so it hides nothing.");
        }

        List<HidHideOwnedDelta> owned;
        try
        {
            owned = (await _store.LoadAsync(cancellationToken).ConfigureAwait(false))?.Deltas ?? [];
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            // Without the ledger WSGM cannot tell its entries from another tool's, so it removes none of
            // them and keeps the file. WSGM's own executables do not depend on the ledger.
            problems.Add($"ownership ledger unreadable ({ex.Message}); its entries were left in HidHide "
                + "and the file was kept");
            owned = [];
        }

        var applications = owned.Where(delta => delta.EntryKind is HidHideEntryKind.Application)
            .Select(delta => delta.Value).Concat(extraApplications).ToArray();
        var devices = owned.Where(delta => delta.EntryKind is HidHideEntryKind.Device)
            .Select(delta => delta.Value).ToArray();
        Remove(state.Applications, applications, HidHideEntryKind.Application, problems);
        Remove(state.Devices, devices, HidHideEntryKind.Device, problems);
        if (problems.Count > 0)
        {
            // The list stays for the next exit to try again; nothing retries here.
            return new HidHideResult(false, "HidHide did not accept: " + string.Join(", ", problems));
        }

        await _store.DeleteAsync(cancellationToken).ConfigureAwait(false);
        return new HidHideResult(true, "WSGM's HidHide entries are gone and the cloak is off.");
    }

    /// <summary>Adds the values HidHide does not list yet, and remembers them as WSGM's.</summary>
    private async Task<HidHideResult> AddAsync(
        HidHideControlState state,
        HidHideEntryKind kind,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        var current = kind is HidHideEntryKind.Application ? state.Applications : state.Devices;
        string[] missing = [.. values.Where(value => !Contains(current, value))];
        if (missing.Length == 0)
        {
            return new HidHideResult(true, "Already listed.");
        }

        // Recorded before the write, so a crash between the two still leaves the entry to clean up.
        HidHideOwnershipLedger ledger;
        try
        {
            ledger = await _store.LoadAsync(cancellationToken).ConfigureAwait(false) ?? new HidHideOwnershipLedger();
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            return UnreadableLedgerForHide();
        }

        ledger.Deltas.AddRange(missing.Select(value => new HidHideOwnedDelta { EntryKind = kind, Value = value }));
        try
        {
            await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UnsavedLedgerForHide(ex);
        }

        int error;
        try
        {
            error = _control.Write(kind, [.. current, .. missing]);
        }
        catch (ArgumentException ex)
        {
            // An entry another tool left in the list cannot be encoded back.
            return new HidHideResult(false, $"HidHide did not accept the {kind} list ({ex.Message}).");
        }

        return error == 0
            ? new HidHideResult(true, "Added.")
            : new HidHideResult(false, $"HidHide did not accept the {kind} list (Win32 error {error}).");
    }

    /// <summary>The refusal a hide returns when the ledger it must record into cannot be read.</summary>
    /// <returns>A not-succeeded result, so controller management reports itself unavailable.</returns>
    private static HidHideResult UnreadableLedgerForHide()
    {
        return new HidHideResult(false,
            "The HidHide ownership ledger could not be read; WSGM does not hide the controller without recording it.");
    }

    /// <summary>The refusal a hide returns when the ledger it must record into cannot be saved.</summary>
    /// <param name="ex">The exception the save threw.</param>
    /// <returns>A not-succeeded result, so controller management reports itself unavailable.</returns>
    private static HidHideResult UnsavedLedgerForHide(Exception ex)
    {
        return new HidHideResult(false,
            $"The HidHide ownership ledger could not be saved ({ex.Message}); WSGM does not hide the controller "
            + "without recording it.");
    }

    /// <summary>Whether a ledger load failed because the file is corrupt or cannot be opened.</summary>
    /// <param name="ex">The exception the load threw.</param>
    /// <returns>Whether the ledger counts as unreadable.</returns>
    private static bool IsUnreadable(Exception ex)
    {
        return ex is IOException or JsonException or UnauthorizedAccessException;
    }

    private void Remove(
        IReadOnlyList<string> current,
        IReadOnlyList<string> owned,
        HidHideEntryKind kind,
        List<string> problems)
    {
        string[] remaining = [.. current.Where(entry => !Contains(owned, entry))];
        if (remaining.Length == current.Count)
        {
            return;
        }

        int error;
        try
        {
            error = _control.Write(kind, remaining);
        }
        catch (ArgumentException ex)
        {
            // An entry another tool left that cannot be encoded must not stop the other list.
            problems.Add($"{kind} list ({ex.Message})");
            return;
        }

        if (error != 0)
        {
            problems.Add($"{kind} list (Win32 error {error})");
        }
    }

    /// <summary>Whether HidHide already lists this entry, in whichever notation it stored it.</summary>
    /// <param name="entries">Entries exactly as HidHide returned them.</param>
    /// <param name="value">The entry WSGM is looking for.</param>
    /// <returns>Whether it is present.</returns>
    /// <remarks>
    ///     A plain string compare is not enough for applications: HidHide stores them as NT device
    ///     paths (<c>\Device\HarddiskVolume3\Program Files\…</c>) while WSGM knows its own executables by
    ///     drive letter. Without normalization the allowlist grows on every activation and cleanup leaves
    ///     the other notation's duplicate behind (device evidence in <c>docs\device-integration.md</c>,
    ///     "HidHide findings").
    /// </remarks>
    internal static bool Contains(IEnumerable<string> entries, string value)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var normalized = NormalizePath(value);
        return entries.Any(entry =>
            string.Equals(entry, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizePath(entry), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reduces an entry to a form both notations agree on.</summary>
    /// <param name="value">A DOS path, an NT device path, or a device instance path.</param>
    /// <returns>The comparable form.</returns>
    /// <remarks>
    ///     Only the volume prefix differs between the two notations, so stripping it leaves the part
    ///     that identifies the file. Device instance paths carry no such prefix and pass through.
    /// </remarks>
    internal static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var path = value.Trim().Replace('/', '\\');

        // \Device\HarddiskVolumeN\rest  ->  \rest
        const string devicePrefix = @"\device\harddiskvolume";
        if (path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var separator = path.IndexOf('\\', devicePrefix.Length);
            return separator < 0 ? string.Empty : path[separator..];
        }

        // C:\rest  ->  \rest. Deliberately only a drive letter: a UNC path has no volume to strip
        // and must keep its server and share, which are part of what identifies it.
        if (path is [_, ':', ..] && char.IsLetter(path[0]))
        {
            return path.Length == 2 ? string.Empty : path[2..];
        }

        return path;
    }
}

[JsonSerializable(typeof(HidHideOwnershipLedger))]
internal sealed partial class HidHideOwnershipJsonContext : JsonSerializerContext;
