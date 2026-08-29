# WSGM 2.0 physical handheld glyph integration

Status: lean implementation plan, rewritten 2026-08-28

Initial artwork source: `victor-borges/handheld-controller-glyphs` commit
`46792aadf3b104efec1c5240ba414d2c0bf84127`

## Goal

Steam Input configuration, relevant Steam Gamepad UI prompts, and WSGM's own handheld surfaces must
show the physical controller in the user's hands. This presentation is independent of whether the
current virtual target is Steam Deck Composite, Xbox 360, or DualShock 4.

Glyph support is a required 2.0 feature. The implementation should be a small static-data path, not
a second plugin platform or a general Steam theming engine.

## Ownership

| Owner | Responsibility |
| --- | --- |
| Device Plugin | Static artwork, physical control map, device/profile ID, source and license notice |
| WSGM glyph service | Validate, normalize, cache, select, and render the active package |
| WSGM Steam adapter | Current Steam routes/selectors, asset delivery, apply/remove, native fallback |
| WSGM Avalonia surfaces | Controller diagrams, preview, input test, OEM rows, navigation hints |

A plugin may provide images and declarative metadata. It may not provide XAML, HTML, JavaScript,
runtime CSS, URLs, Steam selectors, filesystem commands, or executable UI.

This restriction keeps presentation consistent and prevents every device package from carrying a
different fragile Steam integration. It is an ownership boundary, not an attempt to sandbox trusted
plugin code.

## Physical presentation is not controller identity

WSGM tracks four independent concepts:

| Concept | Example | Owner |
| --- | --- | --- |
| Physical profile | MSI Claw A2VM artwork and M1/M2 labels | Active plugin |
| Virtual target | Steam Deck, Xbox 360, DualShock 4 | WSGM controller policy |
| Steam Input binding | User layout | Steam |
| Game-rendered prompts | Whatever the game chooses | Game |

Changing the virtual target or per-application target does not silently change the physical
handheld artwork. WSGM does not promise to modify prompts rendered inside a game.

## Plugin glyph package

The package is a directory inside the sole installed Device Plugin. A minimal manifest contains:

- Stable profile ID and display name.
- Source project/revision and license-notice path.
- Full-controller artwork and optional left/right diagrams.
- Individual face, D-pad, stick, shoulder, trigger, guide, menu, view, QAM, OEM, rear-button, and
  touch glyphs that physically exist.
- Logical control ID to asset/label mapping.
- Present/absent controls such as trackpads, stick touch, L5/R5, or gyro.
- Optional physical-label aliases.

Files must remain beneath the package directory. Validation checks manifest shape, known logical
control IDs, supported image formats, dimensions, file and total size, and duplicate/missing
references. Asset hashes may be used for cache invalidation; they are not a trust or promotion
system.

Malformed assets disable the glyph feature and retain native/generic presentation. They do not
disable the hardware plugin.

## Selection

The Device > Controller page exposes:

- **Automatic**: use the exact profile from the active plugin.
- **Native Steam glyphs**: disable WSGM's Steam glyph patch.
- **Manual profile**: select an installed/reviewed profile for diagnosis.

Automatic selection never guesses another model. An unknown, absent, or invalid profile falls back
to native Steam glyphs and WSGM's existing generic controller prompts.

Turning off only controller management does not remove the physical profile. Turning off Device
Integration does.

## Steam integration

The glyph patch is one built-in client of the persistent Steam UI host. It targets only
controller-oriented Big Picture/Gamepad UI contexts:

- Steam Input controller selection and layout/configuration routes.
- Full and split controller diagrams.
- Binding rows and prompts backed by recognized Steam glyph resources.
- Relevant Big Picture/QAM/Main Menu prompts where the physical glyph is semantically correct.

It never injects into store, community, browser, or game content.

### Direct patch model

The patch keeps a short lifecycle:

1. Confirm an approved Steam target and controller route.
2. Check the small set of selectors/resources needed by the active profile.
3. Deliver only that profile's bounded assets into the current CEF context.
4. Add one WSGM-owned style/resource namespace.
5. Verify the expected diagram/resources changed.
6. Remove the namespace and revoke context-local assets on disable or context loss.

Selectors are grouped only where their failure behavior differs:

- Stable Steam glyph-resource mappings.
- Controller diagram containers.
- Exact inline Valve SVG replacements.
- Capability hiding for positively identified unsupported controls.

If one group stops matching after a Steam update, disable that group and retain the others. Selector
ambiguity leaves native content untouched. The feature does not need a generic four-tier policy
engine, external patch packages, or a universal mutation framework.

