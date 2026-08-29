using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Core;

internal readonly record struct SteamInputGlyphTierEnablement(
    bool StableResources,
    bool ControllerImages,
    bool InlineValveSvg,
    bool CapabilityHiding)
{
    internal static SteamInputGlyphTierEnablement Disabled => new(false, false, false, false);
}

internal sealed class SteamInputGlyphDeliveryState
{
    private SteamInputGlyphPresentation? _presentation;

    internal SteamInputGlyphPresentation? Current => Volatile.Read(ref _presentation);

    internal void Update(ImportedGlyphProfile? profile) =>
        Volatile.Write(ref _presentation, SteamInputGlyphPresentation.Create(profile));
}

internal sealed record SteamInputGlyphAssetReference(string Sha256, string DataUri);

internal sealed record SteamInputGlyphResourceMapping(
    string ValvePath,
    GlyphControlId Control,
    SteamInputGlyphAssetReference Asset);

internal sealed record SteamInputGlyphControllerImageMapping(
    string Slot,
    SteamInputGlyphAssetReference Asset);

internal sealed record SteamInputGlyphInlineMapping(
    string ValvePathSha256,
    SteamInputGlyphAssetReference Asset);

internal sealed record SteamInputGlyphPresentation(
    string ProfileId,
    int Revision,
    IReadOnlyList<SteamInputGlyphResourceMapping> StableResources,
    IReadOnlyList<SteamInputGlyphControllerImageMapping> ControllerImages,
    IReadOnlyList<SteamInputGlyphInlineMapping> InlineMappings,
    IReadOnlyList<GlyphControlId> AbsentControls)
{
    private static readonly (string Path, GlyphControlId Control)[] StableResourceMap =
    [
        ("/steaminputglyphs/shared_color_button_a.svg", GlyphControlId.FaceSouth),
        ("/steaminputglyphs/shared_button_a.svg", GlyphControlId.FaceSouth),
        ("/steaminputglyphs/shared_color_button_b.svg", GlyphControlId.FaceEast),
        ("/steaminputglyphs/shared_button_b.svg", GlyphControlId.FaceEast),
        ("/steaminputglyphs/shared_color_button_x.svg", GlyphControlId.FaceWest),
        ("/steaminputglyphs/shared_button_x.svg", GlyphControlId.FaceWest),
        ("/steaminputglyphs/shared_color_button_y.svg", GlyphControlId.FaceNorth),
        ("/steaminputglyphs/shared_button_y.svg", GlyphControlId.FaceNorth),
        ("/steaminputglyphs/shared_dpad_up.svg", GlyphControlId.DpadUp),
        ("/steaminputglyphs/shared_dpad_down.svg", GlyphControlId.DpadDown),
        ("/steaminputglyphs/shared_dpad_left.svg", GlyphControlId.DpadLeft),
        ("/steaminputglyphs/shared_dpad_right.svg", GlyphControlId.DpadRight),
        ("/steaminputglyphs/shared_l3.svg", GlyphControlId.LeftStick),
        ("/steaminputglyphs/shared_lstick.svg", GlyphControlId.LeftStick),
        ("/steaminputglyphs/shared_lstick_click.svg", GlyphControlId.LeftStick),
        ("/steaminputglyphs/shared_lstick_touch.svg", GlyphControlId.LeftStickTouch),
        ("/steaminputglyphs/shared_r3.svg", GlyphControlId.RightStick),
        ("/steaminputglyphs/shared_rstick.svg", GlyphControlId.RightStick),
        ("/steaminputglyphs/shared_rstick_click.svg", GlyphControlId.RightStick),
        ("/steaminputglyphs/shared_rstick_touch.svg", GlyphControlId.RightStickTouch),
        ("/steaminputglyphs/xbox_button_logo.svg", GlyphControlId.Guide),
        ("/steaminputglyphs/sc_button_steam.svg", GlyphControlId.Guide),
        ("/steaminputglyphs/xbox_button_select.svg", GlyphControlId.View),
        ("/steaminputglyphs/xbox_button_start.svg", GlyphControlId.Menu),
        ("/steaminputglyphs/qam_icon.svg", GlyphControlId.QuickAccess),
        ("/steaminputglyphs/shared_m1.svg", GlyphControlId.RearM1),
        ("/steaminputglyphs/shared_m2.svg", GlyphControlId.RearM2),
    ];

    internal static SteamInputGlyphPresentation? Create(ImportedGlyphProfile? profile)
    {
        if (profile is null || profile.Manifest.ProfileId.Length == 0)
        {
            return null;
        }

        Dictionary<GlyphControlId, GlyphControlMapping> controls = profile.Manifest.Controls
            .ToDictionary(mapping => mapping.Control);
        Dictionary<GlyphControlId, GlyphControlId> aliases = profile.Manifest.Aliases
            .ToDictionary(mapping => mapping.LogicalControl, mapping => mapping.PhysicalControl);
        Dictionary<string, SteamInputGlyphAssetReference> assetReferences =
            new(StringComparer.Ordinal);
        List<SteamInputGlyphResourceMapping> resources = [];
        foreach ((string path, GlyphControlId logicalControl) in StableResourceMap)
        {
            GlyphControlId physicalControl = aliases.GetValueOrDefault(logicalControl, logicalControl);
            if (!controls.TryGetValue(physicalControl, out GlyphControlMapping? mapping)
                || mapping.Presence is not GlyphControlPresence.Present
                || mapping.AssetSha256 is not { Length: > 0 } assetHash
                || !TryGetAsset(
                    profile,
                    assetReferences,
                    assetHash,
                    out SteamInputGlyphAssetReference asset))
            {
                continue;
            }
            resources.Add(new SteamInputGlyphResourceMapping(path, logicalControl, asset));
        }

        List<SteamInputGlyphControllerImageMapping> images = [];
        AddControllerImage(
            profile,
            assetReferences,
            images,
            "full",
            profile.Manifest.ControllerImages.FullSha256);
        AddControllerImage(
            profile,
            assetReferences,
            images,
            "left",
            profile.Manifest.ControllerImages.LeftSha256);
        AddControllerImage(
            profile,
            assetReferences,
            images,
            "right",
            profile.Manifest.ControllerImages.RightSha256);

        GlyphControlId[] absent = profile.Manifest.Controls
            .Where(mapping => mapping.Presence is GlyphControlPresence.Absent)
            .Select(mapping => mapping.Control)
            .OrderBy(control => control)
            .ToArray();

        // Inline Valve paths are intentionally not inferred from semantic control artwork. Each
        // source-path hash needs its own audited catalog mapping before this tier can be enabled.
        return new SteamInputGlyphPresentation(
            profile.Manifest.ProfileId,
            profile.Manifest.Revision,
            resources,
            images,
            [],
            absent);
    }

    private static void AddControllerImage(
        ImportedGlyphProfile profile,
        IDictionary<string, SteamInputGlyphAssetReference> assetReferences,
        ICollection<SteamInputGlyphControllerImageMapping> images,
        string slot,
        string? assetHash)
    {
        if (assetHash is { Length: > 0 }
            && TryGetAsset(
                profile,
                assetReferences,
                assetHash,
                out SteamInputGlyphAssetReference asset))
        {
            images.Add(new SteamInputGlyphControllerImageMapping(slot, asset));
        }
    }

    private static bool TryGetAsset(
        ImportedGlyphProfile profile,
        IDictionary<string, SteamInputGlyphAssetReference> assetReferences,
        string assetHash,
        out SteamInputGlyphAssetReference reference)
    {
        reference = null!;
        if (assetReferences.TryGetValue(
            assetHash,
            out SteamInputGlyphAssetReference? existing)
            && existing is not null)
        {
            reference = existing;
            return true;
        }
        if (!profile.Assets.TryGetValue(assetHash, out ImportedGlyphAsset? asset)
            || !string.Equals(asset.Lock.Sha256, assetHash, StringComparison.Ordinal))
        {
            return false;
        }

        string mediaType;
        ReadOnlySpan<byte> bytes;
        if (asset.Lock.Format is GlyphAssetFormat.Svg && asset.Vector is not null)
        {
            mediaType = "image/svg+xml";
            bytes = asset.Vector.CanonicalSvgUtf8.Span;
        }
        else if (asset.Lock.Format is GlyphAssetFormat.Png && !asset.RasterPng.IsEmpty)
        {
            mediaType = "image/png";
            bytes = asset.RasterPng.Span;
        }
        else
        {
            return false;
        }

        reference = new SteamInputGlyphAssetReference(
            assetHash,
            $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}");
        assetReferences.Add(assetHash, reference);
        return true;
    }
}

