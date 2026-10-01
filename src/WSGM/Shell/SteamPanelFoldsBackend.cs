using System;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Which sections of Steam's Quick Access tabs are open: the Performance and Quick Settings
///     groups, the Extensions tab's items and each theme's patches under its switch. A section
///     starts folded; the store remembers the ones the user opened, under the ids the injected side
///     names them by, so a fold outlives Steam rebuilding a tab.
/// </summary>
internal sealed class SteamPanelFoldsBackend : ISteamPanelFoldsBackend
{
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
        var error = _folds.SetOpen(id, !folded);
        if (error is not null)
        {
            return Task.FromResult(new SteamUiCommandResult(false, $"The fold could not be kept: {error}"));
        }

        Changed?.Invoke();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>Raised when a fold changed, so the list is published with it.</summary>
    internal event Action? Changed;

    /// <summary>The open sections.</summary>
    internal SteamPanelFoldsState ReadState()
    {
        return new SteamPanelFoldsState(_folds.Open);
    }
}
