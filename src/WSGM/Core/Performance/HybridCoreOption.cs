namespace WSGM.Core;

/// <summary>One offered mode with the wording the user reads.</summary>
/// <param name="Mode">The mode this option selects.</param>
/// <param name="Name">Short label.</param>
/// <param name="Description">What it does, in terms of the machine rather than the scheduler.</param>
internal sealed record HybridCoreOption(HybridCoreMode Mode, string Name, string Description);
