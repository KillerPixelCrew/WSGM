using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
/// Restores Valve's native frame-limit presentation with WSGM's authoritative RTSS state.
/// </summary>
public sealed class NativeQamFrameLimitPatch : NativeQamComponentPatch
{
    private static readonly string[] RequiredCounts =
    [
        "performanceActions",
        "performanceRoot",
        "nativeFields",
        "nativeLayout",
        "localization",
        "react",
    ];

    /// <inheritdoc />
    public override string Id => "wsgm.native-qam.frame-limit";

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override string ComponentKind => "frameLimit";

    /// <inheritdoc />
    protected override string StructuralFingerprint =>
        "native-qam-frame-limit-v1:performance-actions+performance-root+valve-slider";

    /// <inheritdoc />
    protected override IReadOnlyList<string> RequiredUniqueCounts => RequiredCounts;

    /// <inheritdoc />
    protected override string ProbeExpression => PerformanceProbeExpression(
        "wsgm_native_frame_limit_probe_");
}

/// <summary>
/// Restores Valve's native performance-overlay presentation with exact RTSS adapter levels.
/// </summary>
public sealed class NativeQamOverlayLevelPatch : NativeQamComponentPatch
{
    private static readonly string[] RequiredCounts =
    [
        "performanceActions",
        "performanceRoot",
        "nativeFields",
        "nativeLayout",
        "localization",
        "react",
    ];

    /// <inheritdoc />
    public override string Id => "wsgm.native-qam.overlay-level";

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override string ComponentKind => "overlayLevel";

    /// <inheritdoc />
    protected override string StructuralFingerprint =>
        "native-qam-overlay-level-v1:performance-actions+performance-root+valve-dropdown";

    /// <inheritdoc />
    protected override IReadOnlyList<string> RequiredUniqueCounts => RequiredCounts;

    /// <inheritdoc />
    protected override string ProbeExpression => PerformanceProbeExpression(
        "wsgm_native_overlay_level_probe_");
}

/// <summary>
/// Restores Valve's native TDP presentation with WSGM's typed primary-power-limit state and action.
/// </summary>
public sealed class NativeQamTdpPatch : NativeQamComponentPatch
{
    private static readonly string[] RequiredCounts =
    [
        "tdpAvailability",
        "tdpPresentation",
        "performanceRoot",
        "nativeFields",
        "nativeLayout",
        "localization",
        "react",
    ];

    /// <inheritdoc />
    public override string Id => "wsgm.native-qam.tdp";

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override string ComponentKind => "tdp";

    /// <inheritdoc />
    protected override string StructuralFingerprint =>
        "native-qam-tdp-v1:availability+presentation+performance-root+valve-fields";

    /// <inheritdoc />
    protected override IReadOnlyList<string> RequiredUniqueCounts => RequiredCounts;

    /// <inheritdoc />
    protected override string ProbeExpression => """
        (()=>{try{
          let req;
          window.webpackChunksteamui.push([["wsgm_native_tdp_probe_"+Date.now()],{},r=>req=r]);
          if(!req||!req.m)return JSON.stringify({error:'webpack unavailable'});
          const count=(tokens)=>Object.values(req.m).reduce((total,factory)=>{
            const source=String(factory);
            return total+(tokens.every(token=>source.includes(token))?1:0);
          },0);
          return JSON.stringify({
            tdpAvailability:count(['is_tdp_limit_available','steamos_tdp_limit_enabled','tdp_limit_min','tdp_limit_max']),
            tdpPresentation:count(['#QuickAccess_Tab_Perf_TDPLimitEnabled','steamos_tdp_limit','showBookendLabels']),
            performanceRoot:count(['#QuickAccess_Tab_Perf_Common_Settings','#QuickAccess_Tab_Perf_BatteryTimeRemaining','TS.ON_FRAME']),
            nativeFields:count(['DialogSlider_Container','DropDownField','SliderField']),
            nativeLayout:count(['PanelSectionTitle','PanelSectionRow','spinner']),
            localization:count(['Attempting to localize token','Unable to find localization token','LocalizeString']),
            react:count(['react.transitional.element','useState','cloneElement','createElement'])
          });
        }catch(error){return JSON.stringify({error:String(error)}); } })()
        """;
}

/// <summary>
/// Adds the approved controller-target projection to the native QAM with Valve's own dropdown,
/// focus, accessibility, and controller-navigation primitives.
/// </summary>
public sealed class NativeQamControllerTargetPatch : NativeQamComponentPatch
{
    private static readonly string[] RequiredCounts =
    [
        "controllerPresentation",
        "performanceRoot",
        "nativeFields",
        "nativeLayout",
        "localization",
        "react",
    ];

    /// <inheritdoc />
    public override string Id => "wsgm.native-qam.controller-target";

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override string ComponentKind => "controllerTarget";

    /// <inheritdoc />
    protected override string StructuralFingerprint =>
        "native-qam-controller-target-v1:controller-presentation+performance-root+valve-dropdown";

    /// <inheritdoc />
    protected override IReadOnlyList<string> RequiredUniqueCounts => RequiredCounts;

