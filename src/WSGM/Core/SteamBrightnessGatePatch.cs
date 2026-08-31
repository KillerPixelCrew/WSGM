using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
/// Reveals Steam's own brightness row, which its settings message hides on Windows.
/// </summary>
/// <remarks>
/// This supplies no backend, because there already is one: Steam tracks the real panel brightness on
/// Windows and both `SetBrightness` and `RegisterForBrightnessChanges` exist. The system settings
/// message simply reports `is_display_brightness_available` as false, and the hook that reads it
/// falls back to true only when the field is absent — never when it is explicitly false.
/// <para>
/// The narrowest patch WSGM has: one boolean, saved and restored. A client already reporting
/// brightness available is refused rather than overwritten, since restoring a value that was never
/// ours to change is how a removal leaves a client worse than it found it.
/// </para>
/// </remarks>
public sealed class SteamBrightnessGatePatch : ISteamUiPatch
{
    private const string BridgeNamespace = SteamUiBridgeIdentity.Namespace;

    /// <inheritdoc />
    public string Id => "wsgm.steam-display.brightness";

    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public string ResourceKey => "wsgm.steam-display.brightness-availability";

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
            bool fieldPresent =
                root.TryGetProperty("fieldPresent", out JsonElement present)
                && present.ValueKind is JsonValueKind.True;
            // Hidden or already ours — never "hidden" alone, which is the pre-patch condition a
            // successful apply invalidates. See the probe expression for the loop that caused.
            bool revealable =
                root.TryGetProperty("revealable", out JsonElement hidden)
                && hidden.ValueKind is JsonValueKind.True;

            // The backend has to be there too. Revealing the row without it would produce a slider
            // that moves and changes nothing, which is worse than no slider.
            bool backendPresent =
                root.TryGetProperty("backendPresent", out JsonElement backend)
                && backend.ValueKind is JsonValueKind.True;

            bool compatible = fieldPresent && revealable && backendPresent;
            return new SteamUiPatchProbeResult(
                true,
                compatible,
                compatible,
                compatible ? "steam-brightness-v1:hidden-flag+present-backend" : null,
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
            "return JSON.stringify(bridge.install());",
            "Brightness gate installation failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            // The setter too: a revealed slider whose writes still reach the native stub is the
            // exact broken state this gate shipped with, and "installed" alone reported it healthy.
            "const status=bridge.status();"
            + "return JSON.stringify({ok:status.installed&&status.available&&status.setterOwned,"
            + "status});",
            "Brightness gate verification failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(
            context,
            "const removed=bridge.remove();const status=bridge.status();"
            + "return JSON.stringify({ok:removed.ok&&!status.available});",
            "Brightness gate removal failed.",
            cancellationToken);

    private static string ProbeExpression => """
        (()=>{try{
          let req;
          window.webpackChunksteamui.push([["wsgm_brightness_probe_"+Date.now()],{},r=>req=r]);
          if(!req||!req.m)return JSON.stringify({error:'webpack unavailable'});
          const store=req('59547')&&req('59547').mG&&req('59547').mG.Get();
          const settings=store&&store.m_msgSettings;
          if(!settings)return JSON.stringify({error:'display settings unavailable'});
          const display=window.SteamClient&&SteamClient.System&&SteamClient.System.Display;
          return JSON.stringify({
            fieldPresent:'is_display_brightness_available' in settings,
            // Hidden, or visible because WSGM's own gate revealed it. Requiring hidden alone was
            // the self-incompatibility teardown loop: a successful apply made this false, the next
            // poll declared the patch incompatible, and the manager removed the reveal it had just
            // verified — the row flickered on a ~25-second cycle on the device (2026-08-30).
            revealable:settings.is_display_brightness_available!==true
              ||settings.__wsgmBrightnessRevealed===true,
            backendPresent:!!display&&typeof display.SetBrightness==='function'
              &&typeof display.RegisterForBrightnessChanges==='function'
          });
        }catch(error){return JSON.stringify({error:String(error)}); } })()
        """;

    private static Task<SteamUiPatchOperationResult> EvaluateAsync(
        SteamUiPatchContext context,
        string body,
        string fallback,
        CancellationToken cancellationToken)
    {
        string expression = "(()=>{const b=window["
            + SteamCef.JsString(BridgeNamespace)
            + "];const bridge=b&&b.gate?b.gate('brightness'):null;"
            + "if(!bridge)return JSON.stringify({ok:false,error:'bridge unavailable'});"
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
