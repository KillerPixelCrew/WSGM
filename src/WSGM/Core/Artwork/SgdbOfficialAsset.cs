namespace WSGM.Core;

/// <summary>One official Steam store asset described by SteamGridDB platform metadata.</summary>
/// <param name="Label">Display label for the official Steam slot.</param>
/// <param name="Url">Official asset URL.</param>
/// <param name="Width">Advertised pixel width.</param>
/// <param name="Height">Advertised pixel height.</param>
/// <param name="Extension">Expected filename extension; downloaded bytes still require validation.</param>
internal sealed record SgdbOfficialAsset(string Label, string Url, int Width, int Height, string Extension);
