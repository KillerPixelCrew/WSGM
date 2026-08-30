# WSGM 2.0 plugin-driven settings page

Status: implementation plan, written 2026-08-30

## Goal

A Device Plugin declares its own settings and WSGM renders them. The plugin ships no UI: no XAML,
HTML, JavaScript, CSS, URLs, or executable presentation of any kind. It declares typed elements and
the sections they belong to, and WSGM draws, validates, stores, and localizes the result.

This closes a real hole. A plugin can express anything WSGM's semantic capability vocabulary already
names, but a device preference that is not a hardware control has nowhere to live at all.

## The boundary that shapes this page

WSGM Settings is for settings. It is never a device control surface. That rule is already enforced
and is already stated to the user in `Settings\Pages\DeviceOwnershipPage.axaml`:

> Hardware controls and profiles live in the overlay's Device destination. Settings owns only
> startup, diagnostics, and top-level ownership policy.

It appears nowhere in `_plan\` or `docs\`, which is why it is easy to propose violating. It must be
recorded as a numbered decision.

The line between the two is not a judgement call, and this is the rule to apply:

| | Plugin setting | Capability |
| --- | --- | --- |
| Effect of a change | Configures how the plugin behaves | Writes hardware state |
| Who stores the value | WSGM, in its configuration | The device |
| Where it renders | This Settings page | Overlay Device destination |
| Example | EC poll interval, suppress OEM software, restore lighting on resume | Power limit, fan duty, zone colour |

A control that writes to the device when the user moves it is a capability and does not belong here,
no matter how much it looks like a preference.

## What already exists

The declarative vocabulary is largely built and already wired — `DeviceCapabilityRouter` validates
every role and value-kind pairing, and `DeviceOverlayBridge.SectionFor` projects it:

| Element | Today |
| --- | --- |
| Toggle | `GenericToggle` + `CapabilityValueKind.Boolean` |
| Dropdown | `GenericChoice` + `Choices` |
| Slider | `GenericRange` + `Integer` with minimum, maximum, step, unit |
| Colour picker | `CapabilityValueKind.Color` |
| Button | `GenericAction` |
| Read-only value | `GenericReadOnly` |
| Curve | `CapabilityValueKind.Curve`, declared and projected but **rendered by nothing** |
| Textbox | missing |

Labels already follow the right pattern: `CapabilityDisplay` carries a WSGM-owned `DisplayKey` plus a
`Custom` escape hatch whose label is bounded, stripped of control and bidirectional-override
characters, and never treated as markup, a format string, or a localization key.

## Plugin setting descriptors

Settings are a separate declaration from capabilities, because they are a different thing with a
different owner and lifetime. A plugin publishes a settings manifest alongside its capability
manifest.

Each descriptor carries a stable id, a value kind, display metadata reusing `CapabilityDisplay`, a
default value, its section assignment and sort order, and bounds appropriate to its kind — minimum,
maximum, step and unit for a number; the legal option set for a choice; a maximum length for text.

Values are stored by WSGM keyed by device definition id and plugin id, delivered to the plugin at
start and on every change, and **revalidated against the current descriptor on load**, because a
plugin update can narrow a range or drop an option. A value that no longer validates falls back to
the declared default, and the fallback is logged with both the stored and the declared bounds.

## Text

`CapabilityValueKind.Text` is added deliberately, without a current in-tree consumer. The SDK is a
public contract for plugin authors WSGM has not met, and a device preference that is genuinely a
short string has no other representation.

It reuses `CustomLabel`'s treatment exactly rather than inventing a second one: a declared maximum
length, control characters and bidirectional overrides rejected, escaped at every sink, never a
format string, never markup, never a localization key.

The bound is on accidental UI corruption, not on the plugin. A plugin is trusted .NET code already
holding WMI, HID, and EC access; a text field is not a privilege boundary and must not be built as
though it were. What it prevents is a malformed string corrupting a log line, hiding its own tail
from a reviewer, or rendering as something other than what it says.

## Sections

A section is declared, not invented at render time. Each carries a stable id, display metadata, and a
sort order. Elements name the section they belong to and their order within it.

Display follows `CapabilityDisplay` exactly — a WSGM-owned section key that WSGM localizes, or
`Custom` with a bounded plain-text title. That tradeoff is already accepted for labels; sections are
the same problem one level up and must not acquire a second, looser rule.

Bounds and failure behaviour, all of which are logged with the values they were decided from:

- A maximum number of sections and elements per section. An unbounded page cannot be navigated with
  a gamepad, and a plugin that declares two hundred rows produces a surface no one can use.
- Section and element ids match the established `^[A-Za-z0-9._-]{1,64}$` shape.
- A duplicate section id is a manifest error; the manifest is refused and the reason names the id.
- An element naming an unknown section renders in a WSGM-owned fallback section rather than
  vanishing. It must never silently disappear.
- A section that ends up with no visible element is not drawn, and the reason is logged.
- Sort ties break on declaration order, so a plugin that declares no ordering still renders
  deterministically.

**Sections govern this page and `Generic*` capabilities only.** A semantic role keeps the home WSGM
gives it — a power limit belongs in Power and thermals on every device, and a plugin may not scatter
semantic controls into invented groupings. That consistency is the whole reason `DisplayKey` exists,
and section assignment must not become the hole in it.

Sections are focus groups. Their ids are stable semantic keys so the overlay's existing per-
destination focus and scroll restoration survives a capability or settings refresh.

## Profile authoring

Settings also gains what the boundary permits and the overlay cannot host well: **defining** named
RGB and fan profiles, including a curve editor. Defining a profile writes no hardware; the overlay
selects which definition is active, globally or per application.

- No curve editor exists anywhere in the tree today, so this is new UI, not a rebind.
- Profiles are device-specific. Channel counts, RPM ranges, and temperature sources come from the
  plugin's `FanCurve` descriptor, so profiles are stored keyed by device definition id and
  revalidated against the live descriptor before they are applied.
- `--settings` starts no DeviceHost and touches no hardware, so the editor is authoring-only against
  declared ranges. It has no live temperature or RPM readout. Observing live telemetry when a shell
  session already exists is a later improvement and is the part that pulls Settings toward the
  device, so it stays out of the first cut.
- Profile selection extends the per-application profile store introduced in `_plan\qam-overhaul.md`
  rather than standing up a second per-app mechanism.

## Not doing

- Plugin-supplied XAML, HTML, JavaScript, CSS, URLs, filesystem commands, or any executable UI.
- Plugin-supplied localization or format strings. WSGM cannot translate text it did not author, and
  a custom label is plain text in one language by definition.
- Free-form layout: no plugin-chosen columns, spacing, ordering primitives, or nesting beyond one
  level of section.
- Device control on this page, in any disguise.

## Implementation slices

1. **SDK** — `CapabilityValueKind.Text`; the plugin settings descriptor and settings manifest; the
   section descriptor and its display key; validation for every bound above, with failures naming
   the offending id.
2. **Storage** — settings values in WSGM configuration through the source-generated JSON path, keyed
   by device definition id and plugin id, with revalidation and logged fallback on load.
3. **Delivery** — settings handed to the plugin at start and on change over the existing wire
   contract; no new privileged channel.
4. **Settings page** — one WSGM-rendered page over the declared manifest, gamepad and touch
   navigable, using the shared controls and themes.
5. **Curve editor** — a reusable control in `Controls\`, used by fan profile authoring.
6. **Profiles** — device-keyed RGB and fan profile definitions, revalidated on apply, selected from
   the overlay globally or per application.
7. **Decision** — record the Settings/overlay boundary as a numbered decision in
   `_plan\2.0-decisions.md`.

## Validation

Automated: descriptor and manifest validation including every bound and every rejection reason;
section assignment and ordering as pure decision tests; stored-value revalidation against changed
descriptors; profile revalidation against a changed `FanCurve` descriptor; configuration round trips
through the existing isolated-HKCU pattern. The synthetic plugin fixture in
`WSGM.DeviceLab\Testing` gains a settings manifest so the page is exercised without hardware.

Attended on the reference device: the page rendered from the Claw plugin's real manifest, gamepad
and touch navigation across sections, a curve authored and then applied from the overlay, and
behaviour after a plugin update narrows a range that a stored value no longer satisfies.