internal abstract class SteamInputGlyphTierPatch : ISteamUiPatch
{
    private const string SelectorNamespace = SteamInputHandheldGlyphPatch.SelectorNamespace;
    private readonly SteamInputGlyphDeliveryState _state;

    protected SteamInputGlyphTierPatch(SteamInputGlyphDeliveryState state) => _state = state;

    public abstract string Id { get; }

    public abstract int Version { get; }

    public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

    public abstract string ResourceKey { get; }

    public SteamUiPatchBounds Bounds { get; } = SteamUiPatchBounds.Default;

    protected abstract string Namespace { get; }

    protected abstract string Fingerprint { get; }

    protected abstract bool HasMappings(SteamInputGlyphPresentation presentation);

    protected abstract string BuildProbeExpression(SteamInputGlyphPresentation presentation);

    protected abstract string BuildMappingLiteral(SteamInputGlyphPresentation presentation);

    protected abstract string BuildResolverExpression();

    protected abstract string BuildVerificationExpression(SteamInputGlyphPresentation presentation);

    public async Task<SteamUiPatchProbeResult> ProbeAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        SteamInputGlyphPresentation? presentation = _state.Current;
        if (presentation is null || !HasMappings(presentation))
        {
            return new SteamUiPatchProbeResult(
                true,
                false,
                false,
                null,
                "No reviewed selected profile supplies an exact mapping for this glyph tier.");
        }

