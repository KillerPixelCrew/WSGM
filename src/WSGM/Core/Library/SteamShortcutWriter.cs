using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>What one apply attempt did.</summary>
/// <param name="AppId">The confirmed shortcut id, or zero.</param>
/// <param name="Confirmed">Whether the id was corroborated by a library read.</param>
/// <param name="Error">Why not, when it was not.</param>
public sealed record ShortcutWriteResult(uint AppId, bool Confirmed, string? Error);

/// <summary>Creates, updates and removes the non-Steam shortcuts an import generates.</summary>
/// <remarks>
///     <para>
///         Every write goes through the running Steam client, which stores the values verbatim and
///         persists them itself. One at a time, with a settle between: each write is a separate
///         evaluation against a client that is mutating its own library store, and a batch fired in
///         parallel is how that store gets corrupted.
///     </para>
///     <para>
///         A new id is confirmed by two independent sources — what the client returned, and a
///         before-and-after diff of the library — and the diff is the authority. The return value's
///         contract has never been verified across client builds; the diff is observation. When they
///         disagree, or the diff is ambiguous, the entry is recorded as unconfirmed and the run
///         stops. It is never retried: the shortcut may well exist, and asking again would create a
///         second one.
///     </para>
/// </remarks>
public sealed class SteamShortcutWriter
{
    /// <summary>How long to let Steam settle between writes.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    private readonly Func<string, string, string, string, CancellationToken, Task<uint>> _add;

    private readonly Func<CancellationToken, Task<IReadOnlyList<uint>>> _listShortcuts;
    private readonly Func<uint, CancellationToken, Task<bool>> _remove;
    private readonly Func<uint, string, string, CancellationToken, Task<bool>> _setLaunch;

    /// <summary>Creates the writer over the client calls it drives.</summary>
    /// <param name="listShortcuts">Lists the non-Steam shortcut ids Steam currently has.</param>
    /// <param name="add">Creates a shortcut and reports the id Steam generated.</param>
    /// <param name="setLaunch">Rewrites an existing shortcut's Target and arguments.</param>
    /// <param name="remove">Deletes a shortcut.</param>
    public SteamShortcutWriter(
        Func<CancellationToken, Task<IReadOnlyList<uint>>> listShortcuts,
        Func<string, string, string, string, CancellationToken, Task<uint>> add,
        Func<uint, string, string, CancellationToken, Task<bool>> setLaunch,
        Func<uint, CancellationToken, Task<bool>> remove)
    {
        ArgumentNullException.ThrowIfNull(listShortcuts);
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(setLaunch);
        ArgumentNullException.ThrowIfNull(remove);
        _listShortcuts = listShortcuts;
        _add = add;
        _setLaunch = setLaunch;
        _remove = remove;
    }

    /// <summary>Creates one shortcut and confirms the id Steam gave it.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="fields">What it should run.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task<ShortcutWriteResult> AddAsync(
        string name, PackagedLauncherShortcutFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var before = await ShortcutsAsync(cancellationToken).ConfigureAwait(false);
        var returned = await _add(
                name, fields.Target, fields.StartDirectory, fields.LaunchOptions, cancellationToken)
            .ConfigureAwait(false);

        await Task.Delay(Settle, cancellationToken).ConfigureAwait(false);
        var after = await ShortcutsAsync(cancellationToken).ConfigureAwait(false);
        var appeared = after.Except(before).ToList();

        // The diff is the authority. A returned id that no library read corroborates describes
        // something this code cannot point at.
        if (appeared.Count != 1)
        {
            return new ShortcutWriteResult(returned, false,
                appeared.Count == 0
                    ? "Steam reported no new entry after creating this shortcut."
                    : $"Steam gained {appeared.Count} entries at once, so which one this is cannot be told.");
        }

        var observed = appeared[0];
        if (returned != 0 && returned != observed)
        {
            return new ShortcutWriteResult(observed, false,
                $"Steam returned {Id(returned)} but the library gained {Id(observed)}.");
        }

        return new ShortcutWriteResult(observed, true, null);
    }

    /// <summary>Rewrites what an existing generated entry runs.</summary>
    /// <param name="appId">The shortcut to change.</param>
    /// <param name="fields">Its new launch values.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    ///     Never remove-and-re-add: that would lose the id and every piece of artwork attached to it.
    /// </remarks>
    public async Task<ShortcutWriteResult> UpdateAsync(
        uint appId, PackagedLauncherShortcutFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var accepted = await _setLaunch(appId, fields.Target, fields.LaunchOptions, cancellationToken)
            .ConfigureAwait(false);
        await Task.Delay(Settle, cancellationToken).ConfigureAwait(false);
        return accepted
            ? new ShortcutWriteResult(appId, true, null)
            : new ShortcutWriteResult(appId, false, "Steam did not accept the new launch command.");
    }

    /// <summary>Deletes one generated entry.</summary>
    /// <param name="appId">The shortcut to delete.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task<ShortcutWriteResult> RemoveAsync(uint appId, CancellationToken cancellationToken)
    {
        var accepted = await _remove(appId, cancellationToken).ConfigureAwait(false);
        await Task.Delay(Settle, cancellationToken).ConfigureAwait(false);
        return accepted
            ? new ShortcutWriteResult(appId, true, null)
            : new ShortcutWriteResult(appId, false, "Steam did not accept the removal.");
    }

    private async Task<HashSet<uint>> ShortcutsAsync(CancellationToken cancellationToken)
    {
        return [.. await _listShortcuts(cancellationToken).ConfigureAwait(false)];
    }

    private static string Id(uint appId)
    {
        return appId.ToString(CultureInfo.InvariantCulture);
    }
}
