using System.Text;
using System.Text.Json;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Serialization;
using WSGM.Tests.Builders;

namespace WSGM.Tests.Core;

public sealed class SteamGlyphCssTests
{
    [Fact]
    public void NativeArtworkStillHidesAbsentControlsAndRestoresArtworkWhenSelected()
    {
        SteamInputGlyphDeliveryState state = new();
        var profile = ImportProfile();
        state.Update(profile, true);
        Assert.NotNull(state.Current);
        Assert.Empty(state.Current.StableResources);
        Assert.Empty(state.Current.ControllerImages);
        Assert.Contains(GlyphControlId.LeftTrackpad, state.Current.AbsentControls);
        Assert.Contains("display: none", SteamGlyphCss.Build(state.Current, true), StringComparison.Ordinal);
        state.Update(profile);
        Assert.NotEmpty(state.Current!.StableResources);
        state.Update(null);
        Assert.Null(state.Current);
    }

    [Fact]
    public void AbsentRearButtonsHideIndividualRowsWithoutHidingSharedSections()
    {
        SteamInputGlyphPresentation presentation = new("device", 1, [], [], [GlyphControlId.RearLeft2], []);
        var css = SteamGlyphCss.Build(presentation, true);
        Assert.Contains($".{SteamGlyphCss.ControlRowClass}:has(img[src=\"/steaminputglyphs/sd_l5.svg\"])", css,
            StringComparison.Ordinal);
        Assert.DoesNotContain(SteamGlyphCss.ControlSectionClass, css, StringComparison.Ordinal);
        Assert.DoesNotContain(SteamGlyphCss.DialogSectionClass, css, StringComparison.Ordinal);
        Assert.DoesNotContain("sd_l4.svg", css, StringComparison.Ordinal);
        Assert.DoesNotContain("sd_r5.svg", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsentControlsAreHiddenInTheControllerDiagramPickersToo()
    {
        // The gyro "select gyro button(s)" picker is a grid of icon checkboxes rather than binding
        // rows, so none of the row or section anchors reach it. A Claw was still offered both
        // trackpads, L5/R5 and both stick-touch controls there.
        SteamInputGlyphPresentation presentation = new("device", 1, [], [],
            [GlyphControlId.LeftStickTouch, GlyphControlId.RearLeft2, GlyphControlId.LeftTrackpad], []);

        var css = SteamGlyphCss.Build(presentation, true);

        Assert.Contains(
            $".{SteamGlyphCss.DialogCheckboxClass}:has(img[src=\"/steaminputglyphs/shared_lstick_touch.svg\"])",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            $".{SteamGlyphCss.DialogCheckboxClass}:has(img[src=\"/steaminputglyphs/sd_l5.svg\"])",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            $".{SteamGlyphCss.DialogCheckboxClass}:has(img[src=\"/steaminputglyphs/sd_ltrackpad_click.svg\"])",
            css,
            StringComparison.Ordinal);

        // A control the device has keeps its checkbox.
        Assert.DoesNotContain("shared_rstick_touch.svg", css, StringComparison.Ordinal);
        Assert.DoesNotContain("sd_r5.svg", css, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInlineDeckSilhouetteIsPaintedOverWithTheFullControllerArtwork()
    {
        // The controller-diagram pickers draw the Deck as an inline svg of fifty-odd paths, so a
        // content: override cannot reach it and its class is a generated hash. It is identified by
        // its view box, hidden path by path, and the device's full-controller image painted on the
        // box that is left, through the custom property the controller images already publish.
        var presentation = SteamInputGlyphPresentation.Create(ImportProfile());
        Assert.NotNull(presentation);

        var css = SteamGlyphCss.Build(presentation, false);

        var diagram = $"svg[viewBox=\"{SteamGlyphCss.DeckDiagramViewBox}\"]";
        Assert.Contains(diagram + " > * {\n  visibility: hidden;", css, StringComparison.Ordinal);
        Assert.Contains(
            diagram + " {\n  background-image: var(--wsgm-controller-full-image);",
            css,
            StringComparison.Ordinal);
        Assert.Contains("--wsgm-controller-full-image: url(\"data:image/svg+xml;base64,", css, StringComparison.Ordinal);

        // No full-controller artwork, no override: the Deck stays rather than turning into nothing.
        SteamInputGlyphPresentation bare = new("device", 1, [], [], [], []);
        Assert.DoesNotContain(SteamGlyphCss.DeckDiagramViewBox, SteamGlyphCss.Build(bare, true), StringComparison.Ordinal);
    }

    [Fact]
    public void ASelectedControlLightsItsHighlightOverTheControllerDiagram()
    {
        // Valve lights a control by filling its hit region, one of the transparent paths in the
        // silhouette, and the region has no name: it is identified by its position among the
        // children. Each lit region publishes one custom property per highlighted control and the
        // base rule stacks every such property over the full-controller image, so several
        // selections light several overlays without a rule per combination.
        SteamInputGlyphAssetReference artwork = new("data:image/svg+xml;base64,QQ==");
        SteamInputGlyphPresentation presentation = new(
            "device",
            1,
            [],
            [new SteamInputGlyphControllerImageMapping("full", artwork)],
            [],
            [
                new SteamInputGlyphHighlightMapping(GlyphControlId.FaceSouth, artwork),
                new SteamInputGlyphHighlightMapping(GlyphControlId.DpadUp, artwork),
                new SteamInputGlyphHighlightMapping(GlyphControlId.DpadLeft, artwork)
            ]);

        var css = SteamGlyphCss.Build(presentation, false);

        var diagram = $"svg[viewBox=\"{SteamGlyphCss.DeckDiagramViewBox}\"]";

        // A is the 36th child; the whole d-pad is the 47th and lights every direction it has art for.
        Assert.Contains(
            diagram + ":has(> :nth-child(36):not([fill=\"transparent\"])) {\n  --wsgm-diagram-facesouth: url(",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            diagram + ":has(> :nth-child(47):not([fill=\"transparent\"])) {\n  --wsgm-diagram-dpadup: url(",
            css,
            StringComparison.Ordinal);
        Assert.Contains("--wsgm-diagram-dpadleft: url(", css, StringComparison.Ordinal);
        Assert.DoesNotContain("--wsgm-diagram-dpaddown", css, StringComparison.Ordinal);
        Assert.Contains(
            "background-image: var(--wsgm-diagram-dpadup, none), var(--wsgm-diagram-dpadleft, none), "
            + "var(--wsgm-diagram-facesouth, none), var(--wsgm-controller-full-image);",
            css,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ImportedProfileProducesOnlyCatalogOwnedExactMappings()
    {
        var profile = ImportProfile();

        var presentation = SteamInputGlyphPresentation.Create(profile);

        Assert.NotNull(presentation);
        Assert.Equal("example.handheld", presentation.ProfileId);
        Assert.All(presentation.StableResources, mapping =>
        {
            Assert.Equal(GlyphControlId.FaceSouth, mapping.Control);
            Assert.StartsWith("/steaminputglyphs/", mapping.ValvePath, StringComparison.Ordinal);
            Assert.StartsWith(
                "data:image/svg+xml;base64,",
                mapping.Asset.DataUri,
                StringComparison.Ordinal);
        });
        Assert.Equal("full", Assert.Single(presentation.ControllerImages).Slot);

        // Absence is the default: the profile declares what the device HAS, and everything it does
        // not name is absent. Requiring each missing control to be declared meant the one nobody
        // remembered to list was the one left on screen.
        Assert.Contains(GlyphControlId.LeftTrackpad, presentation.AbsentControls);
        Assert.DoesNotContain(GlyphControlId.FaceSouth, presentation.AbsentControls);
    }

    [Fact]
    public void EveryValveResourceForAControlIsOverriddenWithTheOnePluginAsset()
    {
        var presentation = Presentation();

        var css = SteamGlyphCss.Build(presentation, false);

        // The south face button is drawn from several Valve resources depending on the controller
        // family; all of them have to resolve to the handheld's own artwork.
        Assert.Contains("img[src=\"/steaminputglyphs/shared_button_a.svg\"]", css, StringComparison.Ordinal);
        Assert.Contains("img[src=\"/steaminputglyphs/shared_color_button_a.svg\"]", css, StringComparison.Ordinal);
        Assert.Contains("img[src=\"/steaminputglyphs/ps_button_x.svg\"]", css, StringComparison.Ordinal);
        Assert.Contains("content: url(\"data:image/svg+xml;base64,", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAliasedLogicalControlIsPresentWhenItsPhysicalArtworkExists()
    {
        var presentation = SteamInputGlyphPresentation.Create(
            ImportProfile(aliasEastToSouth: true));

        Assert.NotNull(presentation);
        Assert.DoesNotContain(GlyphControlId.FaceEast, presentation.AbsentControls);
        Assert.Contains(
            presentation.StableResources,
            mapping => mapping.Control is GlyphControlId.FaceEast);
    }

    [Fact]
    public void OnlyControlsThePluginSuppliesAppearInTheStylesheet()
    {
        var presentation = Presentation();

        var css = SteamGlyphCss.Build(presentation, false);

        // WSGM ships no artwork of its own, so a control the profile does not supply must simply be
        // absent from the sheet and keep Valve's own glyph.
        Assert.DoesNotContain("shared_dpad_up.svg", css, StringComparison.Ordinal);
        Assert.DoesNotContain("xbox_button_start.svg", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ControllerImagesArePublishedAsCustomProperties()
    {
        var presentation = Presentation();

        var css = SteamGlyphCss.Build(presentation, false);

        Assert.Contains("--wsgm-controller-full-image: url(\"data:", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentControlHidesItsRowOnlyWhenHidingIsRequested()
    {
        var presentation = Presentation();

        var hidden = SteamGlyphCss.Build(presentation, true);
        var shown = SteamGlyphCss.Build(presentation, false);

        // The glyph names are the ones the client actually draws. The table previously named
        // sd_ltrackpad_swipe.svg, which this build renders nowhere, so every hide rule matched
        // nothing and a device with no trackpads still showed both trackpad sections.
        Assert.Contains("sd_ltrackpad_up.svg", hidden, StringComparison.Ordinal);
        Assert.Contains("display: none;", hidden, StringComparison.Ordinal);

        // Anchored on the section container, so a control the device lacks takes its heading and
        // its bindings with it rather than leaving an empty group behind.
        Assert.Contains($".{SteamGlyphCss.ControlSectionClass}:has(", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("sd_ltrackpad_up.svg", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInlineSteamLogoIsReplacedFromTheGuideArtwork()
    {
        var presentation = Presentation(true);

        var css = SteamGlyphCss.Build(presentation, false);

        // Valve draws this one as an inline path, so a content override cannot reach it: the inner
        // svg is hidden and the container is painted instead.
        Assert.Contains(SteamGlyphCss.SteamLogoPathData, css, StringComparison.Ordinal);
        Assert.Contains($".{SteamGlyphCss.InlineLogoContainerClass}", css, StringComparison.Ordinal);
        Assert.Contains("background: url(\"data:", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AProfileWithNothingToDrawProducesNoStylesheetAtAll()
    {
        SteamInputGlyphPresentation empty = new("example.handheld", 1, [], [], [], []);

        Assert.Equal(string.Empty, SteamGlyphCss.Build(empty, true));
    }

    [Fact]
    public void OnlyABoundedDataUriMayReachTheStylesheet()
    {
        Assert.Throws<ArgumentException>(() =>
            SteamGlyphCss.Url("https://example.invalid/glyph.svg"));
        Assert.Throws<ArgumentException>(() =>
            SteamGlyphCss.Url("data:image/svg+xml,<svg onload=\"alert(1)\"/>"));
        Assert.Throws<ArgumentException>(() =>
            SteamGlyphCss.Url("data:image/svg+xml;base64,AAA\") ; body { display:none } a{content:url(\""));
    }

    [Fact]
    public void AttributeValuesAreEscapedForTheSelector()
    {
        Assert.Equal("a\\\"b", SteamGlyphCss.Attribute("a\"b"));
        Assert.Equal(@"a\\b", SteamGlyphCss.Attribute(@"a\b"));
    }

    private static SteamInputGlyphPresentation Presentation(bool guide = false)
    {
        var presentation =
            SteamInputGlyphPresentation.Create(ImportProfile(guide));
        Assert.NotNull(presentation);
        return presentation;
    }

    private static ImportedGlyphProfile ImportProfile(
        bool guide = false,
        bool aliasEastToSouth = false)
    {
        var controlSvg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\">"
            + "<path d=\"M 0 0 L 64 64 Z\"/></svg>");
        var guideSvg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\">"
            + "<path d=\"M 0 0 L 32 64 Z\"/></svg>");
        var controllerSvg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 128 64\">"
            + "<path d=\"M 0 0 L 128 64 Z\"/></svg>");
        const string controlId = "face-south";
        const string guideId = "guide";
        const string controllerId = "controller-full";
        var controlAsset = Asset(
            controlId,
            GlyphAssetRole.Control,
            new GlyphViewBox(0, 0, 64, 64));
        var guideAsset = Asset(
            guideId,
            GlyphAssetRole.Control,
            new GlyphViewBox(0, 0, 64, 64));
        var controllerAsset = Asset(
            controllerId,
            GlyphAssetRole.FullController,
            new GlyphViewBox(0, 0, 128, 64));
        List<GlyphControlMapping> controls =
        [
            new()
            {
                Control = GlyphControlId.FaceSouth,
                Presence = GlyphControlPresence.Present,
                AssetId = controlId
            },
            new()
            {
                Control = GlyphControlId.LeftTrackpad,
                Presence = GlyphControlPresence.Absent
            }
        ];
        if (guide)
        {
            controls.Add(new GlyphControlMapping
            {
                Control = GlyphControlId.Guide,
                Presence = GlyphControlPresence.Present,
                AssetId = guideId
            });
        }

        GlyphProfileManifest manifest = new()
        {
            SchemaVersion = GlyphProfileLimits.CurrentSchemaVersion,
            ProfileId = "example.handheld",
            DisplayName = "Example handheld",
            Revision = 4,
            ExactDeviceIds = ["example-device"],
            SourceRevision = "revision-1",
            NoticePath = "THIRD_PARTY_NOTICES.md",
            Assets = guide
                ? [controlAsset, guideAsset, controllerAsset]
                : [controlAsset, controllerAsset],
            ControllerImages = new GlyphControllerImages { FullAssetId = controllerId },
            Controls = controls,
            Aliases = aliasEastToSouth
                ?
                [
                    new GlyphControlAlias
                    {
                        LogicalControl = GlyphControlId.FaceEast,
                        PhysicalControl = GlyphControlId.FaceSouth
                    }
                ]
                : []
        };
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            [GlyphPackageLayout.ProfileManifest(manifest.ProfileId)] =
                JsonSerializer.SerializeToUtf8Bytes(
                    manifest,
                    DeviceJsonContext.Default.GlyphProfileManifest),
            [manifest.NoticePath] = [.. "Example glyph notice\n"u8],
            [GlyphPackageLayout.Asset(controlId, GlyphAssetFormat.Svg)] = controlSvg,
            [GlyphPackageLayout.Asset(guideId, GlyphAssetFormat.Svg)] = guideSvg,
            [GlyphPackageLayout.Asset(controllerId, GlyphAssetFormat.Svg)] = controllerSvg
        };
        var result = GlyphPackageImporter.Import(
            new GlyphTestPackageSource(manifest.ProfileId, files));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return Assert.Single(result.Profiles);
    }

    private static GlyphAssetEntry Asset(
        string assetId,
        GlyphAssetRole role,
        GlyphViewBox viewBox)
    {
        return new GlyphAssetEntry
        {
            AssetId = assetId,
            Format = GlyphAssetFormat.Svg,
            Role = role,
            ViewBox = viewBox
        };
    }
}
