using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Owns the download-queue JSX wrapper through the shared patch lifecycle.</summary>
internal sealed class SteamDownloadSortPatch : ISteamUiPatch
{
    public string Id => "wsgm.download-sort";

    public int Version => 1;

    public SteamUiTargetRole TargetRole => SteamUiTargetRole.MainWindow;

    public string ResourceKey => "steam.downloads.jsx-runtime";

    public SteamUiPatchBounds Bounds { get; } = SteamUiPatchBounds.Default;

    public async Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        SteamUiEvaluationResult result = await context.EvaluateAsync(
            TargetRole,
            "(()=>{try{const W=window.__wsgm;return JSON.stringify({ok:true,"
                + "runtime:!!window.webpackChunksteamui,"
                + "owned:!!(W&&Array.isArray(W.dlSortPatched)&&W.dlSortPatched.length)});"
                + "}catch(e){return JSON.stringify({ok:false,error:String(e)});}})()",
            cancellationToken).ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return new SteamUiPatchProbeResult(
                false,
                false,
                false,
                null,
                result.Error ?? "MainWindow is unavailable.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Value);
            JsonElement root = document.RootElement;
            bool compatible = root.TryGetProperty("ok", out JsonElement ok)
                && ok.ValueKind == JsonValueKind.True
                && root.TryGetProperty("runtime", out JsonElement runtime)
                && runtime.ValueKind == JsonValueKind.True;
            return new SteamUiPatchProbeResult(
                true,
                compatible,
                compatible,
                compatible ? "download-sort-v1:jsx-runtime+focusable+queue-header" : null,
                compatible ? null : result.Value);
        }
        catch (JsonException ex)
        {
            return new SteamUiPatchProbeResult(true, false, false, null, ex.Message);
        }
    }

    public Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) => EvaluateAsync(
            context,
            SteamDownloadSort.InstallExpression,
            "Download queue sort installation failed.",
            cancellationToken);

    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) => EvaluateAsync(
            context,
            "(()=>{const W=window.__wsgm;return JSON.stringify({ok:!!(W"
                + "&&Array.isArray(W.dlSortPatched)&&W.dlSortPatched.length)});})()",
            "Download queue sort verification failed.",
            cancellationToken);

    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) => EvaluateAsync(
            context,
            SteamDownloadSort.RemoveExpression,
            "Download queue sort removal failed.",
            cancellationToken);

    private static Task<SteamUiPatchOperationResult> EvaluateAsync(
        SteamUiPatchContext context,
        string expression,
        string fallback,
        CancellationToken cancellationToken) => SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.MainWindow,
            expression,
            fallback,
            cancellationToken);
}
