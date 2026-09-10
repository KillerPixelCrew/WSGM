using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
/// Puts WSGM's own storage operations behind Steam's revived storage pages.
/// </summary>
/// <remarks>
/// Every action here delegates to the manager that already owns it: <see cref="RemovableDriveManager"/>
/// for eject and <see cref="SdFormatManager"/> for format and library registration. Nothing is
/// reimplemented for Steam's benefit, so the identity checks, the volume-GUID resolution, the
/// backup-and-splice of Steam's library file and the staged destructive sequence are the same code
/// whichever surface asked — which is the whole point of the issue this satisfies.
/// <para>
/// Formatting stays behind its own switch, defaulting off. Steam's page can offer it to a controller
/// press, and this repository treats erasing a card as an attended operation; the switch is what
/// keeps "the pages are alive" from silently also meaning "anyone holding the pad can wipe a drive".
/// A refusal is reported rather than swallowed, so the button says why instead of doing nothing.
/// </para>
/// </remarks>
public sealed class SteamStorageBridge : ISteamStorageBackend
{
    private readonly RemovableDriveManager _drives;
    private readonly SdFormatManager _formats;
    private readonly Func<bool> _formatAllowed;

    /// <summary>Creates the bridge over the managers that already own these operations.</summary>
    /// <param name="drives">The removable-drive manager, which owns safe eject.</param>
    /// <param name="formats">The format manager, which owns erase and library registration.</param>
    /// <param name="formatAllowed">Whether formatting from Steam's own pages is permitted.</param>
    public SteamStorageBridge(
        RemovableDriveManager drives, SdFormatManager formats, Func<bool> formatAllowed)
    {
        _drives = drives ?? throw new ArgumentNullException(nameof(drives));
        _formats = formats ?? throw new ArgumentNullException(nameof(formats));
        _formatAllowed = formatAllowed ?? throw new ArgumentNullException(nameof(formatAllowed));
    }

