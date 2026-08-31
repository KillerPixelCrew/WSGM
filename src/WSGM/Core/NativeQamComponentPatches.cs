using System;
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
    /// <inheritdoc />
    public override string Id => "wsgm.native-qam.frame-limit";

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override string ComponentKind => "frameLimit";

    /// <inheritdoc />
    protected override string StructuralFingerprint =>
        "native-qam-frame-limit-v1:performance-actions+performance-root+valve-slider";

}

/// <summary>
/// Mounts Valve's profile header, which carries the per-game profile toggle inside it.
/// </summary>
/// <remarks>
/// The toggle is not separately mountable — probed 2026-08-30, its token resolves to the same
/// export as the header — so the two arrive together or not at all. Mounting this is what gives the
/// panel a per-application profile concept, since it is the control that creates and removes one.
/// </remarks>
public sealed class NativeQamValveProfileHeaderPatch : NativeQamComponentPatch
{
    /// <inheritdoc />
    public override string Id => "wsgm.native-qam.valve-profile-header";

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override string ComponentKind => "valveProfileHeader";

    /// <inheritdoc />
    protected override string StructuralFingerprint =>
        "native-qam-valve-profile-header-v1:performance-actions+performance-root+valve-header";

}

/// <summary>
/// Mounts Valve's reset-to-default button.
/// </summary>
/// <remarks>
/// Rendered last, because it undoes everything above it: a reset sitting among the controls it
/// clears is one mis-aimed press away from wiping a profile the user was in the middle of tuning.
/// </remarks>
public sealed class NativeQamValveResetPatch : NativeQamComponentPatch
{
    /// <inheritdoc />
    public override string Id => "wsgm.native-qam.valve-reset";

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override string ComponentKind => "valveReset";

    /// <inheritdoc />
    protected override string StructuralFingerprint =>
        "native-qam-valve-reset-v1:performance-actions+performance-root+valve-reset";

}

/// <summary>
/// Mounts Valve's own performance-overlay selector, retiring the hand-rolled imitation.
/// </summary>
public sealed class NativeQamValveOverlayLevelPatch : NativeQamComponentPatch
{
    /// <inheritdoc />
    public override string Id => "wsgm.native-qam.valve-overlay-level";

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override string ComponentKind => "valveOverlayLevel";

    /// <inheritdoc />
    protected override string StructuralFingerprint =>
        "native-qam-valve-overlay-level-v1:performance-actions+performance-root+valve-selector";

}

/// <summary>
/// Mounts Valve's refresh-rate row into Quick Settings.
/// </summary>
/// <remarks>
/// Quick Settings rather than Performance, per S14: resolution and refresh rate are display
/// controls, not performance ones. The component reads
/// <c>limits.display_refresh_manual_hz_min/max</c> from <c>SystemPerfStore</c>, which the
/// projection supplies only under <c>FrameLimitOnly</c> — under the pairing strategies the frame
/// cap owns the refresh rate, so the row hides itself through the state and needs no gate here.
/// </remarks>
public sealed class NativeQamValveRefreshRatePatch : NativeQamComponentPatch
{
    /// <inheritdoc />
    public override string Id => "wsgm.native-qam.valve-refresh-rate";

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override string ComponentKind => "valveRefreshRate";

    /// <inheritdoc />
    protected override string StructuralFingerprint =>
        "native-qam-valve-refresh-rate-v1:performance-actions+performance-root+valve-refresh";

}

/// <summary>
/// Adds a display-resolution row, which this client has no component for.
/// </summary>
/// <remarks>
/// Hand-built on Valve's own field primitives rather than mounted, unlike the frame limit and VRR
/// rows: SteamOS drives resolution through gamescope, so the Windows bundle ships no resolution
/// control to reactivate. It still carries its own id, fingerprint, verification, removal, and kill
/// switch, so a client rebuild that breaks it loses this row and nothing else.
/// </remarks>
public sealed class NativeQamResolutionPatch : NativeQamComponentPatch
{
    /// <inheritdoc />
    public override string Id => "wsgm.native-qam.resolution";

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override string ComponentKind => "resolution";

    /// <inheritdoc />
    protected override string StructuralFingerprint =>
        "native-qam-resolution-v1:performance-actions+performance-root+valve-dropdown";

}

/// <summary>
/// Mounts Valve's own power-limit toggle and slider, retiring the hand-rolled TDP row.
/// </summary>
/// <remarks>
/// Two rows rather than one, because that is how SteamOS models this control: the toggle is the
/// off state and the slider only appears behind it, which is why the slider has no zero position.
/// <para>
/// Unlike every other reactivated row, this one is not gated by <c>SystemPerfStore</c> at all. Both
/// halves read <c>is_tdp_limit_available</c> and the watt range out of the SteamOS Manager RPC, so
/// they render only once <see cref="NativeQamSteamOsManagerPatch"/> has supplied that answer —
/// and they write the <c>steamos_tdp_limit</c> client settings, which the same gate watches and
/// forwards to hardware. The two patches are one mechanism in two halves.
/// </para>
/// </remarks>
public sealed class NativeQamValveTdpPatch : NativeQamComponentPatch
{
    /// <inheritdoc />
    public override string Id => "wsgm.native-qam.valve-tdp";

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override string ComponentKind => "valveTdp";

