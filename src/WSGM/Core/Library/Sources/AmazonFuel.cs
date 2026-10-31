using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What a game's <c>fuel.json</c> says about starting it.</summary>
/// <param name="Command">The executable, relative to the install folder, or empty.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="WorkingSubdirectory">The working folder relative to the install folder, or empty.</param>
/// <param name="NeedsSignIn">Whether the game asks the Amazon Games app for a sign-in token.</param>
internal sealed record AmazonFuel(
    string Command,
    IReadOnlyList<string> Arguments,
    string WorkingSubdirectory,
    bool NeedsSignIn);
