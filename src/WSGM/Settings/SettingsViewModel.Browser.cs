using System;
using System.Collections.Generic;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    /// <summary>The two memory policies, in their persisted enumeration order.</summary>
    public IReadOnlyList<string> BrowserMemoryBehaviors { get; } = ["Unload when closed", "Keep alive"];

    /// <summary>The user's browser memory preference; Save applies it to a retained page at runtime.</summary>
    public int BrowserMemoryBehaviorIndex
    {
        get;
        set => SetFieldIfChanged(ref field, Math.Clamp(value, 0, 1), nameof(BrowserMemoryBehaviorIndex));
    }
}