### Asset delivery

Prefer context-local blob or data URLs created through the existing CDP/bootstrap connection. Do
not create a general unauthenticated asset server. If current Steam CSP makes a local route
necessary, it must be private to the Steam host, random for the process lifetime, and able to serve
only the already-loaded active profile.

### Lifecycle and coexistence

- Steam/`steamwebhelper` restart or context replacement reapplies the desired profile.
- Device/profile changes update the next healthy context without restarting Steam.
- Device Integration off, Native Steam selection, or WSGM exit removes WSGM-owned resources.
- Repeated route changes do not accumulate styles, observers, URLs, or memory.
- Glyph failure does not affect QAM, Wi-Fi, RTSS, or other CEF features.
- If the same handheld theme is active through CSS Loader/Decky, WSGM reports the conflict and does
  not apply its copy. It never removes the external theme.

## Avalonia integration

WSGM's existing generic `GlyphIcon` prompts remain the fallback. The active plugin assets are
converted at build/pack time or on trusted package load into an Avalonia-safe representation:

- Normalize the limited supported SVG shapes; or
- Rasterize reviewed outputs at the required sizes.

Do not add a reflection-heavy SVG dependency to the NativeAOT application and do not pass arbitrary
SVG markup directly into Avalonia controls.

Required consumers are:

- Device controller overview and graphical profile preview.
- Live input-test surface.
- OEM-button assignment rows.
- Controller navigation hints where physical labels matter.
- Glyph diagnostics and selected-profile status.

## Upstream import

The initial Claw package may import artwork from the pinned Handheld Controller Glyphs revision. An
update is a normal reviewed source change:

1. Choose an explicit upstream revision.
2. Review changed artwork and control mappings.
3. Copy only the profiles/assets the plugin actually ships.
4. Regenerate safe Avalonia assets.
5. Update the package license notice and source revision.
6. Run rendering/Steam fixtures and visually check affected hardware.

There is no runtime upstream sync, central promotion database, immutable evidence ledger, or
mandatory asset-provenance workflow beyond the source revision and required license notices.

## MSI Claw A2VM profile

The upstream `MSI Claw` profile is the starting point, not automatic proof of an A2VM match. Verify
on the reference device that:

- Full and split diagrams match the A2VM body and control layout.
- MSI Center/OEM1 and Quick Settings/OEM2 are on the correct front sides.
- M1 is the left rear control and M2 is the right rear control.
- Unsupported trackpads and additional rear controls are absent without hiding valid controls.
- View/Menu/Guide/QAM aliases agree with actual OEM events and the firmware-generated QAM chord.
- Scaling remains sharp and unclipped at supported display scales.

If the upstream artwork is materially wrong, the plugin ships a distinct `msi.claw-a2vm` profile.

## Diagnostics

The Device page reports:

- Selected profile and whether selection is automatic/manual/native.
- Package/source revision.
- Validation failure, if any.
- Steam patch active/incompatible/native-fallback state.
- CSS Loader conflict.
- Number and total size of active assets.

This is enough to diagnose the feature. Per-selector fingerprints may appear in logs when useful,
but they do not require a user-facing evidence or health taxonomy.

## Acceptance

- Steam Input/controller routes show the correct physical diagrams and controls.
- Face, shoulder, stick, D-pad, guide, OEM, and rear-button labels match the active device.
- Steam Deck, Xbox 360, and DualShock 4 target changes leave the physical profile correct.
- WSGM preview, input test, OEM rows, and navigation hints use the same active map.
- Unrelated Steam pages and game-rendered prompts are untouched.
- Steam restart, route churn, suspend/resume, profile changes, and disable/enable leave no stale
  resources.
- Native Valve/generic WSGM presentation remains usable after any missing asset, invalid package,
  selector change, or external-theme conflict.
- The A2VM profile passes physical visual review, including OEM sides and M1/M2 orientation.
- Steady state does no continuous full-DOM polling and repeated context recreation does not leak.

## Implementation order

1. Finalize the small plugin glyph manifest and validator.
2. Accept the A2VM asset package and produce Avalonia-safe outputs.
3. Complete WSGM selection, preview, input-test, and fallback behavior.
4. Complete stable Steam resource mapping, then diagrams, inline mappings, and capability hiding.
5. Validate lifecycle, CSS Loader coexistence, scaling, performance, and the A2VM visual matrix.

The feature is complete when Steam and WSGM accurately present the one active handheld, preserve
native fallback, and require plugin authors to supply only static artwork plus a logical control map.
