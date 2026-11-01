using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The managed-controller target in effect and where it came from.</summary>
/// <param name="Target">Effective virtual target type, including the built-in default when neither profile layer sets one.</param>
/// <param name="Source">Layer supplying the value; None means the built-in default was used.</param>
/// <param name="ApplicationId">Supplied running identity when the Game layer wins; null for Global or the default.</param>
internal sealed record ResolvedControllerTarget(
    ManagedControllerTarget Target,
    ProfileSource Source,
    string? ApplicationId);
