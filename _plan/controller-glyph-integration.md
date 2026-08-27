# WSGM 2.0 handheld controller glyph integration

## Status

This document defines the planned integration of [victor-borges/handheld-controller-glyphs](https://github.com/victor-borges/handheld-controller-glyphs) into WSGM 2.0.

The primary goal is to make Steam's own Gamepad UI and Steam Input configuration pages present the physical handheld correctly, as the upstream Decky/CSS Loader theme does on SteamOS. Reuse in WSGM's Avalonia surfaces is a secondary benefit.

This is a design and integration plan. The upstream asset snapshot is not yet vendored by this document.

## Locked decisions

- WSGM will integrate the project as a pinned, audited third-party glyph and controller-artwork source.
- The first-class consumer is the Steam CEF Gamepad UI, especially Steam Input controller configuration and layout pages.
- WSGM will provide injection and lifecycle through its own versioned Steam UI patch host; users do not need Decky Loader or CSS Loader.
- The WSGM overlay will reuse the same semantic glyph catalog where device-aware prompts or diagrams improve clarity.
- A device plugin may select a reviewed WSGM glyph profile by stable ID. It may not inject CSS, JavaScript, XAML, SVG, URLs, or arbitrary assets.
- The glyph profile describes the physical handheld. It does not change the HIDMaestro virtual target, Steam Input mapping, SDL identity, XInput identity, or game-visible controller type.
- Automatic selection is exact and fail-closed. WSGM does not guess a visually similar handheld when no verified profile exists.
- Native Steam rendering remains the fallback. A missing profile, incompatible Steam build, failed patch, disabled Device Integration, or explicit user opt-out leaves Valve's glyphs untouched.
- Glyph patch failure is independent from Wi-Fi restoration, QAM restoration, RTSS, and all other CEF patches.
- WSGM never downloads or updates glyph assets at runtime.

## Upstream baseline

The initial review baseline is upstream commit `46792aadf3b104efec1c5240ba414d2c0bf84127`, whose theme manifest reports version `v2.1`.

At that revision the repository contains:

- Device-specific full-controller SVGs and left/right controller PNGs.
- Individual face, shoulder, D-pad, stick, guide, QAM, back-button, trackpad, and related glyphs.
- MSI Claw and MSI Claw A8 profiles, including MSI Center, QAM, M1, and M2 presentation.
- Profiles for multiple ASUS, AYANEO, GPD, Lenovo, MSI, ONEXPLAYER, and other handheld families.
- Shared CSS that replaces Steam Input glyph URLs and controller imagery.
- Device capability rules that hide controls which the selected handheld lacks.
- Optional View/Menu/Guide/QAM presentation swaps for devices whose physical labels differ from Valve's default layout.

The project is MIT licensed. WSGM must preserve its license notice and attribution and audit the provenance notes for artwork credited to earlier themes before redistribution.

## Why WSGM owns the runtime integration

The upstream project is intentionally a Decky CSS Loader theme. Its `theme.json` selects a device CSS file and shared CSS modules, and its asset paths use CSS Loader's `/themes_custom/...` route.

Some upstream selectors are relatively stable, such as exact `/steaminputglyphs/...` image URLs. Others depend on hashed Steam class names, inline SVG path data, `:has()`, and the current Steam DOM. Copying the theme into WSGM as an opaque stylesheet would therefore make one fragile dependency responsible for every surface.

WSGM separates stable device knowledge from the Steam-build-specific adapter:

| Layer | Owner | Responsibility |
| --- | --- | --- |
| Upstream snapshot | Third party, pinned by WSGM | Source artwork, mappings, and provenance |
| Glyph catalog | WSGM | Stable profile IDs, semantic controls, features, and approved assets |
| Steam glyph patch | WSGM Steam UI host | Current selectors, route probes, injection, verification, and removal |
| Avalonia glyph service | WSGM | Safe rendering in overlay and other first-party surfaces |
| Device selection | Device plugin | Select one reviewed profile ID for an exactly matched device |

This preserves the upstream result while letting WSGM update Steam selectors independently from artwork and device mappings.

## Presentation model

### Physical presentation versus virtual target

WSGM must track these as different concepts:

| Concept | Example on the Claw | Authority |
| --- | --- | --- |
| Physical glyph profile | `msi.claw` | Exact active device plugin plus user override |
| Virtual controller target | Steam Deck Composite, Xbox 360, or DualShock 4 | WSGM controller policy |
| Steam Input binding | User's Steam Input layout | Steam |
| Game-rendered prompts | Whatever the game chooses to display | Game |

The Steam Input UI may show the Claw body, MSI buttons, and M1/M2 labels while its configured or emulated target is otherwise Xbox, DualShock, or Steam Deck. This is a visual clarification of the hardware in the user's hands, not an input remap.

WSGM cannot promise to replace prompts rendered by a game outside Steam's CEF UI.

### Semantic profile

A WSGM-owned profile needs stable semantic fields rather than copied CSS variables alone:

- Stable profile ID and display name.
- Upstream source revision and asset provenance.
- Full, left, and right controller artwork.
- Face, D-pad, stick, shoulder, trigger, guide, menu, view, QAM, back-button, and touch glyphs where present.
- Logical-to-physical presentation aliases such as M1/M2.
- Supported and absent controls such as trackpads, L5/R5, or gyro.
- Optional presentation swaps required to match printed button legends.
- Asset hashes and format metadata.
- Verification state for each exact WSGM device definition.

The catalog is versioned independently from the runtime plugin ABI and independently from the Steam selector patches.

### Device plugin boundary

The device definition may return a nullable reviewed profile ID as display metadata. WSGM resolves it against its own catalog.

Rules:

- Unknown IDs are ignored and reported; they never become paths or URLs.
- A plugin package cannot bundle a replacement stylesheet or override the catalog.
- A shared profile can be selected by multiple exact device definitions only after each physical layout has been verified.
- Device Lab may recommend a profile from exact board/product evidence, but generated scaffolds leave the field unset until the artwork and logical button positions are confirmed.
- Glyph availability never determines whether a hardware capability is enabled.

## Steam CEF integration

### Patch identity and scope

The integration is a dedicated `ISteamUiPatch`, conceptually `SteamInputHandheldGlyphPatch`. It uses the robust CEF host defined by the main 2.0 design and has its own probe, apply, verify, remove, compatibility fingerprint, diagnostics, and kill switch.

The patch targets Steam's controller-oriented Gamepad UI contexts, including:

- Steam Input controller selection and layout/configuration routes.
- Full-controller and left/right controller diagrams.
- Binding rows and button prompts rendered from Steam Input glyph URLs.
- Relevant Big Picture, Quick Access, and Main Menu controller prompts where the same semantic glyph is appropriate.

It must not inject into arbitrary web pages, store/community web content, desktop Chromium windows, or games.

### Patch tiers

Steam selectors have different stability and should not share one health result:

| Tier | Examples | Failure policy |
| --- | --- | --- |
| Stable resource mapping | Known `/steaminputglyphs/...` image sources | Keep active if verified |
| Structural controller UI | Controller image containers and left/right settings images | Disable only this tier if shape changed |
| Inline Valve SVG matching | Exact path or component-shape matches | Disable only the exact mapping if no longer unique |
| Capability hiding | Trackpads, back buttons, gyro, or model-only controls | Apply only after the expected control set is positively identified |

No selector is considered compatible merely because stylesheet injection succeeded. Verification must observe expected unique matches and resulting asset references.

### Asset delivery

WSGM should not create the CSS Loader `/themes_custom` route and should not add an unauthenticated localhost asset server solely for glyphs.

The implementation experiment should evaluate a selected-profile payload delivered through the existing authenticated CDP session:

1. Load only the reviewed assets needed by the active profile from WSGM's bundled catalog.
2. Enforce per-file, per-profile, dimension, and total-byte limits before injection.
3. Create context-local blob URLs or bounded data URLs through the WSGM bootstrap.
4. Apply WSGM-owned semantic CSS variables and current selector rules in one identified style element.
5. Revoke context-local URLs and remove the style element on patch removal or context replacement.

If this is not reliable under Steam's current CSP and context lifecycle, the fallback experiment may use an authenticated, random-capability local asset route owned by the Steam UI host. A general unauthenticated file server is not acceptable.

### Lifecycle

The selected profile belongs to the long-lived Device Integration state, while every CEF document is transient.

- Device activation or profile change updates the desired presentation.
- Steam or `steamwebhelper` restart causes normal patch rediscovery and reapplication.
- Route navigation and JavaScript-context replacement recreate only the context-local glyph resources.
- Suspending the device does not discard its selected profile.
- Turning Device Integration off removes the WSGM style and restores native Steam rendering.
- WSGM exit removes what can be removed synchronously; Steam context destruction is also a valid cleanup boundary.
- Disabling only virtual-controller management does not remove the physical-device glyph profile.

### Selection and controls

Default behavior is `Automatic`:

- Use the exact verified glyph profile advertised by the active device definition.
- Use native Steam glyphs if there is no exact verified profile.

The WSGM overlay's Device > Controller section should expose:

- `Automatic`.
- `Native Steam glyphs`.
- A manual selection from reviewed catalog profiles as a diagnostic/compatibility override.

The current selection, upstream asset revision, patch health, selector fingerprint, and conflicting-theme status belong in Device diagnostics. This is device presentation and therefore does not move to the WSGM Settings window.

### Coexistence

WSGM owns one style element and one namespace marker per CEF context. It removes only those resources.

If the same Handheld Controller Glyphs theme is already active through Decky/CSS Loader, WSGM should detect its stylesheet or resolved asset paths, report the conflict, and leave the WSGM glyph patch inactive. It must not remove or rewrite the external theme.

Other CEF patches remain independent. A broken controller-image selector cannot disable QAM performance controls, Wi-Fi restoration, or other WSGM features.

## WSGM Avalonia integration

WSGM already has an AOT-safe `GlyphIcon` control for a small bundled Kenney prompt set. The handheld catalog should extend the presentation system rather than replace that reliable fallback.

The upstream SVG set cannot be assumed to fit the current simple `<path fill d>` parser. A build-time importer should inspect each approved SVG and either:

- Normalize supported geometry into a source-generated/Avalonia-safe representation; or
- Rasterize it at reviewed output sizes for WSGM surfaces.

Runtime parsing of arbitrary plugin SVG and a reflection-heavy general SVG dependency are both out of scope. CEF may use the original audited SVG assets, while Avalonia consumes generated safe assets from the same source revision.

Likely WSGM consumers are:

- Device controller overview and input test.
- OEM-button assignment rows.
- Controller navigation hints when the physical source is managed by WSGM.
- Device diagnostics and glyph-profile preview.

Generic Xbox, PlayStation, and Nintendo prompts remain available when no physical handheld presentation is applicable.

## Import and update workflow

The upstream repository should be vendored as a re-syncable, commit-pinned third-party snapshot or imported through a deterministic lock manifest.

An update is a reviewed build-time operation:

1. Fetch an explicit upstream commit.
2. Verify the repository identity and expected license.
3. Produce an asset inventory and hashes.
4. Compare added, removed, and changed profiles, artwork, CSS mappings, and provenance notes.
5. Convert upstream theme mappings into WSGM semantic catalog changes.
6. Regenerate Avalonia-safe assets.
7. Run catalog, rendering, and CEF fixture tests.
8. Visually accept affected devices before marking their profile verified.
9. Update third-party notices and the lock manifest in the same commit.

WSGM must not silently consume upstream `main`, execute upstream scripts, or treat a new marketing name as proof that artwork matches a WSGM device definition.

## MSI Claw 8 AI+ A2VM initial integration

The upstream `MSI Claw` profile is the initial candidate for the A2VM, but it is not automatically locked merely because the family name matches. Upstream also has a separate `MSI Claw A8` profile, demonstrating that family variants can differ.

The A2VM acceptance capture must confirm:

- Full-controller proportions and visible controls are correct enough for the Steam Input diagram.
- Left/right controller images correspond to the A2VM layout.
- MSI Center and QAM front-button glyphs are on the correct physical sides.
- M1 and M2 are assigned to the correct rear-button sides.
- Trackpads and unsupported additional back buttons are hidden without hiding valid controls.
- The visual View/Menu/Guide/QAM aliases agree with the plugin's captured logical OEM events, including the firmware-generated Win+G QAM button.

If any of those fail, WSGM creates a distinct `msi.claw-a2vm` reviewed profile instead of applying a misleading near match.

## Security and reliability rules

- Only bundled, hash-locked assets and WSGM-owned selector templates enter Steam CEF.
- Profile IDs never become filesystem paths without catalog resolution.
- No asset URL comes from a plugin or runtime network source.
- Each injected context has bounded asset bytes, style count, and mutation work.
- Mutation observers, if required, are scoped to the smallest stable controller subtree and are disconnected on removal.
- The patch must not poll at frame rate or rescan the entire document continuously.
- All apply/remove operations are idempotent.
- Native Steam UI is restored by removing the WSGM-owned style/resources; WSGM does not destructively edit Steam files.
- Selector ambiguity fails closed and emits a sanitized compatibility diagnostic.

## Acceptance matrix

### Steam UI behavior

- Open every supported Steam Input controller and layout route and confirm the selected handheld diagram is used.
- Confirm face, D-pad, stick, shoulder, trigger, guide, view, menu, QAM, and back-button glyphs wherever the route exposes them.
- Confirm unsupported physical controls are hidden only when the exact semantic element is identified.
- Confirm controller navigation, focus, localization, scaling, and animations remain native.
- Confirm no store, community, browser, or unrelated Steam page is modified.

### Lifecycle and isolation

- Start WSGM before Steam, after Steam, and while Steam restarts.
- Recreate `steamwebhelper`, switch Big Picture routes repeatedly, and suspend/resume Windows.
- Toggle Automatic, Native Steam, and a manual reviewed profile without stale styles or blob URLs.
- Turn Device Integration off and verify native glyphs return without restarting Steam.
- Disable only controller emulation and confirm the physical glyph theme stays active.
- Break one selector fixture and confirm other glyph tiers and every non-glyph CEF patch remain healthy.

### Identity correctness

- Switch HIDMaestro between Steam Deck, Xbox 360, and DualShock 4 and verify the physical handheld profile remains stable.
- Disable Device Integration and verify WSGM no longer claims a physical handheld profile.
- Use an external/unmanaged controller and verify WSGM does not mislabel it as the active handheld.
- Verify game-rendered prompts and device enumeration are not changed by the CEF patch.

### A2VM visual acceptance

- Compare every A2VM Steam Input diagram and labeled button against photographs and the physical unit.
- Verify the right-front firmware Win+G button is presented as QAM, not as a generic Windows or Xbox Guide action.
- Verify M1/M2 side and label orientation.
- Verify 100%, 125%, 150%, and handheld display scaling without blurry or clipped critical glyphs.

### Performance

- Measure initial injection, route-change reinjection, and steady-state CPU/memory.
- Confirm zero steady-state polling when the DOM is unchanged.
- Confirm the selected profile alone is delivered to each CEF context.
- Confirm repeated context recreation releases WSGM-owned resources.

## Implementation sequence

### Experiment 1: catalog and provenance

- Pin and inventory the reviewed upstream revision.
- Define semantic profile and lock-manifest schemas.
- Audit MIT notices and credited source artwork.
- Map `theme.json` and device CSS variables into generated catalog fixtures.
- Validate the MSI Claw candidate against the A2VM before assigning it automatically.

### Experiment 2: Steam Input patch

- Capture current Windows Steam Input routes and selector fingerprints.
- Implement stable resource mappings first.
- Add controller diagrams, inline SVG replacements, and capability hiding as separately verified tiers.
- Prototype bounded CDP-delivered blob/data assets.
- Validate cleanup, context recreation, and CSS Loader conflict detection.

### Experiment 3: WSGM surfaces

- Add a catalog-backed glyph service around the existing `GlyphIcon` fallback.
- Implement the build-time SVG normalization/rasterization path.
- Add profile preview, OEM glyphs, and controller input-test presentation to the Device overlay.

### Production hardening

- Add Steam-build fixtures and per-tier kill switches.
- Add catalog/update tooling and visual snapshot tests.
- Complete the full lifecycle, coexistence, performance, and A2VM acceptance matrix.
- Ship the upstream license, attribution, pinned revision, and asset inventory.

## References

- Handheld Controller Glyphs: https://github.com/victor-borges/handheld-controller-glyphs
- Reviewed upstream theme manifest: https://github.com/victor-borges/handheld-controller-glyphs/blob/46792aadf3b104efec1c5240ba414d2c0bf84127/theme.json
- Reviewed MSI Claw profile: https://github.com/victor-borges/handheld-controller-glyphs/blob/46792aadf3b104efec1c5240ba414d2c0bf84127/themes/msi/claw.css
- Main WSGM 2.0 design: [2.0-design.md](./2.0-design.md)
- Device plugin/tooling design: [device-plugin-system-and-tooling.md](./device-plugin-system-and-tooling.md)
- MSI Claw 8 AI+ A2VM plan: [claw-8-a2vm-plugin.md](./claw-8-a2vm-plugin.md)

## Completion statement

The integration is complete when Steam's own controller-oriented CEF surfaces automatically and accurately look like the verified physical handheld, survive normal Steam lifecycle events, fall back cleanly after incompatible updates, coexist without fighting CSS Loader, and share the same reviewed semantic glyph catalog with WSGM's overlay without granting device plugins any UI-injection authority.
