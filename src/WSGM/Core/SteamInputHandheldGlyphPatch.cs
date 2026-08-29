using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
/// Installs the independently versioned, fail-closed route selector for future handheld glyph
/// delivery without changing Steam assets, controller identity, mappings, or page presentation.
/// </summary>
public sealed class SteamInputHandheldGlyphPatch : ISteamUiPatch
{
    private const string Namespace = "__wsgmSteamInputHandheldGlyphSelector_b563a91c";
    private const string Owner = "wsgm.steam-input.handheld-glyphs";
    private static readonly string[] RequiredUniqueCounts =
    [
        "configuration",
        "layoutEditor",
        "controllerSettings",
        "inputTest",
        "bindingGlyph",
        "menuPrompt",
        "semanticPrompt",
        "controllerImageContainer",
        "inlineShape",
    ];
    private const string ProbeExpression = """
        (async()=>{try{
          let req;
          window.webpackChunksteamui.push([["wsgm_handheld_glyph_probe_"+Date.now()],{},r=>req=r]);
          if(!req||!req.m)return JSON.stringify({error:'webpack unavailable'});
          const count=(tokens)=>Object.values(req.m).reduce((total,factory)=>{
            const source=String(factory);
            return total+(tokens.every(token=>source.includes(token))?1:0);
          },0);
          const shapeFactories=Object.values(req.m).filter(factory=>{
            const source=String(factory);
            return ['#ControllerVisualization_Joystick_Deadzone_LiveUpdate_Start','#ControllerVisualization_Joystick_Deadzone_LiveUpdate_Stop','d:']
              .every(token=>source.includes(token));
          });
          let inlineShape=0;
          if(shapeFactories.length===1){
            const paths=[...String(shapeFactories[0]).matchAll(/\bd:["']([^"']{12,1600})["']/g)]
              .map(match=>match[1]).filter(path=>/^[Mm]/.test(path));
            if(paths.length===1){
              const digest=await crypto.subtle.digest('SHA-256',new TextEncoder().encode(paths[0]));
              const hash=[...new Uint8Array(digest)].map(value=>value.toString(16).padStart(2,'0')).join('');
              inlineShape=paths[0].length===14&&hash==='52b961386cb4a9cb53cc2eb7baff0251ec7f8b7513efb035262c85bf71fb8d84'?1:0;
            }else inlineShape=paths.length;
          }
          return JSON.stringify({
            configuration:count(['ControllerConfiguratorSummary','useControllerLayoutContext cannot find ControllerLayoutContext!','#AppOverlay_ControllerSettings']),
            layoutEditor:count(['#AppControllerConfiguration_ViewLayout','#ControllerConfigurator_ActionSet','#ControllerConfigurationQuickSettings_EnableBackButtons']),
            controllerSettings:count(['#QuickAccess_Tab_ControllerSettings_Section_Device_Haptics','#Settings_Controller_ConnectedHeader','#ControllerSettings_TurnOffTimeout']),
            inputTest:count(['#Settings_ControllerDeviceSupport_TestingBindAButton','IdentifyController','#Settings_Controller_BindInput']),
            bindingGlyph:count(['/steaminputglyphs/','shared_mouse_scroll_down.svg','glyphFilename','Controller.Glyphs']),
            menuPrompt:count(['/steaminputglyphs/ps4_button_logo.svg','#ControllerButton_PlayStation','HomeMenu']),
            semanticPrompt:count(['/steaminputglyphs/shared_color_button_a.svg','strPath','eControllerSource','PillShapedIcon']),
            controllerImageContainer:count(['ControllerImageRow','ControllerInfoSVG','#AppControllerConfiguration_ViewLayout']),
            inlineShape
          });
        }catch(error){return JSON.stringify({error:String(error)});}})()
        """;

    /// <summary>Current independently deployable patch implementation version.</summary>
    public const int PatchVersion = 1;

    /// <summary>Catalog contract version understood by this selector; no assets are delivered yet.</summary>
    public const int CatalogVersion = 1;

    /// <summary>Current route and subject selector contract version.</summary>
    public const int SelectorVersion = 1;

    /// <inheritdoc />
    public string Id => Owner;

    /// <inheritdoc />
    public int Version => PatchVersion;

