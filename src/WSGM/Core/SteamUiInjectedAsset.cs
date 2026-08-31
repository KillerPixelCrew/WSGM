namespace WSGM.Core;

/// <summary>The script a host injects, and the hash that identifies this exact copy of it.</summary>
/// <param name="Source">The JavaScript to evaluate, containing the configuration placeholder the
/// bridge substitutes.</param>
/// <param name="Sha256">The hash of <paramref name="Source"/>, uppercase hex.</param>
/// <remarks>
/// Supplied by the host rather than read from a fixed location, because the bridge has no business
/// knowing what its consumer injects — it substitutes the configuration, evaluates, and verifies.
/// <para>
/// The hash is not decoration and not a checksum. It goes into the injected configuration, and the
/// script compares it against the one a previous bridge published: neither the execution-context
/// nor the document generation changes when only the host is updated, so without it a new build
/// kept running the previous build's script until Steam itself restarted. That cost a session
/// where a fix to the bootstrap appeared to have no effect and the only clue was a diagnostic
/// field missing from output the new code would have produced.
/// </para>
/// </remarks>
public readonly record struct SteamUiInjectedAsset(string Source, string Sha256);
