using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
/// Supplies the SteamOS Manager RPC answer Valve's own TDP row reads its availability and range
/// from, which the Windows client's stub never fills in.
/// </summary>
/// <remarks>
/// The third gate kind in the taxonomy, and the only one WSGM uses: not an absent JS namespace and
/// not a store getter, but an <em>RPC response</em>. The Windows client ships the SteamOS Manager
/// service and its React hooks; the service simply answers with a state whose
/// <c>is_tdp_limit_available</c> is false, so the row is hidden. Overlaying that one answer with
/// WSGM's real power-limit range turns the row on, with Valve's slider, its bookend labels, its
/// localized explainer and its per-game profile behaviour.
/// <para>
/// The original response is kept and merged into, never replaced. It carries fields WSGM knows
/// nothing about — screen-reader support among them — and a fabricated reply would silently zero
/// every one of them.
/// </para>
/// <para>
/// Writes come back the other way round from every other control here. Valve's slider does not call
/// a namespace: it stores the chosen watts in the <c>steamos_tdp_limit</c> client setting and lets
/// Steam persist it. WSGM therefore watches Steam's own settings-change registration and routes the
/// number to hardware through the same command the hand-rolled row used, which is why this patch
/// shares the <c>wsgm.native-qam.tdp</c> id and its published state rather than adding a second one.
/// </para>
/// </remarks>
public sealed class NativeQamSteamOsManagerPatch : ISteamUiPatch
{
    private const string BridgeNamespace = "__wsgmSteamUi_v1_28d7c54a";

    /// <inheritdoc />
    public string Id => "wsgm.native-qam.tdp";

    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    /// <remarks>
    /// Its own key rather than the performance root's. Nothing is rendered and no React tree is
    /// touched, so a component patch failing must not disable this and this failing must not take
    /// the panel's rows down with it.
    /// </remarks>
    public string ResourceKey => "wsgm.native-qam.steamos-manager-state";

    /// <inheritdoc />
    public SteamUiPatchBounds Bounds { get; } = SteamUiPatchBounds.Default;

    /// <summary>Confirms the client still has the service, the row, and the query layer.</summary>
    /// <param name="context">The evaluation context.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>Whether the manager state can be supplied.</returns>
    /// <remarks>
    /// Four conditions. The service must be findable by its own surface rather than by a minified
    /// export name; the TDP row must still read the fields being supplied, or supplying them
    /// achieves nothing; the query layer must be reachable, because the row's answer is cached and
    /// a state change that cannot invalidate it never reaches the screen; and <c>GetState</c> must
    /// not already be WSGM's own overlay, because wrapping a wrapper makes removal restore the
    /// wrapper instead of Valve's method.
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
            bool managerFound = Flag(root, "managerFound");
            bool rowReadsState = root.TryGetProperty("tdpRow", out JsonElement row)
                && row.TryGetInt32(out int rows)
                && rows > 0;
            bool queryLayer = Flag(root, "queryLayer");
            bool replaceable = Flag(root, "getStateReplaceable");

            bool compatible = managerFound && rowReadsState && queryLayer && replaceable;
            return new SteamUiPatchProbeResult(
                true,
                compatible,
                compatible,
                compatible
                    ? "native-qam-steamos-manager-v1:service+tdp-row+query-layer+own-getstate"
                    : null,
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
            "return JSON.stringify(bridge.steamOsManager.install());",
            "SteamOS Manager state installation failed.",
            cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Verified from the client rather than from the gate's own bookkeeping: the overlay has to be
    /// the method actually on the service. The settings watch is reported but not required — losing
    /// it costs the write path, not the row, and the status says which.
    /// </remarks>
    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "const status=bridge.steamOsManager.status();"
            + "return JSON.stringify({ok:status.installed&&status.getStateOverlaid,status});",
            "SteamOS Manager state verification failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "const removed=bridge.steamOsManager.remove();"
            + "const status=bridge.steamOsManager.status();"
            + "return JSON.stringify({ok:removed.ok&&!status.getStateOverlaid});",
            "SteamOS Manager state removal failed.",
            cancellationToken);

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True;

    /// <remarks>
    /// The service is matched by surface, not by export name: module 90389 exports both the Manager
    /// and a Telemetry service and both have <c>GetState</c>, so the screen-reader method is what
    /// separates them. Everything else is counted the way the component probes count, over factory
    /// sources rather than by touching modules that have not been loaded.
    /// </remarks>
    private static string ProbeExpression => """
        (()=>{try{
          let req;
          window.webpackChunksteamui.push([["wsgm_steamos_manager_probe_"+Date.now()],{},r=>req=r]);
          if(!req||!req.m)return JSON.stringify({error:'webpack unavailable'});
          const count=(tokens)=>Object.values(req.m).reduce((total,factory)=>{
            const source=String(factory);
            return total+(tokens.every(token=>source.includes(token))?1:0);
          },0);
          let manager=null;
          try{
            for(const value of Object.values(req('90389')||{})){
              if(value&&typeof value==='object'
                &&typeof value.GetState==='function'
                &&typeof value.RefreshScreenReaderAutoLocale==='function'){manager=value;break;}
            }
          }catch{}
          let queryLayer=false;
          try{const q=req('21371');queryLayer=typeof q?.L?.invalidateQueries==='function';}catch{}
          return JSON.stringify({
            managerFound:!!manager,
            // Valve's own method, or one of WSGM's overlays that still carries it. Requiring the
            // PRE-patch shape here is the self-incompatibility trap this project has already paid
            // for twice: a successful apply would invalidate its own probe, and the next
            // compatibility pass would tear down what it had just installed.
            getStateReplaceable:!!manager&&(typeof manager.GetState==='function')
              &&(manager.GetState.__wsgmOwnedGetState!==true
                ||typeof manager.GetState.__wsgmOriginalGetState==='function'),
            queryLayer,
            tdpRow:count(['is_tdp_limit_available','tdp_limit_min','tdp_limit_max'])
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
            + "];if(!bridge||!bridge.steamOsManager)"
            + "return JSON.stringify({ok:false,error:'bridge unavailable'});"
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
