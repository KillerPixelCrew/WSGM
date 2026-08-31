namespace WSGM.Core;

/// <summary>
/// The names the injected side is reachable by: the window property carrying the bridge, and the
/// CDP binding it answers through.
/// </summary>
/// <remarks>
/// One source of truth, because these are agreed across a boundary that cannot check itself. The
/// host embeds them in the injected configuration, the injected script defines the window property
/// under them, and nine separate patches evaluate expressions naming them from the C# side. Every
/// one of those was a separate copy of the same literal, and a copy that drifted would not fail to
/// compile — it would produce a patch whose probe silently never finds the bridge.
/// <para>
/// The suffixes are not decoration. They make the property name unlikely to collide with Steam's own
/// globals or with another tool sharing SharedJSContext, which is the same reason WSGM's injected
/// nodes carry their own marker class and never touch CSSLoader's.
/// </para>
/// <para>
/// Held as constants rather than injected because WSGM is currently the only host. When the toolkit
/// is extracted these become a value the host supplies once — the shape is already right for that,
/// which is why callers reference this type rather than a literal.
/// </para>
/// </remarks>
public static class SteamUiBridgeIdentity
{
    /// <summary>The window property the injected bridge is published under.</summary>
    public const string Namespace = "__wsgmSteamUi_v1_28d7c54a";

    /// <summary>The CDP binding name the injected side sends envelopes through.</summary>
    public const string BindingName = "__wsgmNativeBridge_v1_7b24d11c";
}
