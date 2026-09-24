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
  "apiVersion": 5,
  "entryAssembly": "Example.Handheld.dll",
  "entryType": "Example.Handheld.DevicePlugin"
}
```

Do not copy the literal API number without checking `DeviceApi.Version`. Runtime compatibility is
the manifest API integer, not the SDK NuGet or assembly version. The entry assembly is x64 managed
code; the loader requires one public, concrete, non-generic `IDevicePlugin` with a public
parameterless constructor and a `PackageId` matching the manifest.

Keep package-local managed/native dependencies beside the entry assembly. `WSGM.Device.Sdk`,
`WSGM.Plugin.Sdk`, SteamUiToolkit, `WinRT.Runtime` and `Microsoft.Windows.SDK.NET` always resolve to
the host's copy, whatever the package ships. Declare prerequisites and report them unavailable;
never install a driver, edit machine policy, restart a device, or run an installer from plugin code.

## Capabilities, layout, and settings

- Prefer the existing `CapabilityRole`, value kind, unit, display key, reason code, and persistence
  vocabulary. Closed vocabularies keep the host, not plugin text or UI code, in control of
  rendering.
- Publish a complete `CapabilityDescriptorSet`, including its sections, categories, API 5 layout
  hints (`Prominence`, `LayoutPair`), power presets and power pair. Any changed descriptor or layout
  requires a new descriptor generation.
- A value record is a tagged union by contract; constructors do not enforce that exactly one field
  is populated. Build values with the `CapabilityValue` factories (`None()`, `Boolean()`,
  `Integer()` and so on) and validate them.
- Validate identifiers and display text with the shared `PlainText.IsIdentifier` and
  `PlainText.TryValidate` rather than bespoke checks.
- Treat published `IReadOnlyList` values as frozen even if their backing collection is mutable.
- Plugin settings are validated preferences delivered as a complete set. They are not a generic
  action surface and must not smuggle opaque hardware writes.

## Glyph data

Glyphs are static content under `glyphs/profiles/<profileId>.json` and named assets under
`glyphs/assets/<assetId>.<svg|png>`. The asset id is the file name, for example `face-south.svg`
(`GlyphPackageLayout`). The profile also names a confined `.md` or `.txt` notice through
`noticePath`. Import checks identifiers, confined paths, format, role, dimensions, per-asset and
aggregate byte budgets, and the renderable projections. It keeps the author's exact asset bytes.
There is no per-asset hash; the `.wsgmpkg` covers corruption. This is integrity validation, not
sanitization of plugin code or an authorization boundary.

Use Device Lab's glyph import and package validation rather than hand-rolling the rules. Keep
licence and attribution files inside the package.

## Offline and trusted-code author loop

`wsgm-device` is Device Lab's executable, not a tool on `PATH`. Build it, or use
`dotnet run --project src/WSGM.DeviceLab --configuration Release -- <command>` (see the
`wsgm-device-lab` skill). From the WSGM root, adjust paths for the package being authored:

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
`eng/pack-device.ps1 -Source <project> [-RequireGlyphs]` builds, stages and packs a first-party
device project under `publish`.

## Deploying for the maintainer's manual test

Every route here changes the live machine, so run one only when the maintainer directs it:

- `eng/dev-deploy.ps1` publishes WSGM, restarts WSGM and Steam, and rebuilds the Claw package
  through `eng/stage-device-components.ps1`. It copies the `.wsgmpkg` into
  `%ProgramFiles%\WSGM\Plugins` behind one elevation prompt and deletes other builds of the same id.
  It refuses to run unless the baseboard is `MS-1T52`, or `MS-7E16` with `-Desktop`, which also
  implies `-SkipPlugin`. Use `-SkipPlugin` only for pure WSGM changes. After an SDK change, a
  skipped plugin refresh leaves an API-incompatible package that the host rejects. The staging
  script is hard-wired to the Claw project.
- For any other package, close WSGM and copy the `.wsgmpkg` into `%ProgramFiles%\WSGM\Plugins`
  (`docs/device-plugin-authoring.md`, section 5).

## Tests

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

- source XML docs and `src/WSGM.Device.Sdk/docs/reference.md`;
- manifest/examples and serialization metadata only where the actual wire format uses them;
- Device Lab scaffolding/validation;
- every first-party device project, including the Ally X and HC scaffolds;
- WSGM host consumers and tests.

When compatibility is deliberately broken, also update:

- `DeviceApi.Version`, its version-history remarks in `DeviceApi.cs`, and the pinning test in
  `tests/WSGM.Device.Sdk.Tests/Boundaries/ContractBoundaryTests.cs`;
- the SDK csproj `<Version>`, whose minor version moves on a breaking change while the SDK is
  pre-1.0;
- all three `plugin.wsgm.json` files.

The Device Lab scaffold template and `tests/Shared/PluginManifestFixture.cs` take
`DeviceApi.Version` automatically.

Deliver these changes together in one WSGM pull request. A green SDK build alone is insufficient
because lifecycle, input, haptic, and OEM behavior is mostly proven in WSGM and real-plugin tests.

Build before the maintainer's manual test. Run the tests afterwards, and pack only when a package is
requested:

```powershell
dotnet build src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj --configuration Release
dotnet test tests/WSGM.Device.Sdk.Tests/WSGM.Device.Sdk.Tests.csproj --configuration Release
dotnet pack src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj --configuration Release --no-build --output publish/sdk
```

Device test projects are `tests/WSGM.Device.Sdk.Tests`, `tests/WSGM.Device.Msi.Claw8A2Vm.Tests`,
`tests/WSGM.Device.HandheldCompanion.Tests` (manifest only) and `tests/WSGM.DeviceLab.Tests`. Host
tests live under `tests/WSGM.Tests/Shell` and `tests/WSGM.Tests/Core`. The Ally X plugin has no test
project yet.
