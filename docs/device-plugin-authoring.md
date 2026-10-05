# Device plugin authoring

WSGM loads one administrator-installed device plugin through the public `WSGM.Device.Sdk` assembly.
A plugin owns exact device detection, hardware transports, semantic capabilities, input and output,
diagnostics and restoration; it supplies no UI code and cannot use WSGM internals. This document is
the author workflow, create, build, test, pack and install, and the device projects in this
repository that it rests on.

Related:

- `src\WSGM.Device.Sdk\docs\reference.md`: the contract, type by type.
- [device-plugin-system.md](device-plugin-system.md): what WSGM does with each publication, command
  and lifecycle call, with the built-in Claw package as the worked example.

## The device projects in this repository

Both tools an author needs are MIT projects in WSGM: `src\WSGM.Device.Sdk` is the contract and
`src\WSGM.DeviceLab` is the tool. The SDK, Device Lab, the Claw plugin, the ROG Ally plugin and the
Handheld Companion scaffold are maintained here, with source under `src`, tests under `tests`, and
all of them in `WSGM.slnx`. Every consumer references `src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj`,
so a contract change and its consumers build and go through review together.

| Project            | Source and documentation                                                        | Status                                                         |
| ------------------ | ------------------------------------------------------------------------------- | -------------------------------------------------------------- |
| SDK                | [WSGM.Device.Sdk](../src/WSGM.Device.Sdk/README.md)                             | Public MIT contract and NuGet package support                  |
| Device Lab         | [WSGM.DeviceLab](../src/WSGM.DeviceLab/README.md)                               | Separate GUI/CLI executable, optional installer component      |
| MSI Claw           | [WSGM.Device.Msi.Claw](../src/WSGM.Device.Msi.Claw/README.md)                   | Built-in reference plugin, loaded dynamically                  |
| ASUS ROG Ally      | [WSGM.Device.Asus.RogAlly](../src/WSGM.Device.Asus.RogAlly/README.md)           | All four Allys, built blind from HHD and HC, awaiting lab data |
| Handheld Companion | [WSGM.Device.HandheldCompanion](../src/WSGM.Device.HandheldCompanion/README.md) | Design scaffold and IPC proposal, no working plugin yet        |

WSGM references only the SDK at compile time. Device Lab and the plugins are separate assemblies
with their own lifecycle and package boundaries. The installer bundles the Claw package
(hardware-tested), the ROG Ally package (blind) and the optional Device Lab tool; setup installs the
one device package whose hardware rules match the machine, as
[plugins/README.md](../plugins/README.md) describes. The HC scaffold has no entry in
`plugins/curated` and does not ship. The retired Generic PC repository held only a design scaffold:
Windows-wide features belong in Core, device-specific integrations in plugins.

Test a change with the project that covers it, from the repository root:

```powershell
dotnet test tests/WSGM.Device.Sdk.Tests/WSGM.Device.Sdk.Tests.csproj
```

Use the matching test project for Device Lab, Claw or Ally work. When the full `eng/verify.ps1` gate
runs is set by the validation policy in the root `AGENTS.md`. Hardware validation is a separate,
explicitly attended operation.

When a package or publish artifact is needed:

```powershell
dotnet pack src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj --configuration Release --output publish/sdk
./eng/publish-device-lab.ps1
./eng/pack-device.ps1 -Source src/WSGM.Device.Msi.Claw -RequireGlyphs
```

`eng/build-bundle.ps1` builds the bundled plugin packages and `bundle.json` from these same sources,
listed in `plugins/curated`. `eng/pack-device.ps1 -Source <project directory>` packs any device
project, so the Ally plugin and the HC scaffold stay packable; add `-RequireGlyphs` for a package
that ships physical glyphs. It uses `eng/plugin-package-common.ps1` to replace an existing archive
atomically or publish a new one without overwriting a competing file, and a failed replacement keeps
the previous archive.

The imported source trees and their test trees keep their original MIT licences, each with a
`LICENSE` file; the ROG Ally plugin is MIT too. The packaging scripts (`eng/publish-device-lab.ps1`,
`eng/pack-device.ps1`, which merges the former Claw and HC packers, and their shared
`eng/plugin-package-common.ps1` and `eng/device-lab-publish.ps1` helpers) are MIT as well. WSGM's
main application remains GPL-3.0-or-later.

The consolidation of 2026-09-05 imported these merged revisions. History stays in the original
repositories; these identifiers record the exact source baseline of the move:

