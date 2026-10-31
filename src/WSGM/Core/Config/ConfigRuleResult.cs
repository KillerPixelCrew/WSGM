using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Returns a normalized value with the repairs that should be reported to the user or log.</summary>
/// <typeparam name="T">Configuration value type.</typeparam>
/// <param name="Value">Normalized value; may be the same mutable instance supplied to the rule.</param>
/// <param name="Diagnostics">Human-readable repair descriptions; empty when no reported repair was needed.</param>
internal sealed record ConfigRuleResult<T>(T Value, IReadOnlyList<string> Diagnostics);
