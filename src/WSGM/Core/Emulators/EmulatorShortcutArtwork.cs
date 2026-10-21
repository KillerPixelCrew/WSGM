using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Finds optional emulator shortcut art through the existing paced SteamGridDB provider.</summary>
internal static class EmulatorShortcutArtwork
{
    private static readonly SteamGridDbProvider Provider = new();

    internal static async Task<IReadOnlyList<(ArtworkAsset Asset, string Url)>> FindAsync(
        EmulatorDefinition definition, ArtworkConfig config, CancellationToken token)
    {
        var key = SteamGridDbProvider.ResolveKey(config);
        if (key.Length == 0)
        {
            return [];
        }

        // The shared provider owns request pacing, filtering and HTTPS validation. Image bytes
        // still go through SteamArtwork/ArtworkDownload at application time.
        var provider = Provider;
        var id = definition.SteamGridDbId;
        if (id is not > 0)
        {
            var name = definition.Id switch
            {
                "retroarch" => "RetroArch", "xenia" => "Xenia", "ryujinx" => "Ryujinx",
                "model2" => "Model 2 Emulator", _ => definition.Name
            };
            var matches = await provider.SearchGamesAsync(name, key, token).ConfigureAwait(false);
            var match = matches.FirstOrDefault(game => game.Name.Equals(name + " (Emulator)",
                            StringComparison.OrdinalIgnoreCase))
                        ?? matches.FirstOrDefault(game => game.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return [];
            }

            id = match.Id;
        }

        List<(ArtworkAsset Asset, string Url)> result = [];
        foreach (var slot in new[]
                 {
                     ArtworkAsset.Grid, ArtworkAsset.Wide, ArtworkAsset.Hero, ArtworkAsset.Logo, ArtworkAsset.Icon
                 })
        {
            var page = await provider.GetAssetsForGameAsync(slot, id.Value, key,
                new ArtworkQuery(Animated: false, Adult: false, Humor: false, Epilepsy: false), token)
                .ConfigureAwait(false);
            var candidate = page.Candidates.FirstOrDefault(item => !item.Animated && !item.Nsfw && !item.Humor
                && !item.Epilepsy && JsonRead.HttpsUrl(item.Url).Length > 0);
            if (candidate is not null)
            {
                result.Add((slot, candidate.Url));
            }
        }

        return result;
    }
}