        SteamUiEvaluationResult result = await context.EvaluateAsync(
            TargetRole,
            BuildProbeExpression(presentation),
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

        return SteamUiPatchEvaluation.IsSuccessful(result.Value)
            ? new SteamUiPatchProbeResult(true, true, true, Fingerprint, null)
            : new SteamUiPatchProbeResult(
                true,
                false,
                false,
                null,
                SteamUiPatchEvaluation.Bounded(result.Value));
    }

    public Task<SteamUiPatchOperationResult> ApplyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        SteamInputGlyphPresentation? presentation = _state.Current;
        if (presentation is null || !HasMappings(presentation))
        {
            return Task.FromResult(new SteamUiPatchOperationResult(
                false,
                "The selected reviewed profile no longer supplies this tier."));
        }

        string expression = $$"""
            (()=>{try{
              const selector=window[{{SteamCef.JsString(SelectorNamespace)}}];
              if(!selector||selector.owner!=='wsgm.steam-input.handheld-glyphs')
                return JSON.stringify({ok:false,error:'handheld selector unavailable'});
              const key={{SteamCef.JsString(Namespace)}};
              const owner={{SteamCef.JsString(Id)}};
              const prior=window[key];
              if(prior&&prior.owner!==owner)return JSON.stringify({ok:false,error:'tier namespace occupied'});
              if(prior)delete window[key];
              const mappings=Object.freeze({{BuildMappingLiteral(presentation)}});
              const tier=Object.freeze({
                owner,version:{{Version}},profileId:{{SteamCef.JsString(presentation.ProfileId)}},
                profileRevision:{{presentation.Revision}},mappings,
                resolve:{{BuildResolverExpression()}},ownedNodeCount:0,uiMutationCount:0
              });
              Object.defineProperty(window,key,{value:tier,configurable:true,enumerable:false,writable:false});
              return JSON.stringify({ok:true,mappingCount:Object.keys(mappings).length,ownedNodeCount:0,uiMutationCount:0});
            }catch(error){return JSON.stringify({ok:false,error:String(error)}); } })()
            """;
        return EvaluateAsync(context, expression, "Glyph tier installation failed.", cancellationToken);
    }

    public Task<SteamUiPatchOperationResult> VerifyAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken)
    {
        SteamInputGlyphPresentation? presentation = _state.Current;
        if (presentation is null || !HasMappings(presentation))
        {
            return Task.FromResult(new SteamUiPatchOperationResult(
                false,
                "The selected reviewed profile no longer supplies this tier."));
        }

        return EvaluateAsync(
            context,
            BuildVerificationExpression(presentation),
            "Glyph tier verification failed.",
            cancellationToken);
    }

    public Task<SteamUiPatchOperationResult> RemoveAsync(
        SteamUiPatchContext context,
        CancellationToken cancellationToken) => EvaluateAsync(
            context,
            $$"""
            (()=>{try{
              const key={{SteamCef.JsString(Namespace)}};
              const tier=window[key];
              if(!tier)return JSON.stringify({ok:true,absent:true});
              if(tier.owner!=={{SteamCef.JsString(Id)}})
                return JSON.stringify({ok:false,error:'tier ownership changed'});
              delete window[key];
              return JSON.stringify({ok:!Object.hasOwn(window,key)});
            }catch(error){return JSON.stringify({ok:false,error:String(error)}); } })()
            """,
            "Glyph tier removal failed.",
            cancellationToken);

    protected static string ObjectLiteral(IEnumerable<KeyValuePair<string, string>> mappings) =>
        "{" + string.Join(",", mappings.Select(mapping =>
            $"{SteamCef.JsString(mapping.Key)}:{SteamCef.JsString(mapping.Value)}")) + "}";

    protected static string StringArray(IEnumerable<string> values) =>
        "[" + string.Join(",", values.Select(SteamCef.JsString)) + "]";

    private static Task<SteamUiPatchOperationResult> EvaluateAsync(
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
}

