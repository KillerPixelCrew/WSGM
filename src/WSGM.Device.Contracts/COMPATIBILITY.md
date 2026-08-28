# Device contract compatibility policy

Frozen at protocol version `1`, schema fingerprint `wsgm-device-v1`.

Everything in `WSGM.Device.Contracts` is consumed by two sides that ship separately: WSGM itself,
and plugin packages built against a released SDK. A published plugin keeps running against a WSGM
that was built after it, which is the whole reason this policy exists.

## What each version number means

| Identifier                                          | Changes when                                                            |
| --------------------------------------------------- | ----------------------------------------------------------------------- |
| `DeviceProtocol.MaxSupportedVersion`                | The wire encoding, framing, or handshake changes                        |
| `DeviceProtocol.SchemaFingerprint`                  | Any contract shape changes, even without a version bump                 |
| `PluginManifestValidator.MaxSupportedSchemaVersion` | The `plugin.wsgm.json` schema changes                                   |
| `ImplementationModule.Version`                      | A module's behaviour, layout, limits, or recovery policy changes        |
| `CapabilityDescriptorSet.Generation`                | A running plugin republishes descriptors — runtime, not a release event |

The fingerprint exists because the first two can disagree. Two builds can agree on version `1` and
still compile against different record shapes if someone adds a field without bumping anything; the
handshake compares fingerprints and refuses, rather than letting them misread each other's payloads.

## Changes that do not break compatibility

- **Adding an enum member.** A peer that does not know it must handle it as unknown rather than
  crashing — which is why unknown message types are survivable and unknown request types get an
  error reply instead of a disconnect.
- **Adding an optional field** with a default that preserves existing behaviour.
- **Adding a message type.** Older peers ignore unknown notifications and error on unknown requests.
- **Adding a capability role, reason code, or display key.** Consumers render an unrecognised one
  generically rather than failing.
- **Documentation, comments, and test changes.**

Each of these bumps the fingerprint. None bumps the protocol version.

## Changes that break compatibility

- Removing or renaming any public type, member, or enum value.
- Changing a field's type, or making an optional field required.
- Changing the frame header layout or `FrameHeader.Size`.
- Changing what an existing enum value means — the most dangerous change on this list, because
  nothing about it is visible to a compiler on either side.
- Tightening a bound that existing packages already satisfy.

These require `MaxSupportedVersion + 1`, and the previous version stays inside the supported window
for at least one WSGM release so a plugin has time to rebuild.

## Deprecation

1. Mark the member `[Obsolete]` with the replacement named, and keep it working.
2. Ship at least one release in that state.
3. Remove it in a version bump, with the removal listed in the release notes.

A member is never removed in the same release that deprecates it, and the compatibility window is
never widened just to avoid a deprecation — every version inside the window has to keep being
tested.

## Extension without a version bump

Two extension points exist so unusual hardware does not force protocol changes:

- **Generic capability roles** (`GenericToggle`, `GenericRange`, `GenericChoice`, `GenericAction`,
  `GenericReadOnly`) express a device-specific control WSGM has no named role for.
- **`DisplayKey.Custom`** with a bounded, escaped plain-text label names it.

Both are deliberately limited. A plugin still cannot supply markup, formatting, localization
resources, or executable content through either, and neither is a general escape hatch: a capability
that keeps appearing across devices should get a named role and a localized display key instead.

## What is not extensible, ever

The frame header layout, the closed `DeviceMessageType` vocabulary, and the absence of any generic
execute, shell, file, WMI, HID, EC, IOCTL, script, path, helper, or raw-buffer operation. These are
the boundary itself rather than a default, and widening them is not a compatibility decision.

## What must ship together with a change here

- Compatibility tests covering both sides of the supported window.
- An updated fingerprint.
- Regenerated fixtures, with a semantic diff reviewed rather than accepted wholesale.
- The manifest schema version, when the package format changed.