    /// <inheritdoc />
    protected override string ProbeExpression => """
        (()=>{try{
          let req;
          window.webpackChunksteamui.push([["wsgm_native_controller_target_probe_"+Date.now()],{},r=>req=r]);
          if(!req||!req.m)return JSON.stringify({error:'webpack unavailable'});
          const count=(tokens)=>Object.values(req.m).reduce((total,factory)=>{
            const source=String(factory);
            return total+(tokens.every(token=>source.includes(token))?1:0);
          },0);
          return JSON.stringify({
            controllerPresentation:count(['#QuickAccess_Tab_Settings_Section_Controller_Title','#QuickAccess_ReorderControllers_Button','#QuickAccess_Tab_Perf_Title']),
            performanceRoot:count(['#QuickAccess_Tab_Perf_Common_Settings','#QuickAccess_Tab_Perf_BatteryTimeRemaining','TS.ON_FRAME']),
            nativeFields:count(['DialogSlider_Container','DropDownField','SliderField']),
            nativeLayout:count(['PanelSectionTitle','PanelSectionRow','spinner']),
            localization:count(['Attempting to localize token','Unable to find localization token','LocalizeString']),
            react:count(['react.transitional.element','useState','cloneElement','createElement'])
          });
        }catch(error){return JSON.stringify({error:String(error)}); } })()
        """;
}

/// <summary>
/// Shared bounded lifecycle for one independently versioned native-QAM semantic component.
/// </summary>
public abstract class NativeQamComponentPatch : ISteamUiPatch
{
    private const string BridgeNamespace = "__wsgmSteamUi_v1_28d7c54a";

    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract int Version { get; }

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public string ResourceKey => "wsgm.native-qam.performance-root";

    /// <inheritdoc />
    public SteamUiPatchBounds Bounds { get; } = SteamUiPatchBounds.Default;

    /// <summary>Compiled component kind accepted by the embedded bootstrap.</summary>
    protected abstract string ComponentKind { get; }

    /// <summary>Stable structural fingerprint describing the exact positive match.</summary>
    protected abstract string StructuralFingerprint { get; }

    /// <summary>Probe properties that must each report exactly one factory.</summary>
    protected abstract IReadOnlyList<string> RequiredUniqueCounts { get; }

    /// <summary>Read-only structural probe for this patch.</summary>
    protected abstract string ProbeExpression { get; }

    /// <summary>Shared live-verified probe for native QAM performance controls.</summary>
    protected static string PerformanceProbeExpression(string chunkPrefix) => $$"""
        (()=>{try{
          let req;
          window.webpackChunksteamui.push([[{{SteamCef.JsString(chunkPrefix)}}+Date.now()],{},r=>req=r]);
          if(!req||!req.m)return JSON.stringify({error:'webpack unavailable'});
          const count=(tokens)=>Object.values(req.m).reduce((total,factory)=>{
            const source=String(factory);
            return total+(tokens.every(token=>source.includes(token))?1:0);
          },0);
          return JSON.stringify({
            performanceActions:count(['SetFPSLimitEnabled','SetFPSLimit','SetPerfOverlayLevel','SteamClient.System.Perf']),
            performanceRoot:count(['#QuickAccess_Tab_Perf_Common_Settings','#QuickAccess_Tab_Perf_BatteryTimeRemaining','TS.ON_FRAME']),
            nativeFields:count(['DialogSlider_Container','DropDownField','SliderField']),
            nativeLayout:count(['PanelSectionTitle','PanelSectionRow','spinner']),
            localization:count(['Attempting to localize token','Unable to find localization token','LocalizeString']),
            react:count(['react.transitional.element','useState','cloneElement','createElement'])
          });
        }catch(error){return JSON.stringify({error:String(error)}); } })()
        """;

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
            bool unique = true;
            foreach (string property in RequiredUniqueCounts)
            {
                unique &= IsOne(root, property);
            }

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
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        string expression = "(()=>{const bridge=window["
            + SteamCef.JsString(BridgeNamespace)
            + "];if(!bridge||!bridge.nativeComponents)return JSON.stringify({ok:false,error:'bridge unavailable'});"
            + "return JSON.stringify(bridge.nativeComponents.install("
            + SteamCef.JsString(ComponentKind)
            + "));})()";
        return await EvaluateOutcomeAsync(
            context,
            expression,
            "Native-QAM component installation failed.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        string expression = "(()=>{const bridge=window["
            + SteamCef.JsString(BridgeNamespace)
            + "];if(!bridge||!bridge.nativeComponents)return JSON.stringify({ok:false,error:'bridge unavailable'});"
            + "const status=bridge.nativeComponents.status("
            + SteamCef.JsString(ComponentKind)
            + ");return JSON.stringify({ok:status.ok&&status.registered"
            + "&&status.hostVersion===1&&status.performanceRootWrapped,status});})()";
        return await EvaluateOutcomeAsync(
            context,
            expression,
            "Native-QAM component verification failed.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        string expression = "(()=>{const bridge=window["
            + SteamCef.JsString(BridgeNamespace)
            + "];if(!bridge||!bridge.nativeComponents)return JSON.stringify({ok:true,absent:true});"
            + "const removed=bridge.nativeComponents.remove("
            + SteamCef.JsString(ComponentKind)
            + ");const status=bridge.nativeComponents.status("
            + SteamCef.JsString(ComponentKind)
            + ");return JSON.stringify({ok:removed.ok&&!status.registered});})()";
        return await EvaluateOutcomeAsync(
            context,
            expression,
            "Native-QAM component removal failed.",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SteamUiPatchOperationResult> EvaluateOutcomeAsync(
        SteamUiPatchContext context,
        string expression,
        string fallback,
        CancellationToken cancellationToken)
    {
        SteamUiEvaluationResult result = await context.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            expression,
            cancellationToken).ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return new SteamUiPatchOperationResult(false, result.Error ?? fallback);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Value);
            JsonElement root = document.RootElement;
            bool succeeded = root.TryGetProperty("ok", out JsonElement ok)
                && ok.ValueKind == JsonValueKind.True;
            string? diagnostic = root.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : succeeded ? null : fallback;
            return new SteamUiPatchOperationResult(succeeded, diagnostic);
        }
        catch (JsonException ex)
        {
            return new SteamUiPatchOperationResult(false, ex.Message);
        }
    }

    private static bool IsOne(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int count)
        && count == 1;
}