internal sealed class SteamInputStableResourceGlyphPatch(SteamInputGlyphDeliveryState state)
    : SteamInputGlyphTierPatch(state)
{
    internal const string PatchId = "wsgm.steam-input.glyph-resources";

    public override string Id => PatchId;

    public override int Version => 1;

    public override string ResourceKey => "wsgm.steam-input.glyph-resource-map";

    protected override string Namespace => "__wsgmSteamInputGlyphResources_3a19cd7e";

    protected override string Fingerprint =>
        "steam-input-glyph-resources-v1:binding+menu+semantic:exact-resource-set";

    protected override bool HasMappings(SteamInputGlyphPresentation presentation) =>
        presentation.StableResources.Count > 0;

    protected override string BuildProbeExpression(SteamInputGlyphPresentation presentation)
    {
        string expected = StringArray(presentation.StableResources.Select(mapping => mapping.ValvePath));
        return $$"""
            (()=>{try{
              let req;window.webpackChunksteamui.push([["wsgm_glyph_resource_probe_"+Date.now()],{},r=>req=r]);
              if(!req||!req.m)return JSON.stringify({ok:false,error:'webpack unavailable'});
              const sources=Object.values(req.m).map(factory=>String(factory));
              const count=tokens=>sources.filter(source=>tokens.every(token=>source.includes(token))).length;
              const expected={{expected}};
              const known=new Set(sources.flatMap(source=>[...source.matchAll(/\/steaminputglyphs\/[a-z0-9_\-.]+\.(?:svg|png)/gi)].map(match=>match[0])));
              const ok=count(['/steaminputglyphs/','shared_mouse_scroll_down.svg','glyphFilename','Controller.Glyphs'])===1
                &&count(['/steaminputglyphs/ps4_button_logo.svg','#ControllerButton_PlayStation','HomeMenu'])===1
                &&count(['/steaminputglyphs/shared_color_button_a.svg','strPath','eControllerSource','PillShapedIcon'])===1
                &&expected.every(path=>known.has(path));
              return JSON.stringify({ok,expectedCount:expected.length,knownCount:known.size});
            }catch(error){return JSON.stringify({ok:false,error:String(error)}); } })()
            """;
    }

    protected override string BuildMappingLiteral(SteamInputGlyphPresentation presentation) =>
        ObjectLiteral(presentation.StableResources.ToDictionary(
            mapping => mapping.ValvePath,
            mapping => mapping.Asset.DataUri,
            StringComparer.Ordinal));

    protected override string BuildResolverExpression() =>
        "(route,subject,path)=>selector.matches(route,subject)&&Object.hasOwn(mappings,path)?mappings[path]:null";

    protected override string BuildVerificationExpression(SteamInputGlyphPresentation presentation)
    {
        SteamInputGlyphResourceMapping expected = presentation.StableResources[0];
        return $$"""
            (()=>{try{
              const tier=window[{{SteamCef.JsString(Namespace)}}];
              const path={{SteamCef.JsString(expected.ValvePath)}};
              const resolved=tier?.resolve('steam-input-binding-row','handheld',path);
              const ok=!!tier&&tier.owner==={{SteamCef.JsString(Id)}}&&tier.version===1
                &&tier.profileId==={{SteamCef.JsString(presentation.ProfileId)}}
                &&Object.keys(tier.mappings).length==={{presentation.StableResources.Count}}
                &&resolved===tier.mappings[path]&&resolved.startsWith('data:image/')
                &&tier.resolve('store','handheld',path)===null
                &&tier.resolve('steam-input-binding-row','external',path)===null
                &&tier.ownedNodeCount===0&&tier.uiMutationCount===0;
              return JSON.stringify({ok,resolvedReference:!!resolved,mappingCount:Object.keys(tier?.mappings??{}).length});
            }catch(error){return JSON.stringify({ok:false,error:String(error)}); } })()
            """;
    }
}

