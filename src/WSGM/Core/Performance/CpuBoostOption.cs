namespace WSGM.Core;

/// <summary>One offered mode with the wording the user reads.</summary>
/// <param name="Mode">The mode this option selects.</param>
/// <param name="Name">Short label, HC's.</param>
/// <param name="Description">What it does for a game.</param>
internal sealed record CpuBoostOption(CpuBoostMode Mode, string Name, string Description);