    /// <inheritdoc />
    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    /// <inheritdoc />
    public string ResourceKey => "wsgm.steam-input.handheld-glyph-selector";

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
                result.Error ?? "Steam SharedJSContext is unavailable.");
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

            const string fingerprint =
                "steam-input-handheld-glyphs-v1:catalog-1:selector-1:"
                + "configuration+layout-editor+controller-settings+input-test+"
                + "binding-glyph+menu-prompt+semantic-prompt+controller-image+inline-shape";
            return new SteamUiPatchProbeResult(
                true,
                unique,
                unique,
                unique ? fingerprint : null,
                unique ? null : result.Value);
        }
        catch (JsonException ex)
        {
            return new SteamUiPatchProbeResult(true, false, false, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) => EvaluateAsync(
            context,
            $$"""
            (()=>{try{
              const key={{SteamCef.JsString(Namespace)}};
              const owner={{SteamCef.JsString(Owner)}};
              const prior=window[key];
              if(prior&&prior.owner!==owner)return JSON.stringify({ok:false,error:'selector namespace occupied'});
              if(prior&&prior.patchVersion===1&&prior.catalogVersion===1&&prior.selectorVersion===1)
                return JSON.stringify({ok:true,reused:true});
              if(prior)delete window[key];
              const approved=Object.freeze([
                'steam-input-configuration','steam-input-layout-editor','controller-settings',
                'controller-input-test','steam-input-binding-row','main-menu-controller-prompt',
                'quick-access-controller-prompt'
              ]);
              const approvedSet=new Set(approved);
              const selector=Object.freeze({
                owner,patchVersion:1,catalogVersion:1,selectorVersion:1,
                approvedRoutes:approved,assetsDelivered:false,ownedNodeCount:0,
                matches:(route,subject)=>approvedSet.has(route)&&subject==='handheld'
              });
              Object.defineProperty(window,key,{value:selector,configurable:true,enumerable:false,writable:false});
              return JSON.stringify({ok:true,reused:false,approvedRouteCount:approved.length});
            }catch(error){return JSON.stringify({ok:false,error:String(error)}); } })()
            """,
            "Steam Input handheld glyph selector installation failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) => EvaluateAsync(
            context,
            $$"""
            (()=>{try{
              const selector=window[{{SteamCef.JsString(Namespace)}}];
              const approved=[
                'steam-input-configuration','steam-input-layout-editor','controller-settings',
                'controller-input-test','steam-input-binding-row','main-menu-controller-prompt',
                'quick-access-controller-prompt'
              ];
              const excluded=['store','community','browser','game','desktop-chromium',
                'quick-access-performance','quick-access-network','main-menu-unrelated'];
              const ok=!!selector
                &&selector.owner==={{SteamCef.JsString(Owner)}}
                &&selector.patchVersion===1&&selector.catalogVersion===1&&selector.selectorVersion===1
                &&selector.assetsDelivered===false&&selector.ownedNodeCount===0
                &&approved.every(route=>selector.matches(route,'handheld'))
                &&approved.every(route=>!selector.matches(route,'external'))
                &&approved.every(route=>!selector.matches(route,'unresolved'))
                &&excluded.every(route=>!selector.matches(route,'handheld'));
              return JSON.stringify({ok,approvedRouteCount:approved.length,assetsDelivered:selector?.assetsDelivered??null,ownedNodeCount:selector?.ownedNodeCount??null});
            }catch(error){return JSON.stringify({ok:false,error:String(error)}); } })()
            """,
            "Steam Input handheld glyph selector verification failed.",
            cancellationToken);

    /// <inheritdoc />
    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) => EvaluateAsync(
            context,
            $$"""
            (()=>{try{
              const key={{SteamCef.JsString(Namespace)}};
              const selector=window[key];
              if(!selector)return JSON.stringify({ok:true,absent:true});
              if(selector.owner!=={{SteamCef.JsString(Owner)}})
                return JSON.stringify({ok:false,error:'selector ownership changed'});
              delete window[key];
              return JSON.stringify({ok:!Object.hasOwn(window,key)});
            }catch(error){return JSON.stringify({ok:false,error:String(error)}); } })()
            """,
            "Steam Input handheld glyph selector removal failed.",
            cancellationToken);

    private static async Task<SteamUiPatchOperationResult> EvaluateAsync(
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