| Former repository             | Revision                                   |
| ----------------------------- | ------------------------------------------ |
| WSGM.Device.Sdk               | `0d874c72966309d77d36b7c1e965ec21ef8edf57` |
| WSGM.DeviceLab                | `3ea7aaf59f0e9d066afe45ab4f0fe58bb6c799bb` |
| WSGM.Device.Msi.Claw          | `e7092811840c835b43e98a4eeb1c75c9cc6b435a` |
| WSGM.Device.HandheldCompanion | `ea52f2332fe5d69ac6f39e81b553a22332f26c13` |

Only `external/steam-input-lease`, `external/steam-ui-toolkit`, `external/viiper` and
`external/windows-device-control` remain Git submodules; there are no nested SDK pins to advance.

Device Lab compiles in a knowledge base of known handhelds under
`src/WSGM.DeviceLab/Knowledge/Devices`, which `candidates` matches against an inventory. Extracted
records come from the decompiled Handheld Companion source through `eng/extract-hc-devices.ps1` and
the development-only `tools/HcDeviceExtract`, which is not in `WSGM.slnx`. Curated records are
written by hand from the plugins and lab runs. The Device Lab README describes both.

A remote tester runs the Device Lab wizard from one portable `wsgm-device.exe`
(`eng/publish-device-lab.ps1 -Portable`), which replaced the Ally-only AllyXLab tool. The returned
`.wsgmlab` report is read with `wsgm-device report`, `review` and `promote`, and a plugin can be
scaffolded from it. The source comparison and the outstanding hardware validation for the Ally live
beside the Ally plugin.

## 1. Create and implement

Use Device Lab's Plugin Developer flow, or scaffold from a confirmed capture:

```powershell
wsgm-device scaffold --from <capture.wsgmcap> --out-dir <new-plugin-directory>
```

The generated project contains a minimal `IDevicePlugin`, a `plugin.wsgm.json` with the captured
board as its first `hardware` rule and an empty `capabilities` list to fill in, an explicit x64
target, an MIT `LICENSE.txt` the author is expected to put their own name in, and the package
layout. `LICENSE.txt` is kept beside both build and publish output.

A scaffolded plugin links only the MIT SDK, never WSGM, so the author picks its licence freely,
including a closed-source vendor plugin. Inside a WSGM checkout the project references the SDK
through `src\WSGM.Device.Sdk`; an installed Device Lab instead writes an explicit reference to the
exact `WSGM.Device.Sdk.dll` shipped beside the tool. That path is validated before any scaffold file
is written, so no undefined MSBuild property is emitted. Keep the reference on that exact API if the
scaffold is moved to another machine.

Implement exact detection first, then add direct device-owned services. Publish only semantic
descriptors, state, input and diagnostics through `IPluginHostAdapter`; vendor addresses, packets,
handles and recovery state stay inside the plugin.

A plugin owns its Device-tab layout by declaring overlay sections inside every
`CapabilityDescriptorSet` (introduced in API version 2; the current version is `DeviceApi.Version`
in the [SDK reference](../src/WSGM.Device.Sdk/docs/reference.md)): `CapabilitySection` entries with
their categories, each titled by a `SettingSectionKey` or custom text and iconed from the closed
`SectionIcon` vocabulary, with `SectionId`, `CategoryId` and `SortOrder` on each descriptor placing
it. Any role may be placed in a declared section, and the layout ships atomically with the
capabilities it lays out. An unplaced capability keeps the semantic home WSGM derives from its role,
and a semantic role naming an undeclared section rejects the whole set. Layout is grouping only:
WSGM still owns every title string, icon geometry and control shape it renders.

Every hardware write must recheck current identity and bounds, serialize its real transport, read
back when the hardware supports it, and restore the captured original state on failure or stop.
Unknown identity or ranges fail closed. A partial device is valid: publish the working capabilities
and a specific unavailable reason for the others.

## 2. Build and run safely

Build the plugin for 64-bit Windows and place the entry assembly plus package-local dependencies
beside the manifest:

```powershell
dotnet build <plugin.csproj> -c Release -r win-x64
wsgm-device validate <package-directory>
wsgm-device test sample
wsgm-device test plugin <package-directory> --from <inventory.json>
```

`validate` is offline and does not load plugin code. It rejects a missing, malformed or non-x64
entry assembly and enforces the same entry, file, per-file and aggregate-byte package budgets used
by protected staging. `test plugin` loads the package and runs exact detection only.

The attended hardware path needs a temporary state directory:

```powershell
wsgm-device test hardware <package-directory> --from <inventory.json> --state-dir <new-directory> --action haptic
```

