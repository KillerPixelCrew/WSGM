using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
/// Probes the live native performance/QAM structure and installs only WSGM's narrow bridge.
/// It deliberately does not alter a Windows, SteamOS, device, capability, or component gate.
/// </summary>
public sealed class NativeQamBootstrapPatch : ISteamUiPatch
{
    private const string StructuralFingerprint =
        "qam-v1:tdp-availability+tdp-component+perf-actions+profile-readonly";
    private readonly SteamUiBridgeHost _bridge;

    /// <summary>Creates the bootstrap patch around its owned bridge.</summary>
    /// <param name="bridge">The versioned narrow Runtime-binding bridge.</param>
    public NativeQamBootstrapPatch(SteamUiBridgeHost bridge) => _bridge = bridge;

    /// <inheritdoc />
    public string Id => "wsgm.native-qam.bootstrap";

    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public string ResourceKey => "wsgm.native-qam.bridge";

    /// <inheritdoc />
    public SteamUiPatchBounds Bounds { get; } = SteamUiPatchBounds.Default;

    /// <inheritdoc />
    public async Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken)
    {
        var result = await context.EvaluateAsync(
            TargetRole, ProbeExpression, cancellationToken).ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return new SteamUiPatchProbeResult(
                false, false, false, null, result.Error ?? "SharedJSContext is unavailable.");
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            var unique = IsOne(root, "tdpAvailability")
                && IsOne(root, "tdpComponent")
                && IsOne(root, "performanceActions")
                && IsOne(root, "profileProjection");
            return new SteamUiPatchProbeResult(
                true,
                unique,
                unique,
                unique ? StructuralFingerprint : null,
                unique ? null : result.Value);
        }
        catch (JsonException ex)
        {
            return new SteamUiPatchProbeResult(true, false, false, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken) =>
        await _bridge.BootstrapAsync(cancellationToken).ConfigureAwait(false)
            ? new SteamUiPatchOperationResult(true, null)
            : new SteamUiPatchOperationResult(false, "Native-QAM bridge handshake failed.");

    /// <inheritdoc />
    public async Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken)
    {
        if (!_bridge.IsReady)
        {
            return new SteamUiPatchOperationResult(false, "Native-QAM bridge is not ready.");
        }
        var result = await context.EvaluateAsync(
            TargetRole,
            $"(()=>{{const b=window.{SteamUiBridgeIdentity.Namespace};"
                + "return JSON.stringify({ok:!!b,version:b&&b.version});})()",
            cancellationToken).ConfigureAwait(false);
        return result.Reachable
            && result.Value?.Contains("\"ok\":true", StringComparison.Ordinal) == true
            && result.Value.Contains("\"version\":1", StringComparison.Ordinal)
            ? new SteamUiPatchOperationResult(true, null)
            : new SteamUiPatchOperationResult(false, result.Error ?? "Bridge verification failed.");
    }

    /// <inheritdoc />
    public async Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context, CancellationToken cancellationToken)
    {
        await _bridge.RemoveAsync(cancellationToken).ConfigureAwait(false);
        var result = await context.EvaluateAsync(
            TargetRole,
            $"JSON.stringify({{absent:!window.{SteamUiBridgeIdentity.Namespace}}})",
            cancellationToken).ConfigureAwait(false);
        return result.Reachable
            && result.Value?.Contains("\"absent\":true", StringComparison.Ordinal) == true
            ? new SteamUiPatchOperationResult(true, null)
            : new SteamUiPatchOperationResult(false, result.Error ?? "Bridge resource remains present.");
    }

    private static bool IsOne(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var count)
        && count == 1;

    // Live-probed 2026-08-28 against the current Windows Steam SharedJSContext:
    // each conjunction identifies exactly one module. Module ids are intentionally
    // not retained because they are build output, not compatibility evidence.
    private const string ProbeExpression = """
        (()=>{try{
          let req;
          window.webpackChunksteamui.push([["wsgm_qam_probe_"+Date.now()],{},r=>req=r]);
          if(!req||!req.m)return JSON.stringify({error:'webpack unavailable'});
          const count=(tokens)=>Object.values(req.m).reduce((n,f)=>{
            const s=String(f);return n+(tokens.every(t=>s.includes(t))?1:0);},0);
          return JSON.stringify({
            tdpAvailability:count(['is_tdp_limit_available','steamos_tdp_limit_enabled','tdp_limit_min','tdp_limit_max']),
            tdpComponent:count(['#QuickAccess_Tab_Perf_TDPLimitEnabled','steamos_tdp_limit','showBookendLabels']),
            performanceActions:count(['SetFPSLimitEnabled','SetFPSLimit','SetPerfOverlayLevel','SteamClient.System.Perf']),
            profileProjection:count(['#PlatformPerformanceProfile_Label','steamos_platform_performance_profile','rgOptions'])
          });
        }catch(e){return JSON.stringify({error:String(e)});}})()
        """;
}
