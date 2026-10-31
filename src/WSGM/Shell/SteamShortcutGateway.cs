using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The one serialized, account-checked path to Steam's non-Steam shortcuts and collections, shared by
///     the Game Library, the shortcut availability tracker and the emulator shortcuts.
/// </summary>
/// <param name="steam">The session's Steam client.</param>
/// <param name="importState">The import state that records the confirmed Steam account.</param>
internal sealed class SteamShortcutGateway(SteamClient steam, ImportStateStore importState)
{
    private readonly SemaphoreSlim _operations = new(initialCount: 1, maxCount: 1);

    /// <summary>Reads Steam's collections in the confirmed account.</summary>
    /// <param name="accountId">The account the collections must belong to.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The collections, tagged with the account.</returns>
    /// <exception cref="InvalidOperationException">The account changed or the collections could not be read.</exception>
    internal async Task<IReadOnlyList<ImportedCollection>> ReadCollectionsAsync(uint accountId,
        CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RequireSameAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
            var read = await steam.Library.ReadCollectionsAsync(cancellationToken).ConfigureAwait(false);
            await RequireSameAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
            if (!read.Succeeded || read.Value is null)
            {
                throw new InvalidOperationException("Steam collections could not be read. Legacy collection changes remain pending.");
            }

            return read.Value.Select(collection => new ImportedCollection
            {
                AccountId = accountId, Id = collection.Id, Name = collection.Name, AppIds = [.. collection.AppIds]
            }).ToArray();
        }
        finally
        {
            _operations.Release();
        }
    }

    /// <summary>Creates a writer whose every write runs serialized in the account it was created for.</summary>
    /// <returns>The writer.</returns>
    internal SteamShortcutWriter CreateWriter()
    {
        var writerAccount = importState.ActiveAccountId;
        return new SteamShortcutWriter(
            (name, fields, token) => WriteAsync(() => AddShortcutAsync(name, fields, token), token),
            (appId, fields, token) => WriteAsync(async () =>
                (await steam.Apps.SetShortcutLaunchAsync(appId, fields.Target, fields.StartDirectory,
                    fields.LaunchOptions, token).ConfigureAwait(false)).Succeeded, token),
            (appId, token) => WriteAsync(async () =>
                (await steam.Apps.RemoveShortcutAsync(appId, token).ConfigureAwait(false)).Succeeded, token),
            (appId, name, token) => WriteAsync(async () =>
                (await steam.Apps.SetShortcutNameAsync(appId, name, token).ConfigureAwait(false)).Succeeded, token));

        async Task<T> WriteAsync<T>(Func<Task<T>> write, CancellationToken token)
        {
            await _operations.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var account = await ReadAccountAsync(token).ConfigureAwait(false);
                writerAccount ??= account;
                if (writerAccount != account || importState.ActiveAccountId != account)
                {
                    importState.SetActiveAccount(null);
                    throw new InvalidOperationException("The Steam account changed. Scan the library before saving again.");
                }

                var result = await write().ConfigureAwait(false);
                await RequireSameAccountAsync(account, token).ConfigureAwait(false);
                return result;
            }
            finally
            {
                _operations.Release();
            }
        }
    }

    /// <summary>Reads every non-Steam shortcut Steam has, with what each one runs, in one call.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The shortcuts, as a snapshot tagged with the confirmed account.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The library could not be read whole. Refused, not guessed at: an empty or partial list reads
    ///     as "not one of ours", which turns a generated entry into a fresh Add and a second copy of
    ///     the same game, and a recorded one into a hand-edited conflict.
    /// </exception>
    internal async Task<IReadOnlyList<ExistingShortcut>> ReadShortcutsAsync(
        CancellationToken cancellationToken)
    {
        // Transport readiness precedes Steam's complete app-overview update. The bounded read wait
        // keeps startup reconciliation from retaining a transient library-initialization failure.
        if (!await LibraryTabManager.WaitForLibraryAsync(steam, cancellationToken, requireTabClaim: false)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("Steam's library is not ready. Shortcut changes remain pending.");
        }

        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = await ReadAccountAsync(cancellationToken).ConfigureAwait(false);
            if (importState.ActiveAccountId != account)
            {
                importState.SetActiveAccount(null);
            }

            var listed = await steam.Apps.ListShortcutsAsync(cancellationToken).ConfigureAwait(false);
            if (listed is not { Succeeded: true, Value: { } shortcuts })
            {
                throw new InvalidOperationException(listed.Dispatch == SteamUiDispatch.Answered
                    ? $"Steam's library could not be read ({listed.Error}). Nothing was changed; try again."
                    : "Steam is not reachable, so its library could not be read. Nothing was changed.");
            }

            if (await ReadAccountAsync(cancellationToken).ConfigureAwait(false) != account)
            {
                importState.SetActiveAccount(null);
                throw new InvalidOperationException("The Steam account changed while reading its shortcuts. Scan again.");
            }

            var result = shortcuts.Select(shortcut => new ExistingShortcut(shortcut.AppId, shortcut.Target,
                shortcut.LaunchOptions, shortcut.StartDirectory, shortcut.Name)).ToArray();
            importState.SetActiveAccount(account);
            importState.BindLegacyAccount(account, result);
            importState.ObserveShortcutPresence(account, result);
            return new SteamShortcutSnapshot(account, result);
        }
        finally
        {
            _operations.Release();
        }
    }

    /// <summary>Reads one shortcut again, right before an apply writes to it.</summary>
    /// <param name="appId">The shortcut's app id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What it runs, or null when Steam has no shortcut with that id any more.</returns>
    /// <exception cref="InvalidOperationException">Steam could not be reached.</exception>
    internal async Task<ExistingShortcut?> ReadShortcutAsync(uint appId, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = importState.ActiveAccountId;
            if (account is null)
            {
                throw new InvalidOperationException("Read Steam's library before checking a shortcut's ownership.");
            }

            await RequireSameAccountAsync(account.Value, cancellationToken).ConfigureAwait(false);
            var read = await steam.Apps.ReadDetailsAsync(appId, cancellationToken).ConfigureAwait(false);
            if (read.Dispatch != SteamUiDispatch.Answered)
            {
                throw new InvalidOperationException(
                    "Steam is not reachable, so the save stopped. Nothing else was changed.");
            }

            await RequireSameAccountAsync(account.Value, cancellationToken).ConfigureAwait(false);
            // Only an answer in the same confirmed account can establish absence.
            return read.Value is { } details
                ? new ExistingShortcut(appId, details.ShortcutExe, details.ShortcutLaunchOptions, details.ShortcutStartDir,
                    details.Name ?? "")
                : null;
        }
        finally
        {
            _operations.Release();
        }
    }

    /// <summary>Creates one shortcut through the toolkit, which confirms which library entry it is.</summary>
    /// <remarks>
    ///     An add that was sent but not answered may have created the entry, so it is reported as such
    ///     and never repeated: the next scan finds the entry and offers to adopt it.
    /// </remarks>
    /// <param name="name">The shortcut's name.</param>
    /// <param name="fields">What the shortcut runs.</param>
    /// <param name="cancellationToken">Cancels the add before it is sent.</param>
    /// <returns>What Steam answered.</returns>
    private async Task<ShortcutWriteResult> AddShortcutAsync(
        string name, ShortcutFields fields, CancellationToken cancellationToken)
    {
        var added = await steam.Apps.AddShortcutAsync(
                name, fields.Target, fields.StartDirectory, fields.LaunchOptions, cancellationToken)
            .ConfigureAwait(false);
        return added.Outcome switch
        {
            SteamClientWriteOutcome.NotSent => new ShortcutWriteResult(
                0, false, "Steam is not reachable, so nothing was created.", CreationMayExist: false),
            SteamClientWriteOutcome.Unknown => new ShortcutWriteResult(
                0, false,
                "Steam did not answer, so the shortcut may not have been created. Scan again before retrying."),
            SteamClientWriteOutcome.Rejected => new ShortcutWriteResult(
                0, false, added.Error ?? "Steam refused to create the shortcut.", CreationMayExist: false),
            _ => new ShortcutWriteResult(added.AppId, added.Confirmed,
                added.Confirmed ? null : added.Error ?? "Steam did not confirm the new shortcut.", added.Mismatch)
        };
    }

    private async Task<uint> ReadAccountAsync(CancellationToken cancellationToken)
    {
        var read = await steam.Apps.ReadAccountIdAsync(cancellationToken).ConfigureAwait(false);
        if (!read.Succeeded || read.Value == 0)
        {
            importState.SetActiveAccount(null);
            throw new InvalidOperationException("Steam's signed-in account could not be confirmed. Shortcut changes remain pending.");
        }

        return read.Value;
    }

    private async Task RequireSameAccountAsync(uint expected, CancellationToken cancellationToken)
    {
        if (await ReadAccountAsync(cancellationToken).ConfigureAwait(false) != expected
            || importState.ActiveAccountId != expected)
        {
            importState.SetActiveAccount(null);
            throw new InvalidOperationException("The Steam account changed during the operation. Scan again before making changes.");
        }
    }
}
