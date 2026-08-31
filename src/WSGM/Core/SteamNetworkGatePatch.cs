using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
/// Reveals Steam's own Wi-Fi surface, which Windows hides behind a single Deck-only getter.
/// </summary>
/// <remarks>
/// Steam's Windows client genuinely tracks the wireless device — the store reports a real adapter
/// and its enabled state without any help — and only
/// <c>get networkManagementAvailable(){return TS.IS_STEAMOS}</c> keeps the UI away. Overriding that
/// one property affects one surface and is reversible; setting the constant it reads would produce
/// the same row while changing unrelated client behaviour everywhere, which is the spoof D16
/// forbids.
/// <para>
/// This reveals the surface. It does not populate it: every report from the Windows backend carries
/// an empty access-point list, so the network list stays empty until WSGM feeds it from the radio
/// helper through the store's own ingestion path. Installing this alone is a visible Wi-Fi row over
/// no networks, which is why <see cref="VerifyAsync"/> reports the access-point count rather than
/// treating a revealed row as success.
/// </para>
/// </remarks>
public sealed class SteamNetworkGatePatch : ISteamUiPatch
{
    private const string BridgeNamespace = SteamUiBridgeIdentity.Namespace;

    /// <inheritdoc />
    public string Id => "wsgm.steam-network.gate";

    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public string ResourceKey => "wsgm.steam-network.availability";

    /// <inheritdoc />
    public SteamUiPatchBounds Bounds { get; } = SteamUiPatchBounds.Default;

    /// <inheritdoc />
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

            // The getter must exist and be configurable, and it must currently read false. A client
            // that already reports network management available is one WSGM must leave alone.
            bool configurable =
                root.TryGetProperty("getterConfigurable", out JsonElement getter)
                && getter.ValueKind is JsonValueKind.True;
            bool hidden =
                root.TryGetProperty("currentlyHidden", out JsonElement value)
                && value.ValueKind is JsonValueKind.True;
            bool compatible = configurable && hidden;

            return new SteamUiPatchProbeResult(
                true,
                compatible,
                compatible,
                compatible ? "steam-network-gate-v1:configurable-getter+currently-hidden" : null,
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
            "return JSON.stringify(bridge.network.install());",
            "Network gate installation failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "const status=bridge.network.status();"
            + "return JSON.stringify({ok:status.installed&&status.available,status});",
            "Network gate verification failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "const removed=bridge.network.remove();const status=bridge.network.status();"
            + "return JSON.stringify({ok:removed.ok&&!status.available});",
            "Network gate removal failed.",
            cancellationToken);

    private static string ProbeExpression => """
        (()=>{try{
          let req;
          window.webpackChunksteamui.push([["wsgm_network_gate_probe_"+Date.now()],{},r=>req=r]);
          if(!req||!req.m)return JSON.stringify({error:'webpack unavailable'});
          const store=req('77347')&&req('77347').OQ&&req('77347').OQ.Get();
          if(!store)return JSON.stringify({error:'network store unavailable'});
          const d=Object.getOwnPropertyDescriptor(
            Object.getPrototypeOf(store),'networkManagementAvailable');
          return JSON.stringify({
            getterConfigurable:!!d&&d.configurable===true&&typeof d.get==='function',
            // False, or already overridden by US. A getter WSGM installed is not evidence that the
            // client reports network management natively, and reading it that way made this patch
            // refuse itself after a successful apply and tear the network list down.
            currentlyHidden:store.networkManagementAvailable===false
              ||(!!d&&!!d.get&&d.get.__wsgmOwnedGetter===true),
            hasWirelessDevice:store.hasWirelessDevice===true
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
            + "];if(!bridge||!bridge.network)return JSON.stringify({ok:false,error:'bridge unavailable'});"
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
