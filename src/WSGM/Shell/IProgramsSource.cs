using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Programs projection/actions, also supplied explicitly by inert headless preview fixtures.</summary>
internal interface IProgramsSource : IChangeSource
{
    ProgramState[] ReadState();
    bool IsPinned(string id);
    string LastResult { get; }
    Task SaveAsync(ProgramDefinition definition, bool approveExecutable);
    Task RemoveAsync(string id);
    Task RememberWindowAsync(string id, ProgramUiCandidate candidate);
    Task ForgetUiAsync(string id);
    Task ClearPendingAsync(string id);
    Task<string> ActivateAsync(string id, CancellationToken cancellationToken);
    void ReportResult(string message);
}