    /// <summary>Projects what the managers currently see into Steam's own state shape.</summary>
    /// <returns>The state, or null while neither manager has anything to report.</returns>
    /// <remarks>
    /// Null and empty mean different things on this wire. Null is "nothing to say", which leaves
    /// Steam's page as it was; an empty state is the assertion that the machine has no removable
    /// storage, which the page renders as such. Reporting empty before the managers have scanned
    /// would show "no drives" to someone holding a card.
    /// </remarks>
    public SteamStorageState? ReadState()
    {
        var ejectable = _drives.Drives.ToArray();
        var formattable = _formats.Targets.ToArray();
        if (ejectable.Length == 0 && formattable.Length == 0)
        {
            return null;
        }

        // One drive row per format target, because that is the manager that knows a disk by number
        // and can say whether it is erasable at all. Unformatted is reported false rather than
        // guessed: the format manager only lists targets it could erase, and whether one currently
        // carries a filesystem is the volume monitor's business.
        var drives = formattable
            .Select(target => new SteamStorageDrive(target.Id, Formattable: true, Unformatted: false))
            .ToList();

        // Ejectable volumes, which may or may not correspond to a format target. An eject row with
        // no matching drive still has to appear: a USB stick is ejectable and not a format target.
        var devices = new List<SteamStorageBlockDevice>();
        foreach (RemovableDriveEntry entry in ejectable)
        {
            if (entry.Ejected)
            {
                continue;
            }
            devices.Add(new SteamStorageBlockDevice(
                entry.Id,
                DriveId: drives.Any(drive => drive.Id == entry.Id) ? entry.Id : "",
                MountPaths: SplitLetters(entry.Letters),
                HasSteamLibrary: false));
        }

        // Trim is reported unsupported because neither manager exposes one. Claiming otherwise would
        // put a button on Steam's page that could never do anything.
        return new SteamStorageState(
            drives,
            devices,
            AdoptSupported: true,
            UnmountSupported: ejectable.Length > 0,
            TrimSupported: false,
            TrimRunning: false);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> AdoptAsync(string driveId, CancellationToken cancellationToken)
    {
        FormatTargetEntry? target = FindTarget(driveId);
        string? path = FirstMountPath(driveId);
        if (target is null && path is null)
        {
            return Refuse("That drive is no longer present.");
        }

        try
        {
            // The manager registers the folder with the running client and reconciles Steam's own
            // library file; this only names the path.
            await _formats.AddLibraryAsync(path ?? "").ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Steam storage: adopting {driveId} failed: {ex.Message}");
            return Refuse("Could not register that drive as a Steam library.");
        }
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> EjectAsync(
        string blockDeviceId, string driveId, CancellationToken cancellationToken)
    {
        string id = blockDeviceId.Length > 0 ? blockDeviceId : driveId;
        RemovableDriveEntry? entry = _drives.Drives.FirstOrDefault(drive => drive.Id == id);
        if (entry is null)
        {
            return Refuse("That drive is no longer present.");
        }
        if (!entry.ActionEnabled)
        {
            // Busy or already gone. Both are refusals with a reason rather than a silent no-op.
            return Refuse(entry.Ejected ? "That drive is already ejected." : "That drive is busy.");
        }

        try
        {
            await _drives.EjectAsync(entry).ConfigureAwait(false);
            return entry.Ejected
                ? SteamUiCommandResult.Applied
                : Refuse(entry.ResultText.Length > 0
                    ? entry.ResultText
                    : "Windows would not release that drive.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Steam storage: ejecting {id} failed: {ex.Message}");
            return Refuse("Could not eject that drive.");
        }
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> FormatAsync(string driveId, CancellationToken cancellationToken)
    {
        if (!_formatAllowed())
        {
            return Refuse(
                "Formatting from Steam's pages is switched off in WSGM Settings.");
        }

        FormatTargetEntry? target = FindTarget(driveId);
        if (target is null)
        {
            return Refuse("That drive is no longer present.");
        }
        if (_formats.Busy)
        {
            return Refuse("Another format is already running.");
        }

        try
        {
            // The manager re-reads the disk's identity immediately before every destructive step,
            // so a card swapped between this call and the erase aborts there rather than here.
            await _formats.FormatAsync(target).ConfigureAwait(false);
            return SteamUiCommandResult.Applied;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Steam storage: formatting {driveId} failed: {ex.Message}");
            return Refuse("The format did not complete.");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Refused rather than faked. Neither manager exposes a trim, and the published state says
    /// <c>TrimSupported: false</c> so Steam should not offer it; answering anything else here would
    /// make a button that reports success and does nothing.
    /// </remarks>
    public Task<SteamUiCommandResult> TrimAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Refuse("WSGM does not run drive trimming."));

    /// <summary>A refusal that carries its reason, which the contract requires of every failure.</summary>
    /// <param name="reason">What the user is told, on the control they pressed.</param>
    /// <returns>The failed result.</returns>
    private static SteamUiCommandResult Refuse(string reason) => new(false, reason);

    private FormatTargetEntry? FindTarget(string driveId) =>
        driveId.Length == 0 ? null : _formats.Targets.FirstOrDefault(target => target.Id == driveId);

    private string? FirstMountPath(string id)
    {
        RemovableDriveEntry? entry = _drives.Drives.FirstOrDefault(drive => drive.Id == id);
        IReadOnlyList<string> paths = entry is null ? [] : SplitLetters(entry.Letters);
        return paths.Count > 0 ? paths[0] : null;
    }

    /// <summary>Turns the manager's display string of drive letters into mount paths.</summary>
    /// <param name="letters">The entry's letters as it shows them, for example "D:, E:".</param>
    /// <returns>One path per letter, in the form Windows uses.</returns>
    internal static IReadOnlyList<string> SplitLetters(string letters) => (letters ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(part => part.EndsWith('\\') ? part : part + "\\")
        .ToArray();
}