    /// <inheritdoc />
    protected override string StructuralFingerprint =>
        "native-qam-valve-tdp-v1:performance-actions+performance-root+valve-tdp-pair";

}

/// <summary>
/// Adds the variable-refresh switch, which Valve's own component cannot supply on this client.
/// </summary>
/// <remarks>
/// Hand-built rather than reactivated, like the resolution row above. Valve ships a VRR component
/// and it is unusable here: it is gated on a react-query over
/// <c>SteamClient.System.DisplayManager</c>, whose <c>GetState</c> this client does not define, so
/// the query never succeeds and the component returns null before it reads a single field WSGM
/// publishes — live-probed on the reference device 2026-08-30. Supplying that namespace is its own
/// piece of work; this row runs on the device capability already verified through IGCL Arc Sync.
/// </remarks>
public sealed class NativeQamVrrPatch : NativeQamComponentPatch
{
    /// <inheritdoc />
    public override string Id => "wsgm.native-qam.vrr";

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override string ComponentKind => "vrr";

    /// <inheritdoc />
    protected override string StructuralFingerprint =>
        "native-qam-vrr-v1:performance-actions+performance-root+valve-toggle";

}

/// <summary>
/// Adds the approved controller-target projection to the native QAM with Valve's own dropdown,
/// focus, accessibility, and controller-navigation primitives.
/// </summary>
public sealed class NativeQamControllerTargetPatch : NativeQamComponentPatch
{
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
    protected override string PrimaryCountName => "controllerPresentation";

    /// <inheritdoc />
    protected override IReadOnlyList<string> PrimaryTokens { get; } =
    [
        "#QuickAccess_Tab_Settings_Section_Controller_Title",
        "#QuickAccess_ReorderControllers_Button",
        "#QuickAccess_Tab_Perf_Title",
    ];
}

/// <summary>
/// Adds WSGM's AutoTDP switch to the native QAM, beside the power limit it moves.
/// </summary>
/// <remarks>
/// Placed with the TDP control rather than in a section of its own, because the two are one control
/// surface: AutoTDP takes the slider over, and a user who sees the limit move on its own needs the
/// explanation next to the thing that is moving. It therefore requires the same TDP presentation the
/// power-limit patch does — with no native power limit there is nothing for AutoTDP to sit beside,
/// and nothing for it to drive.
/// </remarks>
public sealed class NativeQamAutoTdpPatch : NativeQamComponentPatch
{
    /// <inheritdoc />
    public override string Id => "wsgm.native-qam.auto-tdp";

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override string ComponentKind => "autoTdp";

    /// <inheritdoc />
    protected override string StructuralFingerprint =>
        "native-qam-auto-tdp-v1:presentation+performance-root+valve-toggle";

    /// <inheritdoc />
    protected override string PrimaryCountName => "tdpPresentation";

    /// <inheritdoc />
    protected override IReadOnlyList<string> PrimaryTokens { get; } =
    [
        "#QuickAccess_Tab_Perf_TDPLimitEnabled",
        "steamos_tdp_limit",
        "showBookendLabels",
    ];
}

/// <summary>
/// Shared bounded lifecycle for one independently versioned native-QAM semantic component.
/// </summary>
public abstract class NativeQamComponentPatch : ISteamUiPatch
{
    private const string BridgeNamespace = SteamUiBridgeIdentity.Namespace;
    private static readonly string[] CommonRequiredCounts =
    [
        "performanceRoot",
        "nativeFields",
        "nativeLayout",
        "localization",
        "react",
    ];
    private static readonly string[] PerformanceActionTokens =
    [
        "SetFPSLimitEnabled",
        "SetFPSLimit",
        "SetPerfOverlayLevel",
        "SteamClient.System.Perf",
    ];

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

    /// <summary>The component-specific probe result property.</summary>
    protected virtual string PrimaryCountName => "performanceActions";

    /// <summary>Tokens that uniquely identify the component-specific Steam factory.</summary>
    protected virtual IReadOnlyList<string> PrimaryTokens => PerformanceActionTokens;

    /// <summary>
    /// Stable webpack chunk label retained for live diagnostics and existing probe tooling.
    /// </summary>
    private string ProbeChunkPrefix => ComponentKind switch
    {
        "frameLimit" => "wsgm_native_frame_limit_probe_",
        "valveProfileHeader" => "wsgm_native_valve_header_probe_",
        "valveReset" => "wsgm_native_valve_reset_probe_",
        "valveOverlayLevel" => "wsgm_native_valve_overlay_probe_",
        "valveRefreshRate" => "wsgm_native_valve_refresh_probe_",
        "resolution" => "wsgm_native_resolution_probe_",
        "valveTdp" => "wsgm_native_valve_tdp_probe_",
        "vrr" => "wsgm_native_vrr_probe_",
        "controllerTarget" => "wsgm_native_controller_target_probe_",
        "autoTdp" => "wsgm_native_auto_tdp_probe_",
        _ => throw new InvalidOperationException($"Unknown native QAM component '{ComponentKind}'."),
    };

