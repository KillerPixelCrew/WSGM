namespace WSGM.Core;

/// <summary>Identifies the kind of per-game launch configuration WSGM owns.</summary>
public enum LaunchConfigurationKind
{
    /// <summary>The existing de-elevation or Steam Input wrapper.</summary>
    Wrapper,

    /// <summary>A Steam-native executable or script launch action.</summary>
    CustomAction
}
