using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Outcome of evaluating a compiled filter over the library.</summary>
/// <param name="Reachable">Whether Steam's debug port answered.</param>
/// <param name="Ok">Whether Steam evaluated and returned a valid result.</param>
/// <param name="AppIds">The matching app ids (empty is a valid successful result).</param>
public readonly record struct FilterEvalResult(bool Reachable, bool Ok, IReadOnlyList<long> AppIds);
