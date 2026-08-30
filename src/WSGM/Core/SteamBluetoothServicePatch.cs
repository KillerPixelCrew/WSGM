using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
/// Supplies the Bluetooth backend behind Steam's own pairing UI, which Windows leaves empty.
/// </summary>
/// <remarks>
/// `BluetoothManagerService` and every operation it offers already exist in the Windows client. Its
/// `GetState` round-trips successfully and answers `is_service_available: false` with no adapters
/// and no devices, so the transport and the message shapes are present and only the backend is
/// missing. WSGM replaces the stub's methods; the service cannot be implemented because the
/// `*Handler` exports are message descriptors rather than registration hooks.
/// <para>
/// Availability reaches the UI through react-query with an infinite stale time, so replacing the
/// methods accomplishes nothing on its own — the cache has to be invalidated, which install and
/// remove both do. This is the same second gate the audio store has, in a different layer, and it
/// is why <see cref="VerifyAsync"/> asks the bridge whether the service reports available rather
/// than whether the methods were swapped.
/// </para>
/// </remarks>
public sealed class SteamBluetoothServicePatch : ISteamUiPatch
{
    private const string BridgeNamespace = "__wsgmSteamUi_v1_28d7c54a";

    /// <inheritdoc />
    public string Id => "wsgm.steam-bluetooth.service";

    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public string ResourceKey => "wsgm.steam-bluetooth.manager-service";

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
            bool operations =
                root.TryGetProperty("operationsPresent", out JsonElement present)
                && present.ValueKind is JsonValueKind.True;
            bool writable =
                root.TryGetProperty("methodsWritable", out JsonElement mutable)
                && mutable.ValueKind is JsonValueKind.True;
            bool cacheReachable =
                root.TryGetProperty("queryCacheReachable", out JsonElement cache)
                && cache.ValueKind is JsonValueKind.True;

            // All three, and the third is not optional: without the query cache the row would keep
            // reading the unavailable answer no matter what the methods return.
            bool compatible = operations && writable && cacheReachable;
            return new SteamUiPatchProbeResult(
                true,
                compatible,
                compatible,
                compatible ? "steam-bluetooth-v1:operations+writable-stub+reachable-cache" : null,
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
            "return JSON.stringify(bridge.bluetooth.install());",
            "Bluetooth service installation failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "const status=bridge.bluetooth.status();"
            + "return JSON.stringify({ok:status.installed&&status.replaced>0,status});",
            "Bluetooth service verification failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "const removed=bridge.bluetooth.remove();const status=bridge.bluetooth.status();"
            + "return JSON.stringify({ok:removed.ok&&!status.installed});",
            "Bluetooth service removal failed.",
            cancellationToken);

    private static string ProbeExpression => """
        (()=>{try{
          let req;
          window.webpackChunksteamui.push([["wsgm_bluetooth_probe_"+Date.now()],{},r=>req=r]);
          if(!req||!req.m)return JSON.stringify({error:'webpack unavailable'});
          const RF=req('60517')&&req('60517').RF;
          if(!RF)return JSON.stringify({error:'bluetooth service stub unavailable'});
          const ops=['GetState','SetDiscovering','Pair','CancelPair','Connect','Disconnect',
            'Forget','SetTrusted','SetWakeAllowed','GetDeviceDetails'];
          const missing=ops.filter(n=>typeof RF[n]!=='function');
          const d=Object.getOwnPropertyDescriptor(RF,'GetState');
          let cache=false;
          try{cache=typeof req('21371').L.invalidateQueries==='function';}catch{}
          return JSON.stringify({
            operationsPresent:missing.length===0,
            missing:missing,
            methodsWritable:!!d&&d.writable===true&&d.configurable===true,
            queryCacheReachable:cache
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
            + "];if(!bridge||!bridge.bluetooth)return JSON.stringify({ok:false,error:'bridge unavailable'});"
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
