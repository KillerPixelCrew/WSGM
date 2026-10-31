namespace WSGM.Core;

/// <summary>One person or project WSGM thanks, as the About pages show it.</summary>
/// <param name="Name">Who or what.</param>
/// <param name="Role">What they did for WSGM, one sentence.</param>
/// <param name="Url">Where to find them.</param>
public sealed record Credit(string Name, string Role, string Url);
