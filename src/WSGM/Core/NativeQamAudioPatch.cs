using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
/// Supplies the audio backend Steam's own Quick Settings expects, which Windows does not provide.
/// </summary>
/// <remarks>
/// Not a component patch. Nothing is rendered and no React tree is touched: the store's availability
/// flag is literally <c>null != SteamClient.System.Audio</c>, so defining that namespace is the whole
/// gate. That is why this carries its own resource key rather than sharing the performance root's —
/// it has no interaction with the panel the component patches insert rows into, and a failure in one
/// must not disable the other.
/// <para>
/// Live-verified 2026-08-30: a store constructed against this namespace reports available, builds
/// its device entries, resolves the active output and input, and reads a dual-direction headset
/// correctly. Removal leaves the client exactly as found.
/// </para>
/// </remarks>
public sealed class NativeQamAudioPatch : ISteamUiPatch
{
    private const string BridgeNamespace = "__wsgmSteamUi_v1_28d7c54a";

    /// <inheritdoc />
    public string Id => "wsgm.native-qam.audio";

    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public string ResourceKey => "wsgm.native-qam.audio-namespace";

    /// <inheritdoc />
    public SteamUiPatchBounds Bounds { get; } = SteamUiPatchBounds.Default;

    /// <summary>
    /// Confirms the client still has the shape this patch supplies a backend for.
    /// </summary>
    /// <param name="context">The evaluation context.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>Whether the audio surface can be supplied.</returns>
    /// <remarks>
    /// Three conditions, and the third is the one that is easy to omit. The store must exist and
    /// must derive availability from the namespace; the namespace must be absent, because a client
    /// that grows a real backend must keep it; and the store singleton must be reachable, because
    /// it caches availability at construction and has to be written to directly on a client that is
    /// already running.
    /// </remarks>
    public async Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        SteamUiEvaluationResult result = await context.EvaluateAsync(
            TargetRole,
            ProbeExpression,
            cancellationToken).ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return new SteamUiPatchProbeResult(
                false,
                false,
                false,
                null,
                result.Error ?? "SharedJSContext is unavailable.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Value);
            JsonElement root = document.RootElement;
            bool storeDerivesAvailability =
                root.TryGetProperty("audioStore", out JsonElement store)
                && store.TryGetInt32(out int stores)
                && stores == 1;
            bool namespaceAbsent =
                root.TryGetProperty("audioNamespaceAbsent", out JsonElement absent)
                && absent.ValueKind is JsonValueKind.True;
            bool singletonReachable =
                root.TryGetProperty("storeSingletonReachable", out JsonElement singleton)
                && singleton.ValueKind is JsonValueKind.True;

            bool compatible = storeDerivesAvailability && namespaceAbsent && singletonReachable;
            return new SteamUiPatchProbeResult(
                true,
                compatible,
                compatible,
                compatible ? "native-qam-audio-v1:store+absent-namespace+reachable-singleton" : null,
                compatible ? null : result.Value);
        }
        catch (JsonException ex)
        {
            return new SteamUiPatchProbeResult(true, false, false, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "return JSON.stringify(bridge.audio.install());",
            "Audio namespace installation failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "const status=bridge.audio.status();"
            + "return JSON.stringify({ok:status.installed&&status.namespacePresent,status});",
            "Audio namespace verification failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "const removed=bridge.audio.remove();const status=bridge.audio.status();"
            + "return JSON.stringify({ok:removed.ok&&!status.namespacePresent});",
            "Audio namespace removal failed.",
            cancellationToken);

    private static string ProbeExpression => """
        (()=>{try{
          let req;
          window.webpackChunksteamui.push([["wsgm_native_audio_probe_"+Date.now()],{},r=>req=r]);
          if(!req||!req.m)return JSON.stringify({error:'webpack unavailable'});
          const count=(tokens)=>Object.values(req.m).reduce((total,factory)=>{
            const source=String(factory);
            return total+(tokens.every(token=>source.includes(token))?1:0);
          },0);
          let singleton=false;
          try{const mod=req('1409');singleton=!!(mod&&mod.F5&&('m_bAvailable' in mod.F5));}catch{}
          return JSON.stringify({
            audioStore:count(['SteamClient.System.Audio','RegisterForDeviceAdded','m_bAvailable']),
            audioNamespaceAbsent:(()=>{const a=window.SteamClient&&window.SteamClient.System&&window.SteamClient.System.Audio;
              // Absent, or present and OURS. A namespace WSGM installed is not evidence of a native
              // backend, and treating it as one made this patch declare itself incompatible five
              // seconds after a successful install, tear down, and orphan the namespace it had just
              // defined — leaving Steam's audio page empty until Steam itself restarted.
              return !a||a.__wsgmOwnedNamespace===true;})(),
            storeSingletonReachable:singleton
          });
        }catch(error){return JSON.stringify({error:String(error)}); } })()
        """;

    private static Task<SteamUiPatchOperationResult> EvaluateAsync(
        SteamUiPatchContext context,
        string body,
        string fallback,
        CancellationToken cancellationToken)
    {
        string expression = "(()=>{const bridge=window["
            + SteamCef.JsString(BridgeNamespace)
            + "];if(!bridge||!bridge.audio)return JSON.stringify({ok:false,error:'bridge unavailable'});"
            + body
            + "})()";
        return SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            expression,
            fallback,
            cancellationToken);
    }
}
