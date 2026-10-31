namespace WSGM.Core;

/// <summary>Which artwork a title starts on before the user picks anything.</summary>
public enum ArtworkPreference
{
    /// <summary>The source's own catalog first, such as the Microsoft Store for Xbox titles.</summary>
    Catalog,

    /// <summary>SteamGridDB and the other artwork providers first.</summary>
    Providers
}
