namespace WSGM.Core;

/// <summary>Which profile layer supplied a value.</summary>
public enum ProfileSource
{
    /// <summary>Neither layer sets it. WSGM writes nothing and the device keeps what it has.</summary>
    None,

    /// <summary>The Global profile.</summary>
    Global,

    /// <summary>The running game's enabled profile.</summary>
    Game
}
