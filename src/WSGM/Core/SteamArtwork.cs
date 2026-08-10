using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Outcome of an artwork change.</summary>
/// <param name="Ok">Whether the change was applied.</param>
/// <param name="Detail">A user-facing note (why it failed, or a follow-up such as
/// "restart Steam").</param>
public readonly record struct ArtworkResult(bool Ok, string Detail);

/// <summary>Applies and clears custom game artwork. Grid/Hero/Logo/Wide go through
/// Steam's own robust JS API over the CEF leg (<see cref="SteamCef"/>) —
/// <c>SteamClient.Apps.ClearCustomArtworkForApp</c> then
/// <c>SetCustomArtworkForApp(appid, base64, ext, assetType)</c> — so Steam persists and
/// renders them live with no restart. Icons are the exception: Steam has no client API
/// for them, so a real game's icon is written into <c>appcache\librarycache</c>
/// (a cache overwrite). Non-Steam shortcut icons need a <c>shortcuts.vdf</c> edit + a
/// Steam restart and are reported as not-yet-supported. The image bytes are fetched by
/// <see cref="SteamGridDb"/> and base64-encoded here.</summary>
public static class SteamArtwork
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    // Steam resolves ClearCustomArtworkForApp's promise before the clear finishes, so a
    // set issued immediately can race it (observed in decky-steamgriddb). Wait between.
    private const int ClearSettleMs = 500;

    /// <summary>Applies an image to an artwork slot.</summary>
    /// <param name="appId">The Steam app id (unsigned; a non-Steam shortcut id is
    /// accepted as its unsigned 32-bit form).</param>
    /// <param name="asset">Which slot.</param>
    /// <param name="imageBytes">The raw image bytes (from <see cref="SteamGridDb"/>).</param>
    /// <param name="ext">The image extension, <c>png</c> or <c>jpg</c>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<ArtworkResult> ApplyAsync(
        long appId, ArtworkAsset asset, byte[] imageBytes, string ext,
        CancellationToken cancellationToken = default)
    {
        if (imageBytes.Length == 0)
        {
            return new ArtworkResult(false, "The image was empty.");
        }
        if (asset == ArtworkAsset.Icon)
        {
            return ApplyIcon(appId, imageBytes, ext);
        }

        var b64 = await Task.Run(() => Convert.ToBase64String(imageBytes), cancellationToken)
            .ConfigureAwait(false);
        var extLiteral = SteamCef.JsString(ext is "jpg" or "jpeg" ? "jpg" : "png");
        var app = ToUnsigned(appId);
        var type = ((int)asset).ToString(CultureInfo.InvariantCulture);
        var expression =
            "(async()=>{try{const app=" + app + ",type=" + type + ";" +
            "await SteamClient.Apps.ClearCustomArtworkForApp(app,type);" +
            "await new Promise(r=>setTimeout(r," + ClearSettleMs + "));" +
            "await SteamClient.Apps.SetCustomArtworkForApp(app,\"" + b64 + "\"," + extLiteral + ",type);" +
            "return JSON.stringify({ok:true});}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

        var result = await SteamCef.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        return Interpret(result, "Artwork applied.");
    }

    /// <summary>Resets an artwork slot back to Steam's official art.</summary>
    /// <param name="appId">The Steam app id.</param>
    /// <param name="asset">Which slot.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<ArtworkResult> ClearAsync(
        long appId, ArtworkAsset asset, CancellationToken cancellationToken = default)
    {
        if (asset == ArtworkAsset.Icon)
        {
            return new ArtworkResult(false, "Icons can't be reset from here yet.");
        }
        var app = ToUnsigned(appId);
        var type = ((int)asset).ToString(CultureInfo.InvariantCulture);
        var expression =
            "(async()=>{try{await SteamClient.Apps.ClearCustomArtworkForApp(" + app + "," + type + ");" +
            "return JSON.stringify({ok:true});}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";
        var result = await SteamCef.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        return Interpret(result, "Reset to official art.");
    }

    private static ArtworkResult ApplyIcon(long appId, byte[] bytes, string ext)
    {
        _ = appId;
        _ = bytes;
        _ = ext;
        return new ArtworkResult(false,
            "Steam icons use a versioned per-app cache and cannot be changed safely here yet.");
    }

    // appStore uses the unsigned 32-bit app id; a shortcut id stored in a signed int
    // reads back negative, so normalize to the unsigned value the client expects.
    private static string ToUnsigned(long appId)
        => (appId < 0 ? (uint)appId : appId).ToString(CultureInfo.InvariantCulture);

    private static ArtworkResult Interpret(CefEvalResult result, string okMessage)
    {
        if (!result.Reachable)
        {
            return new ArtworkResult(false, "Steam isn't reachable — is it running?");
        }
        if (result.Value is null)
        {
            return new ArtworkResult(false, "No response from Steam.");
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                return new ArtworkResult(true, okMessage);
            }
            var err = root.TryGetProperty("err", out var e) ? e.GetString() : "unknown error";
            Log.Warn($"Artwork change failed: {err}.");
            return new ArtworkResult(false, err ?? "Steam rejected the change.");
        }
        catch (Exception ex)
        {
            return new ArtworkResult(false, ex.Message);
        }
    }
}