internal sealed class SteamInputControllerImageGlyphPatch(SteamInputGlyphDeliveryState state)
    : SteamInputGlyphTierPatch(state)
{
    internal const string PatchId = "wsgm.steam-input.controller-images";

    public override string Id => PatchId;

    public override int Version => 1;

    public override string ResourceKey => "wsgm.steam-input.controller-image-map";

    protected override string Namespace => "__wsgmSteamInputControllerImages_91a5d482";

    protected override string Fingerprint =>
        "steam-input-controller-images-v1:layout-editor+unique-controller-image-container";

    protected override bool HasMappings(SteamInputGlyphPresentation presentation) =>
        presentation.ControllerImages.Count > 0;

    protected override string BuildProbeExpression(SteamInputGlyphPresentation presentation) => """
        (()=>{try{
          let req;window.webpackChunksteamui.push([["wsgm_controller_image_probe_"+Date.now()],{},r=>req=r]);
          if(!req||!req.m)return JSON.stringify({ok:false,error:'webpack unavailable'});
          const sources=Object.values(req.m).map(factory=>String(factory));
          const count=tokens=>sources.filter(source=>tokens.every(token=>source.includes(token))).length;
          const ok=count(['#AppControllerConfiguration_ViewLayout','#ControllerConfigurator_ActionSet','#ControllerConfigurationQuickSettings_EnableBackButtons'])===1
            &&count(['ControllerImageRow','ControllerInfoSVG','#AppControllerConfiguration_ViewLayout'])===1;
          return JSON.stringify({ok});
        }catch(error){return JSON.stringify({ok:false,error:String(error)});}})()
        """;

    protected override string BuildMappingLiteral(SteamInputGlyphPresentation presentation) =>
        ObjectLiteral(presentation.ControllerImages.ToDictionary(
            mapping => mapping.Slot,
            mapping => mapping.Asset.DataUri,
            StringComparer.Ordinal));

    protected override string BuildResolverExpression() =>
        "(route,subject,slot)=>selector.matches(route,subject)&&Object.hasOwn(mappings,slot)?mappings[slot]:null";

    protected override string BuildVerificationExpression(SteamInputGlyphPresentation presentation)
    {
        SteamInputGlyphControllerImageMapping expected = presentation.ControllerImages[0];
        return $$"""
            (()=>{try{
              const tier=window[{{SteamCef.JsString(Namespace)}}];
              const slot={{SteamCef.JsString(expected.Slot)}};
              const resolved=tier?.resolve('steam-input-layout-editor','handheld',slot);
              const ok=!!tier&&tier.owner==={{SteamCef.JsString(Id)}}&&tier.version===1
                &&Object.keys(tier.mappings).length==={{presentation.ControllerImages.Count}}
                &&resolved===tier.mappings[slot]&&resolved.startsWith('data:image/')
                &&tier.resolve('store','handheld',slot)===null
                &&tier.resolve('steam-input-layout-editor','external',slot)===null
                &&tier.ownedNodeCount===0&&tier.uiMutationCount===0;
              return JSON.stringify({ok,resolvedReference:!!resolved,mappingCount:Object.keys(tier?.mappings??{}).length});
            }catch(error){return JSON.stringify({ok:false,error:String(error)}); } })()
            """;
    }
}

