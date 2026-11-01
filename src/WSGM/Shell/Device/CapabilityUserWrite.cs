using WSGM.Core;

namespace WSGM.Shell;

/// <summary>What a capability value the user set does, by the descriptor's profile scope.</summary>
/// <param name="Command">Whether the value is written to the publisher now.</param>
/// <param name="Layer">The profile layer it is saved to.</param>
internal readonly record struct CapabilityUserWrite(bool Command, ProfileLayer Layer);
