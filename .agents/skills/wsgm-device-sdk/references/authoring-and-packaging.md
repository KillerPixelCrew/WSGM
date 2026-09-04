# Authoring and packaging

## Start with evidence and exact identity

Use the `wsgm-device-lab` skill when the hardware contract is not already established. Detection
should combine the immutable facts needed to distinguish the supported machine and firmware; never
use a marketing name, current USB location, or WMI method enumerability as sufficient proof.

Implement in this order:

1. Exact, side-effect-free detection and an informative no-match reason.
2. Direct device-owned services with fakeable parsers/codecs and serialized transports.
3. Settings declaration, descriptor set, physical devices/haptics, OEM controls, then initial state.
4. Semantic commands with per-command revalidation, readback, rollback, and restoration.
5. Controller acquisition/input normalization/output mapping and ordered release, if supported.
6. Bounded diagnostics and transition logging.
7. Hardware-free lifecycle, partial-availability, cancellation, and cleanup tests.
8. Offline validation and only then a maintainer-directed attended device action.

A partial device is valid. Publish healthy capabilities and precise unavailable reasons for the
others; do not fail the whole plugin because one optional service is missing.

## Package contract

`plugin.wsgm.json` has exactly six camelCase members; unknown members are rejected:

```json
{
  "id": "com.example.handheld",
  "name": "Example Handheld",
  "version": "1.0.0",
  "apiVersion": 3,
  "entryAssembly": "Example.Handheld.dll",
  "entryType": "Example.Handheld.DevicePlugin"
}
```

Do not copy the literal API number without checking `DeviceApi.Version`. Runtime compatibility is
the manifest API integer, not the SDK NuGet or assembly version. The entry assembly is x64 managed
code; the loader requires one public, concrete, non-generic `IDevicePlugin` with a public
parameterless constructor and a `PackageId` matching the manifest.

Keep package-local managed/native dependencies beside the entry assembly. Declare prerequisites and
report them unavailable; never install a driver, edit machine policy, restart a device, or run an
installer from plugin code.

## Capabilities, layout, and settings

- Prefer the existing `CapabilityRole`, value kind, unit, display key, reason code, and persistence
  vocabulary. Closed vocabularies keep the host—not plugin text or UI code—in control of rendering.
- Publish a complete `CapabilityDescriptorSet`, including its sections/categories. Any changed
  descriptor or layout requires a new descriptor generation.
- A value record is a tagged union by contract; constructors do not enforce that exactly one field
  is populated. Validate it.
- Treat published `IReadOnlyList` values as frozen even if their backing collection is mutable.
- Plugin settings are validated preferences delivered as a complete set. They are not a generic
  action surface and must not smuggle opaque hardware writes.

## Glyph data

Glyphs are static content under `glyphs/profiles/<profileId>.json` and content-addressed assets
under `glyphs/assets/<sha256>.<svg|png>`, plus the declared notice. Import validates paths, hashes,
bounds, and renderable projections while retaining the author's exact asset bytes. That is integrity
validation, not sanitization of plugin code or an authorization boundary.

Use Device Lab's glyph import and package validation rather than hand-rolling the rules. Keep
licence and attribution files inside the package.

## Offline and trusted-code author loop

From the WSGM root, adjust paths for the package being authored:

```powershell
dotnet build <plugin.csproj> --configuration Release --runtime win-x64
wsgm-device validate <package-directory>
wsgm-device test sample
wsgm-device test plugin <package-directory> --from <inventory.json>
wsgm-device pack <package-directory> --out <new-package.wsgmpkg>
```

`validate` is static and does not load plugin code. `test plugin` does load, construct, and call
`DetectAsync`; do not run it on an untrusted package merely because it does not request a hardware
write. Use a new explicit output path for pack/scaffold operations.

For deterministic tests, combine fake transports with `TestPluginHostAdapter`, assert each complete
publication and trace, call SDK validators explicitly, and cover:

- exact match and no-match;
- complete and degraded startup;
- cancellation after each acquisition stage;
- stale cycle/descriptor generations;
- verified, unverified, rejected, timed-out, and indeterminate commands;
- partial-write rollback and first-original restoration;
- suspend/resume and controller re-enable with a fresh generation;
- release and stop after repeated calls or failures;
- explicit zero haptics and no publication after dispose.

## Public API change checklist

The SDK is a zero-dependency `net10.0-windows` leaf and every public member requires XML docs. A
public change must update:

- `DeviceApi.Version` and its pinning test when compatibility is deliberately broken;
- source XML docs and `external/WSGM.Device.Sdk/docs/reference.md`;
- manifest/examples and serialization metadata only where the actual wire format uses them;
- Device Lab scaffolding/validation and its SDK gitlink;
- every first-party plugin and its SDK/Device Lab pins;
- WSGM host consumers, tests, and final gitlinks.

Commit and push in dependency order. A green SDK build alone is insufficient because lifecycle,
input, haptic, and OEM behavior is mostly proven in WSGM and real-plugin tests.

Standalone SDK validation from `external/WSGM.Device.Sdk`:

```powershell
dotnet build WSGM.Device.Sdk.slnx --configuration Release
dotnet test WSGM.Device.Sdk.slnx --configuration Release --no-build
dotnet pack src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj --configuration Release --no-build --output artifacts
```
