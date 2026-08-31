using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
/// Supplies the performance backend Steam's own Performance tab was written against, which the
/// Windows client does not have.
/// </summary>
/// <remarks>
/// <c>SystemPerfStore</c>'s constructor optional-chains through <c>SteamClient.System.Perf</c>, so
/// on Windows the registration no-ops, its state stays empty, and every control renders null. The
/// whole integration is that one named seam: define the namespace, write the state, and Valve's
/// components come back. This is not a SteamOS or Deck spoof — no platform constant is touched and
/// no unrelated gate is opened, and <c>force_deck_perf_tab</c> is never set because it is a
/// persisted client setting that would force-show rows WSGM cannot back (D16).
/// <para>
/// Its own resource key, separate from the component patches that mount rows into the panel. This
/// supplies data; they render. A failure to mount one component must not tear down the backend the
/// others are reading, and a backend failure must not look like a broken row.
/// </para>
/// </remarks>
public sealed class NativeQamPerfPatch : ISteamUiPatch
{
    private const string BridgeNamespace = SteamUiBridgeIdentity.Namespace;

    /// <inheritdoc />
    public string Id => "wsgm.native-qam.perf";

    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public string ResourceKey => "wsgm.native-qam.perf-namespace";

    /// <inheritdoc />
    public SteamUiPatchBounds Bounds { get; } = SteamUiPatchBounds.Default;

    /// <summary>
    /// Confirms the client still has the shape this patch supplies a backend for.
    /// </summary>
    /// <param name="context">The evaluation context.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>Whether the performance backend can be supplied.</returns>
    /// <remarks>
    /// Three conditions. The store must exist and must still derive its state from the namespace;
    /// the namespace must be absent, because a client that grows a real backend keeps it and WSGM
    /// must not shadow it; and the store singleton must be reachable with its state message, since
    /// the state is written into a client that is already running rather than delivered to a
    /// constructor.
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
            bool storeDerivesState =
                root.TryGetProperty("perfStore", out JsonElement store)
                && store.TryGetInt32(out int stores)
                && stores == 1;
            bool namespaceAbsent =
                root.TryGetProperty("perfNamespaceAbsent", out JsonElement absent)
                && absent.ValueKind is JsonValueKind.True;
            bool singletonReachable =
                root.TryGetProperty("storeSingletonReachable", out JsonElement singleton)
                && singleton.ValueKind is JsonValueKind.True;

            bool compatible = storeDerivesState && namespaceAbsent && singletonReachable;
            return new SteamUiPatchProbeResult(
                true,
                compatible,
                compatible,
                compatible ? "native-qam-perf-v1:store+absent-namespace+reachable-singleton" : null,
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
            "return JSON.stringify(bridge.perf.install());",
            "Performance namespace installation failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "const status=bridge.perf.status();"
            + "return JSON.stringify({ok:status.installed&&status.namespacePresent,status});",
            "Performance namespace verification failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "const removed=bridge.perf.remove();const status=bridge.perf.status();"
            + "return JSON.stringify({ok:removed.ok&&!status.namespacePresent});",
            "Performance namespace removal failed.",
            cancellationToken);

    /// <remarks>
    /// The store is counted by the source tokens that make it the perf store, never by module id,
    /// so a rebuild that renumbers modules does not silently bind the wrong one. The singleton is
    /// reached through the one export that exposes a <c>Get()</c> returning a state-carrying store,
    /// rather than by a minified export name that changes between builds.
    /// </remarks>
    private static string ProbeExpression => """
        (()=>{try{
          let req;
          window.webpackChunksteamui.push([["wsgm_native_perf_probe_"+Date.now()],{},r=>req=r]);
          if(!req||!req.m)return JSON.stringify({error:'webpack unavailable'});
          const count=(tokens)=>Object.values(req.m).reduce((total,factory)=>{
            const source=String(factory);
            return total+(tokens.every(token=>source.includes(token))?1:0);
          },0);
          let singleton=false;
          try{
            const mod=req('74514');
            const holder=mod&&Object.values(mod).find(v=>v&&typeof v.Get==='function');
            const store=holder?holder.Get():null;
            singleton=!!(store&&'m_msgState' in store);
          }catch{}
          return JSON.stringify({
            perfStore:count(['SteamClient.System.Perf','RegisterForStateChanges','m_msgState']),
            perfNamespaceAbsent:(()=>{const p=window.SteamClient&&window.SteamClient.System&&window.SteamClient.System.Perf;
              // Absent, or present and ours — see NativeQamAudioPatch. An orphaned Perf namespace
              // is the worse case: it leaves SystemPerfStore holding half-written state, which is
              // what crashed the whole Performance tab.
              return !p||p.__wsgmOwnedNamespace===true;})(),
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
            + "];if(!bridge||!bridge.perf)return JSON.stringify({ok:false,error:'bridge unavailable'});"
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
