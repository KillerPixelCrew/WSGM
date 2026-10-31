namespace WSGM.Core;

/// <summary>What a <c>goggame-&lt;id&gt;.info</c> file says about launching its game.</summary>
/// <param name="RootGameId">The game this one belongs to, or empty.</param>
/// <param name="HasPrimaryTask">Whether any play task is marked primary. None means DLC.</param>
/// <param name="Primary">The first primary task that runs a file, or null.</param>
internal sealed record GogGameInfo(string RootGameId, bool HasPrimaryTask, GogPlayTask? Primary);
