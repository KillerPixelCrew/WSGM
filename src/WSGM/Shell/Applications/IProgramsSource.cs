using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Programs projection/actions, also supplied explicitly by inert headless preview fixtures.</summary>
internal interface IProgramsSource : IChangeSource
{
    string LastResult { get; }
    ProgramState[] ReadState();
    bool IsPinned(string id);
    Task SaveAsync(ProgramDefinition definition, bool approveExecutable);
    Task RemoveAsync(string id);
    Task RememberWindowAsync(string id, ProgramUiCandidate candidate);
    Task ForgetUiAsync(string id);
    Task ClearPendingAsync(string id);
    Task<string> ActivateAsync(string id, CancellationToken cancellationToken);

    /// <summary>Reads the applications the Add program list offers, off the UI thread.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The applications, or why they could not be read.</returns>
    Task<InstalledProgramList> ReadInstalledAsync(CancellationToken cancellationToken);

    /// <summary>Reads the icons of offered applications, off the UI thread.</summary>
    /// <param name="keys">The applications' keys.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Each key's bitmap, owned by the caller, or null where there is none.</returns>
    Task<IReadOnlyDictionary<string, Bitmap?>> ReadInstalledIconsAsync(IReadOnlyList<string> keys,
        CancellationToken cancellationToken);
    void ReportResult(string message);

    /// <summary>Whether the installed Xbox app needs updating for Game Mode, and how an update is going.</summary>
    XboxAppUpdateStatus XboxAppUpdate => XboxAppUpdateStatus.None;

    /// <summary>Starts updating the Xbox app; the session owns the operation, not the view.</summary>
    void StartXboxAppUpdate()
    {
    }

    /// <summary>Stops a running Xbox app download.</summary>
    void CancelXboxAppUpdate()
    {
    }
}
