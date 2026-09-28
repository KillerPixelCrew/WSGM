using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Which sections of Steam's Performance and Quick Settings tabs are open, kept in the same file
///     as the Extensions tab's so every Quick Access fold outlives Steam rebuilding a tab. A section
///     starts folded; the store remembers the ones the user opened.
/// </summary>
internal sealed class SteamPanelFoldsBackend : ISteamPanelFoldsBackend
{
    /// <summary>The prefix that keeps a section title apart from an Extensions tab id.</summary>
    private const string Prefix = "panel:";

    private readonly QuickAccessFolds _folds;

    /// <summary>Creates the backend over one fold store.</summary>
    /// <param name="folds">Where the folds are kept.</param>
    internal SteamPanelFoldsBackend(QuickAccessFolds folds)
    {
        _folds = folds;
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetFoldedAsync(string id, bool folded, CancellationToken cancellationToken)
    {
        var error = _folds.SetOpen(Prefix + id, !folded);
        if (error is not null)
        {
            return Task.FromResult(new SteamUiCommandResult(false, $"The fold could not be kept: {error}"));
        }

        Changed?.Invoke();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>Raised when a fold changed, so the list is published with it.</summary>
    internal event Action? Changed;

    /// <summary>The open section titles.</summary>
    internal SteamPanelFoldsState ReadState()
    {
        return new SteamPanelFoldsState(
        [
            .. _folds.Open
                .Where(id => id.StartsWith(Prefix, StringComparison.Ordinal))
                .Select(id => id[Prefix.Length..])
        ]);
    }
}