Use `--action controller` for the bounded controller-management check. A semantic capability write
uses `--action capability --capability <id> --value <semantic-value>` plus optional
`--instance <id>`. Each run accepts exactly one explicit action.

`--action haptic-sweep` is the interactive motor calibration that measures the two
`HapticCapabilities` values a plugin must declare from its motor technology: `MinimumStartIntensity`
(the weakest bounded haptic event the motors render) and `MinimumPulse` (the shortest). The device's
own controls pace it: A steps each descending sweep, B marks the perception boundary. It runs three
phases: continuous strength (informational; the host never floors continuous rumble), 30 ms ticks
(the start intensity), and full-strength pulses of shrinking length (the minimum pulse). The report
prints the values to declare verbatim. A voice coil or LRA that renders everything keeps the zero
defaults; the Claw's ERM motors measured 0.22 / 10 ms this way (Claw, 2026-09-02).

The hardware command refuses:

- redirected input or output;
- CI;
- `--yes`;
- a nonmatching device;
- an active WSGM Device Integration owner;
- a process without elevation;
- a reused state directory.

It requires a local confirmation immediately before activation. The state-path, owner, elevation,
attendance, CI and confirmation checks complete before Device Lab loads the plugin assembly or runs
its constructor. Exact detection runs only after those checks and must match before activation.
Device Lab gives startup and cleanup 15-second cancellation budgets, so the plugin must honor
cancellation for the in-process developer run to return. Never automate this command.

## 3. Test and diagnose

Use `WSGM.Device.Sdk.Testing.TestPluginHostAdapter` for deterministic lifecycle,
partial-availability, publication, cancellation and cleanup tests without touching hardware. Keep
transport parsing and decision logic behind fakes; reserve real WMI, HID and controller checks for
the attended Device Lab path.

The supporting read-only commands require their explicit inputs:

```powershell
wsgm-device doctor --out-dir <diagnostics-directory>
wsgm-device inventory --out-dir <inventory-directory> --shareable
wsgm-device inspect <capture.wsgmcap>
wsgm-device compare <first.wsgmcap> <second.wsgmcap>
wsgm-device correlate <capture.wsgmcap> --action <id> --sources <id,id>
```

They collect or inspect machine and capture evidence without granting mutation authority.

A plugin should leave enough bounded diagnostics to explain detection, service availability,
readback, restoration and dependency failures. Do not log personal identifiers, raw secrets or
unbounded device payloads.

## 4. Pack

Create the deterministic distribution archive only after offline validation passes:

```powershell
wsgm-device pack <package-directory> --out <plugin.wsgmpkg>
```

The archive contains only the validated package files in deterministic path and timestamp order.
Device Lab pins the source tree and regular-file handles before validation, then writes the archive
from those same handles, so a link or file replacement cannot substitute different bytes after a
clean report. Licence and attribution notices required by shipped code or glyph assets stay package
files.

WSGM refuses a package that does not name the WSGM release it was built for, so pack through
`eng\pack-device.ps1`: it publishes the project, stamps `wsgmVersion` from `src\WSGM\WSGM.csproj`
(or `-WsgmVersion`), removes the assemblies WSGM always supplies itself (the SDKs, the WinRT
projection and runtime) together with symbols and XML documentation, validates, then runs
`wsgm-device pack`. An archive packed from a source tree by `wsgm-device pack` alone is unstamped.

## 5. Install or replace the package

A package becomes trusted hardware code that may later inherit WSGM's elevation, so inspect and
validate the exact `.wsgmpkg` you intend to install.

WSGM loads packages straight from their file in the administrator-protected Plugins folder; nothing
is unpacked. Close WSGM first, because a loaded package file is held open, then copy the package in
from an elevated PowerShell:

```powershell
$plugins = Join-Path $env:ProgramFiles 'WSGM\Plugins'
New-Item -ItemType Directory -Path $plugins -Force | Out-Null
Copy-Item -LiteralPath <plugin.wsgmpkg> -Destination $plugins
```

At the next start WSGM validates the file again: bounded file and package sizes, no native images,
the manifest, the exact API version and an x64 entry point
([device plugin system](device-plugin-system.md) §2–§5). For one id the highest version wins, and
older files are reported as superseded rather than deleted. A second device package with a different
id makes WSGM refuse device integration until one of them is removed, so a release and a developer
plugin never run side by side.

Enable Device Integration in WSGM Settings once the package is in place.

To return to core-only WSGM, close WSGM and delete the package file from the same folder.
