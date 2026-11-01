using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>
///     Resolves the concrete app-id set for a <see cref="FilterKind.SdCard" /> node
///     from WSGM's own card model (Steam does not know our card→game mapping, so it is
///     baked into the compiled JS as a literal set).
/// </summary>
public interface ISdCardResolver
{
    /// <summary>
    ///     App ids on the card(s) selected by <paramref name="scope" /> /
    ///     <paramref name="contentId" />. Empty when no such card is known.
    /// </summary>
    /// <param name="scope">Inserted, all known, or one explicitly selected card.</param>
    /// <param name="contentId">Stable card identity used only for Specific scope.</param>
    /// <returns>The matching app IDs, or an empty collection when no selected card is known.</returns>
    IReadOnlyCollection<long> Resolve(SdCardScope scope, string contentId);
}