internal sealed class SteamInputInlineValveSvgGlyphPatch(SteamInputGlyphDeliveryState state)
    : SteamInputGlyphTierPatch(state)
{
    internal const string PatchId = "wsgm.steam-input.inline-svg";

    public override string Id => PatchId;

    public override int Version => 1;

    public override string ResourceKey => "wsgm.steam-input.inline-svg-map";

    protected override string Namespace => "__wsgmSteamInputInlineSvg_77de2b34";

    protected override string Fingerprint =>
        "steam-input-inline-svg-v1:exact-component-shape+path-sha256";

    protected override bool HasMappings(SteamInputGlyphPresentation presentation) =>
        presentation.InlineMappings.Count > 0;

    protected override string BuildProbeExpression(SteamInputGlyphPresentation presentation) =>
        "(()=>JSON.stringify({ok:false,error:'no audited inline Valve path mapping in catalog'}))()";

    protected override string BuildMappingLiteral(SteamInputGlyphPresentation presentation) =>
        ObjectLiteral(presentation.InlineMappings.ToDictionary(
            mapping => mapping.ValvePathSha256,
            mapping => mapping.Asset.DataUri,
            StringComparer.Ordinal));

    protected override string BuildResolverExpression() =>
        "(route,subject,pathHash)=>selector.matches(route,subject)&&Object.hasOwn(mappings,pathHash)?mappings[pathHash]:null";

    protected override string BuildVerificationExpression(SteamInputGlyphPresentation presentation) =>
        "(()=>JSON.stringify({ok:false,error:'inline tier is not live-approved'}))()";
}

internal sealed class SteamInputCapabilityHidingGlyphPatch(SteamInputGlyphDeliveryState state)
    : SteamInputGlyphTierPatch(state)
{
    internal const string PatchId = "wsgm.steam-input.capability-hiding";

    public override string Id => PatchId;

    public override int Version => 1;

    public override string ResourceKey => "wsgm.steam-input.capability-visibility-map";

    protected override string Namespace => "__wsgmSteamInputCapabilityVisibility_4eb27aa1";

    protected override string Fingerprint =>
        "steam-input-capability-hiding-v1:exact-semantic-control-set";

    protected override bool HasMappings(SteamInputGlyphPresentation presentation) =>
        presentation.AbsentControls.Count > 0;

    protected override string BuildProbeExpression(SteamInputGlyphPresentation presentation) =>
        "(()=>JSON.stringify({ok:false,error:'exact capability control-set fingerprint unavailable'}))()";

    protected override string BuildMappingLiteral(SteamInputGlyphPresentation presentation) =>
        ObjectLiteral(presentation.AbsentControls.ToDictionary(
            control => control.ToString(),
            _ => "absent",
            StringComparer.Ordinal));

    protected override string BuildResolverExpression() =>
        "(route,subject,control)=>selector.matches(route,subject)&&mappings[control]==='absent'";

    protected override string BuildVerificationExpression(SteamInputGlyphPresentation presentation) =>
        "(()=>JSON.stringify({ok:false,error:'capability tier is not live-approved'}))()";
}
