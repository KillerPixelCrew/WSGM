using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>What one apply attempt did.</summary>
/// <param name="AppId">The shortcut's id, or zero when nothing is known to exist.</param>
/// <param name="Confirmed">Whether the id is known to be the entry this write made or changed.</param>
/// <param name="Error">Why not, when it was not.</param>
/// <param name="Mismatch">
///     For a confirmed add, the fields Steam did not keep as written, or null when it kept them all.
///     The shortcut exists either way.
/// </param>
public sealed record ShortcutWriteResult(uint AppId, bool Confirmed, string? Error, string? Mismatch = null);

/// <summary>Creates, updates and removes the non-Steam shortcuts an import generates.</summary>
/// <remarks>
///     <para>
///         The Steam mechanics live in the toolkit's <c>SteamApps</c>: one write at a time with a
///         settle, and an add confirmed by a before-and-after diff of the library with its fields read
///         back. This class holds WSGM's policy over them.
///     </para>
///     <para>
///         Nothing is retried. An add Steam did not confirm may well exist, and asking again would
///         create a second one; the caller records it as unconfirmed and stops. And once a write has
///         been sent it runs to its answer even if the user presses Stop, because Steam may already
///         have changed the entry and the caller has to get to record what it did.
///     </para>
/// </remarks>
public sealed class SteamShortcutWriter
{
    private readonly Func<string, ShortcutFields, CancellationToken, Task<ShortcutWriteResult>> _add;
    private readonly Func<uint, CancellationToken, Task<bool>> _remove;
    private readonly Func<uint, ShortcutFields, CancellationToken, Task<bool>> _setLaunch;

    /// <summary>Creates the writer over the client calls it drives.</summary>
    /// <param name="add">Creates a shortcut and confirms which entry it is.</param>
    /// <param name="setLaunch">Rewrites an existing shortcut's Target, start directory and arguments.</param>
    /// <param name="remove">Deletes a shortcut.</param>
    public SteamShortcutWriter(
        Func<string, ShortcutFields, CancellationToken, Task<ShortcutWriteResult>> add,
        Func<uint, ShortcutFields, CancellationToken, Task<bool>> setLaunch,
        Func<uint, CancellationToken, Task<bool>> remove)
    {
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(setLaunch);
        ArgumentNullException.ThrowIfNull(remove);
        _add = add;
        _setLaunch = setLaunch;
        _remove = remove;
    }

    /// <summary>Creates one shortcut and confirms the id Steam gave it.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="fields">What it should run.</param>
    /// <param name="cancellationToken">Cancels the write before it is sent.</param>
    /// <returns>The id, and whether it is confirmed.</returns>
    public async Task<ShortcutWriteResult> AddAsync(
        string name, ShortcutFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        return await _add(name, fields, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Rewrites what an existing generated entry runs and where it runs from.</summary>
    /// <param name="appId">The shortcut to change.</param>
    /// <param name="fields">Its new launch values.</param>
    /// <param name="cancellationToken">Cancels the write before it is sent.</param>
    /// <returns>Whether Steam accepted the new command.</returns>
    /// <remarks>
    ///     The start directory is written with the Target: a title moved from one program to another
    ///     would otherwise run the new one from the old one's folder. Never remove-and-re-add, which
    ///     would lose the id and every piece of artwork attached to it.
    /// </remarks>
    public async Task<ShortcutWriteResult> UpdateAsync(
        uint appId, ShortcutFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        cancellationToken.ThrowIfCancellationRequested();
        return await _setLaunch(appId, fields, CancellationToken.None).ConfigureAwait(false)
            ? new ShortcutWriteResult(appId, true, null)
            : new ShortcutWriteResult(appId, false, "Steam did not accept the new launch command.");
    }

    /// <summary>Deletes one generated entry.</summary>
    /// <param name="appId">The shortcut to delete.</param>
    /// <param name="cancellationToken">Cancels the write before it is sent.</param>
    /// <returns>Whether Steam accepted the removal.</returns>
    public async Task<ShortcutWriteResult> RemoveAsync(uint appId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _remove(appId, CancellationToken.None).ConfigureAwait(false)
            ? new ShortcutWriteResult(appId, true, null)
            : new ShortcutWriteResult(appId, false, "Steam did not accept the removal.");
    }
}
