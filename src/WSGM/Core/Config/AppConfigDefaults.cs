namespace WSGM.Core;

/// <summary>Shared normalization templates; never mutate or expose these instances to callers.</summary>
internal static class AppConfigDefaults
{
    // The single source of the normalization bounds: AppearancePage.axaml mirrors
    // them as literal NumericUpDown Minimum/Maximum and TextBox MaxLength values,
    // and normalization applies the same limits to config load and theme import,
    // so the renderer sees the same bounded values regardless of their source.
    /// <summary>Smallest splash font size the editor and normalization accept.</summary>
    internal const int MinFontSize = 1;

    /// <summary>Largest splash title font size.</summary>
    internal const int MaxTitleFontSize = 400;

    /// <summary>Largest splash caption font size.</summary>
    internal const int MaxCaptionFontSize = 200;

    /// <summary>Smallest spinner size in logical pixels.</summary>
    internal const int MinSpinnerSize = 1;

    /// <summary>Largest spinner size in logical pixels.</summary>
    internal const int MaxSpinnerSize = 1024;

    /// <summary>Smallest logo maximum-edge length.</summary>
    internal const int MinLogoMaxSize = 1;

    /// <summary>Largest logo maximum-edge length.</summary>
    internal const int MaxLogoMaxSize = 4096;

    /// <summary>Smallest anchored-edge padding.</summary>
    internal const int MinPadding = 0;

    /// <summary>Largest anchored-edge padding.</summary>
    internal const int MaxPadding = 4096;

    /// <summary>
    ///     Smallest absolute placement coordinate (an element placed off the
    ///     top-left is unreachable, not a feature).
    /// </summary>
    internal const int MinAbsoluteCoordinate = 0;

    /// <summary>Largest absolute placement coordinate in logical pixels.</summary>
    internal const int MaxAbsoluteCoordinate = 16384;


    // The single source for every persisted default is the config classes' own
    // property initializers; these read-only templates hand them to the JSON
    // repair pass (unknown enum NAME) and to Normalize (unknown enum NUMBER, null
    // string) alike, so the two passes cannot drift apart. Never mutate them and
    // never hand them to a caller.
    /// <summary>Default field values for repair; clone values that would otherwise expose mutable state.</summary>
    internal static readonly AppConfig Defaults = new();

    /// <summary>What one missing or unreadable splash FIELD is repaired to.</summary>
    /// <remarks>
    ///     Deliberately the <see cref="SplashConfig" /> field defaults rather than
    ///     <c>Defaults.Splash</c>, which carries the preset a fresh install is seeded with. Repairing a
    ///     single null field to the shipped preset would splice one preset's value into another's
    ///     splash — a user on Classic with one unreadable field would get part of WSGM 2.0's look.
    ///     Seeding a whole new configuration and repairing one field of an existing one are different
    ///     questions, and only the first is about what WSGM ships with.
    /// </remarks>
    internal static readonly SplashConfig SplashFieldDefaults = new();

    /// <summary>Read-only fallback values for one splash element placement.</summary>
    internal static readonly SplashElementPlacement PlacementDefaults = new();

    // Spelled out rather than left to the property initializers: a repaired filter falls back to
    // the neutral filter a user would recognise ("installed", ANDed, inserted cards), which is not
    // the same as enum member zero. Both repair passes read this one instance, so they cannot
    // disagree about what an unreadable value becomes.
    /// <summary>Read-only neutral installed-game filter used by both JSON and object repair.</summary>
    internal static readonly FilterNode FilterDefaults = new()
    {
        Kind = FilterKind.Installed,
        Mode = FilterMode.And,
        Condition = ThresholdCondition.Above,
        Platform = PlatformKind.Steam,
        ScoreType = ReviewScoreType.SteamPercent,
        Units = TimeUnit.Hours,
        CardScope = SdCardScope.Inserted
    };
}