    /// <summary>Probe properties that must each report exactly one factory.</summary>
    protected IReadOnlyList<string> RequiredUniqueCounts =>
        [PrimaryCountName, .. CommonRequiredCounts];

    /// <summary>Read-only structural probe shared by every native-QAM component.</summary>
    protected string ProbeExpression => $$"""
        (()=>{try{
          let req;
          window.webpackChunksteamui.push([[{{SteamCef.JsString(ProbeChunkPrefix)}}+Date.now()],{},r=>req=r]);
          if(!req||!req.m)return JSON.stringify({error:'webpack unavailable'});
          const count=(tokens)=>Object.values(req.m).reduce((total,factory)=>{
            const source=String(factory);
            return total+(tokens.every(token=>source.includes(token))?1:0);
          },0);
          return JSON.stringify({
            {{PrimaryCountName}}:count({{JsonSerializer.Serialize(PrimaryTokens)}}),
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
        string expression = "(()=>{const b=window["
            + SteamCef.JsString(BridgeNamespace)
            + "];const bridge=b&&b.gate?b.gate('nativeComponents'):null;"
            + "if(!bridge)return JSON.stringify({ok:false,error:'bridge unavailable'});"
            + "return JSON.stringify(bridge.install("
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
        string expression = "(()=>{const b=window["
            + SteamCef.JsString(BridgeNamespace)
            + "];const bridge=b&&b.gate?b.gate('nativeComponents'):null;"
            + "if(!bridge)return JSON.stringify({ok:false,error:'bridge unavailable'});"
            + "const status=bridge.status("
            + SteamCef.JsString(ComponentKind)
            + ");return JSON.stringify({ok:status.ok&&status.registered"
            + "&&status.hostVersion===1&&status.performanceRootWrapped,status});})()";
        SteamUiPatchOperationResult result = await EvaluateOutcomeAsync(
            context,
            expression,
            "Native-QAM component verification failed.",
            cancellationToken).ConfigureAwait(false);

        // Verification asks whether the component registered and the performance root is wrapped.
        // Both can be true while the Quick Access panel shows nothing, because the rows are only
        // inserted if the tree Steam renders contains the section they attach to — and on Windows
        // Steam does not render the SteamOS-gated performance blocks at all. Reporting the append
        // outcome is what separates "WSGM did not run" from "WSGM ran and found nowhere to put it".
        await LogAppendOutcomeAsync(context, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        string expression = "(()=>{const b=window["
            + SteamCef.JsString(BridgeNamespace)
            + "];const bridge=b&&b.gate?b.gate('nativeComponents'):null;"
            + "if(!bridge)return JSON.stringify({ok:true,absent:true});"
            + "const removed=bridge.remove("
            + SteamCef.JsString(ComponentKind)
            + ");const status=bridge.status("
            + SteamCef.JsString(ComponentKind)
            + ");return JSON.stringify({ok:removed.ok&&!status.registered});})()";
        return await EvaluateOutcomeAsync(
            context,
            expression,
            "Native-QAM component removal failed.",
            cancellationToken).ConfigureAwait(false);
    }

    private static Task<SteamUiPatchOperationResult> EvaluateOutcomeAsync(
        SteamUiPatchContext context,
        string expression,
        string fallback,
        CancellationToken cancellationToken) =>
        SteamUiPatchEvaluation.EvaluateOutcomeAsync(
            context,
            SteamUiTargetRole.SharedJsContext,
            expression,
            fallback,
            cancellationToken);

    /// <summary>Reports what the last row-insertion attempt actually achieved.</summary>
    /// <param name="context">The live patch context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// Read-only, and deliberately best-effort: a diagnostic that could fail verification would
    /// make the log a liability. Keyed per component through <see cref="Log.Change"/>, so a steady
    /// outcome is stated once and a change in it is stated again.
    /// </remarks>
    private async Task LogAppendOutcomeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            string expression = "(()=>{const b=window[" + SteamCef.JsString(BridgeNamespace)
                + "];const g=b&&b.gate?b.gate('nativeComponents'):null;"
                + "if(!g)return JSON.stringify({error:'bridge unavailable'});"
                + "const s=g.status(" + SteamCef.JsString(ComponentKind) + ");"
                + "return JSON.stringify({append:s.lastAppend||{never:true},"
                + "rows:s.renderOutcomes,toggle:s.toggleResolved});})()";
            SteamUiEvaluationResult evaluation = await context.EvaluateAsync(
                SteamUiTargetRole.SharedJsContext,
                expression,
                cancellationToken).ConfigureAwait(false);
            if (!evaluation.Reachable || evaluation.Value is null)
            {
                return;
            }

            Log.Change(
                "steam.ui.append." + Id,
                $"Native-QAM rows for {Id}: {evaluation.Value}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Change("steam.ui.append.error." + Id, $"Native-QAM row report failed: {ex.Message}");
        }
    }

    private static bool IsOne(JsonElement root, string property) =>
        SteamUiPatchEvaluation.IsOne(root, property);
}
