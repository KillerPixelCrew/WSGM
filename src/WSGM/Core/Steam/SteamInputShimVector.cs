namespace WSGM.Core;

/// <summary>Which file name the Steam Input shim is deployed under.</summary>
internal enum SteamInputShimVector
{
    /// <summary>Not deployed under any name.</summary>
    None,

    /// <summary>
    ///     Deployed as <c>XInput1_4.dll</c>. The primary vector: Steam's SDL
    ///     backend loads XInput by bare name, and it is the name ValvePlug has shipped
    ///     against a live client for years.
    /// </summary>
    XInput14,

    /// <summary>
    ///     Deployed as <c>dinput8.dll</c>. The fallback, used when something
    ///     else already owns the primary name.
    /// </summary>
    DInput8
}
