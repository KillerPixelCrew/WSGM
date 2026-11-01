using System.Collections.Generic;
using System.Linq;

namespace WSGM.Shell;

/// <summary>Operation results from one profile application; partial writes are not rolled back.</summary>
/// <param name="Operations">Outcomes for attempted settings, in application order; an empty list is successful.</param>
internal sealed record AudioProfileApplyResult(IReadOnlyList<AudioProfileOperationResult> Operations)
{
    /// <summary>Whether every attempted setting succeeded; true when no settings were requested.</summary>
    internal bool Succeeded => Operations.All(static operation => operation.Succeeded);
}
